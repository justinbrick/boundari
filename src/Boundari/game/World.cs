using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Boundari.Map;
using Boundari.Sim;
using Boundari.View;
using Godot;

namespace Boundari;

/// <summary>
/// Root of the game scene: loads the map, runs the match (<see cref="Game"/>), turns
/// clicks into orders, and keeps the map and HUD up to date.
///
/// Controls:
///   Left click (no drag) — during the spawn phase: pick/move your start.
///                          Afterwards: expand into empty land, or attack a neighbour,
///                          using the attack ratio slider's share of your troops. If the
///                          target can't be reached over land, troops go by boat.
///   B — send a boat to the land under the mouse
///   1-7 or hotbar buttons — place a port, city, factory, defense, silo, SAM, or a
///                          warship (on water near one of your ports)
///                          (left click to place, right click / Esc / same key to cancel)
///   M — switch map (Europe, World) and start a new match
///   R — new random map and match
///   H — toggle hill shading
///   8 / 9 / 0 or bomb buttons — aim a MOAB / Fat Man / Tsar Bomba from your nearest loaded
///                          silo (left click to launch, Shift to keep aiming)
///
/// Command-line options (after "--" when launching Godot):
///   --map=ID           load res://maps/ID.bmap ("random" for a generated map)
///   --seed=N           generate a random map with this seed
///   --nations=N        number of nations, the proper AI opponents (default: the map's own
///                      nations, e.g. Europe's 52 like OpenFront; 120 random ones elsewhere)
///   --bots=N           number of bots ("tribes"), the weak filler players (default 400, like OpenFront)
///   --no-cheats        play without the dev cheats (infinite gold, instant building)
///   --classic          OpenFront's rules: no hatched (occupied) land
///   --difficulty=D     easy, medium (default), hard or impossible: how strong nations are
///   --autoplay         spawn the human player automatically (for testing)
///   --ticks=N          fast-forward the match N ticks at start
///   --no-hillshade     start with hill shading off
///   --zoom=Z           start zoomed in (1 = whole map)
///   --focus=X,Y        centre the camera on this point, as 0..1 fractions of the map
///   --screenshot=PATH  save a screenshot after loading, then quit
///   --perf             print the frame rate every 2 seconds
///   --dev=PSC=2,SMO=1  (testing) set dev-tool options by index for this run (not saved)
///   --bench=SECONDS    run flat out (no vsync), print where frame time goes every 2 s, then quit
///   --showcase         (testing) after --autoplay: build one of everything and launch a boat;
///                      --showcase=nukes[:N] also fires a MOAB, Fat Man and Tsar Bomba, then runs N ticks
///                      --showcase=boat frames where your boat is headed
///   --tags             (testing) every name shows every icon above it (alliance one part drained)
///   --hover=X,Y        pretend the mouse is over this map point (0..1 fractions), for screenshots;
///                      --hover=leader hovers the biggest player
/// </summary>
public partial class World : Node2D
{
    /// <summary>Map to load at start (res://maps/ID.bmap). Empty means a random map.</summary>
    [Export] public string StartMap { get; set; } = "europe";
    [Export] public ulong Seed { get; set; }
    [Export] public int MapWidth { get; set; } = 2000;
    [Export] public int MapHeight { get; set; } = 1250;
    /// <summary>Nations on maps without their own list (Europe has its 52).</summary>
    [Export] public int Nations { get; set; } = 120;

    /// <summary>Dev testing: you never run out of gold and your buildings finish instantly.</summary>
    [Export] public bool DevCheats { get; set; } = true;
    [Export] public int Bots { get; set; } = 400;
    [Export] public Difficulty Difficulty { get; set; } = Difficulty.Medium;

    private const float ClickMaxDrag = 6; // pixels; more than this is a camera drag, not a click

    private static readonly string[] BuiltInMaps = { "europe", "world" };

    // Hotbar buttons in boundari.tscn, in hotkey order (1-6).
    private static readonly (BuildingType Type, string Button)[] BuildButtons =
    {
        (BuildingType.Port, "Control2/Port_Button"), (BuildingType.City, "Control3/Barracks_Button"),
        (BuildingType.Factory, "Control4/Fac_Button"), (BuildingType.Defense, "Control5/Def_Button"),
        (BuildingType.Silo, "Control6/Silo_Button"), (BuildingType.Sam, "Control7/Sam_Button"),
        (BuildingType.Warship, "Control8/Ship_Button"),
    };

    // Bomb buttons (prices only for now): MOAB = OpenFront's atom bomb, Fat Man = hydrogen bomb,
    // Tsar Bomba = MIRV.
    private static readonly (NukeType Type, string Button)[] NukeButtons =
    {
        (NukeType.Moab, "Control9/Viper_Button"), (NukeType.FatMan, "Control10/Falcon_Button"),
        (NukeType.TsarBomba, "Control11/Raptor_Button"),
    };
    private readonly System.Collections.Generic.List<(NukeType Type, Button Button)> _nukeButtons = new();

    private MapOverlay _overlay = null!;
    private DevTools _devTools = null!;
    private CoordinateGrid _grid = null!;
    private AllianceArcs _arcs = null!;
    private Control? _mainControl;       // the HUD script (main_control.gd) that opens the radial menu
    private bool _showPlayers = true, _showLabels = true;

    // Dev start: the prompt asking "start of game or mid-game?", and fast-forwarding to mid-game.
    private const int MidGameTicks = 10 * 60 * Balance.TicksPerSecond; // ten minutes in
    private const int FastForwardBatch = 40;                           // ticks per frame while fast-forwarding
    private CanvasLayer _startPrompt = null!;
    private StartupSplash _splash = null!; // the map's name, faded in and out as the match starts
    private AlertFrame _alerts = null!;     // the screen border that flashes on betrayals and big attacks
    private int _fastForwardLeft, _fastForwardTotal;
    private bool _fastForwarding;
    private BuildingType? _placing;
    private NukeType? _aiming;
    private readonly System.Collections.Generic.List<(BuildingType Type, Button Button)> _buildButtons = new();
    private Color[] _palette = Array.Empty<Color>();
    private ColorTheme _colorTheme;
    private Vector2? _showcaseFocus;
    private int _showcaseRailFocus = -1;

    private MapView _view = null!;
    private Camera2D _camera = null!;
    private GameHud _hud = null!;
    private PlayerPanels _panels = null!;
    private PlayerBar _bar = null!;
    private BuildBar _buildBar = null!;
    private NotificationBar _notifications = null!;
    private OutgoingAttacks _outgoing = null!;
    private TerritoryLabels _labels = null!;
    private GameMap _map = null!;
    private string _mapId = "";
    private Args _args;

    private Game _game = null!;
    private Player _me = null!;
    private double _tickTime;
    private Vector2? _pressPosition;
    private double _perfTimer;
    private Label _fps = null!;
    private double _fpsTimer, _stepMs, _worstStepMs;
    private int _steps;

