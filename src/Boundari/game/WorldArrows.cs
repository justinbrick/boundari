using System;
using System.Collections.Generic;
using System.Linq;
using Boundari.Sim;
using Godot;

namespace Boundari;

/// <summary>
/// Directed attacks (Boundari's own): Shift + left press, or holding the left button still
/// for <see cref="ArrowHoldSeconds"/>, starts an arrow; drag it out and let go to attack
/// that way. A short arrow is flat (wide, slow, strong), a long one thin (narrow, fast,
/// weak). The player it points at lights up while you drag; right click or Esc cancels.
/// A plain click still attacks as before, and moving the mouse before the hold time pans.
/// </summary>
public partial class World
{
    private const double ArrowHoldSeconds = 0.3;
    private const double HoldRingAfterSeconds = 0.06; // the filling ring shows from here (a quick click never flashes it)
    private const float MinArrowPixels = 12;          // shorter than this on release = a plain click
    private const float FlatArrowPixels = 40;         // up to this long: the flattest arrow
    private const float ThinArrowPixels = 360;        // from this long: the thinnest
    private const float ArrowMaxAngleCos = 0.64f;     // a neighbour more than ~50 degrees off the arrow isn't what it points at

    private double _pressTime;
    private Vector2 _pressWorld;
    private bool _pressPanned;    // the mouse moved before the hold time: this press pans the camera
    private bool _arrowActive;
    private Vector2 _arrowEnd;    // where the arrow points to (map units)
    private ushort? _arrowTarget; // who it points at (0 = empty land), or null for nobody we can attack
    private Vector2? _arrowTestEnd; // (screenshots) a fixed end instead of the mouse

    // Land next to ours that we could attack, worked out once a tick while an arrow is dragged.
    private readonly List<(int Tile, ushort Owner)> _arrowFrontier = new();
    private int _arrowFrontierTick = -1;

    private bool CanDrawArrow() =>
        _game.DirectedAttacks && _game.Phase == GamePhase.Running && _me.IsAlive && _me.HasSpawned
        && _placing is null && _aiming is null && !_startPrompt.Visible && !_fastForwarding;

    /// <summary>A left press on the map: Shift starts an arrow at once; otherwise the hold timer starts.</summary>
    private void OnLeftPress(InputEventMouseButton button)
    {
        _pressTime = Time.GetTicksMsec() / 1000.0;
        _pressWorld = GetGlobalMousePosition();
        _pressPanned = false;
        if (button.ShiftPressed && CanDrawArrow())
            StartArrow();
    }

    /// <summary>The left button came up: launches the arrow being drawn. False if there was none (or it was too short: a plain click).</summary>
    private bool OnLeftRelease()
    {
        if (!_arrowActive)
            return false;
        _arrowActive = false;
        _view.TargetId = -1;
        _overlay.ClearDragArrow();
        if ((_arrowEnd - _pressWorld).Length() * _camera.Zoom.X < MinArrowPixels)
            return false;
        LaunchArrow(_pressWorld, _arrowEnd);
        return true;
    }

    /// <summary>Right click or Esc while drawing: drops the arrow (the release then does nothing).</summary>
    private bool CancelArrow()
    {
        if (!_arrowActive)
            return false;
        _arrowActive = false;
        _pressPosition = null;
        _view.TargetId = -1;
        _overlay.ClearDragArrow();
        return true;
    }

    private void StartArrow()
    {
        _arrowActive = true;
        _arrowFrontierTick = -1;
        // The camera (camera_2d.gd) started a drag on the press: stop it, and undo any nudge.
        _camera.Set("dragging", false);
        var dragStart = _camera.Get("drag_start_camera");
        if (dragStart.VariantType == Variant.Type.Vector2)
            _camera.Position = dragStart.AsVector2();
    }

