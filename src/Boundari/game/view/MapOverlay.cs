using System;
using System.Collections.Generic;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// Draws things that sit on top of the map: buildings, boats at sea, and the preview
/// of a building being placed. Icons keep a readable on-screen size at any zoom.
/// </summary>
public partial class MapOverlay : Node2D
{
    // Building icon size: about this many tiles across, but never smaller/larger than these pixels.
    private const float BuildingTiles = 9;
    private const float MinBuildingPixels = 16;
    private const float MaxBuildingPixels = 40;
    private const float BoatPixels = 18;

    private static readonly Dictionary<BuildingType, string> IconFiles = new()
    {
        [BuildingType.Port] = "port", [BuildingType.City] = "city", [BuildingType.Factory] = "factory",
        [BuildingType.Defense] = "defense", [BuildingType.Silo] = "silo", [BuildingType.Sam] = "sam",
    };

    private readonly Dictionary<BuildingType, Texture2D?> _icons = new();
    private Color[] _palette = Array.Empty<Color>();
    private Game? _game;
    private Player? _me;
    private float _tickFraction;
    private Font _font = null!;

    // Placement preview.
    private BuildingType? _ghostType;
    private int _ghostTile = -1;
    private bool _ghostValid;

    public override void _Ready()
    {
        _font = ThemeDB.FallbackFont;
        foreach (var (type, file) in IconFiles)
        {
            string path = $"res://ui/small_icons_new/{file}.svg";
            _icons[type] = ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
        }
    }

    public void SetPalette(Color[] palette) => _palette = palette;

    public void SetGame(Game game, Player me)
    {
        _game = game;
        _me = me;
        ClearGhost();
    }

    /// <summary>How far into the current tick the visuals are (0..1), for smooth boat motion.</summary>
    public void SetTickFraction(float fraction) => _tickFraction = fraction;

    public void SetGhost(BuildingType type, int tile, bool valid)
    {
        _ghostType = type;
        _ghostTile = tile;
        _ghostValid = valid;
    }

    public void ClearGhost()
    {
        _ghostType = null;
        _ghostTile = -1;
    }

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        if (_game is null)
            return;
        float zoom = GetViewport().GetCamera2D()?.Zoom.X ?? 1;
        int w = _game.Map.Width;

        // --- Buildings ---
        float size = Math.Clamp(BuildingTiles * zoom, MinBuildingPixels, MaxBuildingPixels) / zoom;
        foreach (var b in _game.Buildings)
            DrawBuilding(b.Type, TileCentre(b.Tile, w), size, ColorOf(b.Owner), 1f);

        // --- Placement preview, with the no-build radius around it ---
        if (_ghostType is { } ghost && _ghostTile >= 0)
        {
            var at = TileCentre(_ghostTile, w);
            var tint = _ghostValid ? new Color(0.45f, 0.95f, 0.5f) : new Color(1f, 0.35f, 0.3f);
            DrawCircle(at, Balance.MinBuildingSpacing, new Color(tint, 0.12f));
            DrawArc(at, Balance.MinBuildingSpacing, 0, Mathf.Tau, 48, new Color(tint, 0.6f), 1.5f / zoom);
            DrawBuilding(ghost, at, size, _me is null ? tint : ColorOf(_me.Id), 0.75f, tint);
        }

