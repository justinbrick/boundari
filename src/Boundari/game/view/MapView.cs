using System;
using System.Buffers.Binary;
using Boundari.Map;
using Godot;

namespace Boundari.View;

/// <summary>
/// Draws a <see cref="GameMap"/> with map.gdshader. Uploads the map's smooth
/// fields and the per-tile owner grid as textures; the shader turns them into
/// curved coastlines and borders.
/// </summary>
public partial class MapView : Sprite2D
{
    public const ushort Unowned = 0;
    public const ushort Water = ushort.MaxValue;

    private const int PaletteSize = 256; // 256x256 = one colour per possible owner id

    private ShaderMaterial _material = null!;
    private GameMap? _map;

    private Image _ownerImage = null!;
    private ImageTexture _ownerTexture = null!;
    private byte[] _ownerBytes = Array.Empty<byte>();

    private Image _paletteImage = null!;
    private ImageTexture _paletteTexture = null!;

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

        _ownerBytes = new byte[w * h * 2];
        _ownerImage = Image.CreateFromData(w, h, false, Image.Format.Rg8, _ownerBytes);
        _ownerTexture = ImageTexture.CreateFromImage(_ownerImage);
        _material.SetShaderParameter("owner_tex", _ownerTexture);
        SetOwners(null);
    }

    /// <summary>
    /// Uploads who owns each tile. Pass null to clear all territory. Water tiles
    /// are always marked as water regardless of the array.
    /// </summary>
    public void SetOwners(ushort[]? owners)
    {
        if (_map is null)
            return;
        for (int i = 0; i < _map.TileCount; i++)
        {
            ushort id = _map.IsWater(i) ? Water : owners?[i] ?? Unowned;
            _ownerBytes[i * 2] = (byte)(id & 0xFF);
            _ownerBytes[i * 2 + 1] = (byte)(id >> 8);
        }
        _ownerImage.SetData(_map.Width, _map.Height, false, Image.Format.Rg8, _ownerBytes);
        _ownerTexture.Update(_ownerImage);
    }

    /// <summary>Sets colours by owner id (index 0 is unclaimed land and is ignored).</summary>
    public void SetPalette(ReadOnlySpan<Color> colors)
    {
        int count = Math.Min(colors.Length, PaletteSize * PaletteSize);
        for (int id = 1; id < count; id++)
            _paletteImage.SetPixel(id % PaletteSize, id / PaletteSize, colors[id]);
        _paletteTexture.Update(_paletteImage);
    }

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
