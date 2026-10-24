using System;
using Boundari.Map;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// Background sounds that follow the camera: what's on screen (sea, green land, barren land,
/// irradiated land) and how far in you're zoomed decide how loud each one is. The sounds and
/// their settings live in audio/ambient_sounds.tscn: this node is its root, and each child is
/// an <see cref="AmbientLayer"/> (one sound). Everything here can be changed in the editor's
/// inspector, and also while the game runs (Remote tab), which applies at once.
/// </summary>
[GlobalClass]
public partial class AmbientSounds : Node
{
    /// <summary>Turns every ambient sound up or down together.</summary>
    [Export(PropertyHint.Range, "-40,12,0.5,suffix:dB")]
    public float MasterVolumeDb { get; set; }

    /// <summary>How quickly the sounds follow the camera, in seconds (bigger = slower, smoother).</summary>
    [Export(PropertyHint.Range, "0.1,5,0.1,suffix:s")]
    public float FollowSeconds { get; set; } = 1.2f;

    // Zoom, as with --zoom: 1 = the whole map fits the screen, 10 = ten times closer.
    // Between two of these the sounds blend from one band into the next.

    /// <summary>Zoomed out at least this far: fully "far".</summary>
    [ExportGroup("Zoom bands (1 = whole map on screen)")]
    [Export(PropertyHint.Range, "1,50,0.1")]
    public float FarZoom { get; set; } = 2.5f;

    /// <summary>Fully "mid" at exactly this zoom.</summary>
    [Export(PropertyHint.Range, "1,100,0.1")]
    public float MidZoom { get; set; } = 7f;

    /// <summary>Zoomed in at least this far: fully "close".</summary>
    [Export(PropertyHint.Range, "1,300,0.5")]
    public float CloseZoom { get; set; } = 18f;

    /// <summary>The game whose map and fallout decide what's on screen (set by World).</summary>
    public Game? Game { get; set; }

    /// <summary>Waits for the simulation thread before reading the game (set by World).</summary>
    public Action? WaitForSim { get; set; }

    private const int SampleColumns = 24, SampleRows = 14;
    private const double SampleSeconds = 0.25;

    private double _sampleTimer;
    private Mix _mix;

    /// <summary>What's on screen, as shares of the map visible (0..1), and the zoom bands' weights.</summary>
    private struct Mix
    {
        public float Far, Mid, Close;
        public float Land, Barren, Green, Ocean, Irradiated;
    }

    public override void _Process(double delta)
    {
        if (Game is null)
            return;
        if ((_sampleTimer -= delta) <= 0)
        {
            _sampleTimer = SampleSeconds;
            _mix = Sample(Game);
        }
        float follow = 1 - MathF.Exp(-(float)delta / Math.Max(FollowSeconds, 0.01f));
        foreach (var child in GetChildren())
            if (child is AmbientLayer layer)
                layer.Advance(Loudness(layer.Places), follow, MasterVolumeDb);
    }

    /// <summary>How loud a sound should be (0..1) for the places it plays in.</summary>
    private float Loudness(AmbientPlaces places)
    {
        // An area's share of the screen counts as its square root: half the screen of sea
        // already sounds like the sea.
        static float Share(float s) => MathF.Sqrt(Math.Clamp(s, 0, 1));
        float sum = 0;
        if (places.HasFlag(AmbientPlaces.FarAnywhere)) sum += _mix.Far;
        if (places.HasFlag(AmbientPlaces.MidLand)) sum += _mix.Mid * Share(_mix.Land);
        if (places.HasFlag(AmbientPlaces.CloseBarren)) sum += _mix.Close * Share(_mix.Barren);
        if (places.HasFlag(AmbientPlaces.CloseGreenLand)) sum += _mix.Close * Share(_mix.Green);
        if (places.HasFlag(AmbientPlaces.CloseOcean)) sum += _mix.Close * Share(_mix.Ocean);
        if (places.HasFlag(AmbientPlaces.CloseIrradiated)) sum += _mix.Close * Share(_mix.Irradiated);
        return Math.Min(sum, 1);
    }

    /// <summary>Looks at a grid of points across the screen and the camera's zoom.</summary>
    private Mix Sample(Game game)
    {
        var map = game.Map;
        var viewport = GetViewport();
        var screen = viewport.GetVisibleRect();
        var toMap = viewport.GetCanvasTransform().AffineInverse();
        var mix = new Mix();

        // Zoom relative to the whole map fitting the screen (like World.FrameCamera).
        float fit = Mathf.Min(screen.Size.X / map.Width, screen.Size.Y / map.Height) * 0.95f;
        float zoom = viewport.GetCamera2D()?.Zoom.X ?? fit;
        float z = MathF.Log(Math.Max(zoom / fit, 0.01f));
        float far = MathF.Log(Math.Max(FarZoom, 0.01f));
        float mid = MathF.Log(Math.Max(MidZoom, FarZoom + 0.01f));
        float close = MathF.Log(Math.Max(CloseZoom, MidZoom + 0.01f));
        mix.Far = Math.Clamp((mid - z) / (mid - far), 0, 1);
        mix.Close = Math.Clamp((z - mid) / (close - mid), 0, 1);
        mix.Mid = 1 - mix.Far - mix.Close;

        WaitForSim?.Invoke(); // (reads the fallout)
        int seen = 0, land = 0, barren = 0, ocean = 0, irradiated = 0;
        for (int row = 0; row < SampleRows; row++)
            for (int column = 0; column < SampleColumns; column++)
            {
                var at = toMap * (screen.Position + screen.Size * new Vector2((column + 0.5f) / SampleColumns, (row + 0.5f) / SampleRows));
                int x = (int)MathF.Floor(at.X), y = (int)MathF.Floor(at.Y);
                if (x < 0 || y < 0 || x >= map.Width || y >= map.Height)
                    continue; // off the map: nothing
                int tile = y * map.Width + x;
                seen++;
                if (map.Terrain[tile] == TerrainType.Ocean)
                    ocean++;
                else if (map.IsLand(tile))
                {
                    land++;
                    if (game.Fallout[tile])
                        irradiated++;
                    else if (IsBarren(map, x, y))
                        barren++;
                }
            }
        if (seen > 0)
        {
            mix.Land = (float)land / seen;
            mix.Barren = (float)barren / seen;
            mix.Irradiated = (float)irradiated / seen;
            mix.Green = (float)(land - barren - irradiated) / seen;
            mix.Ocean = (float)ocean / seen;
        }
        return mix;
    }

    /// <summary>Bare ground or ice (the land cover's Rock and Snow): the nearest thing to desert.</summary>
    private static bool IsBarren(GameMap map, int x, int y)
    {
        if (map.Cover is not { } cover)
            return false;
        int cell = cover.Index(x * cover.Scale + cover.Scale / 2, y * cover.Scale + cover.Scale / 2);
        return cover.Cells[cell] is CoverType.Rock or CoverType.Snow;
    }
}

/// <summary>Where (and at which zoom) an ambient sound plays. A sound can have several.</summary>
[Flags]
public enum AmbientPlaces
{
    FarAnywhere = 1,
    MidLand = 2,
    CloseBarren = 4,      // bare ground and ice (deserts)
    CloseGreenLand = 8,   // land that isn't barren or irradiated
    CloseOcean = 16,
    CloseIrradiated = 32,
}
