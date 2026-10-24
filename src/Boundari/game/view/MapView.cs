using System;
using System.Buffers.Binary;
using Boundari.Map;
using Godot;

namespace Boundari.View;

/// <summary>
/// Draws a <see cref="GameMap"/> with map.gdshader. Uploads the map's smooth
/// fields, land cover and the per-tile owner grid as textures; the shader turns
/// them into curved coastlines, flat land-cover shapes and borders.
/// </summary>
public partial class MapView : Sprite2D
{

    private const int PaletteSize = 256; // 256x256 = one colour per possible owner id

    // Map colours, indexed by CoverType. Water matches the sea so small lakes look the same.
    private static readonly Color WaterColor = new(0.37f, 0.63f, 0.78f);
    private static readonly Color FalloutColor = new(0.13f, 0.17f, 0.10f);
    private static readonly Color[] CoverColors =
    {
        new(0.55f, 0.63f, 0.36f), // None (unused, falls back to farmland)
        new(0.55f, 0.63f, 0.36f), // Farmland: green
        new(0.75f, 0.78f, 0.55f), // Meadow: light green-beige
        new(0.37f, 0.43f, 0.22f), // Forest: olive
        new(0.71f, 0.67f, 0.58f), // Rock: grey-beige
        new(0.96f, 0.96f, 0.95f), // Snow: white
        WaterColor,               // Water: flat blue
    };

    private ShaderMaterial _material = null!;
    private GameMap? _map;

    private TerritoryTextures? _territory;

    private Image _paletteImage = null!;
    private ImageTexture _paletteTexture = null!;

    private int _hoverId = -1;

    /// <summary>The owner whose territory is drawn lighter (under the mouse), or -1 for none.</summary>
    public int HoverId
    {
        set
        {
            if (value == _hoverId)
                return;
            _hoverId = value;
            _material.SetShaderParameter("hover_id", value);
        }
    }

    private int _targetId = -1;

    /// <summary>The owner whose whole territory is highlighted as a directed attack's target while its arrow is dragged, or -1.</summary>
    public int TargetId
    {
        set
        {
            if (value == _targetId)
                return;
            _targetId = value;
            _material.SetShaderParameter("target_id", value);
        }
    }

    public bool ShowTerritory
    {
        get => (bool)_material.GetShaderParameter("show_territory");
        set => _material.SetShaderParameter("show_territory", value);
    }

    public override void _Ready()
    {
        Centered = false;
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://game/view/map.gdshader") };
        Material = _material;

        _paletteImage = Image.CreateEmpty(PaletteSize, PaletteSize, false, Image.Format.Rgba8);
        _paletteTexture = ImageTexture.CreateFromImage(_paletteImage);
        _material.SetShaderParameter("palette_tex", _paletteTexture);

        var colors = new Vector3[CoverColors.Length];
        for (int i = 0; i < colors.Length; i++)
            colors[i] = new Vector3(CoverColors[i].R, CoverColors[i].G, CoverColors[i].B);
        _material.SetShaderParameter("cover_colors", colors);
        _material.SetShaderParameter("water_color", WaterColor);
    }

