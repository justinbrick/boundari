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
///
/// Command-line options (after "--" when launching Godot):
///   --map=ID           load res://maps/ID.bmap ("random" for a generated map)
///   --seed=N           generate a random map with this seed
///   --nations=N        number of nations, the proper AI opponents (default 120)
///   --bots=N           number of bots, the weak filler players (default 400, like OpenFront)
///   --difficulty=D     easy, medium (default), hard or impossible: how strong nations are
///   --autoplay         spawn the human player automatically (for testing)
///   --ticks=N          fast-forward the match N ticks at start
///   --no-hillshade     start with hill shading off
///   --zoom=Z           start zoomed in (1 = whole map)
///   --focus=X,Y        centre the camera on this point, as 0..1 fractions of the map
///   --screenshot=PATH  save a screenshot after loading, then quit
///   --perf             print the frame rate every 2 seconds
///   --showcase         (testing) after --autoplay: build one of everything and launch a boat
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
    [Export] public int Nations { get; set; } = 120;
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
    private BuildingType? _placing;
    private readonly System.Collections.Generic.List<(BuildingType Type, Button Button)> _buildButtons = new();
    private Color[] _palette = Array.Empty<Color>();
    private Vector2? _showcaseFocus;
    private int _showcaseRailFocus = -1;

    private MapView _view = null!;
    private Camera2D _camera = null!;
    private GameHud _hud = null!;
    private PlayerPanels _panels = null!;
    private TerritoryLabels _labels = null!;
    private GameMap _map = null!;
    private string _mapId = "";
    private Args _args;

    private Game _game = null!;
    private Player _me = null!;
    private double _tickTime;
    private Vector2? _pressPosition;
    private double _perfTimer;

    public override void _Ready()
    {
        _view = GetNode<MapView>("MapDisplay");
        _camera = GetNode<Camera2D>("Camera2D");
        _hud = new GameHud(this);
        _panels = new PlayerPanels(this);
        _overlay = new MapOverlay { Name = "MapOverlay" };
        AddChild(_overlay);
        MoveChild(_overlay, _view.GetIndex() + 1); // just above the map, below the HUD
        _labels = new TerritoryLabels { Name = "TerritoryLabels" };
        AddChild(_labels);
        MoveChild(_labels, _overlay.GetIndex() + 1);

        foreach (var (nuke, path) in NukeButtons)
            if (GetNodeOrNull<Button>("Main_Layer/Main_Control/ButtonsRow/" + path) is { } button)
                _nukeButtons.Add((nuke, button));
        foreach (var (type, path) in BuildButtons)
            if (GetNodeOrNull<Button>("Main_Layer/Main_Control/ButtonsRow/" + path) is { } button)
            {
                var t = type;
                button.Pressed += () => TogglePlacing(t);
                _buildButtons.Add((type, button));
            }

        _args = ParseArgs();
        _view.ShowHillshade = !_args.NoHillshade;
        string mapId = _args.Seed is not null ? "random" : _args.Map ?? StartMap;
        if (mapId is "" or "random" || !LoadMap(mapId))
            Generate(_args.Seed ?? (Seed != 0 ? Seed : RandomSeed()));
        FrameCamera(_args.Zoom, _args.Focus ?? _showcaseFocus);

        if (_args.Screenshot is not null)
            SaveScreenshotAndQuit(_args.Screenshot);
    }

    public override void _Process(double delta)
    {
        // Fixed-rate simulation: catch up on missed ticks, but never more than a few per frame.
        _tickTime += delta;
        double tickLength = 1.0 / Balance.TicksPerSecond;
        for (int i = 0; i < 4 && _tickTime >= tickLength; i++)
        {
            int tick = _game.Tick;
            _game.Step();
            _view.RecordTick(_game.Owner, _game.ChangedTiles, tick);
            _game.ChangedTiles.Clear();
            _tickTime -= tickLength;
        }
        _tickTime = Math.Min(_tickTime, tickLength);

        if (_args.Perf && (_perfTimer += delta) >= 2)
        {
            _perfTimer = 0;
            GD.Print($"perf: {Engine.GetFramesPerSecond()} fps, tick {_game.Tick}, {_game.Attacks.Count} attacks");
        }

        _view.FlushTerritory();
        _view.SetVisualTime(_game.Tick - 1 + (float)(_tickTime / tickLength));
        _overlay.SetTickFraction((float)(_tickTime / tickLength));
        UpdatePlacing();
        _hud.Update(_game, _me);
        var hovered = HoveredPlayer();
        _panels.Update(_game, _me, hovered, delta);
    }

    public override void _Input(InputEvent e)
    {
        // Esc cancels placement before the options menu can grab it.
        if (_placing is not null && e is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            StopPlacing();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } && _placing is not null)
        {
            StopPlacing();
            return;
        }
        if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
            return;
        if (button.Pressed)
        {
            _pressPosition = button.Position;
        }
        else if (_pressPosition is { } start)
        {
            _pressPosition = null;
            if (start.DistanceTo(button.Position) <= ClickMaxDrag)
                OnMapClicked(GetGlobalMousePosition());
        }
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } key)
            return;
        switch (key.Keycode)
        {
            case Key.M:
                int next = (Array.IndexOf(BuiltInMaps, _mapId) + 1) % BuiltInMaps.Length;
                if (LoadMap(BuiltInMaps[next]))
                    FrameCamera(1, null);
                break;
            case Key.R:
                Generate(RandomSeed());
                FrameCamera(1, null);
                break;
            case Key.H:
                _view.ShowHillshade = !_view.ShowHillshade;
                break;
            case Key.B:
                SendBoat(_view.TileAt(GetGlobalMousePosition()));
                break;
            case >= Key.Key1 and <= Key.Key7:
                TogglePlacing(BuildButtons[key.Keycode - Key.Key1].Type);
                break;
        }
    }

    // ------------------------------------------------------------------
    //  Building placement
    // ------------------------------------------------------------------

    private void TogglePlacing(BuildingType type)
    {
        if (_placing == type)
            StopPlacing();
        else if (_game.Phase == GamePhase.Running && _me.IsAlive && _me.Gold >= _game.BuildingCost(_me, type))
            _placing = type;
    }

    private void StopPlacing()
    {
        _placing = null;
        _overlay.ClearGhost();
    }

    /// <summary>Moves the placement preview with the mouse and greys out buttons you can't afford.</summary>
    private void UpdatePlacing()
    {
        foreach (var (type, button) in _buildButtons)
        {
            long cost = _game.BuildingCost(_me, type);
            bool affordable = _me.Gold >= cost && _game.Phase == GamePhase.Running;
            float alpha = affordable ? 1f : 0.4f;
            if (button.Modulate.A != alpha)
                button.Modulate = new Color(1, 1, 1, alpha);
            // The hotbar tooltip (hoveroverhaul.gd) shows the price on its third line.
            string price = GameHud.FormatCount(cost);
            if ((string)button.Get("line3") != price)
                button.Set("line3", price);
        }
        foreach (var (nuke, button) in _nukeButtons)
        {
            long cost = _game.NukeCost(nuke);
            float alpha = _me.Gold >= cost && _game.Phase == GamePhase.Running ? 1f : 0.4f;
            if (button.Modulate.A != alpha)
                button.Modulate = new Color(1, 1, 1, alpha);
            string price = GameHud.FormatCount(cost);
            if ((string)button.Get("line3") != price)
                button.Set("line3", price);
        }
        if (_placing is not { } type2)
            return;
        if (_me.Gold < _game.BuildingCost(_me, type2) || !_me.IsAlive)
        {
            StopPlacing();
            return;
        }
        int tile = PlacementTile(type2, _view.TileAt(GetGlobalMousePosition()));
        if (tile < 0)
            _overlay.ClearGhost();
        else
            _overlay.SetGhost(type2, tile, _game.CanBuild(_me, type2, tile));
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

    /// <summary>Boats troops towards the land at this tile (B key, or a click on land you can't walk to).</summary>
    private void SendBoat(int tile)
    {
        if (tile < 0 || !_map.IsLand(tile) || _game.Phase != GamePhase.Running || !_me.HasSpawned || !_me.IsAlive)
            return;
        if (_game.Owner[tile] != _me.Id)
            _game.Submit(new BoatIntent(_me.Id, tile, _hud.AttackRatioPercent));
    }

    /// <summary>The player whose territory is under the mouse (ignoring the HUD), or null.</summary>
    private Player? HoveredPlayer()
    {
        if (_args.Hover is null && !_args.HoverLeader && GetViewport().GuiGetHoveredControl() is { MouseFilter: not Control.MouseFilterEnum.Ignore })
            return null;
        if (_args.HoverLeader)
            return _game.Players.MaxBy(p => p.TileCount);
        var mouse = _args.Hover is { } h ? new Vector2(h.X * _map.Width, h.Y * _map.Height) : GetGlobalMousePosition();
        int tile = _view.TileAt(mouse);
        return tile < 0 ? null : _game.GetPlayer(_game.Owner[tile]);
    }

    private void OnMapClicked(Vector2 worldPosition)
    {
        int tile = _view.TileAt(worldPosition);
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

        if (_game.Phase == GamePhase.Spawning)
        {
            _game.Submit(new SpawnIntent(_me.Id, tile));
            return;
        }
        ushort target = _game.Owner[tile];
        if (target == _me.Id || !_me.IsAlive || !_me.HasSpawned)
            return;
        if (_game.CanReachByLand(_me, tile))
            _game.Submit(new AttackIntent(_me.Id, target, _hud.AttackRatioPercent));
        else
            SendBoat(tile);
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
        StartMatch();

        GD.Print($"{title}: {_map.Width}x{_map.Height}, {_map.LandmassSizes.Length} landmasses, " +
                 $"{100.0 * _map.LandTileCount / _map.TileCount:F0}% land, ready in {loadMs} ms");
        DisplayServer.WindowSetTitle($"Boundari — {title}");
    }

    private void StartMatch()
    {
        _game = new Game(_map, RandomSeed(), difficulty: _args.Difficulty ?? Difficulty);
        _me = _game.AddPlayer("You");
        _game.AddNations(_args.Nations ?? Nations);
        _game.AddBots(_args.Bots ?? Bots);
        _palette = PlayerColors.For(_game.Players, _game.Seed);
        _view.SetPalette(_palette);
        _overlay.SetPalette(_palette);
        _ = _game.Waterways; // prepare sea routes now, not on the first boat
        _labels.SetGame(_game);
        _overlay.SetGame(_game, _me);
        _placing = null;

        if (_args.Autoplay)
            AutoSpawn();
        for (int t = 0; t < _args.Ticks; t++)
            _game.Step();
        if (_args.Showcase)
            Showcase();

        _view.SetOwners(_game.Owner);
        _game.ChangedTiles.Clear();
        _tickTime = 0;
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
        if (_args.ShowcaseFocus == "rails" && _showcaseRailFocus >= 0)
            at = new Vector2(_showcaseRailFocus % _map.Width, _showcaseRailFocus / _map.Width);
        else if (_game.Warships.Count > 0)
            at = new Vector2(_game.Warships[0].Tile % _map.Width, _game.Warships[0].Tile / _map.Width);
        _showcaseFocus = at / new Vector2(_map.Width, _map.Height);
        GD.Print($"showcase: {_game.Buildings.Count()} buildings, {_game.Warships.Count} warships at {string.Join(",", _game.Warships.Select(w => (w.Tile % _map.Width, w.Tile / _map.Width)))}, {_game.Boats.Count} boats, centre {at / new Vector2(_map.Width, _map.Height)}");
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
        for (int i = 0; i < 4; i++)
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"Saved screenshot to {path}");
        GetTree().Quit();
    }

    private record struct Args(string? Map, ulong? Seed, int? Bots, int? Nations, Difficulty? Difficulty, bool Autoplay, int Ticks,
        bool NoHillshade, float Zoom, Vector2? Focus, string? Screenshot, bool Perf, Vector2? Hover, bool HoverLeader, bool Showcase, string? ShowcaseFocus);

    private static Args ParseArgs()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var args = new Args(null, null, null, null, null, false, 0, false, 1, null, null, false, null, false, false, null);
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
                case "difficulty": args.Difficulty = Enum.Parse<Difficulty>(value, ignoreCase: true); break;
                case "autoplay": args.Autoplay = true; break;
                case "ticks": args.Ticks = int.Parse(value, inv); break;
                case "no-hillshade": args.NoHillshade = true; break;
                case "zoom": args.Zoom = float.Parse(value, inv); break;
                case "focus":
                    string[] xy = value.Split(',');
                    args.Focus = new Vector2(float.Parse(xy[0], inv), float.Parse(xy[1], inv));
                    break;
                case "screenshot": args.Screenshot = value; break;
                case "perf": args.Perf = true; break;
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