    public override void _Ready()
    {
        RenderingServer.FramePostDraw += OnFramePostDraw;
        _view = GetNode<MapView>("MapDisplay");
        _camera = GetNode<Camera2D>("Camera2D");
        _hud = new GameHud(this);
        _panels = new PlayerPanels(this) { Intel = _intel };
        _bar = new PlayerBar(this);
        _buildBar = new BuildBar(this);
        _notifications = new NotificationBar(this);
        _outgoing = new OutgoingAttacks(this, intent =>
        {
            SyncSim();
            _game.Submit(intent);
        });
        _grid = new CoordinateGrid { Name = "CoordinateGrid", Visible = false };
        AddChild(_grid);
        MoveChild(_grid, _view.GetIndex() + 1); // on the map, under buildings and labels
        _overlay = new MapOverlay { Name = "MapOverlay" };
        AddChild(_overlay);
        MoveChild(_overlay, _grid.GetIndex() + 1); // just above the map, below the HUD
        _fps = MakeFpsLabel();
        _labels = new TerritoryLabels { Name = "TerritoryLabels", Intel = _intel };
        AddChild(_labels);
        MoveChild(_labels, _overlay.GetIndex() + 1);
        _overlay.WaitForSim = SyncSim;
        _labels.WaitForSim = SyncSim;
        _arcs = new AllianceArcs { Name = "AllianceArcs", Labels = _labels };
        AddChild(_arcs);
        MoveChild(_arcs, _labels.GetIndex() + 1);
        _mainControl = GetNodeOrNull<Control>("Main_Layer/Main_Control");
        _startPrompt = MakeStartPrompt();
        _splash = new StartupSplash();
        AddChild(_splash);
        _splash.Build(this);
        _alerts = AlertFrame.Attach(this);
        _devTools = new DevTools
        {
            View = _view,
            ShowPlayers = show =>
            {
                _showPlayers = show;
                _view.ShowTerritory = show;
                _overlay.Visible = show;
                _arcs.Visible = show;
                _labels.Visible = show && _showLabels;
            },
            ShowLabels = show =>
            {
                _showLabels = show;
                _labels.Visible = show && _showPlayers;
            },
            ShowGrid = show => _grid.Visible = show,
            TextOutlineWidth = width => _labels.OutlineScale = width,
            SetColorTheme = theme =>
            {
                _colorTheme = theme;
                if (_game is not null)
                    ApplyPalette();
            },
            ForceAllianceRequest = ForceAllianceRequest,
        };
        AddChild(_devTools);

        foreach (var (nuke, path) in NukeButtons)
            if (GetNodeOrNull<Button>("Main_Layer/Main_Control/ButtonsRow/" + path) is { } button)
            {
                var n = nuke;
                button.Pressed += () => ToggleAiming(n);
                _nukeButtons.Add((nuke, button));
            }
        foreach (var (type, path) in BuildButtons)
            if (GetNodeOrNull<Button>("Main_Layer/Main_Control/ButtonsRow/" + path) is { } button)
            {
                var t = type;
                button.Pressed += () => TogglePlacing(t);
                _buildButtons.Add((type, button));
            }

        _args = ParseArgs();
        if (_args.Dev is { } dev)
            _devTools.Override(dev);
        string mapId = _args.Seed is not null ? "random" : _args.Map ?? StartMap;
        if (mapId is "" or "random" || !LoadMap(mapId))
            Generate(_args.Seed ?? (Seed != 0 ? Seed : RandomSeed()));
        FrameCamera(_args.Zoom, _args.Focus ?? _showcaseFocus);

        if (_args.Screenshot is not null)
            SaveScreenshotAndQuit(_args.Screenshot);
    }

    // ------------------------------------------------------------------
    //  The simulation runs on its own thread
    // ------------------------------------------------------------------
    //
    // Each tick runs on a worker thread, started just after a frame has been drawn. While it
    // runs, the screen keeps going: the map, camera and territory animation stay smooth, and
    // the overlay and labels keep showing their last drawing until the tick is done (usually
    // within a frame). Nothing on this thread touches the game while the worker has it:
    // input that needs the game waits for the tick to finish first (SyncSim).

    private System.Threading.Tasks.Task? _simTask;
    private int _simTick;          // the tick the worker is running
    private long _simElapsed;      // how long it took (Stopwatch ticks)
    private int _shownTick;        // the game's tick as of the last finished step

    private const int MaxSimWaitMs = 4;

    /// <summary>True while a tick is running on the worker thread.</summary>
    private bool SimBusy => _simTask is { IsCompleted: false };

    /// <summary>Waits for a running tick to finish and takes in its results (territory changes, timings).</summary>
    private void SyncSim()
    {
        if (_simTask is null)
            return;
        try
        {
            _simTask.Wait();
        }
        catch (AggregateException e) when (e.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
        }
        finally
        {
            _simTask = null;
        }
        double stepMs = _simElapsed * 1000.0 / Stopwatch.Frequency;
        _stepMs += stepMs;
        _worstStepMs = Math.Max(_worstStepMs, stepMs);
        _steps++;
        long recordStart = FrameProfiler.Start();
        _view.RecordTick(_game.Owner, _game.Fallout, _game.HatchedUntil, _game.ChangedTiles, _simTick);
        _game.ChangedTiles.Clear();
        ShowDefensePosts();
        ShowGoldGains();
        _alerts.TakeEvents(_game, _me);
        _notifications.TakeEvents(_game, _me);
        FrameProfiler.Stop("territory record", recordStart);
        _shownTick = _game.Tick;
    }

    private readonly System.Collections.Generic.List<(int Tile, int Owner)> _defensePosts = new();

    /// <summary>Hands the map every finished defense post, for the checkerboard on the borders they defend.</summary>
    private void ShowDefensePosts()
    {
        _defensePosts.Clear();
        foreach (var p in _game.Players)
            foreach (int tile in p.DefensePosts)
                _defensePosts.Add((tile, p.Id));
        _view.SetDefensePosts(_defensePosts, _game.Rules.DefensePostRange, _map.Width);
    }

    /// <summary>After each frame is drawn: start the next tick if one is due.</summary>
    private void OnFramePostDraw()
    {
        if (_game is null || _simTask is not null || _startPrompt.Visible)
            return; // (paused while the dev start prompt is up)
        int steps = 1;
        if (_fastForwardLeft > 0)
        {
            steps = Math.Min(_fastForwardLeft, FastForwardBatch);
            _fastForwardLeft -= steps;
            _tickTime = 0;
        }
        else
        {
            double tickLength = 1.0 / Balance.TicksPerSecond;
            if (_tickTime < tickLength)
                return;
            _tickTime -= tickLength;
        }
        var game = _game;
        _simTick = game.Tick;
        _simTask = System.Threading.Tasks.Task.Run(() =>
        {
            long started = Stopwatch.GetTimestamp();
            for (int i = 0; i < steps; i++)
                game.Step();
            _simElapsed = Stopwatch.GetTimestamp() - started;
        });
    }

    public override void _ExitTree()
    {
        RenderingServer.FramePostDraw -= OnFramePostDraw;
        if (_simTask is not null)
            SyncSim();
    }

