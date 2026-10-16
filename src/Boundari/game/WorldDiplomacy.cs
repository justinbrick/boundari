using System;
using System.Collections.Generic;
using System.Linq;
using Boundari.Sim;
using Boundari.View;
using Godot;

namespace Boundari;

// The radial menu's game side (alliances), on-map alliance requests, and the dev start
// prompt (start of game, or fast-forward to mid-game).
public partial class World
{
    /// <summary>The radial menu's alliance wedge (its holder node in RadialMenu.tscn).</summary>
    private const string AllianceWedge = "BR_Rect";

    /// <summary>The boat wedge (bottom left): the same as pressing B on the clicked land.</summary>
    private const string BoatWedge = "BL_Rect";

    /// <summary>The target wedge (top left). With an ally it becomes "break alliance".</summary>
    private const string TargetWedge = "TL_Rect";

    private static readonly Color BreakAllianceColour = new(1.6f, 0.45f, 0.45f);

    // ------------------------------------------------------------------
    //  Radial menu
    // ------------------------------------------------------------------

    /// <summary>
    /// A right click: main_control.gd opens the radial menu; once it exists (end of this
    /// frame), point it at whoever owns the land under the mouse.
    /// </summary>
    private void OnRightClick(Vector2 worldPosition)
    {
        int tile = _view.TileAt(worldPosition);
        ushort target = tile >= 0 && _map.IsLand(tile) ? _game.Owner[tile] : Game.Nobody;
        CallDeferred(MethodName.SetUpRadialMenu, (int)target, tile);
    }

    private void SetUpRadialMenu(int target, int tile)
    {
        if (_mainControl?.Get("current_menu").AsGodotObject() is not Control menu || !IsInstanceValid(menu))
            return;
        if (menu.GetNodeOrNull("Control") is not { } wedges)
            return;
        SyncSim();
        var them = _game.GetPlayer((ushort)target);
        if (them == _me)
            them = null;
        bool allied = them is not null && _me.IsAlliedWith(them);
        var alliance = allied ? _game.AllianceBetween(_me, them!) : null;

        // Alliance wedge, like OpenFront's: ask or accept; with an ally it's the renew
        // button, usable only in the alliance's last 30 s (dimmed before, or once we've asked).
        bool canUse = them is not null && (allied ? _game.CanRenewAlliance(_me, them) : _game.CanRequestAlliance(_me, them));
        wedges.Call("set_wedge_state", AllianceWedge, canUse, Colors.White, "");
        // The moving outline: they've asked us to ally, or to renew.
        bool theyAsked = them is not null && (allied
            ? alliance!.AgreedToRenew(them) && canUse
            : _game.PendingRequest(them, _me) is not null);
        wedges.Call("set_alliance_pending", theyAsked);

        // Target wedge: "break alliance" with an ally (placeholder text for now).
        if (allied)
            wedges.Call("set_wedge_state", TargetWedge, true, BreakAllianceColour, "Break\nalliance");
        wedges.Connect("wedge_pressed", Callable.From<string>(wedge => OnWedgePressed(wedge, (ushort)target, tile)));
    }

    private void OnWedgePressed(string wedge, ushort target, int tile)
    {
        if (_args.RightClick is not null)
            GD.Print($"right-click: wedge {wedge} pressed on #{target}");
        if (wedge == AllianceWedge)
        {
            SyncSim();
            // Asks them, accepts if they asked us, or (with an ally) asks to renew / agrees to renew.
            if (_game.GetPlayer(target) is { } them && them != _me && _me.IsAlive)
                _game.Submit(new AllianceIntent(_me.Id, them.Id));
        }
        else if (wedge == TargetWedge)
        {
            SyncSim();
            if (_game.GetPlayer(target) is { } them && _me.IsAlliedWith(them))
                _game.Submit(new BreakAllianceIntent(_me.Id, them.Id));
            else
                return; // marking a target isn't built yet
        }
        else if (wedge == BoatWedge)
        {
            SyncSim();
            SendBoat(tile);
        }
        else
            return; // the other wedges don't do anything yet
        _mainControl?.Call("hide_radial_menu");
    }

