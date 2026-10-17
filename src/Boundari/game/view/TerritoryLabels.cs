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
        TextureFilter = TextureFilterEnum.LinearWithMipmaps; // the icons above names shrink a long way
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
    //  Icons above names (the user's ui/tags128x; OpenFront's status icons)
    // ------------------------------------------------------------------

    private const float TagHeight = 1.2f;    // as a share of the name's capitals (as tall as the flag)
    private const float TagGap = 0.3f;       // between the icons and the name, likewise
    private const float TagSpacing = 0.15f;  // between icons, as a share of an icon (OpenFront)
    private const float TagOutline = 0.035f; // dark outline around each icon, as a share of it
    private const float AllyFaded = 0.3f;    // the drained part of the alliance icon
    private static readonly Color BombTag = new(0.9f, 0.16f, 0.12f);     // bombs in the air: always red
    private static readonly Color TraitorTag = new(0.78f, 0.18f, 0.16f); // text stand-in until there's an icon
    private static readonly Color TagOutlineColour = new(0, 0, 0, 0.3f);

    /// <summary>The icons, left to right in OpenFront's order. Each is ui/tags128x/FILE128x.svg.</summary>
    private enum Tag { Leader, Traitor, Ally, Request, Target, Embargo, Bombs }
    private static readonly string[] TagFiles = { "crown", "traitor", "alliance", "request", "target", "embargo", "bombing" };

    /// <summary>One icon in the row: tint, how full it is (alliance drain; 1 = full), how visible (flashing).</summary>
    private readonly record struct TagItem(Tag Tag, Color Tint, float Fill, float Alpha);

    /// <summary>Screenshot aid (--tags): every name shows every icon, the alliance one part drained.</summary>
    public bool PreviewAllTags { get; set; }

    private ushort _leader;
    private readonly HashSet<ushort> _bombers = new(); // players with bombs in the air
    private readonly List<TagItem> _tags = new();
    private readonly StyleBoxFlat _tagBox = new() { AntiAliasing = true };
    private readonly (Texture2D? Texture, float Top, float Bottom)[] _tagIcons = new (Texture2D?, float, float)[TagFiles.Length];
    private bool _tagIconsLoaded;

    /// <summary>Loads the icons that exist (a missing one shows as a text tag), and where each one's picture starts and ends.</summary>
    private void LoadTagIcons()
    {
        _tagIconsLoaded = true;
        for (int i = 0; i < TagFiles.Length; i++)
        {
            string path = $"res://ui/tags128x/{TagFiles[i]}128x.svg";
            var texture = ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
            float top = 0, bottom = 1;
            if (texture?.GetImage() is { } image && !image.IsCompressed())
            {
                // The alliance icon drains from the top of its picture to the bottom, so find them.
                int h = image.GetHeight(), w = image.GetWidth(), first = -1, last = -1;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        if (image.GetPixel(x, y).A > 0.1f)
                        {
                            if (first < 0)
                                first = y;
                            last = y;
                            break;
                        }
                if (first >= 0)
                {
                    top = first / (float)h;
                    bottom = (last + 1) / (float)h;
                }
            }
            _tagIcons[i] = (texture, top, bottom);
        }
    }

    /// <summary>Once per draw: who leads, and who has bombs in the air.</summary>
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
            if (!nuke.Gone)
                _bombers.Add(nuke.Owner.Id);
    }

    /// <summary>OpenFront's warning flash: 30-100% visible, pulsing faster (2 to 5 times a second) as the time runs out.</summary>
    private static float Flash(float secondsLeft, float window, float ramp)
    {
        if (secondsLeft <= 0 || secondsLeft > window)
            return 1;
        float elapsed = window - secondsLeft;
        double phase = Time.GetTicksMsec() / 1000.0 * 2 + elapsed * elapsed * ramp;
        return 0.3f + 0.7f * (0.5f + 0.5f * MathF.Cos((float)(phase * Math.Tau)));
    }

    /// <summary>
    /// A row of icons centred above the name (OpenFront's): leader, traitor, ally (drains as
    /// the alliance runs out and flashes in its last 30 s), wants to ally, target, embargo
    /// (either way), bombs in the air.
    /// </summary>
    private void DrawTags(Player player, Vector2 bottomCentre, float height, float zoom, float alpha)
    {
        if (!_tagIconsLoaded)
            LoadTagIcons();
        _tags.Clear();
        var white = Colors.White;
        var game = _game!;
        float tps = Balance.TicksPerSecond;
        if (PreviewAllTags)
        {
            foreach (Tag t in Enum.GetValues<Tag>())
                _tags.Add(new TagItem(t, t == Tag.Bombs ? BombTag : white, t == Tag.Ally ? 0.45f : 1, 1));
        }
        else
        {
            if (player.Id == _leader)
                _tags.Add(new TagItem(Tag.Leader, white, 1, 1));
            if (game.IsTraitor(player))
                _tags.Add(new TagItem(Tag.Traitor, white, 1, Flash((player.TraitorUntilTick - game.Tick) / tps, 15, 0.1f)));
            if (Me is not null && player != Me)
            {
                if (game.AllianceBetween(Me, player) is { } alliance)
                {
                    int left = alliance.ExpiresTick - game.Tick;
                    float window = AllianceRules.RenewWindowTicks / tps;
                    _tags.Add(new TagItem(Tag.Ally, white, Math.Clamp(left / (float)AllianceRules.DurationTicks, 0, 1),
                        Flash(left / tps, window, 1.5f / window)));
                }
                else if (game.PendingRequest(player, Me) is not null)
                    _tags.Add(new TagItem(Tag.Request, white, 1, 1));
                if (game.IsMarkedFor(Me, player))
                    _tags.Add(new TagItem(Tag.Target, white, 1, 1));
                if (game.HasEmbargo(Me, player) || game.HasEmbargo(player, Me))
                    _tags.Add(new TagItem(Tag.Embargo, white, 1, 1));
            }
            if (player != Me && _bombers.Contains(player.Id))
                _tags.Add(new TagItem(Tag.Bombs, BombTag, 1, 1));
        }
        // An emoji they sent you (or everyone) floats above it all for a few seconds, like OpenFront's.
        if (game.ShownEmoji(player, Me) is { } emoji)
            DrawEmoji(emoji, new Vector2(bottomCentre.X, bottomCentre.Y - (_tags.Count > 0 ? height * 1.3f : 0)), height * 2f, zoom, alpha);
        if (_tags.Count == 0)
            return;

        // Icons are squares; one without a picture yet (traitor) is a text tag, laid out at a
        // whole font size near its size on screen, then scaled, like the names.
        float textHeight = height * 0.62f;
        int px = Bucket(textHeight * zoom);
        float s = textHeight / px;
        var font = ThemeDB.FallbackFont;
        float pad = px * 0.45f * s;
        float gap = height * TagSpacing, total = -gap;
        foreach (var tag in _tags)
            total += TagWidth(tag.Tag, height, font, px, s, pad) + gap;
        float x = bottomCentre.X - total / 2, y = bottomCentre.Y - height;
        DrawSetTransform(Vector2.Zero, 0, Vector2.One); // (the name and emoji leave theirs set)
        foreach (var tag in _tags)
        {
            float w = TagWidth(tag.Tag, height, font, px, s, pad);
            float a = alpha * tag.Alpha;
            var (texture, top, bottom) = _tagIcons[(int)tag.Tag];
            if (texture is null)
            {
                DrawSetTransform(new Vector2(x, y), 0, new Vector2(s, s));
                _tagBox.SetCornerRadiusAll((int)(height / s * 0.3f));
                _tagBox.BgColor = new Color(TraitorTag, 0.9f * a);
                DrawStyleBox(_tagBox, new Rect2(0, 0, w / s, height / s));
                DrawString(font, new Vector2(pad / s, height / s / 2 + px * 0.36f), TagFiles[(int)tag.Tag].ToUpperInvariant(),
                    HorizontalAlignment.Left, -1, px, new Color(1, 1, 1, a));
                DrawSetTransform(Vector2.Zero, 0, Vector2.One);
            }
            else
                DrawTagIcon(texture, new Rect2(x, y, height, height), tag.Tint, a, tag.Fill, top, bottom);
            x += w + gap;
        }
    }

    private float TagWidth(Tag tag, float height, Font font, int px, float s, float pad) =>
        _tagIcons[(int)tag].Texture is null
            ? font.GetStringSize(TagFiles[(int)tag].ToUpperInvariant(), HorizontalAlignment.Left, -1, px).X * s + pad * 2
            : height;

    /// <summary>
    /// One icon with a thin dark outline so it reads on any colour. Below the fill line it's
    /// solid, above it faded (OpenFront's alliance drain: it empties from the top down).
    /// </summary>
    private void DrawTagIcon(Texture2D texture, Rect2 at, Color tint, float alpha, float fill, float top, float bottom)
    {
        float o = at.Size.Y * TagOutline;
        var outline = new Color(TagOutlineColour, TagOutlineColour.A * alpha);
        for (int i = 0; i < 8; i++)
        {
            float angle = i * MathF.PI / 4;
            DrawTextureRect(texture, new Rect2(at.Position + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * o, at.Size), false, outline);
        }
        var solid = new Color(tint, tint.A * alpha);
        if (fill >= 1)
        {
            DrawTextureRect(texture, at, false, solid);
            return;
        }
        float cut = top + (1 - fill) * (bottom - top); // share of the icon's height that has drained
        var size = texture.GetSize();
        float cutY = at.Size.Y * cut;
        DrawTextureRectRegion(texture, new Rect2(at.Position, new Vector2(at.Size.X, cutY)),
            new Rect2(0, 0, size.X, size.Y * cut), new Color(tint, tint.A * alpha * AllyFaded));
        DrawTextureRectRegion(texture, new Rect2(at.Position + new Vector2(0, cutY), new Vector2(at.Size.X, at.Size.Y - cutY)),
            new Rect2(0, size.Y * cut, size.X, size.Y * (1 - cut)), solid);
    }

    /// <summary>An emoji this tall, its bottom middle at the given point.</summary>
    private void DrawEmoji(Emoji emoji, Vector2 bottomCentre, float height, float zoom, float alpha)
    {
        if (EmojiIcons.Texture(emoji) is not { } texture)
            return;
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
        DrawTextureRect(texture, new Rect2(bottomCentre - new Vector2(height / 2, height), new Vector2(height, height)), false, new Color(1, 1, 1, alpha));
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