    public override void _Process(double delta)
    {
        // Fixed-rate simulation (10 ticks a second), stepped on a worker thread between frames.
        double tickLength = 1.0 / Balance.TicksPerSecond;
        // (P pauses; comma/period change the game speed.)
        _tickTime = Math.Min(_tickTime + delta * (_paused ? 0 : Speeds[_speed]), 4 * tickLength); // never fall far behind
        // A tick usually finishes within a few milliseconds: wait that long so the overlay
        // stays in step; only a slow tick leaves the overlay showing the last frame.
        if (_simTask is not null && (_simTask.IsCompleted || _simTask.Wait(MaxSimWaitMs)))
            SyncSim();
        bool busy = SimBusy;
        _overlay.Frozen = busy;
        _labels.Frozen = busy;
        float fraction = (float)Math.Min(_tickTime / tickLength, 1);

        UpdateFps(delta);
        if (_args.Perf && (_perfTimer += delta) >= 2)
        {
            _perfTimer = 0;
            GD.Print($"perf: {Engine.GetFramesPerSecond()} fps, tick {_game.Tick}, {_game.Attacks.Count} attacks");
        }

        long flushStart = FrameProfiler.Start();
        _view.FlushTerritory();
        FrameProfiler.Stop("territory upload", flushStart);
        _view.SetVisualTime(_shownTick - 1 + fraction);
        _overlay.SetTickFraction(fraction);
        FrameProfiler.EndFrame();
        if (_args.Bench is { } bench)
            RunBench(delta, bench);
        _notifications.Update(delta);
        if (busy)
            return; // the worker has the game: the HUD and panels wait for the next frame

        long hudStart = FrameProfiler.Start();
        UpdatePlacing();
        UpdateArrow();
        FrameProfiler.Stop("  placing", hudStart);
        long hudOnly = FrameProfiler.Start();
        UpdateFastForward();
        _hud.Update(_game, _me);
        _bar.Update(_game, _me, delta);
        _buildBar.Update(_game, _me, delta);
        _buildBar.SetSelected(_placing, _aiming);
        _buildBar.BlockMouse(_hud.AdjustingRatio);
        // Flak rings: OpenFront shows them while placing these or aiming a bomb, and while
        // the Flak or bomb buttons are hovered.
        _overlay.ShowFlakRanges = _aiming is not null || _placing is BuildingType.Sam or BuildingType.Silo or BuildingType.City
            || _buildBar.HoveringFlakOrBomb;
        _outgoing.Update(_game, _me);
        _alerts.Update(_game, _me);
        _arcs.SetRequests(IncomingAllianceRequests());
        FrameProfiler.Stop("  hud", hudOnly);
        _intel.Update(delta); // (fog: before the panels and labels show anyone's troops)
        var hovered = HoveredPlayer(out bool wilderness);
        if (_args.Hover is null) // name plates stop fading under the mouse while the slider is held
            _labels.MouseOverride = _hud.AdjustingRatio ? new Vector2(float.MinValue, float.MinValue) : null;
        _view.HoverId = _arrowActive ? -1 : hovered?.Id ?? -1; // (the arrow lights up its own target)
        _labels.Hovered = hovered?.Id ?? 0;
        _panels.Update(_game, _me, hovered, wilderness, delta);
        // Fog: hovering a player further away shows when their troops refresh next.
        _labels.RefreshRing = hovered is not null && _intel.RefreshProgress(hovered, fraction) is { } refresh
            ? (hovered.Id, (float)refresh) : null;
        FrameProfiler.Stop("hud+panels", hudStart);
    }

    /// <summary>Gives every player their territory colour in the current theme (the same colours each time for this game).</summary>
    private void ApplyPalette()
    {
        SyncSim();
        _palette = PlayerColors.For(_game.Players, _game.Seed, _colorTheme);
        _view.SetPalette(_palette);
        _overlay.SetPalette(PlayerColors.Solid(_palette));
        _labels.SetPalette(_palette);
    }

    private double _benchTime, _benchReport, _benchWorstFrame;