    /// <summary>Testing aid (--right-click=requester): centres on whoever asked us to ally and right-clicks their land.</summary>
    private async System.Threading.Tasks.Task RightClickRequester()
    {
        AllianceRequest? request = null;
        Vector2 at = default;
        for (int i = 0; i < 120 && (request is null || !_labels.TryGetAnchor(request.From.Id, out at)); i++) // let label placement land
        {
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            SyncSim();
            request = _game.AllianceRequests.FirstOrDefault(r => r.To == _me);
        }
        if (request is null || !_labels.TryGetAnchor(request.From.Id, out at))
        {
            GD.Print(request is null ? "right-click: nobody has asked us to ally" : "right-click: their territory has no label spot yet");
            return;
        }
        _camera.Position = at;
        for (int i = 0; i < 2; i++)
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var screen = GetCanvasTransform() * at;
        GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true, Position = screen, GlobalPosition = screen }, true);
        GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false, Position = screen, GlobalPosition = screen }, true);
        GD.Print($"right-click: {request.From.Name} at {screen}");
        if (_args.RightClick is not ("requester-accept" or "requester-ally-menu"))
            return;
        // Then click the alliance wedge (bottom right of the menu) and see the alliance form.
        for (int i = 0; i < 3; i++)
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var wedge = screen + new Vector2(77, 77); // the alliance wedge's middle at the menu's 0.8 scale
        GetViewport().PushInput(new InputEventMouseMotion { Position = wedge, GlobalPosition = wedge }, true);
        GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = wedge, GlobalPosition = wedge }, true);
        GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = wedge, GlobalPosition = wedge }, true);
        ulong clicked = Time.GetTicksMsec();
        while (Time.GetTicksMsec() - clicked < 500) // a few game ticks
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        SyncSim();
        GD.Print($"right-click: allied with {request.From.Name} now: {_me.IsAlliedWith(request.From)}; menu open: {IsInstanceValid(_mainControl?.Get("current_menu").AsGodotObject())}");
        if (_args.RightClick != "requester-ally-menu")
            return;
        // Then open the menu on the new ally (break alliance, dimmed renew).
        GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true, Position = screen, GlobalPosition = screen }, true);
        GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false, Position = screen, GlobalPosition = screen }, true);
        for (int i = 0; i < 4; i++)
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
    }

    /// <summary>
    /// Dev button (Y): a random player whose territory is on screen (or anywhere, if none
    /// is) asks us to ally, so requests can be tested without waiting for one.
    /// </summary>
    private void ForceAllianceRequest()
    {
        SyncSim();
        if (_game.Phase != GamePhase.Running || !_me.HasSpawned || !_me.IsAlive)
        {
            GD.Print("ally request: only once the match is running and you've spawned");
            return;
        }
        var view = GetCanvasTransform().AffineInverse() * GetViewportRect();
        var candidates = _game.Players
            .Where(p => p != _me && p.IsAlive && p.HasSpawned && !_me.IsAlliedWith(p) && _game.CanRequestAlliance(p, _me))
            .ToList();
        var onScreen = candidates.Where(p => _labels.TryGetAnchor(p.Id, out var at) && view.HasPoint(at)).ToList();
        var pool = onScreen.Count > 0 ? onScreen : candidates;
        if (pool.Count == 0)
            return;
        var asker = pool[Random.Shared.Next(pool.Count)];
        _game.Submit(new AllianceIntent(asker.Id, _me.Id));
        GD.Print($"ally request: {asker.Name} asks you to ally");
    }

    /// <summary>Requests waiting for us, for the on-map arcs.</summary>
    private List<AllianceArcs.Request> IncomingAllianceRequests()
    {
        var list = new List<AllianceArcs.Request>();
        foreach (var r in _game.AllianceRequests)
            if (r.To == _me)
                list.Add(new AllianceArcs.Request(r.From.Id, 1 - (float)(_game.Tick - r.CreatedTick) / AllianceRules.RequestTicks));
        return list;
    }

    /// <summary>Testing aid (--ally-requests=N): the N nations nearest to us ask us to ally.</summary>
    private void AskForAlliances(int count)
    {
        if (!_me.HasSpawned || _me.Border.Count == 0)
            return;
        int home = _me.Border.Min();
        _showcaseFocus ??= new Vector2((home % _map.Width + 0.5f) / _map.Width, (home / _map.Width + 0.5f) / _map.Height);
        long Dist(Player p)
        {
            if (p.Border.Count == 0)
                return long.MaxValue;
            int t = p.Border.Min();
            long dx = t % _map.Width - home % _map.Width, dy = t / _map.Width - home / _map.Width;
            return dx * dx + dy * dy;
        }
        foreach (var nation in _game.Players.Where(p => p.IsNation && p.IsAlive && p.HasSpawned).OrderBy(Dist).Take(count))
            _game.Submit(new AllianceIntent(nation.Id, _me.Id));
        _game.Step();
    }

    // ------------------------------------------------------------------
    //  Dev start: now, or mid-game
    // ------------------------------------------------------------------

    private CanvasLayer MakeStartPrompt()
    {
        var layer = new CanvasLayer { Layer = 95, Name = "DevStartPrompt", Visible = false };
        AddChild(layer);
        var centre = new CenterContainer { AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Ignore };
        layer.AddChild(centre);
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.07f, 0.06f, 0.9f),
            ContentMarginLeft = 22, ContentMarginRight = 22, ContentMarginTop = 16, ContentMarginBottom = 18,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6, CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
        });
        centre.AddChild(panel);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 12);
        panel.AddChild(column);
        var title = new Label { Text = "Dev start", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 22);
        column.AddChild(title);
        column.AddChild(new Label
        {
            Text = "Start with everyone else, or let the match play out for\n10 minutes first and then drop in anywhere on land.",
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        buttons.AddThemeConstantOverride("separation", 14);
        column.AddChild(buttons);
        var now = new Button { Text = "Start of game", CustomMinimumSize = new Vector2(170, 38) };
        var mid = new Button { Text = "Mid-game (10 min in)", CustomMinimumSize = new Vector2(170, 38) };
        now.Pressed += () => layer.Visible = false;
        mid.Pressed += () =>
        {
            layer.Visible = false;
            BeginMidGame();
        };
        buttons.AddChild(now);
        buttons.AddChild(mid);
        return layer;
    }

    /// <summary>Plays the match forward (nations and tribes only) to ten minutes in, then lets us drop in.</summary>
    private void BeginMidGame()
    {
        SyncSim();
        _fastForwardTotal = Math.Max(0, _game.SpawnPhaseTicks - _game.Tick) + MidGameTicks;
        _fastForwardLeft = _fastForwardTotal;
        _fastForwarding = true;
    }

    /// <summary>Progress while fast-forwarding; when it's done, we may spawn anywhere.</summary>
    private void UpdateFastForward()
    {
        if (!_fastForwarding)
            return;
        if (_fastForwardLeft > 0 || SimBusy)
        {
            int done = _fastForwardTotal - _fastForwardLeft;
            _hud.Status = $"Fast-forwarding to mid-game…  {done * 100 / Math.Max(1, _fastForwardTotal)}%";
            return;
        }
        _fastForwarding = false;
        _hud.Status = null;
        _game.AllowLateSpawn(_me);
    }
}
