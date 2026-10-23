using System;
using System.Collections.Generic;
using System.Linq;
using Boundari.Sim;
using Boundari.View;
using Godot;

namespace Boundari;

/// <summary>
/// Ordering your dots:
/// - Buy mode (H, or a HUD button named "BuyDots"): the mouse wheel sets how many, a click
///   on your border buys them there (they walk out from home and spread along it). Right
///   click or Esc leaves; Shift+click buys and stays in buy mode.
/// - Left-drag one of your dots to a spot on your border and it walks there; dragging a
///   selected dot moves the whole selection. Ctrl+left-drag draws a box that selects your
///   dots in it (Shift+Ctrl adds to the selection); a click on your own land, or Esc, clears it.
/// - A click on one of your dots raises it a strength level (costs Funds).
/// - Holding right-click on one of your dots for half a second sells it; a quick right
///   click opens the radial menu as anywhere else.
/// - Hovering a group (standing dots chained along a border) shows its handles: drag an end
///   to spread or cramp the group along the border, the middle bar to slide it.
/// The presses that start these never reach the camera (see <see cref="MapInputGate"/>).
/// </summary>
public partial class World
{
    private const double SellHoldSeconds = 0.5;
    private const double TipDelaySeconds = 0.35;
    private const float HandleGrabPixels = 6;
    private const float SnapPixels = 24; // a drop or buy this close to your border (on screen) lands on it

    private enum DotGesture { None, Press, Drag, Box, Handle, Sell, Sold }

    private bool _buying;
    private int _buyCount = 5;
    private readonly HashSet<int> _selected = new();
    private DotGesture _gesture;
    private int _gestureDot = -1;
    private Vector2 _gestureStart;        // map units
    private Vector2 _gestureStartScreen;
    private bool _gestureShift;
    private ulong _sellStarted;           // when the right button went down on the dot being sold (ms)
    private List<Dot>? _group;            // the group under the mouse (or being re-spread), end to end
    private Vector2[]? _handles;          // its handles: one end, the other end, the middle
    private int _handle = -1;             // the handle being dragged
    private int _tipDot = -1;
    private double _tipHeld;
    private Vector2 _mouseScreen;         // where the last mouse event was, on screen
    private (int Tile, int Count, int Owned, long Tick) _buyPreviewKey = (-1, 0, 0, 0);
    private readonly List<Vector2> _buyPreview = new();
    private (int, int) _spreadPreviewKey = (-1, -1);
    private readonly List<(Vector2 From, Vector2 To)> _spreadPreview = new();

    /// <summary>Puts the input gate in front of the camera and hooks up a "BuyDots" button if the HUD has one.</summary>
    private void SetUpDotOrders()
    {
        // Deferred, so the gate ends up after anything else added while the scene starts.
        CallDeferred(Node.MethodName.AddChild, new MapInputGate { Name = "MapInputGate", Handler = DotInput });
        if (FindChild("BuyDots", true, false) is BaseButton buy)
            buy.Pressed += ToggleBuying;
    }

    /// <summary>The mouse on the map, as of the last mouse event (or where --hover pretends it is).</summary>
    private Vector2 DotMouse() => _args.Hover is not null ? HoverMouse() : GetCanvasTransform().AffineInverse() * _mouseScreen;

    private bool DotOrdersAllowed() =>
        _game.Phase == GamePhase.Running && _me.IsAlive && _me.HasSpawned && _placing is null && _aiming is null
        && !_startPrompt.Visible && !_fastForwarding && !_arrowActive && !_hud.AdjustingRatio;

    private bool OverHud() =>
        GetViewport().GuiGetHoveredControl() is { MouseFilter: not Control.MouseFilterEnum.Ignore } over && !_panels.Contains(over);

    private bool RadialMenuOpen() =>
        _mainControl?.Get("current_menu").AsGodotObject() is Node menu && IsInstanceValid(menu);

    /// <summary>H (or the BuyDots button): buy mode on or off.</summary>
    private void ToggleBuying()
    {
        SyncSim();
        if (_buying)
        {
            StopBuying();
            return;
        }
        StopPlacing();
        if (_game.Phase != GamePhase.Running || !_me.IsAlive || !_me.HasSpawned)
            return;
        _buying = true;
        _hud.Flash("Buying dots: the wheel sets how many, click your border");
    }

    private void StopBuying()
    {
        _buying = false;
        _buyPreviewKey = (-1, 0, 0, 0);
    }

