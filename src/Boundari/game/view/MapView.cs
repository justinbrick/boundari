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

    public bool ShowTerritory
    {
        get => (bool)_material.GetShaderParameter("show_territory");
        set => _material.SetShaderParameter("show_territory", value);
    }

    /// <summary>Soft shading that makes hills and mountains stand out.</summary>
    public bool ShowHillshade
    {
        get => (bool)_material.GetShaderParameter("show_hillshade");
        set => _material.SetShaderParameter("show_hillshade", value);
    }

    /// <summary>Elevation contour lines.</summary>
    public bool ShowContours
    {
        get => (bool)_material.GetShaderParameter("show_contours");
        set => _material.SetShaderParameter("show_contours", value);
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

        // Two half-floats per tile: coast field and elevation.
        var terrain = new byte[w * h * 4];
        var span = terrain.AsSpan();
        for (int i = 0; i < map.TileCount; i++)
        {
            BinaryPrimitives.WriteHalfLittleEndian(span.Slice(i * 4, 2), (Half)map.CoastField[i]);
            BinaryPrimitives.WriteHalfLittleEndian(span.Slice(i * 4 + 2, 2), (Half)map.Elevation[i]);
        }
        var terrainTexture = ImageTexture.CreateFromImage(Image.CreateFromData(w, h, false, Image.Format.Rgh, terrain));
        Texture = terrainTexture; // also sizes the sprite: 1 world unit per tile
        _material.SetShaderParameter("terrain_tex", terrainTexture);

        _material.SetShaderParameter("map_size", new Vector2(w, h));
        _material.SetShaderParameter("metres_per_tile", (float)((map.Geo?.KmPerTile(h) ?? 2.5) * 1000));
        UploadCover(map);

        _territory = new TerritoryTextures(map);
        _material.SetShaderParameter("owner_tex", _territory.OwnerArray);
        _material.SetShaderParameter("capture_tex", _territory.TimeArray);
        _material.SetShaderParameter("owner_chunk", TerritoryTextures.ChunkSize);
        _material.SetShaderParameter("owner_chunks_x", _territory.ChunksX);
        SetOwners(null);
    }

    /// <summary>Shows these owners immediately (pass null to clear all territory).</summary>
    public void SetOwners(ushort[]? owners) => _territory?.SetAll(owners);

    /// <summary>
    /// Records tiles that changed hands during one simulation tick; they'll be animated
    /// in as the visual clock passes that tick. Call <see cref="FlushTerritory"/> after.
    /// </summary>
    public void RecordTick(ushort[] owners, System.Collections.Generic.List<int> changed, int tick) =>
        _territory?.RecordTick(owners, changed, tick);

    /// <summary>Uploads changed territory chunks to the GPU.</summary>
    public void FlushTerritory() => _territory?.Flush();

    /// <summary>
    /// The moment being drawn, in ticks. Kept one tick behind the simulation so captures
    /// from the latest tick can be swept in smoothly instead of popping in all at once.
    /// </summary>
    public void SetVisualTime(float ticks) => _material.SetShaderParameter("visual_time", ticks);

    /// <summary>Sets colours by owner id (index 0 is unclaimed land and is ignored).</summary>
    public void SetPalette(ReadOnlySpan<Color> colors)
    {
        int count = Math.Min(colors.Length, PaletteSize * PaletteSize);
        for (int id = 1; id < count; id++)
            _paletteImage.SetPixel(id % PaletteSize, id / PaletteSize, colors[id]);
        _paletteTexture.Update(_paletteImage);
    }

    private void UploadCover(GameMap map)
    {
        var cover = map.Cover;
        _material.SetShaderParameter("has_cover", cover is not null);
        if (cover is null)
            return;

        var cells = new byte[cover.Cells.Length];
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(cover.Cells.AsSpan()).CopyTo(cells);
        var coverTexture = ImageTexture.CreateFromImage(Image.CreateFromData(cover.Width, cover.Height, false, Image.Format.R8, cells));
        _material.SetShaderParameter("cover_tex", coverTexture);
        _material.SetShaderParameter("cover_scale", (float)cover.Scale);

        // Average colour of each tile's cells, for viewing from far away (with mipmaps).
        int w = map.Width, h = map.Height, s = cover.Scale;
        var average = new byte[w * h * 4];
        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                float r = 0, g = 0, b = 0;
                int n = 0;
                for (int cy = y * s; cy < (y + 1) * s; cy++)
                    for (int cx = x * s; cx < (x + 1) * s; cx++)
                    {
                        var type = cover.Cells[cover.Index(cx, cy)];
                        if (type == CoverType.None)
                            continue;
                        var c = CoverColors[(int)type];
                        r += c.R; g += c.G; b += c.B;
                        n++;
                    }
                var fallback = CoverColors[(int)CoverType.Farmland];
                int i = (y * w + x) * 4;
                average[i] = ToByte(n > 0 ? r / n : fallback.R);
                average[i + 1] = ToByte(n > 0 ? g / n : fallback.G);
                average[i + 2] = ToByte(n > 0 ? b / n : fallback.B);
                average[i + 3] = 255;
            }
        });
        var averageImage = Image.CreateFromData(w, h, false, Image.Format.Rgba8, average);
        averageImage.GenerateMipmaps();
        _material.SetShaderParameter("cover_avg_tex", ImageTexture.CreateFromImage(averageImage));
    }

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
