using System;
using System.Diagnostics;
using System.IO;
using Boundari.DevTools;
using Boundari.Map;
using Boundari.View;
using Godot;

namespace Boundari;

/// <summary>
/// Root of the game scene: loads or generates the map, shows it, and frames the camera.
///
/// Keys (temporary, for testing):
///   M — switch between the built-in maps (Europe, World)
///   R — new random map
///   T — toggle demo territories (fake nations, to preview borders)
///   H — toggle hill shading
///   C — toggle contour lines
///
/// Command-line options (after "--" when launching Godot):
///   --map=ID           load res://maps/ID.bmap ("random" for a generated map)
///   --seed=N           generate a random map with this seed
///   --demo             start with demo territories on
///   --no-hillshade     start with hill shading off
///   --no-contours      start with contour lines off
///   --zoom=Z           start zoomed in (1 = whole map)
///   --focus=X,Y        centre the camera on this point, as 0..1 fractions of the map
///   --screenshot=PATH  save a screenshot after loading, then quit
/// </summary>
public partial class World : Node2D
{
    /// <summary>Map to load at start (res://maps/ID.bmap). Empty means a random map.</summary>
    [Export] public string StartMap { get; set; } = "europe";
    [Export] public ulong Seed { get; set; }
    [Export] public int MapWidth { get; set; } = 2000;
    [Export] public int MapHeight { get; set; } = 1250;
    [Export] public int DemoNations { get; set; } = 40;

    private static readonly string[] BuiltInMaps = { "europe", "world" };

    private MapView _view = null!;
    private Camera2D _camera = null!;
    private GameMap _map = null!;
    private bool _demo;
    private string _mapId = "";

    public override void _Ready()
    {
        _view = GetNode<MapView>("MapDisplay");
        _camera = GetNode<Camera2D>("Camera2D");
        _view.SetPalette(MakePalette(4096));

        var args = ParseArgs();
        _demo = args.Demo;
        _view.ShowHillshade = !args.NoHillshade;
        _view.ShowContours = !args.NoContours;
        string mapId = args.Seed is not null ? "random" : args.Map ?? StartMap;
        if (mapId is "" or "random" || !LoadMap(mapId))
            Generate(args.Seed ?? (Seed != 0 ? Seed : RandomSeed()));
        FrameCamera(args.Zoom, args.Focus);

        if (args.Screenshot is not null)
            SaveScreenshotAndQuit(args.Screenshot);
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
            case Key.T:
                _demo = !_demo;
                ApplyDemo();
                break;
            case Key.H:
                _view.ShowHillshade = !_view.ShowHillshade;
                break;
            case Key.C:
                _view.ShowContours = !_view.ShowContours;
                break;
        }
    }

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
        ApplyDemo();

        int large = _map.CountLandmasses((int)(_map.LandTileCount * 0.04));
        GD.Print($"{title}: {_map.Width}x{_map.Height}, {large} large landmasses, {_map.LandmassSizes.Length} total, " +
                 $"{100.0 * _map.LandTileCount / _map.TileCount:F0}% land, ready in {loadMs} ms");
        DisplayServer.WindowSetTitle($"Boundari — {title}");
    }

    private void ApplyDemo()
    {
        _view.SetOwners(_demo ? DemoTerritories.Generate(_map, _map.Seed, DemoNations) : null);
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

    private record struct Args(string? Map, ulong? Seed, bool Demo, bool NoHillshade, bool NoContours, float Zoom, Vector2? Focus, string? Screenshot);

    private static Args ParseArgs()
    {
        var args = new Args(null, null, false, false, false, 1, null, null);
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            string[] kv = arg.TrimStart('-').Split('=', 2);
            string value = kv.Length > 1 ? kv[1] : "";
            switch (kv[0])
            {
                case "map": args.Map = value; break;
                case "seed": args.Seed = ulong.Parse(value); break;
                case "demo": args.Demo = true; break;
                case "no-hillshade": args.NoHillshade = true; break;
                case "no-contours": args.NoContours = true; break;
                case "zoom": args.Zoom = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "focus":
                    string[] xy = value.Split(',');
                    args.Focus = new Vector2(
                        float.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture),
                        float.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case "screenshot": args.Screenshot = value; break;
            }
        }
        return args;
    }
}
