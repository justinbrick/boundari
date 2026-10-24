using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Boundari.Map;
using Godot;

namespace Boundari.View;

/// <summary>
/// The greyed-out world drawn around a regional map (Europe), under it. Zoomed out it's the
/// whole world at low detail (world.bmap); zoomed in, full-detail pieces of it at the map's
/// own tile size (maps/world/{x}_{y}.wchunk, built by tools/MapBuilder worldbackdrop) are
/// loaded in the background for what's on screen, so the grey land matches the map's detail
/// right up to its edge. Only the pieces near the camera are kept.
/// </summary>
public partial class WorldBackdrop : Node2D
{
    private const int Chunk = 1024;               // tiles per piece side (as Backdrop.Chunk in the map builder)
    private const string Folder = "res://maps/world";
    private const float ShowFromPixelsPerTile = 0.3f; // closer than this the low-detail world looks blurry
    private const int MaxPieces = 40;             // pieces kept loaded
    private const int MaxLoading = 3;             // pieces loading at once
    private const float FieldPerTile = 0.004f;    // coast field units per tile (as the map builder's)

    private MapView _far = null!;
    private Node2D _near = null!;
    private readonly HashSet<(int X, int Y)> _available = new();
    private readonly Dictionary<(int X, int Y), Piece> _pieces = new();
    private readonly HashSet<(int X, int Y)> _loading = new();
    private GameMap? _region;
    private Rect2 _worldRect;
    private double _sinceCheck;
    private long _frame;

    private sealed class Piece
    {
        public Sprite2D Sprite = null!;
        public long LastSeen;
    }

    /// <summary>The whole world's size and place, in the region's tiles.</summary>
    public Rect2 WorldRect => _worldRect;

