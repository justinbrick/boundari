using System;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// The building icons layer: each building is one quad drawn by structure_icons.gdshader,
/// which works the shape out exactly (so it's crisp at any zoom) and lays the white glyph
/// over it. Shapes per the design notes: harbor pentagon, barracks square, depot hexagon,
/// defense circle, silo triangle, flak octagon. Drawn behind <see cref="MapOverlay"/>'s
/// own drawing (badges, ships, bombs) and above the rails layer.
/// </summary>
public partial class StructureIcons : Node2D
{
    public enum Mode
    {
        WithGlyph = 0,
        ShapeOnly = 1,
        PreviewValid = 2,
        PreviewInvalid = 3,
    }

    private const int Cell = 128;          // atlas cell size, in pixels (the glyphs are imported at 128)
    private const float Quad = 1.5f;       // quad size, in icon sizes (room for the biggest shape)

    // Per building type (in BuildingType order): sides (0 = circle), rotation, circumradius in icon sizes.
    private static readonly (int Sides, float Rotation, float Radius)[] Shapes =
    {
        (5, -Mathf.Pi / 2, 0.525f), // harbor: pentagon, point up
        (4, Mathf.Pi / 4, 0.6f),    // barracks: square
        (6, 0, 0.525f),             // depot: hexagon
        (0, 0, 0.475f),             // defense: circle
        (3, -Mathf.Pi / 2, 0.625f), // silo: triangle, point up
        (8, Mathf.Pi / 8, 0.5f),    // flak: octagon
    };

    private ImageTexture _atlas = null!;

    /// <summary>Called to draw the icons (from inside this node's _Draw).</summary>
    public Action<StructureIcons>? Paint { get; set; }

    /// <summary>Builds the glyph atlas from the icon pictures, by building type.</summary>
    public void Setup(Func<BuildingType, Texture2D?> glyph)
    {
        var atlas = Image.CreateEmpty(Cell * Shapes.Length, Cell, false, Image.Format.Rgba8);
        for (int i = 0; i < Shapes.Length; i++)
        {
            if (glyph((BuildingType)i)?.GetImage() is not { } image)
                continue;
            if (image.IsCompressed())
                image.Decompress();
            image.Convert(Image.Format.Rgba8);
            if (image.GetWidth() != Cell || image.GetHeight() != Cell)
                image.Resize(Cell, Cell, Image.Interpolation.Lanczos);
            atlas.BlitRect(image, new Rect2I(0, 0, Cell, Cell), new Vector2I(i * Cell, 0));
        }
        atlas.GenerateMipmaps();
        _atlas = ImageTexture.CreateFromImage(atlas);

        var shapes = new Godot.Collections.Array();
        foreach (var (sides, rotation, radius) in Shapes)
            shapes.Add(new Vector3(sides, rotation, radius));
        var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://game/view/structure_icons.gdshader") };
        material.SetShaderParameter("glyphs", _atlas);
        material.SetShaderParameter("columns", Shapes.Length);
        material.SetShaderParameter("shapes", shapes);
        material.SetShaderParameter("quad", Quad);
        Material = material;
    }

    public override void _Draw() => Paint?.Invoke(this);

    /// <summary>Draws one icon, <paramref name="size"/> map units across, centred on <paramref name="at"/>.</summary>
    public void DrawIcon(BuildingType type, Vector2 at, float size, Color owner, float alpha, Mode mode)
    {
        int column = Math.Clamp((int)type, 0, Shapes.Length - 1);
        float side = size * Quad;
        var rect = new Rect2(at - new Vector2(side, side) / 2, new Vector2(side, side));
        var source = new Rect2(column * Cell, (int)mode * Cell, Cell, Cell);
        DrawTextureRectRegion(_atlas, rect, source, new Color(owner, alpha));
    }
}

/// <summary>A plain drawing layer whose contents come from a callback (used to put the rails under the icons).</summary>
public partial class OverlayLayer : Node2D
{
    public Action<OverlayLayer>? Paint { get; set; }

    public override void _Draw() => Paint?.Invoke(this);
}