    public void ShowMap(GameMap map)
    {
        _map = map;
        int w = map.Width, h = map.Height;

        // Four half-floats per tile: coast field, elevation, hill shade, and how steep the
        // coast field is (precomputed here so the shader needn't sample the neighbours).
        double metresPerTile = (map.Geo?.KmPerTile(h) ?? 2.5) * 1000;
        var terrain = new byte[w * h * 8];
        var span = terrain.AsSpan();
        for (int i = 0; i < map.TileCount; i++)
        {
            int x = i % w, y = i / w;
            int l = y * w + Math.Max(x - 1, 0), r = y * w + Math.Min(x + 1, w - 1);
            int u = Math.Max(y - 1, 0) * w + x, d = Math.Min(y + 1, h - 1) * w + x;
            BinaryPrimitives.WriteHalfLittleEndian(span.Slice(i * 8, 2), (Half)map.CoastField[i]);
            BinaryPrimitives.WriteHalfLittleEndian(span.Slice(i * 8 + 2, 2), (Half)map.Elevation[i]);
            BinaryPrimitives.WriteHalfLittleEndian(span.Slice(i * 8 + 4, 2), (Half)HillShade(map, l, r, u, d, metresPerTile));
            float slope = 0.5f * MathF.Sqrt(Sq(map.CoastField[r] - map.CoastField[l]) + Sq(map.CoastField[d] - map.CoastField[u]));
            BinaryPrimitives.WriteHalfLittleEndian(span.Slice(i * 8 + 6, 2), (Half)slope);
        }
        var terrainTexture = ImageTexture.CreateFromImage(Image.CreateFromData(w, h, false, Image.Format.Rgbah, terrain));
        Texture = terrainTexture; // also sizes the sprite: 1 world unit per tile
        _material.SetShaderParameter("terrain_tex", terrainTexture);

        _material.SetShaderParameter("map_size", new Vector2(w, h));
        // Shorelines are sized in tiles: keep them the same on the ground on finer maps.
        _material.SetShaderParameter("shoreline_tiles", 0.32f * (float)map.TileScale);
        _material.SetShaderParameter("shore_glow_tiles", 2.5f * (float)map.TileScale);
        UploadCover(map);
        UploadPlayArea(map);

        _territory = new TerritoryTextures(map);
        _material.SetShaderParameter("owner_tex", _territory.OwnerArray);
        _material.SetShaderParameter("capture_tex", _territory.TimeArray);
        _material.SetShaderParameter("hatch_tex", _territory.HatchArray);
        _material.SetShaderParameter("dist_units", TerritoryTextures.DistUnits);
        BorderWidth = _borderTiles; // (the new material starts at the shader's defaults)
        _hatchPeriod = 0; // (set on the next frame)
        _lastScreen = null;
        _material.SetShaderParameter("hatch_settle_ticks", (float)Boundari.Sim.HatchRules.SettleTicks);
        _material.SetShaderParameter("defense_cell_tiles", (float)map.TileScale);
        _material.SetShaderParameter("defense_count", 0);
        _defensePosts.Clear();
        DefensePreview = null;
        _material.SetShaderParameter("owner_chunk", TerritoryTextures.ChunkSize);
        _material.SetShaderParameter("owner_chunks_x", _territory.ChunksX);
        SetOwners(null);
    }

    /// <summary>
    /// Uploads the play area's edge field (if the map has one) for the shader to grey out
    /// what's past it: red = land, green = water, each 128 + 16 per tile inside the edge.
    /// </summary>
    private void UploadPlayArea(GameMap map)
    {
        byte[]? play = map.PlayField;
        if (play is null)
        {
            // (an older map: just the outside tiles, so the edge follows the tiles)
            bool any = false;
            play = new byte[map.TileCount * 2];
            for (int i = 0; i < map.TileCount; i++)
            {
                bool outside = map.IsOutside(i);
                play[i * 2] = play[i * 2 + 1] = outside ? (byte)112 : (byte)144;
                any |= outside;
            }
            if (!any)
            {
                _material.SetShaderParameter("has_play_area", false);
                _material.SetShaderParameter("play_tex", default);
                return;
            }
        }
        _material.SetShaderParameter("has_play_area", true);
        _material.SetShaderParameter("play_tex", ImageTexture.CreateFromImage(Image.CreateFromData(map.Width, map.Height, false, Image.Format.Rg8, play)));
    }

    /// <summary>
    /// Shows <paramref name="world"/> greyed out as the backdrop of a regional map (drawn under
    /// it), stretched onto the region's projection so the coasts carry on past its edges.
    /// </summary>
    public void ShowAsBackdrop(GameMap world, GameMap region)
    {
        ShowMap(world);
        _material.SetShaderParameter("backdrop", true);
        _material.SetShaderParameter("show_territory", false);
        var w = world.Geo!;
        var r = region.Geo!;
        double x0 = r.TileX(w.LonMin, region.Width), x1 = r.TileX(w.LonMax, region.Width);
        double y0 = r.TileY(w.LatMax, region.Height), y1 = r.TileY(w.LatMin, region.Height);
        Position = new Vector2((float)x0, (float)y0);
        Scale = new Vector2((float)((x1 - x0) / world.Width), (float)((y1 - y0) / world.Height));
        // Shorelines are sized in the backdrop's (much bigger) tiles: match the region's on screen.
        float perTile = Math.Min(Scale.X, Scale.Y);
        _material.SetShaderParameter("shoreline_tiles", 0.32f * (float)region.TileScale / perTile);
        _material.SetShaderParameter("shore_glow_tiles", 2.5f * (float)region.TileScale / perTile);
        Visible = true;
    }