    /// <summary>Every frame: starts the arrow once the button's been held still long enough, and follows the mouse.</summary>
    private void UpdateArrow()
    {
        _overlay.SetHoldRing(null);
        if (!_arrowActive)
        {
            if (_pressPosition is not { } press || _pressPanned)
                return;
            if (_arrowTestEnd is null && !Input.IsMouseButtonPressed(MouseButton.Left))
                return; // let go where the map didn't hear it: no hold
            if (GetViewport().GetMousePosition().DistanceTo(press) > ClickMaxDrag)
            {
                _pressPanned = true;
                return;
            }
            double held = Time.GetTicksMsec() / 1000.0 - _pressTime;
            if (held < ArrowHoldSeconds || !CanDrawArrow())
            {
                if (held >= HoldRingAfterSeconds && CanDrawArrow()) // the ring fills up as the hold goes on
                    _overlay.SetHoldRing((GetGlobalMousePosition(), (float)((held - HoldRingAfterSeconds) / (ArrowHoldSeconds - HoldRingAfterSeconds))));
                return;
            }
            StartArrow();
        }
        if (!CanDrawArrow())
        {
            CancelArrow();
            return;
        }
        if (_arrowTestEnd is null && !Input.IsMouseButtonPressed(MouseButton.Left))
        {
            // Let go over the HUD (the map never heard the release): launch it all the same.
            _pressPosition = null;
            OnLeftRelease();
            return;
        }
        _arrowEnd = _arrowTestEnd ?? GetGlobalMousePosition();
        _arrowTarget = ArrowTarget(_pressWorld, _arrowEnd);
        _view.TargetId = _arrowTarget is { } t and not Game.Nobody ? t : -1;
        _overlay.SetDragArrow(_pressWorld, _arrowEnd, ArrowThinness(_pressWorld, _arrowEnd), _arrowTarget is not null);
    }

    /// <summary>0 for a short (flat) arrow .. 1 for a long (thin) one, by its length on screen.</summary>
    private float ArrowThinness(Vector2 from, Vector2 to) =>
        Math.Clamp(((to - from).Length() * _camera.Zoom.X - FlatArrowPixels) / (ThinArrowPixels - FlatArrowPixels), 0, 1);

    private void LaunchArrow(Vector2 from, Vector2 to)
    {
        if (ArrowTarget(from, to) is not { } target)
            return;
        int fromTile = ClampedTile(from), toTile = ClampedTile(to);
        if (fromTile == toTile)
            return;
        float thin = ArrowThinness(from, to);
        _game.Submit(new DirectedAttackIntent(_me.Id, target, _hud.AttackRatioPercent, fromTile, toTile, (int)Math.Round(thin * 1000)));
        _overlay.LaunchArrow(from, to, thin, fromTile, toTile);
    }

    /// <summary>The tile at a map position, pulled onto the map if it's off the edge.</summary>
    private int ClampedTile(Vector2 at) =>
        _map.Index(Math.Clamp((int)MathF.Floor(at.X), 0, _map.Width - 1), Math.Clamp((int)MathF.Floor(at.Y), 0, _map.Height - 1));

    /// <summary>
    /// Who an arrow points at: whoever owns the land under its tip if we border them;
    /// otherwise the neighbour (or empty land) whose land next to ours lies most nearly the
    /// way the arrow points from where it starts. Null when it points at nothing we can attack.
    /// </summary>
    private ushort? ArrowTarget(Vector2 from, Vector2 to)
    {
        SyncSim();
        RefreshArrowFrontier();
        if (_arrowFrontier.Count == 0)
            return null;
        var dir = to - from;
        if (dir.LengthSquared() < 1e-6f)
            return null;
        dir = dir.Normalized();

        int tip = _view.TileAt(to);
        if (tip >= 0 && _map.IsLand(tip) && _game.Owner[tip] is var tipOwner && tipOwner != _me.Id
            && _arrowFrontier.Exists(f => f.Owner == tipOwner))
            return tipOwner;

        float reach = _game.Rules.D(1000);
        float best = float.MaxValue;
        ushort? target = null;
        foreach (var (tile, owner) in _arrowFrontier)
        {
            var v = new Vector2(tile % _map.Width + 0.5f, tile / _map.Width + 0.5f) - from;
            float dist = v.Length();
            float cos = dist < 1 ? 1 : v.Dot(dir) / dist;
            if (cos < ArrowMaxAngleCos)
                continue;
            float score = 1 - cos + dist / reach; // mostly the angle; nearer breaks near-ties
            if (score < best)
            {
                best = score;
                target = owner;
            }
        }
        return target;
    }

