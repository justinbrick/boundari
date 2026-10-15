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
        CallDeferred(MethodName.SetUpRadialMenu, (int)target);
    }

    private void SetUpRadialMenu(int target)
    {
        if (_mainControl?.Get("current_menu").AsGodotObject() is not Control menu || !IsInstanceValid(menu))
            return;
        if (menu.GetNodeOrNull("Control") is not { } wedges)
            return;
        SyncSim();
        var them = _game.GetPlayer((ushort)target);
        bool askedUs = them is not null && them != _me && _game.PendingRequest(them, _me) is not null;
        wedges.Call("set_alliance_pending", askedUs);
        wedges.Connect("wedge_pressed", Callable.From<string>(wedge => OnWedgePressed(wedge, (ushort)target)));
    }

    private void OnWedgePressed(string wedge, ushort target)
    {
        if (_args.RightClick is not null)
            GD.Print($"right-click: wedge {wedge} pressed on #{target}");
        if (wedge != AllianceWedge)
            return; // the other wedges don't do anything yet
        SyncSim();
        if (_game.GetPlayer(target) is { } them && them != _me && _me.IsAlive && !_me.IsAlliedWith(them))
            _game.Submit(new AllianceIntent(_me.Id, them.Id)); // asks them, or accepts if they asked us
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
        if (_args.RightClick != "requester-accept")
            return;
        // Then click the alliance wedge (bottom right of the menu) and see the alliance form.
        for (int i = 0; i < 3; i++)
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var wedge = screen + new Vector2(58, 58);
        GetViewport().PushInput(new InputEventMouseMotion { Position = wedge, GlobalPosition = wedge }, true);
        GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = wedge, GlobalPosition = wedge }, true);
        GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = wedge, GlobalPosition = wedge }, true);
        ulong clicked = Time.GetTicksMsec();
        while (Time.GetTicksMsec() - clicked < 500) // a few game ticks
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        SyncSim();
        GD.Print($"right-click: allied with {request.From.Name} now: {_me.IsAlliedWith(request.From)}; menu open: {IsInstanceValid(_mainControl?.Get("current_menu").AsGodotObject())}");
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
