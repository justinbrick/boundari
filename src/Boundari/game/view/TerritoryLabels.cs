using System;
using System.Collections.Generic;
using Boundari.Map;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// Draws each player's flag, name and troop count on the map, placed and sized the way
/// OpenFront does it: in the largest rectangle that fits inside the player's biggest
/// connected piece of land (coasts and the water just off them count as room), with the
/// name centred in it, the flag just left of the name and the troops underneath.
/// Placements are worked out every few seconds and labels glide to them.
/// </summary>
public partial class TerritoryLabels : Node2D
{
    private const double RefreshSeconds = 3.0;          // OpenFront: every 30 ticks
    private const double SpawningRefreshSeconds = 0.5;  // while players are still picking spots
    private const float GlideRate = 10f;                // OpenFront's name lerp speed
    private const float MinScreenCapPixels = 5.5f;      // names with capitals smaller than this aren't drawn
    private const float HoveredCapPixels = 10f;         // the hovered player's name is never smaller
    // Our font (Exo 2) is wider than OpenFront's and its "ascent" is a full em, so OpenFront's
    // sizes are scaled down and everything is measured from the capitals' height instead.
    private const float SizeFactor = 0.65f;
    private const float CapHeight = 0.7f;               // Exo 2's capitals, as a share of the font size
    private const float FlagCapShare = 1.05f;           // flag height, as a share of the capitals' height
    private const float TroopLineOffset = 0.9f;         // troop line's middle below the name's, in font sizes
    private const float HoverFadeAlpha = 0.5f;          // OpenFront's see-through name plate under the mouse
    private const int NearShoreWater = 10;              // water this close to land counts as room
    private const float FlagAspect = 128f / 85f;        // OpenFront's flag cells (3:2)
    private const float TroopScale = 0.6f;

    private static readonly Color TextColor = new(0.16f, 0.16f, 0.16f, 0.92f);
    private static readonly Color OutlineColor = new(1, 1, 1, 0.28f);

    /// <summary>A name's spot and OpenFront "size" (the font size that fits its rectangle).</summary>
    private readonly record struct Placement(Vector2 Centre, float Size);

    private sealed class Label
    {
        public Vector2 Target, Position;
        public float TargetSize, Size;
    }

    private readonly Dictionary<ushort, Label> _labels = new();

    /// <summary>Set while the simulation runs on its worker thread: keep the last drawing instead of reading the game.</summary>
    public bool Frozen { get; set; }

    /// <summary>Waits for the simulation's worker to finish (in case Godot redraws us mid-tick on its own).</summary>
    public Action? WaitForSim { get; set; }

    /// <summary>Screenshot aid (--hover): pretend the mouse is here (map tiles).</summary>
    public Vector2? MouseOverride { get; set; }

    /// <summary>The player under the mouse (0 = none): their label is always shown, on top, readable.</summary>
    public ushort Hovered { get; set; }

    /// <summary>Where a player's name sits on the map (the middle of their main territory), if they have one.</summary>
    public bool TryGetAnchor(ushort id, out Vector2 position)
    {
        bool found = _labels.TryGetValue(id, out var label);
        position = found ? label!.Position : default;
        return found;
    }
    private readonly Dictionary<string, Texture2D?> _flags = new();
    private Game? _game;
    private double _sinceRefresh = RefreshSeconds;
    private Font _font = null!;
    private Vector2 _mouse;     // in map tiles, for fading the name plate under it

    // Placement scratch (background thread only).
    private bool[] _room = Array.Empty<bool>();  // shore tiles and water near land: names may overhang them
    private int[] _seen = Array.Empty<int>();
    private int[] _queue = Array.Empty<int>();
    private int _stamp;

    public override void _Ready()
    {
        // A distance-field copy of the font stays sharp at any zoom level.
        var file = (FontFile)GD.Load<FontFile>("res://fonts/Exo2-VariableFont_wght.ttf").Duplicate();
        file.MultichannelSignedDistanceField = true;
        _font = new FontVariation
        {
            BaseFont = file,
            VariationOpentype = new Godot.Collections.Dictionary { { "wght", 700 } },
        };
    }

    public void SetGame(Game game)
    {
        _placing?.Wait(); // let a placement for the old map finish before swapping arrays
        _placing = null;
        _game = game;
        _labels.Clear();
        _names.Clear();
        _troops.Clear();
        _room = RoomAroundCoasts(game.Map);
        _seen = new int[game.Map.TileCount];
        _queue = new int[game.Map.TileCount];
        _stamp = 0;
        _sinceRefresh = RefreshSeconds;
    }