    /// <summary>
    /// --bench=SECONDS: runs the real game flat out (no vsync) and prints where frame time
    /// goes every 2 seconds, plus the GPU's time per frame, then quits.
    /// </summary>
    private void RunBench(double delta, double seconds)
    {
        var viewport = GetViewport().GetViewportRid();
        if (_benchTime == 0)
        {
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            RenderingServer.ViewportSetMeasureRenderTime(viewport, true);
            FrameProfiler.Report(); // start clean
        }
        _benchTime += delta;
        _benchWorstFrame = Math.Max(_benchWorstFrame, delta);
        if ((_benchReport += delta) >= 2)
        {
            _benchReport = 0;
            double gpu = RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport);
            double renderCpu = RenderingServer.ViewportGetMeasuredRenderTimeCpu(viewport);
            GD.Print($"bench t={_benchTime:F0}s {Engine.GetFramesPerSecond()} fps, slowest frame {_benchWorstFrame * 1000:F1} ms, sim tick avg {_stepMs / Math.Max(1, _steps):F1} ms (worst {_worstStepMs:F1}), tick {_game.Tick}, gpu {gpu:F2} ms, render cpu {renderCpu:F2} ms | {FrameProfiler.Report()}");
            _benchWorstFrame = 0;
        }
        if (_benchTime >= seconds)
            GetTree().Quit();
    }

    public override void _Input(InputEvent e)
    {
        // Esc cancels placement before the options menu can grab it, and a right click
        // cancels it before the radial menu (main_control.gd) can open.
        if ((_placing is not null || _aiming is not null)
            && e is InputEventKey { Pressed: true, Keycode: Key.Escape } or InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right })
        {
            StopPlacing();
            GetViewport().SetInputAsHandled();
        }
        // ...and they drop a directed-attack arrow being drawn.
        if (_arrowActive && e is InputEventKey { Pressed: true, Keycode: Key.Escape } or InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right })
        {
            CancelArrow();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (_hud.AdjustingRatio && e is InputEventMouse)
            return; // dragging the attack ratio slider: the mouse is only for the slider
        SyncSim(); // input may read or change the game: let the running tick finish first
        if (e is InputEventMouseButton { Pressed: true, ShiftPressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel)
        {
            _hud.AdjustRatio(wheel.ButtonIndex == MouseButton.WheelUp ? AttackRatioStep : -AttackRatioStep); // Shift+wheel, like OpenFront
            return;
        }
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } rightClick)
            OnRightClick(GetCanvasTransform().AffineInverse() * rightClick.Position);
        if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
            return;
        if (button.Pressed)
        {
            _pressPosition = button.Position;
            _pressIsDouble = button.DoubleClick;
            OnLeftPress(button); // may start a directed-attack arrow (WorldArrows)
        }
        else if (_pressPosition is { } start)
        {
            _pressPosition = null;
            if (OnLeftRelease())
                return; // an arrow was let go
            if (start.DistanceTo(button.Position) <= ClickMaxDrag)
                OnMapClicked(GetGlobalMousePosition(), _pressIsDouble);
        }
    }

    // OpenFront's game speeds (single player): half, normal, double, as fast as it'll go.
    private static readonly double[] Speeds = { 0.5, 1, 2, 1000 };
    private static readonly string[] SpeedNames = { "Speed 0.5x", "Speed 1x", "Speed 2x", "Speed: fastest" };
    private int _speed = 1;
    private bool _paused;
    private bool _rocketUp = true; // bombs arc up the map (U flips it, like OpenFront)

    /// <summary>
    /// Keyboard shortcuts, OpenFront's defaults: 1-0 build / aim bombs (numpad too), B boat,
    /// G attack under the mouse, Shift+R hit back at the latest attacker, T/Y attack ratio,
    /// K/L/N ally / break alliance / target under the mouse, F and R the emoji and chat menu,
    /// U flip bomb arcs, C centre on you, M coordinate grid, Space (held) hide territory, P
    /// pause, comma/period game speed, Shift+D the frame counter. WASD/arrows pan and Q/E or
    /// -/= zoom (camera_2d.gd). Dev: F9 next map, F10 random map.
    /// </summary>
    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is not InputEventKey key)
            return;
        if (key.Keycode == Key.Space && !key.Echo) // held: the map without territory (OpenFront's alternate view)
        {
            _view.ShowTerritory = !key.Pressed;
            return;
        }
        if (!key.Pressed)
            return;
        SyncSim();
        var mouseTile = _view.TileAt(GetGlobalMousePosition());
        switch (key.Keycode)
        {
            case Key.T: // these two repeat while held, like OpenFront's
                _hud.AdjustRatio(-AttackRatioStep);
                return;
            case Key.Y:
                _hud.AdjustRatio(AttackRatioStep);
                return;
        }
        if (key.Echo)
            return;
        switch (key.Keycode)
        {
            case Key.F9:
                int next = (Array.IndexOf(BuiltInMaps, _mapId) + 1) % BuiltInMaps.Length;
                if (LoadMap(BuiltInMaps[next]))
                    FrameCamera(1, null);
                break;
            case Key.F10:
                Generate(RandomSeed());
                FrameCamera(1, null);
                break;
            case Key.M:
                _devTools.Toggle("GRD");
                break;
            case Key.B:
                SendBoat(mouseTile);
                break;
            case Key.G:
                GroundAttack(mouseTile);
                break;
            case Key.R when key.ShiftPressed:
                Retaliate();
                break;
            case Key.R or Key.F: // the emoji / quick-chat menu (ours is the radial menu, with its chat fans)
                OpenRadialMenuAtMouse();
                break;
            case Key.K:
                if (PlayerAt(mouseTile) is { } asked && _game.CanRequestAlliance(_me, asked))
                    _game.Submit(new AllianceIntent(_me.Id, asked.Id));
                break;
            case Key.L:
                if (PlayerAt(mouseTile) is { } ally && _me.IsAlliedWith(ally))
                    _game.Submit(new BreakAllianceIntent(_me.Id, ally.Id));
                break;
            case Key.N:
                if (PlayerAt(mouseTile) is { } marked && _game.CanTarget(_me, marked))
                    _game.Submit(new TargetIntent(_me.Id, marked.Id));
                break;
            case Key.U:
                _rocketUp = !_rocketUp;
                _hud.Flash(_rocketUp ? "Bombs arc up" : "Bombs arc down");
                break;
            case Key.C:
                if (_labels.TryGetAnchor(_me.Id, out var home))
                    _camera.Position = home;
                break;
            case Key.P:
                _paused = !_paused;
                _hud.Flash(_paused ? "Paused" : "Unpaused");
                break;
            case Key.Comma or Key.Period:
                _speed = Math.Clamp(_speed + (key.Keycode == Key.Period ? 1 : -1), 0, Speeds.Length - 1);
                _hud.Flash(SpeedNames[_speed]);
                break;
            case Key.D when key.ShiftPressed:
                _fps.Visible = !_fps.Visible;
                break;
            case >= Key.Key1 and <= Key.Key9:
            case Key.Key0:
                PressBuildKey(key.Keycode == Key.Key0 ? 0 : (int)(key.Keycode - Key.Key0));
                break;
            case >= Key.Kp1 and <= Key.Kp9:
            case Key.Kp0:
                PressBuildKey(key.Keycode == Key.Kp0 ? 0 : (int)(key.Keycode - Key.Kp0));
                break;
        }
    }

    private const int AttackRatioStep = 10; // OpenFront's attack ratio increment

    /// <summary>1-7 build (in the build bar's order), 8/9/0 aim a Mortar / Missile / Atomic Bomb.</summary>
    private void PressBuildKey(int digit)
    {
        switch (digit)
        {
            case >= 1 and <= 7:
                TogglePlacing(BuildButtons[digit - 1].Type);
                break;
            case 8:
                ToggleAiming(NukeType.Moab);
                break;
            case 9:
                ToggleAiming(NukeType.FatMan);
                break;
            case 0:
                ToggleAiming(NukeType.TsarBomba);
                break;
        }
    }

    private int? _aimAt; // testing aid (--aim): where the bomb preview points instead of the mouse

    private void AimForScreenshot(NukeType type)
    {
        var target = _game.Players.Where(p => p != _me && p.IsNation && p.IsAlive).MaxBy(p => p.TileCount);
        for (int i = 0; i < 200 && _game.ReadySilo(_me, 0) is null; i++)
            _game.Step(); // let the showcase's silo finish
        if (target is null || _game.ReadySilo(_me, 0) is not { } silo)
        {
            GD.Print($"aim: target {target?.Name ?? "none"}, silos {_game.Buildings.Count(b => b.Owner == _me.Id && b.Type == BuildingType.Silo)}");
            return;
        }
        int tile = Enumerable.Range(0, _map.TileCount).First(t => _game.Owner[t] == target.Id);
        _aiming = type;
        _aimAt = tile;
        var a = new Vector2(silo.Tile % _map.Width, silo.Tile / _map.Width);
        var b = new Vector2(tile % _map.Width, tile / _map.Width);
        _showcaseFocus = (a + b) / 2 / new Vector2(_map.Width, _map.Height);
        var preview = _game.PreviewNuke(_me, type, tile);
        GD.Print($"aim: {type} at {target.Name}, {preview?.X.Length ?? 0} points, intercept {preview?.InterceptIndex}");
    }

    /// <summary>Whoever owns this land tile (not you), or null.</summary>
    private Player? PlayerAt(int tile) =>
        tile >= 0 && _map.IsLand(tile) && _game.GetPlayer(_game.Owner[tile]) is { } p && p != _me ? p : null;

    /// <summary>G: attack whoever's land is under the mouse (or empty land), as a click would.</summary>
    private void GroundAttack(int tile)
    {
        if (tile < 0 || !_map.IsLand(tile) || _game.Phase != GamePhase.Running || !_me.HasSpawned || !_me.IsAlive)
            return;
        ushort target = _game.Owner[tile];
        if (target != _me.Id && !_me.Allies.Contains(target) && _game.CanReachByLand(_me, tile))
            _game.Submit(new AttackIntent(_me.Id, target, _hud.AttackRatioPercent));
    }

    /// <summary>
    /// Shift+R, OpenFront's retaliate: hit back at whoever attacked you most recently (not a
    /// tribe), with as many troops as they sent, up to your attack ratio's worth.
    /// </summary>
    private void Retaliate()
    {
        if (_game.Phase != GamePhase.Running || !_me.IsAlive)
            return;
        Attack? latest = null;
        foreach (var a in _game.Attacks)
            if (a.TargetId == _me.Id && !a.Attacker.IsBot)
                latest = a; // attacks are kept in the order they started
        if (latest is null)
            return;
        long troops = Math.Min(latest.Troops, _me.Troops * _hud.AttackRatioPercent / 100);
        if (troops > 0)
            _game.Submit(new AttackIntent(_me.Id, latest.Attacker.Id, _hud.AttackRatioPercent, troops));
    }

    /// <summary>F / R: the radial menu (with its emoji and chat fans) at the mouse, as a right click opens it.</summary>
    private void OpenRadialMenuAtMouse()
    {
        if (_mainControl is null)
            return;
        var screen = GetViewport().GetMousePosition();
        _mainControl.Call("show_radial_menu", screen);
        OnRightClick(GetCanvasTransform().AffineInverse() * screen);
    }

    /// <summary>This tick's gold income: pop-ups on the map and the "+gold" pip by your gold.</summary>
    private void ShowGoldGains()
    {
        foreach (var e in _game.Events)
        {
            if (e.Kind != GameEventKind.GoldEarned || e.A != _me.Id)
                continue;
            if (e.Tile >= 0)
                _overlay.AddIncomePopup(new Vector2(e.Tile % _map.Width + 0.5f, e.Tile / _map.Width + 0.5f), e.Detail);
            else if (_labels.TryGetAnchor(e.B, out var under)) // conquest: under the beaten player's name
                _overlay.AddConquestPopup(under + new Vector2(0, 8 * (float)_game.Rules.Linear), e.Detail);
            _bar.ShowGoldGain(e.Detail);
        }
    }

    // ------------------------------------------------------------------
    //  Building placement
    // ------------------------------------------------------------------

    private void TogglePlacing(BuildingType type)
    {
        SyncSim();
        if (_placing == type)
            StopPlacing();
        else if (_game.Phase == GamePhase.Running && _me.IsAlive && _me.CanAfford(_game.BuildingCost(_me, type)))
        {
            StopPlacing();
            _placing = type;
        }
    }

    /// <summary>Bomb buttons / keys 8, 9, 0: aim a MOAB, Fat Man or Tsar Bomba with the mouse.</summary>
    private void ToggleAiming(NukeType type)
    {
        SyncSim();
        if (_aiming == type)
            StopPlacing();
        else if (_game.Phase == GamePhase.Running && _me.IsAlive && _me.CanAfford(_game.NukeCost(type)))
        {
            StopPlacing();
            _aiming = type;
        }
    }

    private void StopPlacing()
    {
        _placing = null;
        _aiming = null;
        _overlay.ClearGhost();
        _ghostRoadsFor = (default, -1, -1);
        ShowSecurePatch(-1);
    }

    /// <summary>Moves the placement preview with the mouse and greys out buttons you can't afford.</summary>
    private double _sinceButtons = 1;
    private readonly System.Collections.Generic.Dictionary<Button, string> _buttonPrices = new();

    private void UpdatePlacing()
    {
        // Prices and affordability change slowly (and working out prices scans every
        // building), so the hotbar refreshes 5 times a second, not every frame.
        if ((_sinceButtons += GetProcessDeltaTime()) >= 0.2)
        {
            _sinceButtons = 0;
            UpdateButtons();
        }
        if (_hud.AdjustingRatio)
            return; // the preview stays put while the attack ratio slider is held
        if (_aiming is { } nukeType)
        {
            if (!_me.CanAfford(_game.NukeCost(nukeType)) || !_me.IsAlive)
            {
                StopPlacing();
                return;
            }
            int aim = _aimAt ?? _view.TileAt(GetGlobalMousePosition());
            if (aim < 0)
                _overlay.ClearNukeGhost();
            else
                _overlay.SetNukeGhost(nukeType, aim, _game.CanLaunch(_me, nukeType, aim), _rocketUp);
            return;
        }
        if (_placing is not { } type2)
            return;
        if (!_me.CanAfford(_game.BuildingCost(_me, type2)) || !_me.IsAlive)
        {
            StopPlacing();
            return;
        }
        int tile = PlacementTile(type2, _view.TileAt(GetGlobalMousePosition()));
        bool canBuild = tile >= 0 && _game.CanBuild(_me, type2, tile);
        if (tile < 0)
            _overlay.ClearGhost();
        else
            _overlay.SetGhost(type2, tile, canBuild);
        ShowSecurePatch(canBuild ? tile : -1);
        ShowGhostRoads(type2, canBuild && _game.UpgradeTarget(_me, type2, tile) is null ? tile : -1);
    }

    // The roads the structure being placed would lay, for the tile and tick they were worked out.
    private (BuildingType Type, int Tile, int Tick) _ghostRoadsFor = (default, -1, -1);

    /// <summary>Previews the roads a Depot (or a Barracks / Harbor near one) placed here would lay (-1 = none).</summary>
    private void ShowGhostRoads(BuildingType type, int tile)
    {
        var key = (type, tile, tile < 0 ? -1 : _game.Tick / 10); // buildings change: redo once a second
        if (key == _ghostRoadsFor)
            return;
        _ghostRoadsFor = key;
        _overlay.SetGhostRoads(tile < 0 ? new System.Collections.Generic.List<int[]>() : _game.PreviewRails(type, tile));
    }

    // The hatched patch the structure being placed would secure, highlighted on the map,
    // and the tick it was worked out (land settles, so it's redone every tick).
    private readonly System.Collections.Generic.HashSet<int> _securePatch = new();
    private int _securePatchTick = -1;

    /// <summary>Highlights the hatched land a structure placed on this tile would secure (-1 = none).</summary>
    private void ShowSecurePatch(int tile)
    {
        if (_game is null || (tile < 0 && _securePatch.Count == 0))
            return;
        if (tile >= 0 && _securePatchTick == _game.Tick && _securePatch.Contains(tile))
            return; // still pointing into the same patch
        var patch = tile >= 0 ? _game.HatchedPatch(_me, tile) : new System.Collections.Generic.List<int>();
        _securePatchTick = _game.Tick;
        if (patch.Count == _securePatch.Count && patch.TrueForAll(_securePatch.Contains))
            return; // unchanged
        _securePatch.Clear();
        _securePatch.UnionWith(patch);
        _view.SetSecureHighlight(patch.Count > 0 ? patch : null);
    }

    /// <summary>Greys out what you can't afford, and keeps each button's tooltip price current.</summary>
    private void UpdateButtons()
    {
        foreach (var (type, button) in _buildButtons)
        {
            long cost = _game.BuildingCost(_me, type);
            SetButton(button, _me.CanAfford(cost) && _game.Phase == GamePhase.Running, cost);
        }
        bool siloReady = _game.ReadySilo(_me, 0) is not null;
        foreach (var (nuke, button) in _nukeButtons)
        {
            long cost = _game.NukeCost(nuke);
            SetButton(button, _me.CanAfford(cost) && !_game.NukesLocked && siloReady, cost);
        }
    }

    // What you can't afford (or can't use yet) is dimmed: drawn darker.
    private static readonly Color UnusableButton = new(0.4f, 0.4f, 0.4f);

    private void SetButton(Button button, bool usable, long cost)
    {
        var tint = usable ? Colors.White : UnusableButton;
        if (button.Modulate != tint)
            button.Modulate = tint; // (its count and anything else on it)
        _buildBar.SetUsable(button, usable); // its picture
        // The hotbar tooltip (hoveroverhaul.gd) shows the price on its third line.
        string price = GameHud.FormatCount(cost);
        if (!_buttonPrices.TryGetValue(button, out var shown) || shown != price)
        {
            _buttonPrices[button] = price;
            button.Set("line3", price);
        }
    }

    // How far (in screen pixels) a port snaps to your nearest coastline.
    private const float PortSnapPixels = 70;

    /// <summary>
    /// Where a building would actually go for the tile under the mouse. Ports snap like
    /// a magnet to the nearest stretch of your own coast that they can be built on.
    /// </summary>
    private int PlacementTile(BuildingType type, int tile)
    {
        // Pointing at (or near) one of your own buildings of this type: stack onto it.
        if (_game.UpgradeTarget(_me, type, tile) is { } existing)
            return existing.Tile;
        if (tile < 0 || type != BuildingType.Port || _game.CanBuild(_me, type, tile))
            return tile;
        float zoom = Math.Max(_camera.Zoom.X, 0.01f);
        int radius = Math.Clamp((int)(PortSnapPixels / zoom), 6, 400);
        int x0 = tile % _map.Width, y0 = tile / _map.Width, best = -1;
        long bestDist = long.MaxValue;
        foreach (int t in _me.Coast)
        {
            long dx = t % _map.Width - x0, dy = t / _map.Width - y0, d = dx * dx + dy * dy;
            if (d <= (long)radius * radius && d < bestDist && _game.CanBuild(_me, type, t))
            {
                bestDist = d;
                best = t;
            }
        }
        return best >= 0 ? best : tile;
    }

    /// <summary>Boats troops towards the land at this tile (B key, or a double click on land you can't walk to).</summary>
    private void SendBoat(int tile)
    {
        if (tile < 0 || !_map.IsLand(tile) || _game.Phase != GamePhase.Running || !_me.HasSpawned || !_me.IsAlive)
            return;
        if (_game.Owner[tile] != _me.Id)
            _game.Submit(new BoatIntent(_me.Id, tile, _hud.AttackRatioPercent));
    }

    /// <summary>The player whose territory is under the mouse (ignoring the HUD), or null.</summary>
    /// <summary>
    /// The player whose land is under the mouse, or null (<paramref name="wilderness"/> says
    /// whether it's unclaimed land). The HUD hides the map, except the hover panel itself: the
    /// mouse looks through it, so the panel doesn't vanish and come back under the mouse.
    /// </summary>
    private Player? HoveredPlayer(out bool wilderness)
    {
        wilderness = false;
        if (_hud.AdjustingRatio)
            return null;
        if (_args.Hover is null && !_args.HoverLeader && GetViewport().GuiGetHoveredControl() is { MouseFilter: not Control.MouseFilterEnum.Ignore } over
            && !_panels.Contains(over))
            return null;
        if (_args.HoverLeader)
            return _game.Players.MaxBy(p => p.TileCount);
        int tile = _view.TileAt(HoverMouse());
        if (tile < 0)
            return null;
        wilderness = _game.Owner[tile] == Game.Nobody && _map.IsLand(tile);
        return _game.GetPlayer(_game.Owner[tile]);
    }

    private bool _pressIsDouble;

    /// <summary>The mouse on the map (or where --hover pretends it is).</summary>
    private Vector2 HoverMouse() => _args.Hover is { } h ? new Vector2(h.X * _map.Width, h.Y * _map.Height) : GetGlobalMousePosition();

    // The light fog of war: what we're shown of other players' troops and gold.
    private readonly View.TroopIntel _intel = new();

    /// <summary>A left click on the map (the second click of a double click has <paramref name="doubleClick"/> set).</summary>
    private void OnMapClicked(Vector2 worldPosition, bool doubleClick)
    {
        int tile = _view.TileAt(worldPosition);
        if (_aiming is { } nuke)
        {
            if (_game.CanLaunch(_me, nuke, tile))
            {
                _game.Submit(new NukeIntent(_me.Id, nuke, tile, _rocketUp));
                if (!Input.IsKeyPressed(Key.Shift)) // hold Shift to keep firing
                    StopPlacing();
            }
            return;
        }
        if (_placing is { } type && _game.Phase == GamePhase.Running)
        {
            tile = PlacementTile(type, tile); // ports snap to the coast, even from a click on the water
            if (_game.CanBuild(_me, type, tile))
            {
                _game.Submit(new BuildIntent(_me.Id, type, tile));
                if (!Input.IsKeyPressed(Key.Shift)) // hold Shift to place several
                    StopPlacing();
            }
            return;
        }
        if (tile < 0 || !_map.IsLand(tile))
            return;

        if (_startPrompt.Visible || _fastForwarding)
            return; // the dev start prompt is up, or the game is fast-forwarding
        if (_game.Phase == GamePhase.Spawning || (_me.LateSpawn && !_me.HasSpawned))
        {
            _game.Submit(new SpawnIntent(_me.Id, tile));
            return;
        }
        ushort target = _game.Owner[tile];
        if (target == _me.Id || !_me.IsAlive || !_me.HasSpawned)
            return;
        if (_game.CanReachByLand(_me, tile))
            _game.Submit(new AttackIntent(_me.Id, target, _hud.AttackRatioPercent));
        else if (doubleClick)
            SendBoat(tile); // land you can't walk to: a double click sends a boat (or press B)
    }

    // ------------------------------------------------------------------
    //  Map and match setup
    // ------------------------------------------------------------------

    private void Generate(ulong seed)
    {
        var timer = Stopwatch.StartNew();
        var map = MapGenerator.Generate(seed, new MapGenSettings { Width = MapWidth, Height = MapHeight });
        _mapId = "random";
        Show(map, $"Random map, seed {seed}", timer.ElapsedMilliseconds);
    }

    /// <summary>Loads a pre-built map from res://maps. Returns false if it's missing or broken.</summary>
    private bool LoadMap(string id)
    {
        string path = $"res://maps/{id}.bmap";
        var timer = Stopwatch.StartNew();
        byte[] bytes = Godot.FileAccess.GetFileAsBytes(path);
        if (bytes.Length == 0)
        {
            GD.PushError($"Map file not found: {path}");
            return false;
        }
        try
        {
            using var stream = new MemoryStream(bytes);
            var map = MapFile.Read(stream);
            _mapId = id;
            Show(map, map.Name, timer.ElapsedMilliseconds);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
        {
            GD.PushError($"Could not read {path}: {e.Message}");
            return false;
        }
    }

    private void Show(GameMap map, string title, long loadMs)
    {
        _map = map;
        _view.ShowMap(_map);
        _grid.SetMap(_map.Width, _map.Height);
        _devTools.ApplyAll(); // a new map resets the map material's settings
        _splash.SetMapName(_mapId == "random" ? "Random" : _map.Name);
        StartMatch();

        GD.Print($"{title}: {_map.Width}x{_map.Height}, {_map.LandmassSizes.Length} landmasses, " +
                 $"{100.0 * _map.LandTileCount / _map.TileCount:F0}% land, ready in {loadMs} ms");
        DisplayServer.WindowSetTitle($"Boundari — {title}");
    }

    private void StartMatch()
    {
        SyncSim();
        _game = new Game(_map, RandomSeed(), difficulty: _args.Difficulty ?? Difficulty) { HatchedLand = !_args.Classic, DirectedAttacks = !_args.Classic, FogOfWar = !_args.Classic, KeepCutOffLand = !_args.Classic };
        _me = _game.AddPlayer("Player", flag: "us");
        _game.EventsFor = _me.Id; // notifications
        if (_args.Bench is null || _args.Autoplay)
            _game.StartsWhenSpawned = _me.Id; // no countdown: the match starts when you pick your start
        _labels.Me = _me;
        if (DevCheats && !_args.NoCheats)
            _game.SetCheats(_me, infiniteGold: true, instantBuild: true);
        if (_args.Nations is null && MapNations.For(_mapId) is { } mapNations)
            _game.AddNations(mapNations); // the map's own nations, at their real homes
        else
            _game.AddNations(_args.Nations ?? Nations);
        _game.AddBots(_args.Bots ?? Bots);
        ApplyPalette();
        _ = _game.Waterways; // prepare sea routes now, not on the first boat
        _labels.SetGame(_game);
        _intel.SetGame(_game, _me);
        _labels.MouseOverride = _args.Hover is { } fake ? new Vector2(fake.X * _map.Width, fake.Y * _map.Height) : null;
        _overlay.SetGame(_game, _me);
        _placing = null;
        _securePatch.Clear(); // (the new match's territory upload clears the highlight)

        if (_args.Autoplay)
            AutoSpawn();
        for (int t = 0; t < _args.Ticks; t++)
            _game.Step();
        if (_args.Showcase)
            Showcase();
        if (_args.Aim is { } aimArg && Enum.TryParse<NukeType>(aimArg, true, out var aimType))
            AimForScreenshot(aimType);
        if (_args.Incoming)
        {
            // Up to three neighbours attack us.
            foreach (var them in _game.BorderingPlayers(_me).Take(3))
                _game.Submit(new AttackIntent(them.Id, _me.Id, 30));
            _game.Step();
            GD.Print($"incoming: {_game.Attacks.Count(a => a.TargetId == _me.Id)} attacks on us");
        }
        if (_args.Arrow is { } arrowMode)
            ArrowForScreenshot(arrowMode, _args.ArrowPixels);

        if (_args.Alert is { } alert)
            _alerts.Test(betrayal: alert == "betrayal");
        if (_args.AllyRequests > 0)
            AskForAlliances(_args.AllyRequests);
        _labels.PreviewAllTags = _args.Tags;
        if (_args.Notifs)
            _notifications.ShowSamples(_game, _me);

        _view.SetOwners(_game.Owner, _game.Fallout, _game.HatchedUntil);
        _shownTick = _game.Tick;
        _game.ChangedTiles.Clear();
        _tickTime = 0;

        _arcs.Me = _me.Id;
        _arcs.SetRequests(Array.Empty<AllianceArcs.Request>());
        _fastForwardLeft = 0;
        _fastForwarding = false;
        _hud.Status = null;
        // Dev start: ask whether to start now or fast-forward to mid-game (not for scripted runs).
        string start = _args.Start ?? (DevCheats && !_args.NoCheats && _args.Screenshot is null && !_args.Autoplay && _args.Bench is null ? "ask" : "now");
        _startPrompt.Visible = start == "ask";
        if (start == "mid")
            BeginMidGame();
        else if (start == "now")
            PlaySplash();
    }

    /// <summary>The startup splash, except in scripted runs (screenshots, benchmarks) unless --splash asks for it.</summary>
    private void PlaySplash()
    {
        if (_args.Splash || (_args.Screenshot is null && _args.Bench is null))
            _splash.Play();
    }

    /// <summary>Testing aid: spawns the human player on a random big landmass.</summary>
    private void AutoSpawn()
    {
        var rng = new Core.Rng(_game.Seed + 1);
        for (int tries = 0; tries < 1000; tries++)
        {
            int t = rng.RangeInt(0, _map.TileCount - 1);
            if (_map.IsLand(t) && _game.Owner[t] == Game.Nobody && _map.LandmassSizes[_map.LandmassId[t]] > 10_000
                && (!_args.Showcase || _game.IsCoastal(t)))
            {
                _game.Submit(new SpawnIntent(_me.Id, t));
                _game.Step();
                return;
            }
        }
    }

    /// <summary>Testing aid: one of each building on your land, and a boat heading off.</summary>
    private void Showcase()
    {
        for (int wave = 0; wave < 4; wave++)
        {
            _game.Submit(new AttackIntent(_me.Id, Game.Nobody, 90));
            for (int t = 0; t < 150; t++)
                _game.Step();
        }
        typeof(Player).GetProperty(nameof(Player.Gold))!.SetValue(_me, 20_000_000L);
        var mine = Enumerable.Range(0, _map.TileCount).Where(t => _game.Owner[t] == _me.Id).ToList();
        if (mine.Count == 0)
        {
            GD.Print("showcase: you were wiped out before it started; skipping");
            return;
        }
        foreach (BuildingType type in Enum.GetValues<BuildingType>())
        {
            var candidates = type == BuildingType.Warship
                ? Enumerable.Range(0, _map.TileCount).Where(t => _map.IsWater(t)) : mine;
            int tile = candidates.FirstOrDefault(t => _game.CanBuild(_me, type, t), -1);
            if (tile >= 0)
            {
                _game.Submit(new BuildIntent(_me.Id, type, tile));
                _game.Step();
            }
        }
        // A small rail network: cities spread around a factory, one city stacked twice.
        var centre = mine[mine.Count / 2];
        int Dist2(int t) { int dx = t % _map.Width - centre % _map.Width, dy = t / _map.Width - centre / _map.Width; return dx * dx + dy * dy; }
        foreach (int ring in new[] { 40, 55, 70, 85 })
        {
            int city = mine.Where(t => Math.Abs(Math.Sqrt(Dist2(t)) - ring) < 4).FirstOrDefault(t => _game.CanBuild(_me, BuildingType.City, t), -1);
            if (city >= 0)
            {
                _game.Submit(new BuildIntent(_me.Id, BuildingType.City, city));
                _game.Step();
            }
        }
        for (int t = 0; t < 30; t++)
            _game.Step();
        var firstCity = _game.Buildings.FirstOrDefault(b => b.Type == BuildingType.City && b.Owner == _me.Id);
        for (int stack = 0; stack < 2 && firstCity is not null; stack++)
        {
            _game.Submit(new BuildIntent(_me.Id, BuildingType.City, firstCity.Tile));
            _game.Step();
        }
        int factorySite = mine.OrderBy(Dist2).FirstOrDefault(t => _game.CanBuild(_me, BuildingType.Factory, t), -1);
        if (factorySite >= 0)
        {
            _game.Submit(new BuildIntent(_me.Id, BuildingType.Factory, factorySite));
            for (int t = 0; t < 400; t++)
                _game.Step();
        }
        // One more city, just ordered, to show the construction ring.
        int late = mine.FirstOrDefault(t => _game.CanBuild(_me, BuildingType.City, t), -1);
        if (late >= 0)
        {
            _game.Submit(new BuildIntent(_me.Id, BuildingType.City, late));
            _game.Step();
        }
        GD.Print($"showcase: {_game.Railroads.Count} railroads, {_game.Trains.Count} trains, train gold {_me.TrainGold:N0}, " +
                 $"cities {_game.CountLevels(_me, BuildingType.City)} levels, max troops {_me.MaxTroops:N0}");
        _showcaseRailFocus = factorySite;
        if (_showcaseRailFocus < 0 && _game.Railroads.MaxBy(r => r.Tiles.Length) is { } longest)
            _showcaseRailFocus = longest.Tiles[longest.Tiles.Length / 2]; // no depot of ours: someone else's roads

        // Nearby land across water (often just over a river): try candidates until a boat can go.
        int home = _map.LandmassId[mine[0]], hx = mine[0] % _map.Width, hy = mine[0] / _map.Width;
        var targets = Enumerable.Range(0, _map.TileCount)
            .Where(t => _map.IsLand(t) && _map.LandmassId[t] != home && _map.LandmassSizes[_map.LandmassId[t]] > 2000
                        && Math.Abs(t % _map.Width - hx) + Math.Abs(t / _map.Width - hy) > 60)
            .OrderBy(t => Math.Abs(t % _map.Width - hx) + Math.Abs(t / _map.Width - hy)).Take(4000).Where((_, k) => k % 200 == 0);
        foreach (int far in targets)
        {
            _game.Submit(new BoatIntent(_me.Id, far, 40));
            _game.Step();
            if (_game.Boats.Count > 0)
                break;
        }
        for (int t = 0; t < 10; t++)
            _game.Step();
        var at = new Vector2(mine[mine.Count / 2] % _map.Width, mine[mine.Count / 2] / _map.Width);
        if (_args.ShowcaseFocus?.StartsWith("nukes") == true && ShowcaseNukes() is { } blast)
        {
            _showcaseFocus = blast / new Vector2(_map.Width, _map.Height);
            return;
        }
        if (_args.ShowcaseFocus == "rails" && _showcaseRailFocus >= 0)
            at = new Vector2(_showcaseRailFocus % _map.Width, _showcaseRailFocus / _map.Width);
        else if (_args.ShowcaseFocus == "boat" && _game.Boats.FirstOrDefault(b => b.Owner == _me) is { } boat)
            at = new Vector2(boat.LandingTile % _map.Width, boat.LandingTile / _map.Width);
        else if (_game.Warships.Count > 0)
            at = new Vector2(_game.Warships[0].Tile % _map.Width, _game.Warships[0].Tile / _map.Width);
        _showcaseFocus = at / new Vector2(_map.Width, _map.Height);
        GD.Print($"showcase: {_game.Buildings.Count()} buildings, {_game.Warships.Count} warships at {string.Join(",", _game.Warships.Select(w => (w.Tile % _map.Width, w.Tile / _map.Width)))}, {_game.Boats.Count} boats, centre {at / new Vector2(_map.Width, _map.Height)}");
    }

    /// <summary>
    /// Testing aid (--showcase=nukes[:TICKS]): fires a MOAB, a Fat Man and a Tsar Bomba from your
    /// silo at nearby enemy land, then runs TICKS ticks (default 25: bombs in the air). Returns
    /// the point between the silo and the targets, for the camera.
    /// </summary>
    private Vector2? ShowcaseNukes()
    {
        var silo = _game.Buildings.FirstOrDefault(b => b.Owner == _me.Id && b.Type == BuildingType.Silo);
        if (silo is null)
            return null;
        for (int level = 1; level < 3; level++) // three missiles ready at once
            _game.Submit(new BuildIntent(_me.Id, BuildingType.Silo, silo.Tile));
        while (_game.NukesLocked)
            _game.Step();
        int w = _map.Width, sx = silo.Tile % w, sy = silo.Tile / w;
        var enemy = Enumerable.Range(0, _map.TileCount)
            .Where(t => _game.Owner[t] != Game.Nobody && _game.Owner[t] != _me.Id)
            .Select(t => (Tile: t, D: Math.Sqrt((t % w - sx) * (t % w - sx) + (t / w - sy) * (t / w - sy))))
            .Where(x => x.D is > 120 and < 320).OrderBy(x => x.D).Select(x => x.Tile).ToList();
        if (enemy.Count == 0)
            return null;
        int Pick(double fraction) => enemy[Math.Min(enemy.Count - 1, (int)(enemy.Count * fraction))];
        int moab = Pick(0.1), fatMan = Pick(0.6), tsar = Pick(0.95);
        _game.Submit(new NukeIntent(_me.Id, NukeType.Moab, moab));
        _game.Submit(new NukeIntent(_me.Id, NukeType.FatMan, fatMan));
        _game.Submit(new NukeIntent(_me.Id, NukeType.TsarBomba, tsar));
        int ticks = _args.ShowcaseFocus!.Split(':') is [_, var n] ? int.Parse(n, System.Globalization.CultureInfo.InvariantCulture) : 25;
        for (int t = 0; t < ticks; t++)
            _game.Step();
        GD.Print($"showcase nukes: {_game.Nukes.Count} in the air, {_game.Explosions.Count} explosions, {_game.FalloutTiles} fallout tiles");
        return ticks > 20 // after impact: look at the blasts; before: the whole flight
            ? new Vector2((moab % w + fatMan % w) / 2f, (moab / w + fatMan / w) / 2f)
            : new Vector2((sx + moab % w + fatMan % w) / 3f, (sy + moab / w + fatMan / w) / 3f);
    }

    /// <summary>Top-left readout: frames per second, frame time, and how long simulation ticks take.</summary>
    private Label MakeFpsLabel()
    {
        var layer = new CanvasLayer { Name = "FpsLayer", Layer = 100 };
        AddChild(layer);
        var label = new Label { Name = "Fps", Text = "-- FPS", Position = new Vector2(10, 8), MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", 14);
        label.AddThemeColorOverride("font_color", new Color(0.85f, 0.95f, 0.8f));
        label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
        label.AddThemeConstantOverride("outline_size", 4);
        layer.AddChild(label);
        return label;
    }

    private void UpdateFps(double delta)
    {
        if ((_fpsTimer += delta) < 0.5)
            return;
        double fps = Engine.GetFramesPerSecond();
        string sim = _steps > 0 ? $"tick {_stepMs / _steps:F1} ms (worst {_worstStepMs:F0})" : "tick -";
        _fps.Text = $"{fps:F0} FPS  {1000.0 / Math.Max(fps, 1):F1} ms  |  {sim}";
        _fpsTimer = 0;
        _stepMs = _worstStepMs = 0;
        _steps = 0;
    }

    private void FrameCamera(float zoom, Vector2? focus)
    {
        Vector2 mapSize = new(_map.Width, _map.Height);
        Vector2 screen = GetViewportRect().Size;
        float fit = Mathf.Min(screen.X / mapSize.X, screen.Y / mapSize.Y) * 0.95f;

        _camera.Offset = Vector2.Zero;
        _camera.Position = mapSize * (focus ?? new Vector2(0.5f, 0.5f));
        _camera.Zoom = Vector2.One * fit * zoom;
        _camera.Set("min_zoom", fit * 0.5f); // camera_2d.gd limits
        _camera.Set("max_zoom", 120.0f);
    }

    private static ulong RandomSeed() => (ulong)Random.Shared.NextInt64(1, 1_000_000_000);

    private async void SaveScreenshotAndQuit(string path)
    {
        if (_args.RightClick?.StartsWith("requester") == true)
            await RightClickRequester();
        while (_fastForwarding) // --start=mid: wait for the fast-forward
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        // A few frames, and enough time for background work (label placement) to land.
        var started = Time.GetTicksMsec();
        for (int i = 0; i < 4 || Time.GetTicksMsec() - started < 600; i++)
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"Saved screenshot to {path}");
        GetTree().Quit();
    }

    private record struct Args(string? Map, ulong? Seed, int? Bots, int? Nations, Difficulty? Difficulty, bool NoCheats, double? Bench, string? Dev, bool Autoplay, int Ticks,
        float Zoom, Vector2? Focus, string? Screenshot, bool Perf, Vector2? Hover, bool HoverLeader, bool Showcase, string? ShowcaseFocus,
        string? Start = null, int AllyRequests = 0, string? RightClick = null, string? Aim = null, bool Incoming = false, bool Tags = false, bool Notifs = false,
        bool Splash = false, string? Alert = null, bool Classic = false, string? Arrow = null, float ArrowPixels = 250);

    private static Args ParseArgs()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var args = new Args(null, null, null, null, null, false, null, null, false, 0, 1, null, null, false, null, false, false, null);
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            string[] kv = arg.TrimStart('-').Split('=', 2);
            string value = kv.Length > 1 ? kv[1] : "";
            switch (kv[0])
            {
                case "map": args.Map = value; break;
                case "seed": args.Seed = ulong.Parse(value); break;
                case "bots": args.Bots = int.Parse(value, inv); break;
                case "nations": args.Nations = int.Parse(value, inv); break;
                case "tribes": args.Bots = int.Parse(value, inv); break;
                case "no-cheats": args.NoCheats = true; break;
                case "classic": args.Classic = true; break;
                case "arrow": args.Arrow = value; break; // (testing) "drag" or "launch[:TICKS]": a directed attack at the biggest neighbour
                case "arrow-px": args.ArrowPixels = float.Parse(value, inv); break; // ...this long on screen (40 = flattest, 360+ = thinnest)
                case "bench": args.Bench = double.Parse(value, inv); break;
                case "dev": args.Dev = value; break;
                case "difficulty": args.Difficulty = Enum.Parse<Difficulty>(value, ignoreCase: true); break;
                case "autoplay": args.Autoplay = true; break;
                case "ticks": args.Ticks = int.Parse(value, inv); break;
                case "zoom": args.Zoom = float.Parse(value, inv); break;
                case "focus":
                    string[] xy = value.Split(',');
                    args.Focus = new Vector2(float.Parse(xy[0], inv), float.Parse(xy[1], inv));
                    break;
                case "screenshot": args.Screenshot = value; break;
                case "perf": args.Perf = true; break;
                case "start": args.Start = value; break;             // now / mid / ask
                case "ally-requests": args.AllyRequests = int.Parse(value, inv); break;
                case "alert": args.Alert = value; break; // (testing) "betrayal" or "attack": flash the alert frame
                case "splash": args.Splash = true; break; // (testing) the startup splash shows in screenshot runs too
                case "notifs": args.Notifs = true; break; // (testing) sample messages in the notification list, opened
                case "tags": args.Tags = true; break; // (testing) every name shows every icon above it
                case "incoming": args.Incoming = true; break; // (testing) a neighbour attacks you, to show the incoming tabs
                case "aim": args.Aim = value; break; // (testing) "moab"/"fatman": aim at the biggest other nation, screen centred on the arc
                case "right-click": args.RightClick = value; break; // "requester": right-click whoever asked us to ally (screenshots)
                case "showcase": args.Showcase = true; args.ShowcaseFocus = value; break;
                case "hover" when value == "leader": args.HoverLeader = true; break;
                case "hover":
                    string[] hv = value.Split(',');
                    args.Hover = new Vector2(float.Parse(hv[0], inv), float.Parse(hv[1], inv));
                    break;
            }
        }
        return args;
    }
}