    /// <summary>The greyed-out colour of the open sea on a backdrop (for the space past the world's edges).</summary>
    public static Color BackdropSea
    {
        get
        {
            float lum = WaterColor.R * 0.299f + WaterColor.G * 0.587f + WaterColor.B * 0.114f;
            float Grey(float c) => (lum + (c - lum) * 0.22f) * 0.66f + 0.05f; // as map.gdshader's backdrop_grey
            return new Color(Grey(WaterColor.R), Grey(WaterColor.G), Grey(WaterColor.B));
        }
    }

    // Hill shading: how much the land is exaggerated, and where the light comes from (north-west).
    private const double ReliefExaggeration = 5;
    private static readonly Vector3 LightDir = new Vector3(-1, -1, 1.5f).Normalized();

    private static float Sq(float v) => v * v;

    /// <summary>Brightness of a tile's slope under the light (1 = flat), from its neighbours' heights.</summary>
    private static float HillShade(GameMap map, int left, int right, int up, int down, double metresPerTile)
    {
        double gx = (ElevationScale.ToMetres(map.Elevation[right]) - ElevationScale.ToMetres(map.Elevation[left])) * 0.5 / metresPerTile * ReliefExaggeration;
        double gy = (ElevationScale.ToMetres(map.Elevation[down]) - ElevationScale.ToMetres(map.Elevation[up])) * 0.5 / metresPerTile * ReliefExaggeration;
        var normal = new Vector3((float)-gx, (float)-gy, 1).Normalized();
        return Math.Clamp(normal.Dot(LightDir) / LightDir.Z, 0.45f, 1.4f);
    }

    // Hatched land's stripes repeat every this many OpenFront-sized tiles, or this many screen
    // pixels when that's wider (zoomed out), measured along x + y like the shader.
    private const float HatchStripeTiles = 4.5f;
    private const float HatchMinPixels = 22f;
    private double _hatchPeriod, _hatchOffset;
    private Transform2D? _lastScreen;

    public override void _Process(double delta) => UpdateHatching();

    /// <summary>
    /// Sizes the hatching's stripes for this frame's zoom. Zoomed in they're painted on the land
    /// (a fixed number of tiles apart). Zoomed out they keep one size on screen instead, all
    /// alike, so they never crowd into a haze; as the zoom changes they're shifted so the
    /// point the camera zooms around keeps its place in the stripes (no jumps or sliding there),
    /// and when panning they move with the land.
    /// </summary>
    private void UpdateHatching()
    {
        if (_map is null)
            return;
        var screen = GetScreenTransform();
        double zoom = screen.X.Length(); // screen pixels per tile
        if (zoom <= 0)
            return;
        double period = Math.Max(HatchStripeTiles * _map.TileScale, HatchMinPixels / zoom);
        if (period == _hatchPeriod)
        {
            _lastScreen = screen;
            return;
        }
        if (_lastScreen is { } last && _hatchPeriod > 0)
        {
            double lastZoom = last.X.Length();
            if (Math.Abs(zoom - lastZoom) > 1e-9)
            {
                // The map point that stayed put on screen through this zoom step.
                double fx = (screen.Origin.X - last.Origin.X) / (lastZoom - zoom);
                double fy = (screen.Origin.Y - last.Origin.Y) / (lastZoom - zoom);
                double s = fx + fy;
                _hatchOffset = s - (s - _hatchOffset) * period / _hatchPeriod;
            }
        }
        _hatchOffset -= Math.Floor(_hatchOffset / period) * period; // (only its place within a period matters)
        _hatchPeriod = period;
        _lastScreen = screen;
        _material.SetShaderParameter("hatch_period", (float)period);
        _material.SetShaderParameter("hatch_offset", (float)_hatchOffset);
    }