    /// <summary>Land tiles on a coast, and water within <see cref="NearShoreWater"/> tiles of land.</summary>
    private static bool[] RoomAroundCoasts(GameMap map)
    {
        int w = map.Width, h = map.Height;
        var room = new bool[map.TileCount];
        var dist = new byte[map.TileCount];
        var queue = new Queue<int>();
        for (int t = 0; t < map.TileCount; t++)
        {
            if (!map.IsLand(t))
                continue;
            int x = t % w, y = t / w;
            bool shore = (x > 0 && map.IsWater(t - 1)) || (x < w - 1 && map.IsWater(t + 1))
                || (y > 0 && map.IsWater(t - w)) || (y < h - 1 && map.IsWater(t + w));
            if (!shore)
                continue;
            room[t] = true;
            queue.Enqueue(t);
        }
        while (queue.Count > 0)
        {
            int t = queue.Dequeue(), d = dist[t] + 1, x = t % w, y = t / w;
            if (d >= NearShoreWater)
                continue;
            if (x > 0) Visit(t - 1);
            if (x < w - 1) Visit(t + 1);
            if (y > 0) Visit(t - w);
            if (y < h - 1) Visit(t + w);

            void Visit(int n)
            {
                if (map.IsLand(n) || room[n])
                    return;
                room[n] = true;
                dist[n] = (byte)d;
                queue.Enqueue(n);
            }
        }
        return room;
    }

    public override void _Process(double delta)
    {
        if (_game is null)
            return;
        // Where labels go is worked out on a background thread (it reads the owner grid while
        // the game runs; an occasional stale tile doesn't matter for a label).
        if (_placing is { IsCompleted: true } done)
        {
            _placing = null;
            long started = FrameProfiler.Start();
            Apply(done.Result);
            FrameProfiler.Stop("labels place", started);
        }
        double every = _game.Phase == GamePhase.Spawning ? SpawningRefreshSeconds : RefreshSeconds;
        if ((_sinceRefresh += delta) >= every && _placing is null && !Frozen)
        {
            _sinceRefresh = 0;
            var game = _game;
            var names = new Dictionary<ushort, int>();
            foreach (var p in game.Players)
                names[p.Id] = Math.Max(1, p.Name.Length);
            _placing = System.Threading.Tasks.Task.Run(() => Place(game, names));
        }

        float t = 1 - MathF.Exp(-GlideRate * (float)delta);
        foreach (var label in _labels.Values)
        {
            label.Position = label.Position.Lerp(label.Target, t);
            label.Size += (label.TargetSize - label.Size) * t;
        }
        _mouse = MouseOverride ?? GetGlobalMousePosition();
        if (!Frozen)
            QueueRedraw();
    }

    private System.Threading.Tasks.Task<Dictionary<ushort, Placement>>? _placing;

    private readonly record struct Cluster(int Tiles, int MinX, int MinY, int MaxX, int MaxY);

    /// <summary>Every player's label spot (see the class summary).</summary>
    private Dictionary<ushort, Placement> Place(Game game, Dictionary<ushort, int> nameLengths)
    {
        var result = new Dictionary<ushort, Placement>();
        foreach (var (id, box) in LargestClusters(game))
        {
            if (!nameLengths.TryGetValue(id, out int length))
                continue;
            result[id] = PlaceName(game, id, box, length);
        }
        return result;
    }

    /// <summary>Bounding box of each owner's biggest connected piece of land.</summary>
    private Dictionary<ushort, Cluster> LargestClusters(Game game)
    {
        var map = game.Map;
        var owners = game.Owner;
        int w = map.Width, h = map.Height, stamp = ++_stamp;
        var seen = _seen;
        var queue = _queue;
        var best = new Dictionary<ushort, Cluster>();
        for (int start = 0; start < owners.Length; start++)
        {
            ushort o = owners[start];
            if (o == 0 || seen[start] == stamp || !map.IsLand(start))
                continue;
            int head = 0, tail = 0;
            queue[tail++] = start;
            seen[start] = stamp;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            while (head < tail)
            {
                int t = queue[head++], x = t % w, y = t / w;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                if (x > 0) Visit(t - 1);
                if (x < w - 1) Visit(t + 1);
                if (y > 0) Visit(t - w);
                if (y < h - 1) Visit(t + w);
            }
            if (!best.TryGetValue(o, out var b) || tail > b.Tiles)
                best[o] = new Cluster(tail, minX, minY, maxX, maxY);

            void Visit(int n)
            {
                if (seen[n] != stamp && owners[n] == o)
                {
                    seen[n] = stamp;
                    queue[tail++] = n;
                }
            }
        }
        return best;
    }

