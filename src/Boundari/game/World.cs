using System;
using System.Diagnostics;
using System.IO;
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
///                          using the attack ratio slider's share of your troops.
///   M — switch map (Europe, World) and start a new match
///   R — new random map and match
///   H — toggle hill shading
///   C — toggle contour lines
///
/// Command-line options (after "--" when launching Godot):
///   --map=ID           load res://maps/ID.bmap ("random" for a generated map)
///   --seed=N           generate a random map with this seed
///   --bots=N           number of bots (default 40)
///   --autoplay         spawn the human player automatically (for testing)
///   --ticks=N          fast-forward the match N ticks at start
///   --no-hillshade     start with hill shading off
///   --no-contours      start with contour lines off
///   --zoom=Z           start zoomed in (1 = whole map)
///   --focus=X,Y        centre the camera on this point, as 0..1 fractions of the map
///   --screenshot=PATH  save a screenshot after loading, then quit
///   --perf             print the frame rate every 2 seconds
/// </summary>
public partial class World : Node2D
{
    /// <summary>Map to load at start (res://maps/ID.bmap). Empty means a random map.</summary>
    [Export] public string StartMap { get; set; } = "europe";
    [Export] public ulong Seed { get; set; }
    [Export] public int MapWidth { get; set; } = 2000;
    [Export] public int MapHeight { get; set; } = 1250;
    [Export] public int Bots { get; set; } = 40;

    private const float ClickMaxDrag = 6; // pixels; more than this is a camera drag, not a click

    private static readonly string[] BuiltInMaps = { "europe", "world" };

    private MapView _view = null!;
    private Camera2D _camera = null!;
    private GameHud _hud = null!;
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
        _view.SetPalette(MakePalette(4096));

        _args = ParseArgs();
        _view.ShowHillshade = !_args.NoHillshade;
        _view.ShowContours = !_args.NoContours;
        string mapId = _args.Seed is not null ? "random" : _args.Map ?? StartMap;
        if (mapId is "" or "random" || !LoadMap(mapId))
            Generate(_args.Seed ?? (Seed != 0 ? Seed : RandomSeed()));
        FrameCamera(_args.Zoom, _args.Focus);

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
        _hud.Update(_game, _me);
    }

    public override void _UnhandledInput(InputEvent e)
    {
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
            case Key.C:
                _view.ShowContours = !_view.ShowContours;
                break;
        }
    }

    private void OnMapClicked(Vector2 worldPosition)
    {
        int tile = _view.TileAt(worldPosition);
        if (tile < 0 || !_map.IsLand(tile))
            return;

        if (_game.Phase == GamePhase.Spawning)
        {
            _game.Submit(new SpawnIntent(_me.Id, tile));
            return;
        }
        ushort target = _game.Owner[tile];
        if (target != _me.Id && _me.IsAlive && _me.HasSpawned)
            _game.Submit(new AttackIntent(_me.Id, target, _hud.AttackRatioPercent));
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
        _game = new Game(_map, RandomSeed());
        _me = _game.AddPlayer("You");
        _game.AddBots(_args.Bots ?? Bots);

        if (_args.Autoplay)
            AutoSpawn();
        for (int t = 0; t < _args.Ticks; t++)
            _game.Step();

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
            if (_map.IsLand(t) && _game.Owner[t] == Game.Nobody && _map.LandmassSizes[_map.LandmassId[t]] > 10_000)
            {
                _game.Submit(new SpawnIntent(_me.Id, t));
                _game.Step();
                return;
            }
        }
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

    private static Color[] MakePalette(int count)
    {
        var colors = new Color[count];
        var rng = new Core.Rng(12345);
        for (int i = 1; i < count; i++)
        {
            // Golden-ratio hue steps keep neighbouring ids visually distinct.
            float hue = (float)(i * 0.618033988749895 % 1.0);
            colors[i] = Color.FromHsv(hue, (float)rng.Range(0.45, 0.7), (float)rng.Range(0.75, 0.95));
        }
        return colors;
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

    private record struct Args(string? Map, ulong? Seed, int? Bots, bool Autoplay, int Ticks,
        bool NoHillshade, bool NoContours, float Zoom, Vector2? Focus, string? Screenshot, bool Perf);

    private static Args ParseArgs()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var args = new Args(null, null, null, false, 0, false, false, 1, null, null, false);
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            string[] kv = arg.TrimStart('-').Split('=', 2);
            string value = kv.Length > 1 ? kv[1] : "";
            switch (kv[0])
            {
                case "map": args.Map = value; break;
                case "seed": args.Seed = ulong.Parse(value); break;
                case "bots": args.Bots = int.Parse(value, inv); break;
                case "autoplay": args.Autoplay = true; break;
                case "ticks": args.Ticks = int.Parse(value, inv); break;
                case "no-hillshade": args.NoHillshade = true; break;
                case "no-contours": args.NoContours = true; break;
                case "zoom": args.Zoom = float.Parse(value, inv); break;
                case "focus":
                    string[] xy = value.Split(',');
                    args.Focus = new Vector2(float.Parse(xy[0], inv), float.Parse(xy[1], inv));
                    break;
                case "screenshot": args.Screenshot = value; break;
                case "perf": args.Perf = true; break;
            }
        }
        return args;
    }
}