    public override void _Ready()
    {
        _far = new MapView { Name = "Far" };
        AddChild(_far);
        _near = new Node2D { Name = "Near" };
        AddChild(_near);
        using var dir = DirAccess.Open(Folder);
        if (dir is null)
            return; // (no full-detail pieces: the low-detail world alone)
        foreach (string file in dir.GetFiles())
        {
            string name = file.EndsWith(".remap") ? file[..^6] : file;
            if (!name.EndsWith(".wchunk"))
                continue;
            string[] parts = name[..^7].Split('_');
            if (parts.Length == 2 && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y))
                _available.Add((x, y));
        }
    }

    /// <summary>Shows <paramref name="world"/> around <paramref name="region"/> (see the class notes).</summary>
    public void Show(GameMap world, GameMap region)
    {
        if (_region != region)
            Clear();
        _region = region;
        _far.ShowAsBackdrop(world, region);
        _worldRect = new Rect2(_far.Position, _far.Texture.GetSize() * _far.Scale);
        Visible = true;
    }

    /// <summary>No world around this map.</summary>
    public void HideWorld()
    {
        Visible = false;
        Clear();
        _region = null;
    }

    private void Clear()
    {
        foreach (var piece in _pieces.Values)
            piece.Sprite.QueueFree();
        _pieces.Clear();
    }

    public override void _Process(double delta)
    {
        _frame++;
        _sinceCheck += delta;
        if (_region is null || !Visible || _sinceCheck < 0.1)
            return;
        _sinceCheck = 0;

        // What's on screen, in the region's tiles.
        var canvas = GetViewport().GetCanvasTransform() * GetGlobalTransform();
        float pixelsPerTile = canvas.X.Length();
        bool near = pixelsPerTile >= ShowFromPixelsPerTile;
        _near.Visible = near;
        if (!near || _available.Count == 0)
            return;
        var inverse = canvas.AffineInverse();
        var size = GetViewport().GetVisibleRect().Size;
        Vector2 a = inverse * Vector2.Zero, b = inverse * size;
        int x0 = (int)Math.Floor(Math.Min(a.X, b.X) / Chunk), x1 = (int)Math.Floor(Math.Max(a.X, b.X) / Chunk);
        int y0 = (int)Math.Floor(Math.Min(a.Y, b.Y) / Chunk), y1 = (int)Math.Floor(Math.Max(a.Y, b.Y) / Chunk);

        // Nearest the middle first.
        var wanted = new List<(int X, int Y)>();
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                if (_available.Contains((x, y)))
                    wanted.Add((x, y));
        float mx = (x0 + x1) * 0.5f, my = (y0 + y1) * 0.5f;
        wanted.Sort((p, q) => (Math.Abs(p.X - mx) + Math.Abs(p.Y - my)).CompareTo(Math.Abs(q.X - mx) + Math.Abs(q.Y - my)));
        foreach (var key in wanted)
        {
            if (_pieces.TryGetValue(key, out var piece))
                piece.LastSeen = _frame;
            else if (!_loading.Contains(key) && _loading.Count < MaxLoading)
                Load(key);
        }

        // Too many kept: drop the ones seen longest ago.
        while (_pieces.Count > MaxPieces)
        {
            (int, int) oldest = default;
            long when = long.MaxValue;
            foreach (var (key, piece) in _pieces)
                if (piece.LastSeen < when)
                    (oldest, when) = (key, piece.LastSeen);
            _pieces[oldest].Sprite.QueueFree();
            _pieces.Remove(oldest);
        }
    }

    /// <summary>Reads and unpacks a piece on a worker thread, then adds it on the main thread.</summary>
    private void Load((int X, int Y) key)
    {
        _loading.Add(key);
        var region = _region!;
        string path = $"{Folder}/{key.X}_{key.Y}.wchunk";
        Task.Run(() =>
        {
            Image? image = null;
            try
            {
                image = Unpack(Godot.FileAccess.GetFileAsBytes(path));
            }
            catch (Exception e)
            {
                GD.PushWarning($"World backdrop piece {path}: {e.Message}");
            }
            Callable.From(() => Add(key, image, region)).CallDeferred();
        });
    }

    /// <summary>A piece's PNG (coast field, hill shading) as map.gdshader's terrain texture.</summary>
    private static Image? Unpack(byte[] png)
    {
        if (png.Length == 0)
            return null;
        var source = new Image();
        if (source.LoadPngFromBuffer(png) != Error.Ok)
            return null;
        if (source.GetFormat() != Image.Format.La8)
            source.Convert(Image.Format.La8);
        int side = source.GetWidth();
        byte[] la = source.GetData();
        var field = new float[side * side];
        for (int i = 0; i < field.Length; i++)
            field[i] = (la[i * 2] - 128) / 16f * FieldPerTile;
        var terrain = new byte[side * side * 8];
        var span = terrain.AsSpan();
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                int i = y * side + x;
                int l = y * side + Math.Max(x - 1, 0), r = y * side + Math.Min(x + 1, side - 1);
                int u = Math.Max(y - 1, 0) * side + x, d = Math.Min(y + 1, side - 1) * side + x;
                float slope = 0.5f * MathF.Sqrt((field[r] - field[l]) * (field[r] - field[l]) + (field[d] - field[u]) * (field[d] - field[u]));
                float shade = 0.45f + la[i * 2 + 1] / 255f * (1.4f - 0.45f);
                System.Buffers.Binary.BinaryPrimitives.WriteHalfLittleEndian(span.Slice(i * 8, 2), (Half)field[i]);
                System.Buffers.Binary.BinaryPrimitives.WriteHalfLittleEndian(span.Slice(i * 8 + 2, 2), (Half)0f);
                System.Buffers.Binary.BinaryPrimitives.WriteHalfLittleEndian(span.Slice(i * 8 + 4, 2), (Half)shade);
                System.Buffers.Binary.BinaryPrimitives.WriteHalfLittleEndian(span.Slice(i * 8 + 6, 2), (Half)slope);
            }
        return Image.CreateFromData(side, side, false, Image.Format.Rgbah, terrain);
    }

    private void Add((int X, int Y) key, Image? image, GameMap region)
    {
        _loading.Remove(key);
        if (image is null)
        {
            _available.Remove(key); // (broken: don't try again)
            return;
        }
        if (_region != region || _pieces.ContainsKey(key))
            return;

        int side = image.GetWidth();
        var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://game/view/map.gdshader") };
        MapView.ApplyBaseColors(material);
        var texture = ImageTexture.CreateFromImage(image);
        material.SetShaderParameter("terrain_tex", texture);
        material.SetShaderParameter("map_size", new Vector2(side, side));
        material.SetShaderParameter("backdrop", true);
        material.SetShaderParameter("show_territory", false);
        material.SetShaderParameter("shoreline_tiles", 0.32f * (float)region.TileScale);
        material.SetShaderParameter("shore_glow_tiles", 2.5f * (float)region.TileScale);

        // The piece's own tiles (inside its one-tile border), cut to the world's top and bottom.
        var own = new Rect2(key.X * Chunk, key.Y * Chunk, Chunk, Chunk);
        var shown = own.Intersection(_worldRect);
        if (shown.Size.X <= 0 || shown.Size.Y <= 0)
            return;
        var sprite = new Sprite2D
        {
            Texture = texture,
            Material = material,
            Centered = false,
            RegionEnabled = true,
            RegionRect = new Rect2(shown.Position - own.Position + Vector2.One, shown.Size),
            Position = shown.Position,
        };
        _near.AddChild(sprite);
        _pieces[key] = new Piece { Sprite = sprite, LastSeen = _frame };
    }
}