    /// <summary>
    /// OpenFront's name placement: sample the cluster's box on a coarse grid (coarser for
    /// bigger boxes), find the largest rectangle of cells that are the player's land, a
    /// coast or water near land, and centre the name in it with a font size that fits.
    /// </summary>
    private Placement PlaceName(Game game, ushort id, Cluster box, int nameLength)
    {
        var map = game.Map;
        var owners = game.Owner;
        var fallout = game.Fallout;
        int size = Math.Min(box.MaxX - box.MinX, box.MaxY - box.MinY);
        int scale = size < 25 ? 1 : size < 50 ? 2 : size < 100 ? 4 : size < 250 ? 8 : size < 500 ? 16 : 32;

        int gx0 = box.MinX / scale, gy0 = box.MinY / scale;
        int gw = box.MaxX / scale - gx0 + 1, gh = box.MaxY / scale - gy0 + 1;
        var heights = new int[gw];
        var stack = new int[gw + 1];
        int bestArea = 0, bestX = 0, bestY = 0, bestW = 0, bestH = 0;
        for (int gy = 0; gy < gh; gy++)
        {
            // Column heights of usable cells ending at this row...
            for (int gx = 0; gx < gw; gx++)
            {
                int x = (gx0 + gx) * scale, y = (gy0 + gy) * scale;
                bool usable = false;
                if (x < map.Width && y < map.Height)
                {
                    int t = y * map.Width + x;
                    usable = _room[t] || owners[t] == id || fallout[t];
                }
                heights[gx] = usable ? heights[gx] + 1 : 0;
            }
            // ...and the largest rectangle under that histogram (stack of rising bars).
            int top = 0;
            for (int i = 0; i <= gw; i++)
            {
                int hgt = i == gw ? 0 : heights[i];
                while (top > 0 && hgt < heights[stack[top - 1]])
                {
                    int barHeight = heights[stack[--top]];
                    int left = top == 0 ? 0 : stack[top - 1] + 1;
                    int width = i - left;
                    if (barHeight * width > bestArea)
                    {
                        bestArea = barHeight * width;
                        bestX = left;
                        bestY = gy - barHeight + 1;
                        bestW = width;
                        bestH = barHeight;
                    }
                }
                stack[top++] = i;
            }
        }

        float rx = bestX * scale, ry = bestY * scale, rw = bestW * scale, rh = bestH * scale;
        float fontSize = Math.Min(rw / nameLength * 2, rh / 3);
        var centre = new Vector2(
            MathF.Floor(rx + rw / 2 + box.MinX),
            MathF.Floor(ry + rh / 2 + box.MinY) - fontSize / 3);
        return new Placement(new Vector2(MathF.Ceiling(centre.X), MathF.Ceiling(centre.Y)) + new Vector2(0.5f, 0.5f), fontSize);
    }

    private void Apply(Dictionary<ushort, Placement> placements)
    {
        foreach (var key in new List<ushort>(_labels.Keys))
            if (!placements.ContainsKey(key))
                _labels.Remove(key);
        foreach (var (id, place) in placements)
        {
            if (!_labels.TryGetValue(id, out var label))
            {
                _labels[id] = label = new Label { Position = place.Centre, Size = place.Size };
            }
            label.Target = place.Centre;
            label.TargetSize = place.Size;
        }
    }

    public override void _Draw()
    {
        if (_game is null)
            return;
        if (Frozen)
            WaitForSim?.Invoke();
        long started = FrameProfiler.Start();
        DrawLabels();
        FrameProfiler.Stop("labels draw", started);
    }

    private void DrawLabels()
    {
        if (_game is null)
            return;
        float zoom = GetViewport().GetCamera2D()?.Zoom.X ?? 1;
        var view = GetCanvasTransform().AffineInverse() * GetViewportRect();
        var canvas = GetCanvasItem();

        foreach (var (id, label) in _labels)
            if (id != Hovered)
                DrawLabel(id, label, zoom, view, canvas, false);
        if (_labels.TryGetValue(Hovered, out var hovered))
            DrawLabel(Hovered, hovered, zoom, view, canvas, true); // last, so it's on top
    }

