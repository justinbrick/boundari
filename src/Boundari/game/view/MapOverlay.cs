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
    private const float BuildingTiles = 11; // just under the 12-tile spacing, so icons never overlap
    private const float MinBuildingPixels = 32;
    private const float MaxBuildingPixels = 80;
    private const float BoatPixels = 18;
    private const float WarshipPixels = 40;

    // Zoomed out past this (pixels per tile), icons stop holding their minimum size and
    // shrink with the map, so a zoomed-out view isn't buried in icons.
    private const float ShrinkBelowPixelsPerTile = 2.2f;
    private const float GlyphMinPixels = 14; // smaller icons are drawn as plain shapes

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
    private float _zoom = 1;
    private Font _font = null!;

    // Placement preview.
    private BuildingType? _ghostType;
    private int _ghostTile = -1;
    private bool _ghostValid;
    private bool _ghostStacks;  // placing on top of one of ours: adds a level
    private int _ghostLevel = 1;

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
        var target = _game is not null && _me is not null ? _game.UpgradeTarget(_me, type, tile) : null;
        _ghostStacks = target is not null;
        _ghostLevel = target is null ? 1 : target.Level + 1;
    }

    public void ClearGhost()
    {
        _ghostType = null;
        _ghostTile = -1;
        ClearNukeGhost();
    }

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        if (_game is null)
            return;
        float zoom = GetViewport().GetCamera2D()?.Zoom.X ?? 1;
        _zoom = zoom;
        int w = _game.Map.Width;

        // --- Railroads and trains (under the buildings) ---
        DrawRails(zoom, w);

        // --- Buildings ---
        float size = IconPixels(zoom, MinBuildingPixels, MaxBuildingPixels, BuildingTiles) / zoom;
        foreach (var b in _game.Buildings)
        {
            var at = TileCentre(b.Tile, w);
            bool ready = b.IsReady(_game.Tick);
            DrawBuilding(b.Type, at, size, ColorOf(b.Owner), ready ? 1f : 0.55f);
            if (!ready) // construction progress ring
            {
                float done = Math.Clamp((float)(_game.Tick - b.StartTick) / Math.Max(1, b.ReadyTick - b.StartTick), 0, 1);
                DrawArc(at, size * 0.62f, -Mathf.Pi / 2, -Mathf.Pi / 2 + Mathf.Tau * done, 32, Colors.White, size * 0.08f, true);
            }
            if (b.Level > 1 && size * zoom >= GlyphMinPixels)
                DrawLevel(at + new Vector2(size * 0.42f, -size * 0.42f), b.Level, size, zoom);
        }

        // --- Placement preview, with the no-build radius and what the building reaches ---
        if (_ghostType is { } ghost && _ghostTile >= 0)
        {
            var at = TileCentre(_ghostTile, w);
            var tint = _ghostValid ? new Color(0.45f, 0.95f, 0.5f) : new Color(1f, 0.35f, 0.3f);
            if (!_ghostStacks)
            {
                DrawCircle(at, Balance.MinBuildingSpacing, new Color(tint, 0.12f));
                DrawArc(at, Balance.MinBuildingSpacing, 0, Mathf.Tau, 48, new Color(tint, 0.6f), 1.5f / zoom);
            }
            float reach = ghost switch
            {
                BuildingType.Defense => Balance.DefensePostRange,
                BuildingType.Sam => Balance.SamRange(_ghostLevel),
                BuildingType.Factory => RailRules.StationMaxRange,
                _ => 0,
            };
            if (reach > 0)
                DrawArc(at, reach, 0, Mathf.Tau, 96, new Color(1, 1, 1, 0.55f), 1.5f / zoom, true);
            if (_ghostStacks && size * zoom >= GlyphMinPixels)
                DrawLevel(at + new Vector2(size * 0.42f, -size * 0.42f), _ghostLevel, size, zoom, plus: true);
            if (ghost == BuildingType.Warship)
                DrawWarship(at, 0, IconPixels(zoom, WarshipPixels, WarshipPixels, 1) / zoom, _me is null ? tint : ColorOf(_me.Id), 1f, tint);
            else
                DrawBuilding(ghost, at, size, _me is null ? tint : ColorOf(_me.Id), 0.75f, tint);
        }

        // --- Trade ships: just the ship, no lines ---
        float boatSize = IconPixels(zoom, BoatPixels, BoatPixels, 1) / zoom;
        foreach (var ship in _game.TradeShips)
        {
            long progress = ship.Progress - Balance.TradeShipSpeedPerTick + (long)(Balance.TradeShipSpeedPerTick * _tickFraction);
            var (pos, heading) = AlongPath(ship.Path, Math.Max(0, progress), w);
            DrawBoat(pos, heading, boatSize * 0.8f, ColorOf(ship.Owner.Id), trade: true);
        }

        DrawWarships(zoom, w);

        // --- Transport boats (drawn one tick behind, like territory, so they glide) ---
        foreach (var boat in _game.Boats)
        {
            long progress = boat.Progress - Balance.BoatSpeedPerTick + (long)(Balance.BoatSpeedPerTick * _tickFraction);
            var (pos, heading) = AlongPath(boat.Path, Math.Max(0, progress), w);
            DrawTrail(boat, Math.Max(0, progress), pos, w, zoom);
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

        // --- Bombs, interceptors and explosions (on top of everything) ---
        DrawNukeLayer(zoom, w);
    }

    /// <summary>
    /// Fills a small convex shape. It's drawn around its own centre at unit scale: tiny shapes
    /// far from the map origin otherwise lose float precision, fail Godot's triangulation and
    /// flood the log with errors. Shapes under about a pixel are skipped.
    /// </summary>
    private void FillShape(Vector2[] points, Color colour)
    {
        var centre = Vector2.Zero;
        foreach (var p in points)
            centre += p;
        centre /= points.Length;
        float radius = 0;
        foreach (var p in points)
            radius = Math.Max(radius, (p - centre).Length());
        if (!(radius * _zoom >= 0.5f)) // also catches NaN
            return;
        var local = new Vector2[points.Length];
        for (int i = 0; i < points.Length; i++)
            local[i] = (points[i] - centre) / radius;
        DrawSetTransform(centre, 0, new Vector2(radius, radius));
        DrawColoredPolygon(local, colour);
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }

    private void DrawBuilding(BuildingType type, Vector2 at, float size, Color owner, float alpha, Color? outlineOverride = null)
    {
        var points = Shape(type, at, size / 2);
        var fill = owner.Lerp(Colors.White, 0.35f);
        var outline = outlineOverride ?? owner.Darkened(0.45f);
        FillShape(points, new Color(fill, alpha));
        var closed = new Vector2[points.Length + 1];
        points.CopyTo(closed, 0);
        closed[^1] = points[0];
        DrawPolyline(closed, new Color(outline, alpha), size * 0.09f, true);
        if (size * GetViewport().GetCamera2D()!.Zoom.X >= GlyphMinPixels && _icons.GetValueOrDefault(type) is { } icon)
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

    /// <summary>A small level badge in the corner of a building ("+3" while previewing a stack).</summary>
    private void DrawLevel(Vector2 at, int level, float size, float zoom, bool plus = false)
    {
        string text = (plus ? "+" : "") + level;
        float r = size * 0.24f;
        DrawCircle(at, r, new Color(0.08f, 0.09f, 0.1f, 0.9f));
        int fontSize = Math.Max(1, (int)(r * 1.3f));
        var font = ThemeDB.FallbackFont;
        var extent = font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize);
        DrawString(font, at + new Vector2(-extent.X / 2, fontSize * 0.36f), text, HorizontalAlignment.Left, -1, fontSize, Colors.White);
    }

    /// <summary>Railroads as thin dark lines, trains as short strings of cars along them.</summary>
    private void DrawRails(float zoom, int w)
    {
        var railColour = new Color(0.18f, 0.16f, 0.14f, 0.75f);
        float railWidth = Math.Max(1.2f / zoom, 0.25f);
        foreach (var rail in _game!.Railroads)
        {
            // Rails are straight runs with right-angle turns: just draw the corners.
            var points = new List<Vector2> { TileCentre(rail.Tiles[0], w) };
            for (int i = 1; i < rail.Tiles.Length - 1; i++)
                if (rail.Tiles[i] - rail.Tiles[i - 1] != rail.Tiles[i + 1] - rail.Tiles[i])
                    points.Add(TileCentre(rail.Tiles[i], w));
            points.Add(TileCentre(rail.Tiles[^1], w));
            if (points.Count >= 2)
                DrawPolyline(points.ToArray(), railColour, railWidth, true);
        }

        float car = Math.Max(3f / zoom, 0.6f);
        foreach (var train in _game.Trains)
        {
            float progress = (train.Progress - RailRules.TrainSpeedPerTick + RailRules.TrainSpeedPerTick * _tickFraction) / 1000f;
            var colour = ColorOf(train.Owner.Id);
            for (int c = 0; c < 6; c++) // engine + 5 cars, a couple of tiles apart
            {
                float along = progress - c * 1.6f;
                if (along < 0)
                    break;
                int i = Math.Min((int)along, train.Tiles.Length - 1);
                int j = Math.Min(i + 1, train.Tiles.Length - 1);
                var pos = TileCentre(train.Tiles[i], w).Lerp(TileCentre(train.Tiles[j], w), along - (int)along);
                DrawRect(new Rect2(pos - new Vector2(car, car) / 2, new Vector2(car, car)), c == 0 ? colour.Darkened(0.4f) : colour);
            }
        }
    }

    /// <summary>
    /// On-screen icon size: about <paramref name="tiles"/> tiles across, held between the
    /// given pixel sizes, except when zoomed far out, where it shrinks with the map.
    /// </summary>
    private static float IconPixels(float zoom, float min, float max, float tiles)
    {
        if (zoom < ShrinkBelowPixelsPerTile)
            return min * zoom / ShrinkBelowPixelsPerTile;
        return Math.Clamp(tiles * zoom, min, max);
    }

    private void DrawWarships(float zoom, int w)
    {
        float size = IconPixels(zoom, WarshipPixels, WarshipPixels, 1) / zoom;
        foreach (var ship in _game!.Warships)
        {
            var from = TileCentre(ship.PreviousTile, w);
            var to = TileCentre(ship.Tile, w);
            var pos = from.Lerp(to, _tickFraction);
            float heading = (to - from).LengthSquared() > 0 ? (to - from).Angle() : ship.Id * 0.7f;
            DrawWarship(pos, heading, size, ColorOf(ship.Owner.Id), (float)ship.Health / WarshipRules.MaxHealth);
        }
        // Shells: bright dots flying from the shooter to the target.
        foreach (var shell in _game.Shells)
        {
            int target = shell.Target switch { Boat b => b.Tile, Warship s => s.Tile, _ => shell.FromTile };
            float t = Math.Clamp((_game.Tick - 1 + _tickFraction - shell.FiredTick) / Math.Max(1, shell.HitTick - shell.FiredTick), 0, 1);
            var at = TileCentre(shell.FromTile, w).Lerp(TileCentre(target, w), t);
            DrawCircle(at, 2.5f / zoom, new Color(1f, 0.9f, 0.5f));
            DrawCircle(at, 1.2f / zoom, Colors.White);
        }
    }

    /// <summary>A grey warship with its owner's colour along the deck, and a health bar when damaged.</summary>
    private void DrawWarship(Vector2 at, float heading, float size, Color owner, float health, Color? tint = null)
    {
        float l = size, hw = size * 0.22f;
        var rot = Transform2D.Identity.Rotated(heading);
        Vector2[] hull =
        {
            new(l * 0.5f, 0), new(l * 0.3f, hw), new(-l * 0.45f, hw), new(-l * 0.5f, 0),
            new(-l * 0.45f, -hw), new(l * 0.3f, -hw),
        };
        Vector2[] deck = { new(l * 0.2f, hw * 0.45f), new(-l * 0.3f, hw * 0.45f), new(-l * 0.3f, -hw * 0.45f), new(l * 0.2f, -hw * 0.45f) };
        for (int i = 0; i < hull.Length; i++)
            hull[i] = at + rot * hull[i];
        for (int i = 0; i < deck.Length; i++)
            deck[i] = at + rot * deck[i];
        FillShape(hull, tint is { } c ? new Color(c, 0.6f) : new Color(0.55f, 0.58f, 0.62f));
        FillShape(deck, owner);
        var closed = new Vector2[hull.Length + 1];
        hull.CopyTo(closed, 0);
        closed[^1] = hull[0];
        DrawPolyline(closed, new Color(0.15f, 0.17f, 0.2f), size * 0.07f, true);
        if (health < 0.999f)
        {
            var barAt = at + new Vector2(-l * 0.4f, -size * 0.55f);
            var full = new Vector2(l * 0.8f, size * 0.09f);
            DrawRect(new Rect2(barAt, full), new Color(0, 0, 0, 0.6f));
            DrawRect(new Rect2(barAt, new Vector2(full.X * health, full.Y)), health > 0.5f ? new Color(0.4f, 0.9f, 0.4f) : new Color(0.95f, 0.4f, 0.3f));
        }
    }

    /// <summary>A solid line along the water the boat has already crossed.</summary>
    private void DrawTrail(Boat boat, long progress, Vector2 boatPos, int w, float zoom)
    {
        int upTo = (int)Math.Min(progress / 1000, boat.Path.Length - 1);
        if (upTo < 1)
            return;
        // About one point every couple of pixels is plenty.
        int step = Math.Max(1, (int)(2.5f / Math.Max(zoom, 0.01f)));
        var points = new List<Vector2>(upTo / step + 2);
        for (int i = 0; i <= upTo; i += step)
            points.Add(TileCentre(boat.Path[i], w));
        points.Add(boatPos);
        DrawPolyline(points.ToArray(), new Color(ColorOf(boat.Owner.Id).Darkened(0.15f), 0.85f), 2f / zoom, true);
    }

    private void DrawBoat(Vector2 at, float heading, float size, Color owner, bool trade = false)
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
        FillShape(hull, owner.Lerp(Colors.White, trade ? 0.5f : 0.2f));
        var closed = new Vector2[hull.Length + 1];
        hull.CopyTo(closed, 0);
        closed[^1] = hull[0];
        DrawPolyline(closed, owner.Darkened(0.55f), size * 0.1f, true);
        if (trade) // a little cargo box on deck sets trade ships apart from troop boats
        {
            var box = new Vector2[] { new(-l * 0.18f, -hw * 0.45f), new(l * 0.12f, -hw * 0.45f), new(l * 0.12f, hw * 0.45f), new(-l * 0.18f, hw * 0.45f) };
            for (int i = 0; i < box.Length; i++)
                box[i] = at + rot * box[i];
            FillShape(box, owner.Darkened(0.35f));
        }
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