    /// <summary>Shows these owners immediately (pass null to clear all territory).</summary>
    public void SetOwners(ushort[]? owners, bool[]? fallout = null, int[]? hatched = null) => _territory?.SetAll(owners, fallout, hatched);

    /// <summary>
    /// Records tiles that changed hands during one simulation tick; they'll be animated
    /// in as the visual clock passes that tick. Call <see cref="FlushTerritory"/> after.
    /// </summary>
    public void RecordTick(ushort[] owners, bool[]? fallout, int[]? hatched, System.Collections.Generic.List<int> changed, int tick) =>
        _territory?.RecordTick(owners, fallout, hatched, changed, tick);

    private readonly System.Collections.Generic.List<(int Tile, int Owner)> _defensePosts = new();
    private Image? _defenseImage;
    private ImageTexture? _defenseTexture;

    /// <summary>
    /// The finished defense posts (tile, owner): their owner's land in range is darkened and
    /// its borders get a checkerboard. Only uploads when the list changed.
    /// </summary>
    public void SetDefensePosts(System.Collections.Generic.List<(int Tile, int Owner)> posts, int range, int mapWidth)
    {
        if (posts.Count == _defensePosts.Count && System.Linq.Enumerable.SequenceEqual(posts, _defensePosts))
            return;
        // The tiles near a post are marked, so the shader only looks for posts there.
        var now = new System.Collections.Generic.HashSet<int>();
        foreach (var p in posts)
            now.Add(p.Tile);
        var before = new System.Collections.Generic.HashSet<int>();
        foreach (var p in _defensePosts)
            before.Add(p.Tile);
        foreach (int tile in before)
            if (!now.Contains(tile))
                _territory?.AddPostReach(tile, range + PostReachMargin, -1);
        foreach (int tile in now)
            if (!before.Contains(tile))
                _territory?.AddPostReach(tile, range + PostReachMargin, +1);
        _defensePosts.Clear();
        _defensePosts.AddRange(posts);
        int capacity = 64;
        while (capacity < posts.Count)
            capacity *= 2;
        if (_defenseImage is null || _defenseImage.GetWidth() != capacity)
        {
            _defenseImage = Image.CreateEmpty(capacity, 1, false, Image.Format.Rgbf);
            _defenseTexture = ImageTexture.CreateFromImage(_defenseImage);
            _material.SetShaderParameter("defense_tex", _defenseTexture);
        }
        for (int i = 0; i < posts.Count; i++)
            _defenseImage.SetPixel(i, 0, new Color(posts[i].Tile % mapWidth, posts[i].Tile / mapWidth, posts[i].Owner));
        _defenseTexture!.Update(_defenseImage);
        _material.SetShaderParameter("defense_count", posts.Count);
        _material.SetShaderParameter("defense_range", (float)range);
    }

    private const int PostReachMargin = 2; // tiles past a post's range still marked (the field's soft edge)

    /// <summary>
    /// While placing a defense post: where it would go and for whom (tile, owner), so the
    /// land it would protect is shown darkened; null = none.
    /// </summary>
    public (int Tile, int Owner)? DefensePreview
    {
        set
        {
            if (value == _defensePreview)
                return;
            _defensePreview = value;
            if (_map is null || value is not { } p)
            {
                _material.SetShaderParameter("defense_preview_on", false);
                return;
            }
            _material.SetShaderParameter("defense_preview", new Vector3(p.Tile % _map.Width, p.Tile / _map.Width, p.Owner));
            _material.SetShaderParameter("defense_preview_on", true);
        }
    }
    private (int Tile, int Owner)? _defensePreview;


    /// <summary>Uploads changed territory chunks to the GPU.</summary>
    public void FlushTerritory() => _territory?.Flush();