    /// <summary>Land next to ours we could attack (not allies'), with its owner; redone once a tick.</summary>
    private void RefreshArrowFrontier()
    {
        if (_arrowFrontierTick == _game.Tick)
            return;
        _arrowFrontierTick = _game.Tick;
        _arrowFrontier.Clear();
        var seen = new HashSet<int>();
        int w = _map.Width, h = _map.Height;
        foreach (int t in _me.Border)
        {
            int x = t % w, y = t / w;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                    continue;
                int n = ny * w + nx;
                ushort owner = _game.Owner[n];
                if (owner == _me.Id || !_map.IsLand(n) || _me.Allies.Contains(owner) || !seen.Add(n))
                    continue;
                _arrowFrontier.Add((n, owner));
            }
        }
    }

    /// <summary>
    /// --arrow=drag|launch[:TICKS] (screenshots): an arrow from our border towards the
    /// biggest neighbouring nation's middle (or tribe's, if there's no nation), --arrow-px long on screen; "drag" shows it being dragged
    /// (target lit up), "launch" lets it go and plays TICKS ticks (default 30).
    /// </summary>
    private void ArrowForScreenshot(string mode, float pixels)
    {
        var them = _game.BorderingPlayers(_me).OrderBy(p => p.IsBot).ThenByDescending(p => p.TileCount).FirstOrDefault();
        if (them is null)
        {
            GD.Print("arrow: no neighbour");
            return;
        }
        int w = _map.Width;
        long sx = 0, sy = 0, n = 0;
        for (int t = 0; t < _map.TileCount; t++)
            if (_game.Owner[t] == them.Id)
            {
                sx += t % w;
                sy += t / w;
                n++;
            }
        var middle = new Vector2(sx / (float)n, sy / (float)n);
        bool Touches(int t) =>
            (t % w + 1 < w && _game.Owner[t + 1] == them.Id) || (t % w > 0 && _game.Owner[t - 1] == them.Id)
            || (t + w < _map.TileCount && _game.Owner[t + w] == them.Id) || (t >= w && _game.Owner[t - w] == them.Id);
        var touching = _me.Border.Where(Touches).OrderBy(t => t).ToList();
        if (touching.Count == 0)
            return;
        int start = touching.MinBy(t => new Vector2(t % w, t / w).DistanceSquaredTo(middle));
        var border = new Vector2(start % w + 0.5f, start / w + 0.5f);
        var dir = (middle - border).Normalized();
        // The camera is framed after this (see FrameCamera): work out its zoom, and centre it on the arrow.
        var screen = GetViewportRect().Size;
        float zoom = Mathf.Min(screen.X / _map.Width, screen.Y / _map.Height) * 0.95f * _args.Zoom;
        float length = pixels / zoom;
        var from = border - dir * length * 0.35f;
        var to = from + dir * length;
        _showcaseFocus = from.Lerp(to, 0.5f) / new Vector2(_map.Width, _map.Height);
        string[] parts = mode.Split(':');
        if (parts[0] == "launch")
        {
            LaunchArrow(from, to);
            int ticks = parts.Length > 1 ? int.Parse(parts[1]) : 30;
            for (int t = 0; t < ticks; t++)
            {
                _game.Step();
                if (t == 0)
                    GD.Print($"arrow: after a tick, {_game.Attacks.Count(a => a.Arrow is not null)} directed attacks ({_game.Attacks.Where(a => a.Arrow is not null).Sum(a => a.Troops)} troops)");
            }
            GD.Print($"arrow: launched at {them.Name}, {_game.Attacks.Count(a => a.Arrow is not null)} directed attacks running");
        }
        else
        {
            _pressWorld = from;
            _arrowTestEnd = to;
            _arrowActive = true;
            GD.Print($"arrow: dragging towards {them.Name}");
        }
    }
}