    private void DrawLabel(ushort id, Label label, float zoom, Rect2 view, Rid canvas, bool hovered)
    {
        var player = _game!.GetPlayer(id);
        if (player is null || !player.IsAlive)
            return;

        // OpenFront's sizing: a font size in tiles from the placement's size (scaled to
        // suit our wider font), measured by the letters' cap height.
        float baseSize = MathF.Max(1, MathF.Floor(label.Size));
        float nameSize = MathF.Max(4, MathF.Floor(baseSize * 0.4f));
        float nameScale = MathF.Min(baseSize * 0.25f, 3);
        float em = nameSize * nameScale * SizeFactor;
        if (hovered && em * CapHeight * zoom < HoveredCapPixels)
            em = HoveredCapPixels / (CapHeight * zoom); // too small to show (or read): readable while hovered
        float cap = em * CapHeight;
        if (cap * zoom < MinScreenCapPixels)
            return;
        float scale = em / RefSize;
        var name = NameLine(id, player.Name);
        float nameWidth = name.GetSize().X * scale;
        var pos = label.Position;
        if (!view.Grow(nameWidth + em * 4).HasPoint(pos))
            return;

        // The name's capitals are centred on the spot, the flag sits just left of it, and
        // the troop count, smaller, goes underneath.
        var flag = FlagTexture(player.Flag);
        float flagHeight = cap * FlagCapShare, flagWidth = flag is null ? 0 : flagHeight * FlagAspect;
        var troops = TroopLine(id, GameHud.FormatCount(player.Troops));
        float troopScale = scale * TroopScale;
        float troopWidth = troops.GetSize().X * troopScale;
        float troopCap = cap * TroopScale;
        float troopMiddle = pos.Y + em * TroopLineOffset;

        // Like OpenFront, a name plate under the mouse turns see-through, so you can see what's beneath.
        var plate = new Rect2(pos.X - nameWidth / 2 - flagWidth, pos.Y - Math.Max(cap, flagHeight) / 2,
            nameWidth + flagWidth, troopMiddle + troopCap - (pos.Y - Math.Max(cap, flagHeight) / 2));
        plate = plate.Merge(new Rect2(pos.X - troopWidth / 2, plate.Position.Y, troopWidth, plate.Size.Y));
        float alpha = plate.Grow(cap * 0.15f).HasPoint(_mouse) ? HoverFadeAlpha : 1f;
        var text = TextColor with { A = TextColor.A * alpha };
        var outline = OutlineColor with { A = OutlineColor.A * alpha };

        if (flag is not null)
            DrawTextureRect(flag, new Rect2(pos.X - nameWidth / 2 - flagWidth, pos.Y - flagHeight / 2, flagWidth, flagHeight), false, new Color(1, 1, 1, alpha));
        // TextLine draws from the top of its line; the baseline is one ascent (= one em) down.
        DrawSetTransform(new Vector2(pos.X - nameWidth / 2, pos.Y + cap / 2 - em), 0, new Vector2(scale, scale));
        name.DrawOutline(canvas, Vector2.Zero, RefOutline, outline);
        name.Draw(canvas, Vector2.Zero, text);
        DrawSetTransform(new Vector2(pos.X - troopWidth / 2, troopMiddle + troopCap / 2 - em * TroopScale), 0, new Vector2(troopScale, troopScale));
        troops.DrawOutline(canvas, Vector2.Zero, RefOutline, outline);
        troops.Draw(canvas, Vector2.Zero, text);
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }

    // Labels are laid out at this font size, then scaled to fit.
    private const int RefSize = 64;
    private const int RefOutline = 6;
    private readonly Dictionary<ushort, (string Text, TextLine Line)> _names = new();
    private readonly Dictionary<ushort, (string Text, TextLine Line)> _troops = new();

    private TextLine NameLine(ushort id, string text) => Cached(_names, id, text);
    private TextLine TroopLine(ushort id, string text) => Cached(_troops, id, text);

    private TextLine Cached(Dictionary<ushort, (string Text, TextLine Line)> cache, ushort id, string text)
    {
        if (cache.TryGetValue(id, out var entry) && entry.Text == text)
            return entry.Line;
        var line = new TextLine();
        line.AddString(text, _font, RefSize);
        cache[id] = (text, line);
        return line;
    }

    private Texture2D? FlagTexture(string code)
    {
        if (string.IsNullOrEmpty(code))
            return null;
        if (!_flags.TryGetValue(code, out var texture))
        {
            string path = $"res://ui/flags - large/{code}.png";
            texture = ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
            _flags[code] = texture;
        }
        return texture;
    }
}