    /// <summary>Input seen before the camera: true keeps it (a dot, box or handle grabbed, the wheel in buy mode...).</summary>
    private bool DotInput(InputEvent e)
    {
        if (e is InputEventMouse at)
            _mouseScreen = at.Position;
        SyncSim();
        if (!DotOrdersAllowed())
        {
            EndGesture();
            return false;
        }
        var mouse = DotMouse();
        switch (e)
        {
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }:
                if (_gesture is DotGesture.Drag or DotGesture.Box or DotGesture.Handle)
                    EndGesture();
                else if (_buying)
                    StopBuying();
                else if (_selected.Count > 0)
                    _selected.Clear();
                else
                    return false;
                return true;

            case InputEventMouseButton { Pressed: true, ShiftPressed: false, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel when _buying && !OverHud():
                _buyCount = Math.Clamp(_buyCount + (wheel.ButtonIndex == MouseButton.WheelUp ? 1 : -1), 1, Math.Max(1, _me.DotCap));
                return true;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
                if (OverHud() || _buying || RadialMenuOpen())
                    return false; // (in buy mode a click buys and a drag pans, as usual)
                _gestureStart = mouse;
                _gestureStartScreen = _mouseScreen;
                _gestureShift = press.ShiftPressed;
                if (press.CtrlPressed)
                    _gesture = DotGesture.Box;
                else if (_handles is not null && HandleAt(mouse) is >= 0 and var h)
                    (_gesture, _handle) = (DotGesture.Handle, h);
                else if (MyDotAt(mouse) is { } dot)
                    (_gesture, _gestureDot) = (DotGesture.Press, dot.Id);
                else
                    return false;
                return true;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }:
                if (_gesture is not (DotGesture.Press or DotGesture.Drag or DotGesture.Box or DotGesture.Handle))
                    return false;
                FinishGesture(mouse);
                return true;

            case InputEventMouseMotion:
                if (_gesture == DotGesture.Press && _mouseScreen.DistanceTo(_gestureStartScreen) > ClickMaxDrag)
                    _gesture = DotGesture.Drag;
                return false;

            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true }:
                if (OverHud() || RadialMenuOpen())
                    return false;
                if (_buying)
                    StopBuying();
                else if (_gesture != DotGesture.None)
                    EndGesture(); // a right click drops whatever is being dragged
                else if (MyDotAt(mouse) is { } selling)
                    (_gesture, _gestureDot, _sellStarted) = (DotGesture.Sell, selling.Id, Time.GetTicksMsec());
                else
                    return false;
                return true;

            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false }:
                if (_gesture == DotGesture.Sell)
                {
                    EndGesture(); // let go early: the radial menu, as a right click anywhere else
                    OpenRadialMenuAtMouse();
                    return true;
                }
                if (_gesture != DotGesture.Sold)
                    return false;
                EndGesture();
                return true;
        }
        return false;
    }

    private void EndGesture()
    {
        _spreadPreviewKey = (-1, -1);
        _gesture = DotGesture.None;
        _gestureDot = -1;
        _handle = -1;
    }

    private void FinishGesture(Vector2 mouse)
    {
        switch (_gesture)
        {
            case DotGesture.Press:
                UpgradeClicked(_gestureDot);
                break;
            case DotGesture.Drag:
                int target = DropTile(mouse);
                if (target >= 0)
                    _game.Submit(new MoveDotsIntent(_me.Id, MovingIds(), target));
                else
                    _hud.Flash("Drop dots on your own border");
                break;
            case DotGesture.Box:
                var box = new Rect2(_gestureStart, Vector2.Zero).Expand(mouse);
                if (!_gestureShift)
                    _selected.Clear();
                foreach (var d in _game.Dots)
                    if (d.Owner == _me && box.HasPoint(_overlay.DotDrawnAt(d)))
                        _selected.Add(d.Id);
                break;
            case DotGesture.Handle when _group is not null:
                var (from, to) = HandleEnds(mouse);
                _game.Submit(new SpreadDotsIntent(_me.Id, _group.Select(d => d.Id).ToArray(), _view.TileAt(from), _view.TileAt(to)));
                break;
        }
        EndGesture();
    }

    private void UpgradeClicked(int dotId)
    {
        if (_game.DotById(dotId) is not { } dot || dot.Owner != _me)
            return;
        if (dot.Strength >= DotRules.MaxStrength)
        {
            _hud.Flash("This dot is at the top level");
            return;
        }
        long cost = Game.DotUpgradeCost(dot);
        if (!_me.CanAfford(cost))
        {
            _hud.Flash($"Raising this dot takes {GameHud.FormatCount(cost)} Funds");
            return;
        }
        _game.Submit(new UpgradeDotIntent(_me.Id, dot.Id));
        _hud.Flash($"Dot raised to level {dot.Strength + 1}");
    }

    /// <summary>The dots a drag moves: the whole selection if the dragged dot is in it, else just that dot.</summary>
    private int[] MovingIds() => _selected.Contains(_gestureDot) ? _selected.Order().ToArray() : new[] { _gestureDot };

    /// <summary>A click in buy mode: buys there if it's on (or near) your border. False if not buying.</summary>
    private bool ClickToBuy(int tile)
    {
        if (!_buying || !DotOrdersAllowed())
            return false;
        int border = tile < 0 ? -1 : _game.BorderTileWithin(_me, tile, SnapTiles());
        if (border < 0)
            _hud.Flash("Click your own border to place dots");
        else if (_game.DotsAffordable(_me, _buyCount) == 0)
            _hud.Flash(CantBuyReason());
        else
        {
            _game.Submit(new BuyDotsIntent(_me.Id, _buyCount, border));
            if (!Input.IsKeyPressed(Key.Shift)) // hold Shift to keep buying
                StopBuying();
        }
        return true;
    }

    private string CantBuyReason() =>
        _me.DotCount >= _me.DotCap ? $"Dot cap reached ({_me.DotCount}/{_me.DotCap})" : $"A dot costs {GameHud.FormatCount(DotRules.Price)} Funds";

    /// <summary>How far (in tiles) a drop or buy may land from your border and still snap onto it.</summary>
    private int SnapTiles() => Math.Max(_game.Rules.DotSpacing, (int)Math.Ceiling(SnapPixels / Math.Max(_camera.Zoom.X, 0.01f)));

    /// <summary>Your border tile a drop at this point lands on (-1 = none near).</summary>
    private int DropTile(Vector2 at)
    {
        int tile = _view.TileAt(at);
        return tile < 0 ? -1 : _game.BorderTileWithin(_me, tile, SnapTiles());
    }

    private Vector2 TileMiddle(int tile) => new(tile % _map.Width + 0.5f, tile / _map.Width + 0.5f);

    /// <summary>Your dot under the mouse (the nearest, if several), or null.</summary>
    private Dot? MyDotAt(Vector2 at)
    {
        float zoom = Math.Max(_camera.Zoom.X, 0.01f), reach = _overlay.DotRadius(zoom) + 3 / zoom;
        Dot? best = null;
        float bestDist = reach * reach;
        foreach (var d in _game.Dots)
        {
            if (d.Owner != _me)
                continue;
            float dist = _overlay.DotDrawnAt(d).DistanceSquaredTo(at);
            if (dist <= bestDist)
                (best, bestDist) = (d, dist);
        }
        return best;
    }

    /// <summary>The group handle under the mouse (0, 1 = the ends, 2 = the middle), or -1.</summary>
    private int HandleAt(Vector2 at)
    {
        if (_handles is null)
            return -1;
        float zoom = Math.Max(_camera.Zoom.X, 0.01f), reach = _overlay.DotRadius(zoom) * 0.95f + HandleGrabPixels / zoom;
        int best = -1;
        float bestDist = reach * reach;
        for (int i = 0; i < _handles.Length; i++)
            if (_handles[i].DistanceSquaredTo(at) <= bestDist)
                (best, bestDist) = (i, _handles[i].DistanceSquaredTo(at));
        return best;
    }

    /// <summary>A group's dots from one end to the other (roughly: by distance from the dot farthest from the first).</summary>
    private List<Dot> EndToEnd(List<Dot> group)
    {
        var first = TileMiddle(group[0].Tile);
        var end = group.MaxBy(d => TileMiddle(d.Tile).DistanceSquaredTo(first))!;
        var from = TileMiddle(end.Tile);
        return group.OrderBy(d => TileMiddle(d.Tile).DistanceSquaredTo(from)).ThenBy(d => d.Id).ToList();
    }

    /// <summary>A group's handles: just past each end along the line, and a bar beside its middle, on your side.</summary>
    private Vector2[] HandlesOf(List<Dot> group, float r)
    {
        Vector2 P(int i) => TileMiddle(group[i].Tile);
        int n = group.Count;
        Vector2 a = P(0), b = P(n - 1);
        var along = (b - a).Normalized();
        if (along == Vector2.Zero)
            along = Vector2.Right;
        var outA = (a - P(1)).Normalized();
        var outB = (b - P(n - 2)).Normalized();
        if (outA == Vector2.Zero)
            outA = -along;
        if (outB == Vector2.Zero)
            outB = along;
        var middle = n % 2 == 0 ? (P(n / 2 - 1) + P(n / 2)) / 2 : P(n / 2);
        var side = new Vector2(-along.Y, along.X);
        if (!Mine(middle + side * r * 2.5f) && Mine(middle - side * r * 2.5f))
            side = -side; // the bar goes on your side of the line
        return new[] { a + outA * r * 2.2f, b + outB * r * 2.2f, middle + side * r * 2.2f };
    }

    private bool Mine(Vector2 at) => _view.TileAt(at) is var t and >= 0 && _game.Owner[t] == _me.Id;

    /// <summary>Where a group would spread from and to if the dragged handle were let go here.</summary>
    private (Vector2 From, Vector2 To) HandleEnds(Vector2 mouse)
    {
        var shift = mouse - _gestureStart;
        Vector2 a = TileMiddle(_group![0].Tile), b = TileMiddle(_group[^1].Tile);
        return _handle switch
        {
            0 => (a + shift, b),
            1 => (a, b + shift),
            _ => (a + shift, b + shift),
        };
    }

    /// <summary>True if the mouse is still about the group (near one of its dots or handles), so its handles stay up.</summary>
    private bool NearGroup(Vector2 at, float r) =>
        _group!.Any(d => TileMiddle(d.Tile).DistanceTo(at) < r * 3) || (_handles?.Any(h => h.DistanceTo(at) < r * 2) ?? false);

    private bool GroupStillStands(List<Dot> group) => group.TrueForAll(d => _game.DotById(d.Id) == d && !d.IsWalking);

    private string TipFor(Dot dot)
    {
        string level = dot.Strength < DotRules.MaxStrength
            ? $"Level {dot.Strength} · click: level {dot.Strength + 1} for {GameHud.FormatCount(Game.DotUpgradeCost(dot))}"
            : $"Level {dot.Strength} (the top)";
        return $"{level}\nhold right-click: sell for {GameHud.FormatCount(Game.DotRefund(dot))}";
    }

    /// <summary>Each frame: what the dot controls show (selection, previews, handles, the sell ring, the buy label), and selling once held long enough.</summary>
    private void UpdateDotOrders(double delta)
    {
        var view = _overlay.DotOrders;
        view.Ghosts.Clear();
        view.Lines.Clear();
        view.GhostsOk = true;
        view.Box = null;
        view.Handles = null;
        view.HotHandle = -1;
        view.SellingDot = -1;
        view.Label = null;
        view.Tip = null;
        view.TipDot = -1;
        _selected.RemoveWhere(id => _game.DotById(id) is not { } d || d.Owner != _me);
        view.Selected.Clear();
        view.Selected.UnionWith(_selected);
        if (!DotOrdersAllowed())
        {
            EndGesture();
            _group = null;
            _handles = null;
            if (_buying && (_game.Phase != GamePhase.Running || !_me.IsAlive))
                StopBuying();
            return;
        }
        var mouse = DotMouse();
        float zoom = Math.Max(_camera.Zoom.X, 0.01f), r = _overlay.DotRadius(zoom);
        if (_group is not null && !GroupStillStands(_group))
        {
            _group = null;
            _handles = null;
            if (_gesture == DotGesture.Handle)
                EndGesture();
        }

        switch (_gesture)
        {
            case DotGesture.Sell:
                if (_game.DotById(_gestureDot) is not { } selling)
                {
                    EndGesture();
                    break;
                }
                double held = (Time.GetTicksMsec() - _sellStarted) / 1000.0; // real time: a slow frame doesn't sell early
                if (held >= SellHoldSeconds)
                {
                    _hud.Flash($"Dot sold: +{GameHud.FormatCount(Game.DotRefund(selling))} Funds");
                    _game.Submit(new SellDotIntent(_me.Id, selling.Id));
                    _gesture = DotGesture.Sold;
                }
                else
                {
                    view.SellingDot = selling.Id;
                    view.SellDone = (float)(held / SellHoldSeconds);
                }
                break;
            case DotGesture.Drag:
                int target = DropTile(mouse);
                view.GhostsOk = target >= 0;
                foreach (int id in MovingIds())
                    if (_game.DotById(id) is { } moving)
                        view.Lines.Add((_overlay.DotDrawnAt(moving), mouse));
                view.Ghosts.Add(target >= 0 ? TileMiddle(target) : mouse);
                break;
            case DotGesture.Box:
                view.Box = new Rect2(_gestureStart, Vector2.Zero).Expand(mouse);
                break;
            case DotGesture.Handle when _group is not null && _handles is not null:
                // Exactly where the sim would put them (worked out again only when the ends move to other tiles).
                var (from, to) = HandleEnds(mouse);
                var key = (_view.TileAt(from), _view.TileAt(to));
                if (key != _spreadPreviewKey)
                {
                    _spreadPreviewKey = key;
                    _spreadPreview.Clear();
                    if (_game.SpreadPlan(_me, _group.Select(d => d.Id).ToArray(), key.Item1, key.Item2, out _) is { } plan)
                        _spreadPreview.AddRange(plan.Select(p => (_overlay.DotDrawnAt(p.Dot), TileMiddle(p.Spot))));
                }
                view.GhostsOk = _spreadPreview.Count > 0;
                foreach (var (dotAt, spot) in _spreadPreview)
                {
                    view.Ghosts.Add(spot);
                    view.Lines.Add((dotAt, spot));
                }
                var shift = mouse - _gestureStart;
                view.Handles = _handles.Select((h, i) => i == _handle || _handle == 2 ? h + shift : h).ToArray();
                view.HotHandle = _handle;
                break;
            case DotGesture.None:
                // The group under the mouse shows its handles; they stay while the mouse is about it.
                var hovered = OverHud() || _buying ? null : MyDotAt(mouse);
                if (hovered is { IsWalking: false })
                {
                    var group = _game.DotGroup(hovered);
                    _group = group.Count >= 2 ? EndToEnd(group) : null;
                }
                else if (_group is not null && (_buying || OverHud() || !NearGroup(mouse, r)))
                    _group = null;
                else if (_group is not null)
                    _group = _game.DotGroup(_group[0]) is { Count: >= 2 } fresh ? EndToEnd(fresh) : null;
                _handles = _group is null ? null : HandlesOf(_group, r);
                view.Handles = _handles;
                view.HotHandle = HandleAt(mouse);
                // Hovering one of your dots a moment: what a click and a held right click do.
                if (hovered is not null && view.HotHandle < 0)
                {
                    if (hovered.Id != _tipDot)
                        (_tipDot, _tipHeld) = (hovered.Id, 0);
                    _tipHeld += delta;
                    if (_tipHeld >= TipDelaySeconds)
                        (view.Tip, view.TipDot) = (TipFor(hovered), hovered.Id);
                }
                else
                    _tipDot = -1;
                break;
        }

        if (_buying)
            ShowBuying(view, mouse);
    }

    /// <summary>Buy mode: how many and what it costs by the mouse, and where they'd stand if bought at the border under it.</summary>
    private void ShowBuying(DotOrdersView view, Vector2 mouse)
    {
        int tile = _view.TileAt(mouse);
        int border = tile < 0 ? -1 : _game.BorderTileWithin(_me, tile, SnapTiles());
        int can = _game.DotsAffordable(_me, _buyCount);
        view.LabelAt = mouse;
        if (can == 0)
        {
            view.Label = CantBuyReason() + "\nright-click: done";
            return;
        }
        view.Label = $"Buy {can} dot{(can == 1 ? "" : "s")} · {GameHud.FormatCount(can * DotRules.Price)}"
            + (can < _buyCount ? $" ({_buyCount} wanted)" : "")
            + (border < 0 ? "\npoint at your border" : "\nclick to buy") + " · wheel: how many";
        if (border < 0)
            return;
        var key = (border, _buyCount, _me.DotCount, _game.Tick / 10);
        if (key != _buyPreviewKey)
        {
            _buyPreviewKey = key;
            _buyPreview.Clear();
            foreach (int spot in _game.BuySpotsPreview(_me, border, _buyCount))
                _buyPreview.Add(TileMiddle(spot));
        }
        view.Ghosts.AddRange(_buyPreview);
    }
}
