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
    // Sizes follow OpenFront's formula, scaled down for our wider lettering and measured
    // by the capitals' height. The look follows the user's mockups (TNameMockup and
    // TTroopMockup in boundari.tscn): the default font (Open Sans SemiBold) emboldened and
    // squashed to 95% height, letter-spaced, dark grey with a light outline and a faint
    // shadow down and to the right; name in capitals at 32, troops at 20, flag on the left.
    private const float SizeFactor = 0.65f;
    private const float CapHeight = 0.68f;              // the capitals, as a share of the font size
    private const float FlagCapShare = 1.2f;            // flag height, as a share of the capitals' height (mockup: 26 / 21.7)
    private const float FlagAspect = 4f / 3f;           // mockup flag: 34.7 x 26
    private const float FlagGap = 9.3f / 32;            // gap between flag and name, in font sizes
    private const float TroopLineOffset = 0.91f;        // troop line's middle below the name's, in font sizes
    private const float TroopScale = 20f / 32;
    private const float HoverFadeAlpha = 0.5f;          // OpenFront's see-through name plate under the mouse
    private const int NearShoreWater = 10;              // water this close to land counts as room (reference tiles)
    // Mockup values at their font sizes (name 32, troops 20), as shares of the font size.
    private const float NameSpacing = 8f / 32, TroopSpacing = 4f / 20;
    private const float NameOutline = 4f / 32, TroopOutline = 3f / 20;
    private const float NameShadow = 4f / 32, TroopShadow = 3f / 20;
    private const float OutlineLighten = 0.68f;         // outline = the territory's colour, mostly whitened

    private static readonly Color TextColor = new(0.117f, 0.117f, 0.117f, 1f);
    private static readonly Color ShadowColor = new(0, 0, 0, 0.157f);
    private Color[] _palette = Array.Empty<Color>();

    /// <summary>Territory colours by owner id (the outline is a light version of each).</summary>
    public void SetPalette(Color[] palette) => _palette = palette;

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

    /// <summary>The player at this screen: tags like "ally" are from their point of view.</summary>
    public Player? Me { get; set; }

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
    private Font _baseFont = null!;
    private Vector2 _mouse;     // in map tiles, for fading the name plate under it

    // Placement scratch (background thread only).
    private bool[] _room = Array.Empty<bool>();  // shore tiles and water near land: names may overhang them
    private int[] _seen = Array.Empty<int>();
    private int[] _queue = Array.Empty<int>();
    private int _stamp;

    public override void _Ready()
    {
        // Plain (not distance-field) text: emboldening a distance-field font punches holes
        // where letter strokes overlap. To stay crisp at any zoom, text is laid out at the
        // nearest of a set of fixed sizes instead (see Bucket).
        _baseFont = ThemeDB.FallbackFont;
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
        int nearShore = Math.Max(1, (int)Math.Round(NearShoreWater * map.TileScale));
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
            if (d >= nearShore)
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

        GatherTagFacts();
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
        int nameBucket = Bucket(em * zoom), troopBucket = Bucket(em * TroopScale * zoom);
        float scale = em / nameBucket;
        var name = Line(_names, id, player.Name.ToUpperInvariant(), nameBucket, NameSpacing);
        float nameWidth = name.GetSize().X * scale;
        var pos = label.Position;
        if (!view.Grow(nameWidth + em * 4).HasPoint(pos))
            return;

        // The name's capitals are centred on the spot, the flag sits just left of it, and
        // the troop count, smaller, goes underneath.
        var flag = FlagTexture(player.Flag);
        float flagHeight = cap * FlagCapShare, flagWidth = flag is null ? 0 : flagHeight * FlagAspect;
        float flagGap = flag is null ? 0 : em * FlagGap;
        var troops = Line(_troops, id, GameHud.FormatCount(player.Troops), troopBucket, TroopSpacing);
        float troopScale = em * TroopScale / troopBucket;
        float troopWidth = troops.GetSize().X * troopScale;
        float troopCap = cap * TroopScale;
        float troopMiddle = pos.Y + em * TroopLineOffset;

        // Like OpenFront, a name plate under the mouse turns see-through, so you can see what's beneath.
        var plate = new Rect2(pos.X - nameWidth / 2 - flagWidth - flagGap, pos.Y - Math.Max(cap, flagHeight) / 2,
            nameWidth + flagWidth + flagGap, troopMiddle + troopCap - (pos.Y - Math.Max(cap, flagHeight) / 2));
        plate = plate.Merge(new Rect2(pos.X - troopWidth / 2, plate.Position.Y, troopWidth, plate.Size.Y));
        float alpha = plate.Grow(cap * 0.15f).HasPoint(_mouse) ? HoverFadeAlpha : 1f;
        var text = TextColor with { A = TextColor.A * alpha };
        var shadow = ShadowColor with { A = ShadowColor.A * alpha };
        var fillColour = id < _palette.Length ? _palette[id] : Colors.White;
        var outline = new Color(fillColour.Lerp(Colors.White, OutlineLighten), alpha);

        if (flag is not null)
            DrawTextureRect(flag, new Rect2(pos.X - nameWidth / 2 - flagGap - flagWidth, pos.Y - flagHeight / 2, flagWidth, flagHeight), false, new Color(1, 1, 1, alpha));
        // TextLine draws from the top of its line; the baseline is one ascent down.
        float nameAscent = name.GetLineAscent() * scale;
        DrawText(name, new Vector2(pos.X - nameWidth / 2, pos.Y + cap / 2 - nameAscent), scale,
            NameShadow * nameBucket, NameOutline * nameBucket, text, outline, shadow, canvas);
        float troopAscent = troops.GetLineAscent() * troopScale;
        DrawText(troops, new Vector2(pos.X - troopWidth / 2, troopMiddle + troopCap / 2 - troopAscent), troopScale,
            TroopShadow * troopBucket, TroopOutline * troopBucket, text, outline, shadow, canvas);
        DrawTags(player, new Vector2(pos.X, pos.Y - Math.Max(cap, flagHeight) / 2 - cap * TagGap), cap * TagHeight, zoom, alpha);
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }

    // ------------------------------------------------------------------
    //  Tags above names (placeholders for OpenFront's icons)
    // ------------------------------------------------------------------

    private const float TagHeight = 0.75f;  // as a share of the name's capitals
    private const float TagGap = 0.3f;      // between the tags and the name, likewise
    private static readonly Color LeaderTag = new(0.85f, 0.66f, 0.12f);
    private static readonly Color AllyTag = new(0.24f, 0.62f, 0.3f);
    private static readonly Color RequestTag = new(0.36f, 0.56f, 0.36f);
    private static readonly Color TraitorTag = new(0.78f, 0.18f, 0.16f);
    private static readonly Color BombTag = new(0.3f, 0.3f, 0.32f);
    private static readonly Color EmbargoTag = new(0.55f, 0.36f, 0.12f);
    private static readonly Color TargetTag = new(0.86f, 0.24f, 0.2f);
    private static readonly Color BombAtMeTag = new(0.85f, 0.2f, 0.15f);

    private ushort _leader;
    private readonly Dictionary<ushort, bool> _bombers = new(); // player with bombs in the air: aimed at me?
    private readonly List<(string Text, Color Colour)> _tags = new();
    private readonly StyleBoxFlat _tagBox = new() { AntiAliasing = true };

    /// <summary>Once per draw: who leads, and who has bombs in the air (and whether any are coming at me).</summary>
    private void GatherTagFacts()
    {
        _leader = 0;
        int most = 0;
        foreach (var p in _game!.Players)
            if (p.IsAlive && p.HasSpawned && p.TileCount > most)
            {
                most = p.TileCount;
                _leader = p.Id;
            }
        _bombers.Clear();
        foreach (var nuke in _game.Nukes)
        {
            if (nuke.Gone)
                continue;
            bool atMe = Me is not null && _game.Owner[nuke.To] == Me.Id;
            _bombers[nuke.Owner.Id] = atMe || _bombers.GetValueOrDefault(nuke.Owner.Id);
        }
    }

    /// <summary>Small coloured tags in a row, centred above the name: leader, ally, wants to ally, traitor, embargo, target, bombs.</summary>
    private void DrawTags(Player player, Vector2 bottomCentre, float height, float zoom, float alpha)
    {
        _tags.Clear();
        if (player.Id == _leader)
            _tags.Add(("LEADER", LeaderTag));
        if (Me is not null && player != Me)
        {
            if (Me.IsAlliedWith(player))
                _tags.Add(("ALLY", AllyTag));
            else if (_game!.PendingRequest(player, Me) is not null)
                _tags.Add(("WANTS ALLIANCE", RequestTag));
        }
        if (_game!.IsTraitor(player))
            _tags.Add(("TRAITOR", TraitorTag));
        if (Me is not null && player != Me)
        {
            if (_game.HasEmbargo(Me, player))
                _tags.Add(("EMBARGOED", EmbargoTag));
            else if (_game.HasEmbargo(player, Me))
                _tags.Add(("EMBARGOES YOU", EmbargoTag));
            if (_game.IsMarkedFor(Me, player))
                _tags.Add(("TARGET", TargetTag));
        }
        if (player != Me && _bombers.TryGetValue(player.Id, out bool atMe))
            _tags.Add((atMe ? "BOMBING YOU" : "BOMBS", atMe ? BombAtMeTag : BombTag));
        if (_tags.Count == 0)
            return;

        // Laid out at a whole font size near its size on screen, then scaled, like the names.
        float textHeight = height * 0.62f;
        int px = Bucket(textHeight * zoom);
        float s = textHeight / px;
        var font = ThemeDB.FallbackFont;
        float pad = px * 0.45f, gap = px * 0.35f, boxHeight = height / s;
        float total = -gap;
        foreach (var (t, _) in _tags)
            total += font.GetStringSize(t, HorizontalAlignment.Left, -1, px).X + pad * 2 + gap;
        DrawSetTransform(new Vector2(bottomCentre.X - total * s / 2, bottomCentre.Y - height), 0, new Vector2(s, s));
        float x = 0;
        _tagBox.SetCornerRadiusAll((int)(boxHeight * 0.3f));
        foreach (var (t, colour) in _tags)
        {
            float w = font.GetStringSize(t, HorizontalAlignment.Left, -1, px).X + pad * 2;
            _tagBox.BgColor = new Color(colour, 0.9f * alpha);
            DrawStyleBox(_tagBox, new Rect2(x, 0, w, boxHeight));
            DrawString(font, new Vector2(x + pad, boxHeight / 2 + px * 0.36f), t, HorizontalAlignment.Left, -1, px, new Color(1, 1, 1, alpha));
            x += w + gap;
        }
    }

    /// <summary>One line in the mockup style: a faint shadow down-right, then a light outline, then the dark text (sizes in layout units).</summary>
    private void DrawText(TextLine line, Vector2 topLeft, float scale, float shadowOffset, float outlineSize,
        Color text, Color outline, Color shadow, Rid canvas)
    {
        DrawSetTransform(topLeft, 0, new Vector2(scale, scale));
        line.Draw(canvas, new Vector2(shadowOffset, shadowOffset), shadow);
        line.DrawOutline(canvas, Vector2.Zero, (int)MathF.Round(outlineSize), outline);
        line.Draw(canvas, Vector2.Zero, text);
    }

    // Text is laid out at the smallest of these sizes (pixels) that's at least its size on
    // screen, then scaled down a touch to fit: always sharp, never blown up from a small one.
    private static readonly int[] Buckets = { 10, 14, 20, 28, 40, 56, 80, 112, 160, 224, 320, 448 };

    private static int Bucket(float screenPixels)
    {
        foreach (int b in Buckets)
            if (b >= screenPixels)
                return b;
        return Buckets[^1];
    }

    private readonly Dictionary<(ushort Id, int Size), (string Text, TextLine Line)> _names = new();
    private readonly Dictionary<(ushort Id, int Size), (string Text, TextLine Line)> _troops = new();
    private readonly Dictionary<(int Size, float Spacing), Font> _styled = new();

    /// <summary>The mockup's styling at one size: emboldened, 95% height, letter-spaced.</summary>
    private Font Styled(int size, float spacing)
    {
        if (!_styled.TryGetValue((size, spacing), out var font))
            _styled[(size, spacing)] = font = new FontVariation
            {
                BaseFont = _baseFont,
                VariationEmbolden = 1.0f,
                VariationTransform = new Transform2D(new Vector2(1, 0), new Vector2(0, 0.95f), Vector2.Zero),
                SpacingGlyph = (int)MathF.Round(spacing * size),
            };
        return font;
    }

    private TextLine Line(Dictionary<(ushort Id, int Size), (string Text, TextLine Line)> cache, ushort id, string text, int size, float spacing)
    {
        if (cache.TryGetValue((id, size), out var entry) && entry.Text == text)
            return entry.Line;
        var line = new TextLine();
        line.AddString(text, Styled(size, spacing), size);
        cache[(id, size)] = (text, line);
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