        // --- Boats (drawn one tick behind, like territory, so they glide) ---
        float boatSize = BoatPixels / zoom;
        foreach (var boat in _game.Boats)
        {
            long progress = boat.Progress - Balance.BoatSpeedPerTick + (long)(Balance.BoatSpeedPerTick * _tickFraction);
            var (pos, heading) = AlongPath(boat.Path, Math.Max(0, progress), w);
            if (boat.Owner == _me)
                DrawRoute(boat, progress, w, zoom);
            DrawBoat(pos, heading, boatSize, ColorOf(boat.Owner.Id));
            string troops = GameHud.FormatCount(boat.Troops);
            int fontSize = Math.Max(1, (int)(12 / zoom));
            float textWidth = _font.GetStringSize(troops, HorizontalAlignment.Left, -1, fontSize).X;
            var textPos = pos + new Vector2(-textWidth / 2, boatSize * 1.15f);
            DrawStringOutline(_font, textPos, troops, HorizontalAlignment.Left, -1, fontSize, Math.Max(1, (int)(3 / zoom)), new Color(0, 0, 0, 0.6f));
            DrawString(_font, textPos, troops, HorizontalAlignment.Left, -1, fontSize, Colors.White);
        }
    }

    private void DrawBuilding(BuildingType type, Vector2 at, float size, Color owner, float alpha, Color? outlineOverride = null)
    {
        var points = Shape(type, at, size / 2);
        var fill = owner.Lerp(Colors.White, 0.35f);
        var outline = outlineOverride ?? owner.Darkened(0.45f);
        DrawColoredPolygon(points, new Color(fill, alpha));
        var closed = new Vector2[points.Length + 1];
        points.CopyTo(closed, 0);
        closed[^1] = points[0];
        DrawPolyline(closed, new Color(outline, alpha), size * 0.09f, true);
        if (_icons.GetValueOrDefault(type) is { } icon)
        {
            float s = size * 0.55f;
            DrawTextureRect(icon, new Rect2(at - new Vector2(s, s) / 2, new Vector2(s, s)), false, new Color(owner.Darkened(0.6f), alpha));
        }
    }

    /// <summary>Shapes from the design notes: port pentagon, city square, factory hexagon,
    /// defense circle, silo triangle, SAM octagon.</summary>
    private static Vector2[] Shape(BuildingType type, Vector2 at, float r)
    {
        (int sides, float rotation, float scale) = type switch
        {
            BuildingType.Port => (5, -Mathf.Pi / 2, 1.05f),
            BuildingType.City => (4, Mathf.Pi / 4, 1.2f),
            BuildingType.Factory => (6, 0, 1.05f),
            BuildingType.Defense => (32, 0, 0.95f),
            BuildingType.Silo => (3, -Mathf.Pi / 2, 1.25f),
            _ => (8, Mathf.Pi / 8, 1.0f),
        };
        var points = new Vector2[sides];
        for (int i = 0; i < sides; i++)
        {
            float a = rotation + i * Mathf.Tau / sides;
            points[i] = at + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r * scale;
        }
        return points;
    }

    private void DrawBoat(Vector2 at, float heading, float size, Color owner)
    {
        float l = size, hw = size * 0.32f;
        Vector2[] hull =
        {
            new(l * 0.55f, 0), new(l * 0.2f, hw), new(-l * 0.45f, hw * 0.85f),
            new(-l * 0.45f, -hw * 0.85f), new(l * 0.2f, -hw),
        };
        var rot = Transform2D.Identity.Rotated(heading);
        for (int i = 0; i < hull.Length; i++)
            hull[i] = at + rot * hull[i];
        DrawColoredPolygon(hull, owner.Lerp(Colors.White, 0.2f));
        var closed = new Vector2[hull.Length + 1];
        hull.CopyTo(closed, 0);
        closed[^1] = hull[0];
        DrawPolyline(closed, owner.Darkened(0.55f), size * 0.1f, true);
    }

    /// <summary>A faint dotted line showing where your boat is heading.</summary>
    private void DrawRoute(Boat boat, long progress, int w, float zoom)
    {
        int from = (int)(progress / 1000);
        int step = Math.Max(1, (int)(6 / Math.Max(zoom, 0.01f)));
        var colour = new Color(1, 1, 1, 0.45f);
        float r = 1.6f / zoom;
        for (int i = from + step; i < boat.Path.Length; i += step)
            DrawCircle(TileCentre(boat.Path[i], w), r, colour);
        DrawCircle(TileCentre(boat.LandingTile, w), r * 2.5f, colour);
    }

    private static (Vector2 Position, float Heading) AlongPath(int[] path, long progress, int w)
    {
        int i = (int)Math.Min(progress / 1000, path.Length - 1);
        int j = Math.Min(i + 1, path.Length - 1);
        float t = (progress % 1000) / 1000f;
        var a = TileCentre(path[i], w);
        var b = TileCentre(path[j], w);
        // Heading from a few steps ahead, so the boat doesn't twitch at every corner.
        var ahead = TileCentre(path[Math.Min(i + 4, path.Length - 1)], w);
        var dir = ahead - a;
        return (a.Lerp(b, t), dir.LengthSquared() > 0 ? dir.Angle() : 0);
    }

    private static Vector2 TileCentre(int tile, int w) => new(tile % w + 0.5f, tile / w + 0.5f);

    private Color ColorOf(ushort id) => id < _palette.Length ? _palette[id] : Colors.Gray;
}
