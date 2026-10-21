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
    // Building icons, sized the way OpenFront does it (its numbers x1.5, our measured
    // screen-pixel factor), by zoom in screen pixels per tile:
    //   zoomed far out (<= DotsZoom): just the shape, a fixed size on screen however far out;
    //   up to FullSizeZoom: shrinking with the map, from full size down;
    //   up to GrowZoom: a fixed BuildingPixels on screen;
    //   further in: growing with the map, as if drawn on it.
    private const float BuildingPixels = 64;
    private const float DotsZoom = 1.8f;
    private const float DotScale = 0.3f;
    private const float FullSizeZoom = 4.5f;
    private const float GrowZoom = 10.5f;
    private const float BoatPixels = 18;

    // Where your own boats are headed: the destination crosshair, red and a little see-through,
    // turning slowly in place, a fixed size on screen.
    private const string DestinationIconFile = "res://ui/misc/destination64x.svg";
    private const float DestinationPixels = 40;
    private const float DestinationSecondsPerTurn = 8;
    private static readonly Color DestinationColour = new(1f, 0.15f, 0.15f, 0.7f);
    private Texture2D? _destinationIcon;
    private const float WarshipPixels = 40;

    // Zoomed out past this (pixels per tile), icons stop holding their minimum size and
    // shrink with the map, so a zoomed-out view isn't buried in icons.
    private const float ShrinkBelowPixelsPerTile = 2.2f;

    private static readonly Dictionary<BuildingType, string> IconFiles = new()
    {
        [BuildingType.Port] = "res://ui/structures32x/harbour32x.svg",
        [BuildingType.City] = "res://ui/structures32x/barracks32x.svg",
        [BuildingType.Factory] = "res://ui/structures32x/depot32x.svg",
        [BuildingType.Defense] = "res://ui/structures32x/defense32x.svg",
        [BuildingType.Silo] = "res://ui/structures32x/silo32x.svg",
        [BuildingType.Sam] = "res://ui/structures32x/flak32x.svg",
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
        foreach (var (type, path) in IconFiles)
            _icons[type] = ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
        _destinationIcon = ResourceLoader.Exists(DestinationIconFile) ? GD.Load<Texture2D>(DestinationIconFile) : null;

        // Layers drawn behind this node's own drawing, in this order: roads (in a group, so
        // they fade with zoom as one picture), trains and placement ranges, then the building
        // icons (crisp, from a shader). Badges, construction rings, ships and bombs are drawn
        // by this node, on top.
        _roadGroup = new CanvasGroup { Name = "Roads", ShowBehindParent = true, FitMargin = 0, ClearMargin = 0 };
        AddChild(_roadGroup);
        _roads = new OverlayLayer { Name = "RoadLines", Paint = PaintRoads };
        _roadGroup.AddChild(_roads);
        _under = new OverlayLayer { Name = "RailsAndRanges", ShowBehindParent = true, Paint = PaintUnder };
        AddChild(_under);
        _iconLayer = new StructureIcons { Name = "StructureIcons", ShowBehindParent = true, Paint = PaintIcons };
        AddChild(_iconLayer);
        _iconLayer.Setup(type => _icons.GetValueOrDefault(type));
    }

    private OverlayLayer _under = null!;
    private CanvasGroup _roadGroup = null!;
    private OverlayLayer _roads = null!;
    private StructureIcons _iconLayer = null!;

    /// <summary>Whether there's a game to draw; waits for a running tick if needed.</summary>
    private bool ReadyToPaint(CanvasItem layer)
    {
        if (_game is null)
            return false;
        if (Frozen)
            WaitForSim?.Invoke();
        _zoom = GetViewport().GetCamera2D()?.Zoom.X ?? 1;
        _visible = layer.GetCanvasTransform().AffineInverse() * layer.GetViewportRect();
        return true;
    }

    /// <summary>The roads, faded by zoom like OpenFront's railroads (see <see cref="RoadFade"/>).</summary>
    private void PaintRoads(OverlayLayer c)
    {
        if (!ReadyToPaint(c))
            return;
        long part = FrameProfiler.Start();
        DrawRoads(c, _zoom, _game!.Map.Width);
        FrameProfiler.Stop("  overlay roads", part);
    }

    /// <summary>Under the icons: trains, and the placement preview's ranges.</summary>
    private void PaintUnder(OverlayLayer c)
    {
        if (!ReadyToPaint(c))
            return;
        float zoom = _zoom;
        int w = _game!.Map.Width;
        long part = FrameProfiler.Start();
        DrawTrains(c, zoom, w);
        FrameProfiler.Stop("  overlay trains", part);
        if (_ghostType is { } ghost && _ghostTile >= 0)
        {
            var at = TileCentre(_ghostTile, w);
            var tint = _ghostValid ? new Color(0.45f, 0.95f, 0.5f) : new Color(1f, 0.35f, 0.3f);
            if (!_ghostStacks)
            {
                c.DrawCircle(at, _game.Rules.MinBuildingSpacing, new Color(tint, 0.12f));
                c.DrawArc(at, _game.Rules.MinBuildingSpacing, 0, Mathf.Tau, 48, new Color(tint, 0.6f), 1.5f / zoom);
            }
            float reach = ghost switch
            {
                BuildingType.Defense => _game.Rules.DefensePostRange,
                BuildingType.Sam => _game.Rules.SamRange(_ghostLevel),
                BuildingType.Factory => _game.Rules.StationMaxRange,
                _ => 0,
            };
            if (reach > 0)
                c.DrawArc(at, reach, 0, Mathf.Tau, 96, new Color(1, 1, 1, 0.55f), 1.5f / zoom, true);
        }
    }

    /// <summary>The building icons (and the placement preview's), crisp at any zoom.</summary>
    private void PaintIcons(StructureIcons c)
    {
        if (!ReadyToPaint(c))
            return;
        long part = FrameProfiler.Start();
        float zoom = _zoom;
        int w = _game!.Map.Width;
        float size = BuildingIconPixels(zoom) / zoom;
        var mode = Dots(zoom) ? StructureIcons.Mode.ShapeOnly : StructureIcons.Mode.WithGlyph;
        foreach (var b in _game.Buildings)
        {
            var at = TileCentre(b.Tile, w);
            if (OnScreen(at, size))
                c.DrawIcon(b.Type, at, size, ColorOf(b.Owner), b.IsReady(_game.Tick) ? 1f : 0.55f, mode);
        }
        if (_ghostType is { } ghost && ghost != BuildingType.Warship && _ghostTile >= 0)
        {
            var preview = _ghostValid ? StructureIcons.Mode.PreviewValid : StructureIcons.Mode.PreviewInvalid;
            var tint = _ghostValid ? new Color(0.45f, 0.95f, 0.5f) : new Color(1f, 0.35f, 0.3f);
            c.DrawIcon(ghost, TileCentre(_ghostTile, w), size, _me is null ? tint : ColorOf(_me.Id), 0.75f, preview);
        }
        FrameProfiler.Stop("  overlay buildings", part);
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

    /// <summary>Set while the simulation runs on its worker thread: keep the last drawing instead of reading the game.</summary>
    public bool Frozen { get; set; }

    /// <summary>Waits for the simulation's worker to finish (in case Godot redraws us mid-tick on its own).</summary>
    public Action? WaitForSim { get; set; }

    public override void _Process(double delta)
    {
        if (Frozen)
            return;
        QueueRedraw();
        // The group's own colour fades the whole road picture at once (no darker overlaps).
        float fade = RoadFade(GetViewport().GetCamera2D()?.Zoom.X ?? 1);
        _roadGroup.Visible = fade > 0;
        _roadGroup.SelfModulate = new Color(1, 1, 1, fade);
        if (fade > 0)
            _roads.QueueRedraw();
        _under.QueueRedraw();
        _iconLayer.QueueRedraw();
    }

    public override void _Draw()
    {
        if (_game is null)
            return;
        if (Frozen)
            WaitForSim?.Invoke();
        long started = FrameProfiler.Start();
        DrawAll();
        FrameProfiler.Stop("overlay draw", started);
    }

    private void DrawAll()
    {
        if (_game is null)
            return;
        float zoom = GetViewport().GetCamera2D()?.Zoom.X ?? 1;
        _zoom = zoom;
        int w = _game.Map.Width;
        // What's on screen (in map units): anything outside isn't drawn at all.
        _visible = GetCanvasTransform().AffineInverse() * GetViewportRect();

        // --- On top of the building icons: construction rings and level badges ---
        long part = FrameProfiler.Start();
        float size = BuildingIconPixels(zoom) / zoom;
        bool dots = Dots(zoom);
        foreach (var b in _game.Buildings)
        {
            var at = TileCentre(b.Tile, w);
            if (!OnScreen(at, size))
                continue;
            if (!b.IsReady(_game.Tick)) // construction progress ring
            {
                float done = Math.Clamp((float)(_game.Tick - b.StartTick) / Math.Max(1, b.ReadyTick - b.StartTick), 0, 1);
                DrawArc(at, size * 0.62f, -Mathf.Pi / 2, -Mathf.Pi / 2 + Mathf.Tau * done, 32, Colors.White, size * 0.08f, true);
            }
            if (b.Level > 1 && !dots)
                DrawLevel(at + new Vector2(size * 0.42f, -size * 0.42f), b.Level, size, zoom);
        }
        if (_ghostType is { } ghost && _ghostTile >= 0)
        {
            var at = TileCentre(_ghostTile, w);
            var tint = _ghostValid ? new Color(0.45f, 0.95f, 0.5f) : new Color(1f, 0.35f, 0.3f);
            if (_ghostStacks && !dots)
                DrawLevel(at + new Vector2(size * 0.42f, -size * 0.42f), _ghostLevel, size, zoom, plus: true);
            if (ghost == BuildingType.Warship)
                DrawWarship(at, 0, IconPixels(zoom, WarshipPixels, WarshipPixels, 1) / zoom, _me is null ? tint : ColorOf(_me.Id), 1f, tint);
        }

        FrameProfiler.Stop("  overlay buildings", part);
        part = FrameProfiler.Start();
        // --- Trade ships: just the ship, no lines ---
        float boatSize = IconPixels(zoom, BoatPixels, BoatPixels, 1) / zoom;
        foreach (var ship in _game.TradeShips)
        {
            if (!OnScreen(TileCentre(ship.Tile, w), boatSize * 2))
                continue;
            long progress = ship.Progress - _game.Rules.TradeShipSpeedPerTick + (long)(_game.Rules.TradeShipSpeedPerTick * _tickFraction);
            var (pos, heading) = AlongPath(ship.Path, Math.Max(0, progress), w);
            DrawBoat(pos, heading, boatSize * 0.8f, ColorOf(ship.Owner.Id), trade: true);
        }

        DrawWarships(zoom, w);

        // --- Transport boats (drawn one tick behind, like territory, so they glide) ---
        foreach (var boat in _game.Boats)
        {
            long progress = boat.Progress - _game.Rules.BoatSpeedPerTick + (long)(_game.Rules.BoatSpeedPerTick * _tickFraction);
            var (pos, heading) = AlongPath(boat.Path, Math.Max(0, progress), w);
            if (boat.Owner == _me)
                DrawRoute(boat, progress, w, zoom); // your own boats' routes may cross the screen
            if (!OnScreen(pos, boatSize * 30))
                continue;
            DrawTrail(boat, Math.Max(0, progress), pos, w, zoom);
            DrawBoat(pos, heading, boatSize, ColorOf(boat.Owner.Id));
            if (boatSize * zoom < TinyIconPixels)
                continue; // too small to label
            string troops = GameHud.FormatCount(boat.Troops);
            int fontSize = Math.Max(1, (int)(12 / zoom));
            float textWidth = _font.GetStringSize(troops, HorizontalAlignment.Left, -1, fontSize).X;
            var textPos = pos + new Vector2(-textWidth / 2, boatSize * 1.15f);
            DrawStringOutline(_font, textPos, troops, HorizontalAlignment.Left, -1, fontSize, Math.Max(1, (int)(3 / zoom)), new Color(0, 0, 0, 0.6f));
            DrawString(_font, textPos, troops, HorizontalAlignment.Left, -1, fontSize, Colors.White);
        }

        FrameProfiler.Stop("  overlay ships+boats", part);
        part = FrameProfiler.Start();
        // --- Bombs, interceptors and explosions (on top of everything) ---
        DrawAttackLabels(zoom, w);
        DrawNukeLayer(zoom, w);
        DrawGoldPopups(zoom);
        FrameProfiler.Stop("  overlay bombs+reloads", part);
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

    // Roads (the depots' railroads), like OpenFront's: one reference tile wide (its
    // railThickness 1) in whole screen pixels, at least RoadMinPixels. Your own roads are
    // white (OpenFront's focusedBorderColor #e6e6e6; black on very light land), others' are
    // the land owner's border colour, unowned land and bridges light grey, bridges with
    // OpenFront's red sides. Once wide enough, a dashed line runs down the middle in the
    // land's colour, darker.
    private const float RoadMinPixels = 2;
    private const float RoadTiles = 1;               // reference tiles
    private const int MarkingsFromPixels = 6;        // narrower roads have no centre line
    private const float MarkingWidth = 0.2f;         // of the road's width
    private const float DashTiles = 1;               // reference tiles of line, then as much gap
    private const float BridgeWidth = 2;             // the red sides span twice the road's width
    private const float BorderDarken = 0.35f;        // map.gdshader's border_darken
    private static readonly Color OwnRoadColour = new(0.902f, 0.902f, 0.902f);
    private static readonly Color NeutralRoadColour = new(0.75f, 0.75f, 0.75f);
    private static readonly Color BridgeColour = new(0.773f, 0.271f, 0.282f);

    private enum RoadPass { Bridges, Roads, Markings }

    // Roads fade in with zoom like OpenFront's railroads (its railMinZoom 3 and railFadeRange 2,
    // x1.5 for our screen pixels): invisible at or below RoadHiddenZoom, fully shown from
    // RoadShownZoom (reference screen pixels per tile), linear in between.
    private const float RoadHiddenZoom = 1.5f;
    private const float RoadShownZoom = 4.5f;

    private float RoadFade(float zoom) =>
        Math.Clamp((ReferenceZoom(zoom) - RoadHiddenZoom) / (RoadShownZoom - RoadHiddenZoom), 0, 1);

    private readonly List<Railroad> _roadsOnScreen = new();

    private void DrawRoads(CanvasItem layer, float zoom, int w)
    {
        float linear = (float)_game!.Rules.Linear;
        int widthPixels = Math.Max((int)RoadMinPixels, (int)MathF.Round(RoadTiles * linear * zoom));
        // The centre line: same odd/even pixel count as the road, so it sits exactly in the middle.
        int markPixels = Math.Max(1, (int)MathF.Round(widthPixels * MarkingWidth));
        if ((widthPixels - markPixels) % 2 != 0)
            markPixels++;
        var toScreen = layer.GetCanvasTransform();
        var toMap = toScreen.AffineInverse();
        _roadsOnScreen.Clear();
        foreach (var rail in _game.Railroads)
        {
            var shape = _railShapes.GetValue(rail, r => new RailShape(r, w));
            if (shape.Corners.Length >= 2 && _visible.Grow(2 * widthPixels / zoom).Intersects(shape.Bounds, includeBorders: true))
                _roadsOnScreen.Add(rail);
        }
        // Bridges under every road, then the roads, then the centre lines on top of them all.
        float half = widthPixels / zoom / 2;
        foreach (var rail in _roadsOnScreen)
            DrawRoad(layer, rail, w, RoadPass.Bridges, half * BridgeWidth, 0, toScreen, toMap);
        foreach (var rail in _roadsOnScreen)
            DrawRoad(layer, rail, w, RoadPass.Roads, half, 0, toScreen, toMap);
        if (widthPixels >= MarkingsFromPixels)
            foreach (var rail in _roadsOnScreen)
                DrawRoad(layer, rail, w, RoadPass.Markings, markPixels / zoom / 2, DashTiles * linear, toScreen, toMap);
    }

    private void DrawTrains(CanvasItem layer, float zoom, int w)
    {
        float car = Math.Max(3f / zoom, 0.6f);
        foreach (var train in _game!.Trains)
        {
            float progress = (train.Progress - _game.Rules.TrainSpeedPerTick + _game.Rules.TrainSpeedPerTick * _tickFraction) / 1000f;
            var colour = ColorOf(train.Owner.Id);
            for (int c = 0; c < 6; c++) // engine + 5 cars, a couple of tiles apart
            {
                float along = progress - c * 1.6f;
                if (along < 0)
                    break;
                int i = Math.Min((int)along, train.Tiles.Length - 1);
                if (c == 0 && !OnScreen(TileCentre(train.Tiles[i], w), 20))
                    break; // the whole train is off screen
                int j = Math.Min(i + 1, train.Tiles.Length - 1);
                var pos = TileCentre(train.Tiles[i], w).Lerp(TileCentre(train.Tiles[j], w), along - (int)along);
                layer.DrawRect(new Rect2(pos - new Vector2(car, car) / 2, new Vector2(car, car)), c == 0 ? colour.Darkened(0.4f) : colour);
            }
        }
    }

    // Icons smaller than this on screen (pixels) are drawn as plain squares.
    private const float TinyIconPixels = 6;
    private Rect2 _visible;

    /// <summary>Whether something at this point (with this much room around it) could be on screen.</summary>
    private bool OnScreen(Vector2 at, float margin) => _visible.Grow(margin).HasPoint(at);

    // Rails as just their corner points (they're straight runs), worked out once per rail.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Railroad, RailShape> _railShapes = new();

    /// <summary>
    /// One layer of one road (its bridges, its surface or its centre line): straight runs
    /// between its right-angle turns, cut wherever the colour changes (someone else's land,
    /// a gap between dashes). Each piece is a flat rectangle snapped to whole screen pixels,
    /// so the road stays crisp; pieces reach half a width past their ends so corners join.
    /// </summary>
    private void DrawRoad(CanvasItem layer, Railroad rail, int w, RoadPass pass, float half, float dash, Transform2D toScreen, Transform2D toMap)
    {
        var tiles = rail.Tiles;
        Vector2 runStart = TileCentre(tiles[0], w), runEnd = runStart;
        Color? runColour = null;
        Vector2 runDirection = Vector2.Zero;
        float along = 0; // tiles from the road's start, for the dashes
        for (int i = 1; i < tiles.Length; i++)
        {
            // Half-tile steps: tile i's colour covers from halfway back to halfway forward.
            Vector2 a = TileCentre(tiles[i - 1], w), b = TileCentre(tiles[i], w), mid = (a + b) / 2;
            var direction = (b - a).Normalized();
            Step(a, direction, PassColour(pass, tiles[i - 1]));
            Step(mid, direction, PassColour(pass, tiles[i]));
        }
        Flush();

        void Step(Vector2 from, Vector2 direction, Color? colour)
        {
            float end = along + 0.5f;
            if (pass != RoadPass.Markings || colour is null)
                Extend(from, from + direction * 0.5f, direction, colour);
            else
            {
                // Dashes: even-numbered stretches of `dash` tiles are line, odd ones gap. Each
                // step moves to the next stretch's start (or the half-tile's end), always forward,
                // so rounding can never stall it.
                float at = along;
                while (at < end - 1e-4f)
                {
                    long k = (long)Math.Floor(at / dash + 1e-4f);
                    float next = Math.Min(end, (k + 1) * dash);
                    if (next <= at)
                        next = end;
                    Extend(from + direction * (at - along), from + direction * (next - along), direction, k % 2 == 0 ? colour : null);
                    at = next;
                }
            }
            along = end;
        }

        void Extend(Vector2 from, Vector2 to, Vector2 direction, Color? colour)
        {
            if (direction != runDirection && runDirection != Vector2.Zero || colour != runColour)
            {
                Flush();
                runStart = from;
            }
            runDirection = direction;
            runColour = colour;
            runEnd = to;
        }

        void Flush()
        {
            if (runColour is not Color colour)
                return;
            var min = new Vector2(Math.Min(runStart.X, runEnd.X), Math.Min(runStart.Y, runEnd.Y)) - new Vector2(half, half);
            var max = new Vector2(Math.Max(runStart.X, runEnd.X), Math.Max(runStart.Y, runEnd.Y)) + new Vector2(half, half);
            // Snap to whole screen pixels so edges are sharp and the width never wobbles.
            var a = toScreen * min;
            var b = toScreen * max;
            a = new Vector2(MathF.Round(a.X), MathF.Round(a.Y));
            b = new Vector2(Math.Max(MathF.Round(b.X), a.X + 1), Math.Max(MathF.Round(b.Y), a.Y + 1));
            var from = toMap * a;
            var to = toMap * b;
            layer.DrawRect(new Rect2(from, to - from), colour);
        }
    }

    private Color? PassColour(RoadPass pass, int tile) => pass switch
    {
        RoadPass.Bridges => _game!.Map.IsLand(tile) ? null : BridgeColour,
        RoadPass.Roads => RoadColour(tile),
        _ => MarkingColour(tile),
    };

    /// <summary>A road's colour on this tile (OpenFront's railroad colours, see the constants).</summary>
    private Color RoadColour(int tile)
    {
        ushort owner = _game!.Owner[tile];
        if (owner == Game.Nobody || !_game.Map.IsLand(tile))
            return NeutralRoadColour;
        var land = ColorOf(owner);
        if (owner == _me?.Id)
            return Brightness(land) > 0.8f ? Colors.Black : OwnRoadColour;
        return land.Darkened(BorderDarken);
    }

    /// <summary>The centre line's colour: the land's colour, darker (darker still on others' darker roads).</summary>
    private Color MarkingColour(int tile)
    {
        ushort owner = _game!.Owner[tile];
        if (owner == Game.Nobody || !_game.Map.IsLand(tile))
            return NeutralRoadColour.Darkened(0.4f);
        return ColorOf(owner).Darkened(owner == _me?.Id ? BorderDarken : 0.6f);
    }

    // Perceived brightness, 0-1 (colord's brightness(), which OpenFront uses for this).
    private static float Brightness(Color c) => (c.R * 299 + c.G * 587 + c.B * 114) / 1000f;

    private sealed class RailShape
    {
        public readonly Vector2[] Corners;
        public readonly Rect2 Bounds;

        public RailShape(Railroad rail, int w)
        {
            var points = new List<Vector2> { TileCentre(rail.Tiles[0], w) };
            for (int i = 1; i < rail.Tiles.Length - 1; i++)
                if (rail.Tiles[i] - rail.Tiles[i - 1] != rail.Tiles[i + 1] - rail.Tiles[i])
                    points.Add(TileCentre(rail.Tiles[i], w));
            points.Add(TileCentre(rail.Tiles[^1], w));
            Corners = points.ToArray();
            var bounds = new Rect2(Corners[0], Vector2.Zero);
            foreach (var p in Corners)
                bounds = bounds.Expand(p);
            Bounds = bounds;
        }
    }

    /// <summary>A building icon's size on screen at this zoom (OpenFront's structure scaling; see the constants).</summary>
    /// <summary>Zoom in screen pixels per tile of the reference map (finer maps have smaller tiles; see Sim.ScaledRules).</summary>
    private float ReferenceZoom(float zoom) => zoom * (float)(_game?.Rules.Linear ?? 1);

    /// <summary>Zoomed out far enough that buildings are plain shapes.</summary>
    private bool Dots(float zoom) => ReferenceZoom(zoom) <= DotsZoom;

    private float BuildingIconPixels(float mapZoom)
    {
        float zoom = ReferenceZoom(mapZoom);
        if (zoom <= DotsZoom)
            return BuildingPixels * DotScale;
        if (zoom >= GrowZoom)
            return BuildingPixels * zoom / GrowZoom;
        return BuildingPixels * Math.Min(1, zoom / FullSizeZoom);
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
            if (!OnScreen(pos, size))
                continue;
            if (size * zoom < TinyIconPixels)
            {
                DrawRect(new Rect2(pos - new Vector2(size, size) * 0.3f, new Vector2(size, size) * 0.6f), ColorOf(ship.Owner.Id).Darkened(0.2f));
                continue;
            }
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
        // About one point every couple of pixels is plenty, and never more than ~64 points.
        int step = Math.Max(Math.Max(1, (int)(2.5f / Math.Max(zoom, 0.01f))), upTo / 64);
        var points = new List<Vector2>(upTo / step + 2);
        for (int i = 0; i <= upTo; i += step)
            points.Add(TileCentre(boat.Path[i], w));
        points.Add(boatPos);
        DrawPolyline(points.ToArray(), new Color(ColorOf(boat.Owner.Id).Darkened(0.15f), 0.85f), 2f / zoom, true);
    }

    private void DrawBoat(Vector2 at, float heading, float size, Color owner, bool trade = false)
    {
        if (size * _zoom < TinyIconPixels) // a few pixels across: just a dot
        {
            DrawRect(new Rect2(at - new Vector2(size, size) * 0.3f, new Vector2(size, size) * 0.6f), owner.Lerp(Colors.White, trade ? 0.5f : 0.2f));
            return;
        }
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
        // A recalled boat heads back to the shore it left from.
        var destination = TileCentre(boat.Retreating ? boat.SourceTile : boat.LandingTile, w);
        if (_destinationIcon is null)
        {
            DrawCircle(destination, r * 2.5f, colour);
            return;
        }
        float size = DestinationPixels / zoom;
        float angle = (float)(Time.GetTicksMsec() / 1000.0 / DestinationSecondsPerTurn % 1.0 * Mathf.Tau);
        DrawSetTransform(destination, angle, Vector2.One);
        DrawTextureRect(_destinationIcon, new Rect2(-size / 2, -size / 2, size, size), false, DestinationColour);
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
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