    /// <summary>
    /// The moment being drawn, in ticks. Kept one tick behind the simulation so captures
    /// from the latest tick can be swept in smoothly instead of popping in all at once.
    /// </summary>
    public void SetVisualTime(float ticks)
    {
        _material.SetShaderParameter("visual_time", ticks);
        if (_territory is not null)
            _territory.VisualTime = ticks;
    }

    /// <summary>Sets colours by owner id (index 0 is unclaimed land and is ignored).</summary>
    public void SetPalette(ReadOnlySpan<Color> colors)
    {
        int count = Math.Min(colors.Length, TerritoryTextures.MaxPlayerIds);
        for (int id = 1; id < count; id++)
            _paletteImage.SetPixel(id % PaletteSize, id / PaletteSize, colors[id]);
        // Fallout: scorched, sickly olive.
        _paletteImage.SetPixel(TerritoryTextures.Fallout % PaletteSize, TerritoryTextures.Fallout / PaletteSize, FalloutColor);
        _paletteTexture.Update(_paletteImage);
    }

    private void UploadCover(GameMap map)
    {
        var cover = map.Cover;
        _material.SetShaderParameter("has_cover", cover is not null);
        _coverCells = Array.Empty<byte>();
        if (cover is null)
            return;

        var cells = new byte[cover.Cells.Length];
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(cover.Cells.AsSpan()).CopyTo(cells);
        _coverLevels = new ImageTexture?[3];
        _coverCells = cells;
        _coverSize = (cover.Width, cover.Height, cover.Scale);
        _patchScale = -1;
        SetPatchScale(_wantedPatchScale);

        // Average colour of each tile's cells (and, in alpha, how much is water), for
        // viewing from far away (with mipmaps).
        int w = map.Width, h = map.Height, s = cover.Scale;
        var average = new byte[w * h * 4];
        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float r = 0, g = 0, b = 0;
                int n = 0, water = 0;
                for (int cy = y * s; cy < (y + 1) * s; cy++)
                    for (int cx = x * s; cx < (x + 1) * s; cx++)
                    {
                        var type = cover.Cells[cover.Index(cx, cy)];
                        if (type == CoverType.None)
                            continue;
                        if (type == CoverType.Water)
                            water++;
                        var c = CoverColors[(int)type];
                        r += c.R; g += c.G; b += c.B;
                        n++;
                    }
                var fallback = CoverColors[(int)CoverType.Farmland];
                int i = (y * w + x) * 4;
                average[i] = ToByte(n > 0 ? r / n : fallback.R);
                average[i + 1] = ToByte(n > 0 ? g / n : fallback.G);
                average[i + 2] = ToByte(n > 0 ? b / n : fallback.B);
                average[i + 3] = ToByte(n > 0 ? (float)water / n : 0); // share of small water, for the shader
            }
        });
        var averageImage = Image.CreateFromData(w, h, false, Image.Format.Rgba8, average);
        averageImage.GenerateMipmaps();
        _material.SetShaderParameter("cover_avg_tex", ImageTexture.CreateFromImage(averageImage));
    }

    // --- Dev tools (see DevTools.cs) ---

    private byte[] _coverCells = Array.Empty<byte>();
    private (int Width, int Height, int Scale) _coverSize;
    private ImageTexture?[] _coverLevels = new ImageTexture?[3];
    private int _patchScale = -1, _wantedPatchScale;

    /// <summary>
    /// Land-cover patch size: 0 = the map's own detail, 1 = patches merged 2x2, 2 = merged 4x4
    /// (each merged cell takes the most common cover of its block). Built when first chosen.
    /// </summary>
    public void SetPatchScale(int level)
    {
        _wantedPatchScale = level;
        if (_coverCells.Length == 0 || level == _patchScale)
            return;
        _patchScale = level;
        int factor = 1 << level;
        _coverLevels[level] ??= ImageTexture.CreateFromImage(level == 0
            ? Image.CreateFromData(_coverSize.Width, _coverSize.Height, false, Image.Format.R8, _coverCells)
            : MergedCover(factor));
        _material.SetShaderParameter("cover_tex", _coverLevels[level]!);
        _material.SetShaderParameter("cover_scale", (float)_coverSize.Scale / factor);
    }

    private Image MergedCover(int factor)
    {
        int w = (_coverSize.Width + factor - 1) / factor, h = (_coverSize.Height + factor - 1) / factor;
        var merged = new byte[w * h];
        var cells = _coverCells;
        int srcW = _coverSize.Width, srcH = _coverSize.Height;
        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            Span<int> counts = stackalloc int[8];
            for (int x = 0; x < w; x++)
            {
                counts.Clear();
                for (int cy = y * factor; cy < Math.Min((y + 1) * factor, srcH); cy++)
                    for (int cx = x * factor; cx < Math.Min((x + 1) * factor, srcW); cx++)
                        counts[Math.Min((int)cells[cy * srcW + cx], 7)]++;
                int best = 0;
                for (int k = 1; k < 8; k++) // most common cover ("none" only if nothing else)
                    if (counts[k] > counts[best] || (best == 0 && counts[k] > 0))
                        best = k;
                merged[y * w + x] = (byte)best;
            }
        });
        return Image.CreateFromData(w, h, false, Image.Format.R8, merged);
    }

    public float PatchShape { set => _material.SetShaderParameter("patch_shape", value); }
    public float PatchMorph { set => _material.SetShaderParameter("patch_morph", value); }
    public float TerritoryOpacity { set => _material.SetShaderParameter("territory_opacity", value); }

    /// <summary>
    /// Border thickness in tiles (from the line between two territories inwards). Up to
    /// ThinBorderTiles the shader measures it from its own border smoothing (the vote lead
    /// between owners, about erf(distance / sqrt 2)); thicker borders also use each tile's
    /// distance to its border, which TerritoryTextures then keeps up to date.
    /// </summary>
    public float BorderWidth
    {
        set
        {
            _borderTiles = value;
            float edge = Erf(Math.Min(value, ThinBorderTiles) / MathF.Sqrt(2)); // (border_smoothing is 1 tile)
            bool thick = value > ThinBorderTiles;
            _material.SetShaderParameter("border_width", edge);
            _material.SetShaderParameter("coast_border_width", thick ? value : edge * 2);
            _material.SetShaderParameter("border_tiles", value);
            _material.SetShaderParameter("border_field", thick);
            SetBorderDistances(thick);
        }
    }

    private void SetBorderDistances(bool on)
    {
        if (_territory is null)
            return;
        _territory.BorderDistances = on;
        if (_territory.DistArray is { } distances)
            _material.SetShaderParameter("dist_tex", distances);
    }
    private float _borderTiles = DefaultBorderTiles;

    /// <summary>The border thickness the game uses (1.6 tiles; was the dev tools' BRD setting).</summary>
    public const float DefaultBorderTiles = 1.645f;

    // The thickest border measured from the smoothing's vote lead (0.85 here). Past this, its
    // shortcut for pixels deep inside a territory (a sparse ring of tiles 2 away) starts to
    // miss border tiles and leaves tile-sized notches, so thicker ones use the distances.
    public const float ThinBorderTiles = 1.44f;

    /// <summary>The error function (Abramowitz and Stegun 7.1.26, good to about 1e-7).</summary>
    private static float Erf(float x)
    {
        float t = 1 / (1 + 0.3275911f * MathF.Abs(x));
        float y = 1 - ((((1.061405429f * t - 1.453152027f) * t + 1.421413741f) * t - 0.284496736f) * t + 0.254829592f) * t * MathF.Exp(-x * x);
        return MathF.Sign(x) * y;
    }

    /// <summary>Contour lines every this many metres (0 = off).</summary>
    public float ContourInterval { set => _material.SetShaderParameter("contour_interval", value); }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    /// <summary>World position (tile units) to tile index, or -1 if off the map.</summary>
    public int TileAt(Vector2 worldPos)
    {
        if (_map is null)
            return -1;
        Vector2 local = ToLocal(worldPos);
        int x = (int)MathF.Floor(local.X), y = (int)MathF.Floor(local.Y);
        return _map.InBounds(x, y) ? _map.Index(x, y) : -1;
    }
}
