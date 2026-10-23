using System;
using System.Collections.Generic;

namespace Boundari.Sim;

/// <summary>
/// The front (the user's design): dots standing on a border hold it, and an attack (a
/// <see cref="Push"/>) makes every dot on one stretch of border press into a neighbour's
/// land. On each tile in front of that stretch the attackers' force is set against what
/// holds it (the defenders' dots near it, plus a little from the land itself); where the
/// attackers are stronger the line moves, faster the bigger the difference, slower through
/// forest, marsh and rivers. Gaps between defending dots hold little, so they cave in; a
/// dot pressed from several sides holds less. Dots stand a few tiles behind the line, a
/// dot's width apart; those that don't fit form a second row behind, which pushes and holds
/// as hard. They move with the line: back when it comes at them (lost only when cut off),
/// and forward when it moves away from them. Pushing into someone's land tires dots (weaker
/// both ways) until they rest; when an attack stops, its dots dig in and the line they hold
/// is hardened for a while, like land near an OpenFront Defense post.
/// </summary>
public sealed partial class Game
{
    private readonly List<Push> _pushes = new(); // in id order
    private int _nextPushId = 1;

    // How much of a dot's force reaches each tile of the square around it, in thousandths
    // (row by row, its reach radius each way; 0 = out of reach): a table for each row of dots.
    private readonly int[]?[] _reach = new int[2][];

    // Scratch for LineField: each tile's distance to the line over a square of the map.
    private int[] _field = Array.Empty<int>();
    private int _fieldX0, _fieldY0, _fieldSide;

    // Scratch: attack force gathered on each tile this tick (tiles carrying the stamp).
    private long[]? _frontForce;
    private int[]? _frontStamp;
    private int _frontSearch;
    private readonly List<int> _frontTiles = new();
    private readonly Dictionary<ushort, List<Dot>> _holding = new(); // standing dots by owner, this tick
    private readonly Dictionary<int, int> _sides = new();            // dot id -> sides it is pressed from, this tick
    private readonly List<int> _ringA = new(), _ringB = new(), _foundPath = new(); // NearestOnLine's search rings and result
    private readonly HashSet<int> _attacking = new();                // attacker id << 16 | target id, this tick
    private readonly Dictionary<int, (long Tick, List<ushort> Owners)> _faced = new(); // dot id -> whose land it faces
    private readonly HashSet<int> _tiring = new();                   // ids of dots pushing into someone's land, this tick
    private readonly Dictionary<int, (ushort Owner, long Until)> _hardened = new(); // tile -> who dug in there, until when
    private readonly List<int> _expired = new();

    /// <summary>Goes up whenever the hardened tiles change (so the map redraws them only then).</summary>
    public int HardenedVersion { get; private set; }

    /// <summary>True if this tile's owner has dug in here: attacks on it take <see cref="DotRules.HardenedSlowdown"/> times as long.</summary>
    public bool IsHardened(int tile) =>
        _hardened.Count > 0 && _hardened.TryGetValue(tile, out var h) && h.Owner == Owner[tile] && Tick < h.Until;

    /// <summary>The tiles hardened now (any order).</summary>
    public IEnumerable<int> HardenedTiles
    {
        get
        {
            foreach (var (tile, h) in _hardened)
                if (h.Owner == Owner[tile] && Tick < h.Until)
                    yield return tile;
        }
    }

    public IReadOnlyList<Push> Pushes => _pushes;

    /// <summary>The attack this dot is part of, if any (the first, if its stretch faces two).</summary>
    public Push? PushOf(Dot dot)
    {
        foreach (var p in _pushes)
            if (p.Members.Contains(dot))
                return p;
        return null;
    }

    /// <summary>True if the dot is part of an attack into <paramref name="target"/>'s land (Nobody = wilderness).</summary>
    public bool IsPushingInto(Dot dot, ushort target) => _pushes.Exists(p => p.TargetId == target && p.Members.Contains(dot));

    /// <summary>
    /// The dot a click on this tile would send (or stop): the player's standing dot nearest the
    /// tile, of those facing the land it is on. Null if the tile can't be attacked (own land,
    /// an ally's, water) or no dot of theirs faces that land.
    /// </summary>
    public Dot? DotToPush(Player player, int tile)
    {
        if (tile < 0 || tile >= Map.TileCount || !Map.IsLand(tile))
            return null;
        ushort target = Owner[tile];
        if (target == player.Id || target == Nobody || GetPlayer(target) is not { IsAlive: true } || player.Allies.Contains(target))
            return null;
        int w = Map.Width, x = tile % w, y = tile / w;
        Dot? best = null;
        long bestDist = long.MaxValue;
        foreach (var d in _dots)
        {
            if (d.Owner != player || d.IsWalking)
                continue;
            long dx = d.Tile % w - x, dy = d.Tile / w - y, dist = dx * dx + dy * dy;
            if (dist < bestDist && Faces(d, target))
                (best, bestDist) = (d, dist);
        }
        return best;
    }

    /// <summary>A dot's force: strength level x stamina, in thousandths of a fresh level-1 dot.</summary>
    public static long DotForce(Dot dot) => (long)dot.Strength * dot.Stamina * DotRules.DotForce / DotRules.MaxStamina;

    /// <summary>How many tiles behind the line dots of this row stand (0 = the first row, 1 = the second).</summary>
    private int BackOf(int row) => row == 0 ? Rules.DotStandBack : Rules.DotSecondRowBack;

    /// <summary>How far from a first-row dot its force reaches, in tiles: across the land it stands back on, then its reach past the line.</summary>
    private int ReachRadius => ReachRadiusOf(0);

    /// <summary>How far from a dot of this row its force reaches.</summary>
    private int ReachRadiusOf(int row) => BackOf(row) - 1 + Rules.DotReach;

    /// <summary>
    /// How much of a dot's force reaches a tile this far away (thousandths; 0 beyond its
    /// reach): all of it across its own land up to the line it stands back from (over the
    /// first row, for a second-row dot), then fading past the line exactly as if it stood on
    /// the line itself.
    /// </summary>
    private int ReachWeight(int dx, int dy, int row = 0)
    {
        int r = ReachRadiusOf(row), side = 2 * r + 1, back = BackOf(row), reach = Rules.DotReach;
        if (_reach[row] is not { } table)
        {
            _reach[row] = table = new int[side * side];
            for (int y = -r; y <= r; y++)
                for (int x = -r; x <= r; x++)
                {
                    double past = Math.Max(0, Math.Sqrt(x * x + y * y) - (back - 1));
                    table[(y + r) * side + x + r] = Math.Max(0, (int)Math.Round(1000 * (1 - past / (reach + 1))));
                }
        }
        return Math.Abs(dx) > r || Math.Abs(dy) > r ? 0 : table[(dy + r) * side + dx + r];
    }

    /// <summary>
    /// Works out at once, for every tile within <paramref name="r"/> tiles of (x, y), what
    /// <see cref="LineDistance"/> would say (capped at max + 1): a chessboard distance
    /// transform over the square, in two passes. Read it with <see cref="FieldAt"/>.
    /// </summary>
    private void LineField(int x, int y, int r, ushort me, ushort? target, int max)
    {
        int big = r + max + 1, side = 2 * big + 1, cap = max + 1, w = Map.Width, h = Map.Height;
        if (_field.Length < side * side)
            _field = new int[side * side];
        _fieldX0 = x - big;
        _fieldY0 = y - big;
        _fieldSide = side;
        var f = _field;
        for (int j = 0; j < side; j++)
            for (int i = 0; i < side; i++)
            {
                int tx = _fieldX0 + i, ty = _fieldY0 + j, k = j * side + i, d = cap;
                if (tx >= 0 && ty >= 0 && tx < w && ty < h)
                {
                    int t = ty * w + tx;
                    ushort o = Owner[t];
                    if (o != me && Map.IsLand(t) && (target is null || o == target))
                        d = 0;
                }
                if (d != 0)
                {
                    if (i > 0) d = Math.Min(d, f[k - 1] + 1);
                    if (j > 0)
                    {
                        d = Math.Min(d, f[k - side] + 1);
                        if (i > 0) d = Math.Min(d, f[k - side - 1] + 1);
                        if (i < side - 1) d = Math.Min(d, f[k - side + 1] + 1);
                    }
                }
                f[k] = d;
            }
        for (int j = side - 1; j >= 0; j--)
            for (int i = side - 1; i >= 0; i--)
            {
                int k = j * side + i, d = f[k];
                if (d == 0)
                    continue;
                if (i < side - 1) d = Math.Min(d, f[k + 1] + 1);
                if (j < side - 1)
                {
                    d = Math.Min(d, f[k + side] + 1);
                    if (i < side - 1) d = Math.Min(d, f[k + side + 1] + 1);
                    if (i > 0) d = Math.Min(d, f[k + side - 1] + 1);
                }
                f[k] = Math.Min(d, cap);
            }
    }

    /// <summary>A tile's distance to the line from the last <see cref="LineField"/> (the tile must be inside its square).</summary>
    private int FieldAt(int tile) => _field[(tile / Map.Width - _fieldY0) * _fieldSide + tile % Map.Width - _fieldX0];

    /// <summary>
    /// How far a tile of <paramref name="me"/>'s is from the line with <paramref name="target"/>'s
    /// land (Nobody = the wilderness; null = anyone else's land), in tiles (Chebyshev), looking
    /// up to <paramref name="max"/>; max + 1 if it is further.
    /// </summary>
    private int LineDistance(int tile, ushort me, ushort? target, int max)
    {
        int w = Map.Width, h = Map.Height, x0 = tile % w, y0 = tile / w;
        for (int d = 1; d <= max; d++)
            for (int dy = -d; dy <= d; dy++)
            {
                int y = y0 + dy;
                if (y < 0 || y >= h)
                    continue;
                int step = Math.Abs(dy) == d ? 1 : 2 * d; // full top and bottom rows, just the ends of the others
                for (int dx = -d; dx <= d; dx += step)
                {
                    int x = x0 + dx;
                    if (x < 0 || x >= w)
                        continue;
                    int t = y * w + x;
                    ushort o = Owner[t];
                    if (o != me && Map.IsLand(t) && (target is null ? true : o == target))
                        return d;
                }
            }
        return max + 1;
    }

    private readonly Dictionary<ushort, List<Dot>> _standing = new(); // each player's dots, this tick (FollowLine)

    /// <summary>
    /// True if another of the dot's owner's dots is closer than just under a dot's width
    /// (nineteen twentieths of the spacing) to this tile, standing there or heading there: the
    /// two would overlap (so a second-row dot stands between two in front, not right behind one). For a dot checking its own place, only older dots count, so of two dots on
    /// one spot the newer one moves off and the older stays.
    /// </summary>
    private bool Crowded(Dot dot, int tile, bool olderOnly)
    {
        if (!_standing.TryGetValue(dot.Owner.Id, out var dots))
            return false;
        int w = Map.Width, x = tile % w, y = tile / w;
        long near = Math.Max(1, Rules.DotSpacing * 19 / 20);
        foreach (var o in dots)
        {
            if (o == dot || (olderOnly && o.Id > dot.Id))
                continue;
            int at = o.Destination;
            long dx = at % w - x, dy = at / w - y;
            if (dx * dx + dy * dy < near * near)
                return true;
        }
        return false;
    }

    /// <summary>True if the tile touches (4 ways) land that isn't <paramref name="me"/>'s: one of their border tiles.</summary>
    private bool TouchesOthers(int tile, ushort me)
    {
        Span<int> nb = stackalloc int[4];
        int count = LandNeighbours(tile, nb);
        for (int k = 0; k < count; k++)
            if (Owner[nb[k]] != me)
                return true;
        return false;
    }

    /// <summary>True if a dot of <paramref name="me"/>'s belongs on this tile in that row: <see cref="BackOf"/> tiles behind the line with that land (null = anyone's).</summary>
    private bool IsStandTile(int tile, ushort me, ushort? target, int row = 0) =>
        Owner[tile] == me && LineDistance(tile, me, target, BackOf(row)) == BackOf(row);

    /// <summary>How many tiles a dot stands behind the line with anyone else's land (for tests and drawing).</summary>
    public int DistanceToLine(Dot dot) => LineDistance(dot.Tile, dot.Owner.Id, null, ReachRadiusOf(1));

    /// <summary>
    /// Where a dot sent to this border tile stands in that row: the nearest of its owner's
    /// tiles the right distance behind the line (the border tile itself if the land is too thin).
    /// </summary>
    private int StandTileNear(Player player, int borderTile, int row = 0)
    {
        int back = BackOf(row), w = Map.Width, h = Map.Height, r = back + 2, x0 = borderTile % w, y0 = borderTile / w;
        if (row > 0)
            LineField(x0, y0, r, player.Id, null, back);
        int best = borderTile;
        long bestDist = long.MaxValue;
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                int x = x0 + dx, y = y0 + dy;
                if (x < 0 || y < 0 || x >= w || y >= h)
                    continue;
                int t = y * w + x;
                long dist = dx * dx + dy * dy;
                if (dist < bestDist && (row > 0 ? Owner[t] == player.Id && FieldAt(t) == back : IsStandTile(t, player.Id, null)))
                    (best, bestDist) = (t, dist);
            }
        return best;
    }

    /// <summary>True if the tile is land of <paramref name="target"/> (or wilderness, for Nobody) touching the player's land.</summary>
    private bool IsFrontTile(int tile, ushort target, ushort player) =>
        Owner[tile] == target && Map.IsLand(tile) && Touches(tile, player);

    /// <summary>True if the dot stands within reach of the line with <paramref name="target"/> (worked out once a tick).</summary>
    private bool Faces(Dot dot, ushort target)
    {
        if (_faced.TryGetValue(dot.Id, out var seen) && seen.Tick == Tick && Owner[dot.Tile] == dot.Owner.Id)
            return seen.Owners.Contains(target);
        var owners = seen.Owners ?? new List<ushort>();
        owners.Clear();
        int w = Map.Width, h = Map.Height, r = ReachRadiusOf(dot.Row), x0 = dot.Tile % w, y0 = dot.Tile / w;
        ushort me = dot.Owner.Id;
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                int x = x0 + dx, y = y0 + dy;
                if (x < 0 || y < 0 || x >= w || y >= h || ReachWeight(dx, dy, dot.Row) == 0)
                    continue;
                int t = y * w + x;
                ushort o = Owner[t];
                if (o != me && !owners.Contains(o) && Map.IsLand(t) && Touches(t, me))
                    owners.Add(o);
            }
        _faced[dot.Id] = (Tick, owners);
        return owners.Contains(target);
    }

    private void StartPush(Player player, int dotId, int targetTile)
    {
        if (DotById(dotId) is not { } dot || dot.Owner != player || dot.IsWalking)
            return;
        if (targetTile < 0 || targetTile >= Map.TileCount || !Map.IsLand(targetTile))
            return;
        ushort target = Owner[targetTile];
        if (target == player.Id || target == Nobody)
            return; // (the wilderness is expanded into, not pushed: see GameExpansion.cs)
        if ((GetPlayer(target) is not { IsAlive: true } || player.Allies.Contains(target)))
            return; // (allies can't attack each other)
        if (!Faces(dot, target) || _pushes.Exists(p => p.Attacker == player && p.TargetId == target && p.Members.Contains(dot)))
            return;
        var push = new Push(_nextPushId++, player, target);
        push.Members.Add(dot);
        _pushes.Add(push);
        GatherMembers(push);
        MergePushes();
    }

    private void StopPush(Player player, int dotId)
    {
        if (DotById(dotId) is not { } dot || dot.Owner != player)
            return;
        for (int i = _pushes.Count - 1; i >= 0; i--)
            if (_pushes[i].Members.Contains(dot))
            {
                Harden(_pushes[i]);
                _pushes.RemoveAt(i);
            }
    }

    /// <summary>A stopped attack's dots dig in: their owner's border within their reach is hardened for a while.</summary>
    private void Harden(Push push)
    {
        int w = Map.Width, h = Map.Height;
        var owner = push.Attacker;
        foreach (var dot in push.Members)
        {
            int x0 = dot.Tile % w, y0 = dot.Tile / w, r = ReachRadiusOf(dot.Row);
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = x0 + dx, y = y0 + dy;
                    if (x < 0 || y < 0 || x >= w || y >= h || ReachWeight(dx, dy, dot.Row) == 0)
                        continue;
                    int t = y * w + x;
                    if (Owner[t] == owner.Id && owner.Border.Contains(t))
                        _hardened[t] = (owner.Id, Tick + DotRules.HardenTicks);
                }
        }
        HardenedVersion++;
    }

    /// <summary>Forgets hardened tiles whose time is up or that changed hands.</summary>
    private void ForgetHardened()
    {
        if (_hardened.Count == 0)
            return;
        _expired.Clear();
        foreach (var (tile, h) in _hardened)
            if (h.Owner != Owner[tile] || Tick >= h.Until)
                _expired.Add(tile);
        foreach (int t in _expired)
            _hardened.Remove(t);
        if (_expired.Count > 0)
            HardenedVersion++;
    }

    /// <summary>Dots pushing into someone's land tire; all others rest.</summary>
    private void TireAndRest()
    {
        _tiring.Clear();
        foreach (var p in _pushes)
            if (p.TargetId != Nobody)
                foreach (var d in p.Members)
                    _tiring.Add(d.Id);
        foreach (var d in _dots)
            d.Stamina = _tiring.Contains(d.Id)
                ? Math.Max(DotRules.MinStamina, d.Stamina - DotRules.TirePerTick)
                : Math.Min(DotRules.MaxStamina, d.Stamina + DotRules.RestPerTick);
    }

    /// <summary>The attack's dots: those still on its stretch, plus every standing dot linked to them along the line.</summary>
    private void GatherMembers(Push push)
    {
        var candidates = new List<Dot>();
        foreach (var d in _dots)
            if (d.Owner == push.Attacker && !d.IsWalking && Faces(d, push.TargetId))
                candidates.Add(d);
        var members = push.Members.FindAll(candidates.Contains);
        int w = Map.Width, link = Rules.DotLink;
        for (int i = 0; i < members.Count; i++)
        {
            int a = members[i].Tile;
            foreach (var c in candidates)
                if (Math.Max(Math.Abs(a % w - c.Tile % w), Math.Abs(a / w - c.Tile / w)) <= link && !members.Contains(c))
                    members.Add(c);
        }
        members.Sort((a, b) => a.Id.CompareTo(b.Id));
        push.Members.Clear();
        push.Members.AddRange(members);
    }

    /// <summary>Two attacks by the same player into the same land that now share dots become one (the older).</summary>
    private void MergePushes()
    {
        for (int i = 0; i < _pushes.Count; i++)
            for (int j = _pushes.Count - 1; j > i; j--)
            {
                Push a = _pushes[i], b = _pushes[j];
                if (a.Attacker != b.Attacker || a.TargetId != b.TargetId || !a.Members.Exists(b.Members.Contains))
                    continue;
                foreach (var d in b.Members)
                    if (!a.Members.Contains(d))
                        a.Members.Add(d);
                a.Members.Sort((x, y) => x.Id.CompareTo(y.Id));
                foreach (var (tile, done) in b.Progress)
                    a.Progress[tile] = Math.Max(done, a.Progress.GetValueOrDefault(tile));
                _pushes.RemoveAt(j);
            }
    }

    /// <summary>Every attack presses on the line for one tick; tiles pushed far enough change hands.</summary>
    private void RunFronts()
    {
        _attacking.Clear();
        ForgetHardened();
        for (int i = _pushes.Count - 1; i >= 0; i--)
        {
            var p = _pushes[i];
            bool over = p.Attacker.TileCount == 0
                || (p.TargetId != Nobody && (_players[p.TargetId].TileCount == 0 || p.Attacker.Allies.Contains(p.TargetId)));
            if (!over)
                GatherMembers(p);
            if (over && p.Attacker.TileCount > 0)
                Harden(p); // (its target is gone or became an ally: the line it holds digs in)
            if (over || p.Members.Count == 0)
                _pushes.RemoveAt(i);
        }
        MergePushes();
        TireAndRest();
        if (_pushes.Count == 0)
            return;

        _holding.Clear();
        _sides.Clear();
        foreach (var p in _pushes)
            _attacking.Add(p.Attacker.Id << 16 | p.TargetId);
        foreach (var d in _dots)
            if (!d.IsWalking)
            {
                if (!_holding.TryGetValue(d.Owner.Id, out var list))
                    _holding[d.Owner.Id] = list = new List<Dot>();
                list.Add(d);
            }
        foreach (var p in _pushes)
            PressOn(p);
    }

    private void PressOn(Push push)
    {
        int n = Map.TileCount, w = Map.Width, h = Map.Height;
        _frontForce ??= new long[n];
        _frontStamp ??= new int[n];
        int stamp = ++_frontSearch;
        ushort me = push.Attacker.Id, target = push.TargetId;
        _frontTiles.Clear();
        foreach (var dot in push.Members)
        {
            long force = DotForce(dot);
            int x0 = dot.Tile % w, y0 = dot.Tile / w, r = ReachRadiusOf(dot.Row);
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = x0 + dx, y = y0 + dy, weight = ReachWeight(dx, dy, dot.Row);
                    if (x < 0 || y < 0 || x >= w || y >= h || weight == 0)
                        continue;
                    int t = y * w + x;
                    if (!IsFrontTile(t, target, me))
                        continue;
                    if (_frontStamp[t] != stamp)
                    {
                        _frontStamp[t] = stamp;
                        _frontForce[t] = 0;
                        _frontTiles.Add(t);
                    }
                    _frontForce[t] += force * weight / 1000;
                }
        }
        _frontTiles.Sort();

        // Only tiles pushed this tick keep what has been pushed into them.
        var progress = new Dictionary<int, long>();
        var taken = new List<int>();
        foreach (int t in _frontTiles)
        {
            long net = _frontForce[t] - Holding(t, target);
            if (net <= 0)
                continue;
            long step = Math.Min(Rules.DotMaxPushPerTick, net * Rules.DotPushSpeedPerTick / DotRules.DotForce);
            if (IsHardened(t))
                step /= DotRules.HardenedSlowdown;
            long done = push.Progress.GetValueOrDefault(t) + step;
            if (done >= 100L * CoverTenths[t]) // farmland: one whole tile; forest twice that
                taken.Add(t);
            else
                progress[t] = done;
        }
        push.Progress.Clear();
        foreach (var (t, done) in progress)
            push.Progress[t] = done;
        foreach (int t in taken)
            if (IsFrontTile(t, target, me))
                SetOwner(t, me);
    }

    /// <summary>How much holds this tile of <paramref name="owner"/>'s against an attack (thousandths of a fresh level-1 dot).</summary>
    public long Holding(int tile, ushort owner)
    {
        if (owner == Nobody)
            return 0; // wilderness
        var player = _players[owner];
        if (player.IsBot)
            return DotRules.LocalsForce;
        long hold = DotRules.UndefendedForce;
        if (!_holding.TryGetValue(owner, out var dots))
            return hold;
        int w = Map.Width, x = tile % w, y = tile / w;
        foreach (var d in dots)
        {
            int weight = ReachWeight(d.Tile % w - x, d.Tile / w - y, d.Row);
            if (weight > 0)
                hold += DotForce(d) * weight / 1000 / SidesPressed(d);
        }
        return hold;
    }

    /// <summary>
    /// How hard this tile holds against an attack right now, worked out afresh from where its
    /// owner's standing dots are (for the AI picking a weak spot; leaves out how many sides they
    /// are pressed from): thousandths of a fresh level-1 dot, times the slowdown if hardened.
    /// </summary>
    public long DefenceAt(int tile)
    {
        ushort owner = Owner[tile];
        if (owner == Nobody)
            return 0;
        var player = _players[owner];
        if (player.IsBot)
            return DotRules.LocalsForce;
        long hold = DotRules.UndefendedForce;
        int w = Map.Width, x = tile % w, y = tile / w;
        foreach (var d in _dots)
            if (d.Owner == player && !d.IsWalking && ReachWeight(d.Tile % w - x, d.Tile / w - y, d.Row) is var weight and > 0)
                hold += DotForce(d) * weight / 1000;
        return IsHardened(tile) ? hold * DotRules.HardenedSlowdown : hold;
    }

    /// <summary>
    /// How many sides a dot is pressed from (1-3): from how much of the land around it (a
    /// ring <see cref="ScaledRules.DotSidesRing"/> tiles out) is held by players attacking its owner.
    /// </summary>
    public int SidesPressed(Dot dot)
    {
        if (_sides.TryGetValue(dot.Id, out int sides))
            return sides;
        int w = Map.Width, h = Map.Height, r = Rules.DotSidesRing, x0 = dot.Tile % w, y0 = dot.Tile / w;
        int land = 0, enemy = 0;
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                int x = x0 + dx, y = y0 + dy;
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r || x < 0 || y < 0 || x >= w || y >= h)
                    continue;
                int t = y * w + x;
                if (!Map.IsLand(t))
                    continue;
                land++;
                ushort o = Owner[t];
                if (o != Nobody && o != dot.Owner.Id && _attacking.Contains(o << 16 | dot.Owner.Id))
                    enemy++;
            }
        sides = enemy * 8 > land * 7 ? 3 : enemy * 8 > land * 5 ? 2 : 1;
        _sides[dot.Id] = sides;
        return sides;
    }

    /// <summary>Of the land on a ring around the tile, how much is held by players attacking <paramref name="owner"/>, in 1/1000.</summary>
    private int AttackedShare(int tile, ushort owner)
    {
        int w = Map.Width, h = Map.Height, r = Rules.DotSidesRing, x0 = tile % w, y0 = tile / w;
        int land = 0, enemy = 0;
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                int x = x0 + dx, y = y0 + dy;
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r || x < 0 || y < 0 || x >= w || y >= h || !Map.IsLand(y * w + x))
                    continue;
                land++;
                ushort o = Owner[y * w + x];
                if (o != Nobody && o != owner && _attacking.Contains(o << 16 | owner))
                    enemy++;
            }
        return land == 0 ? 0 : enemy * 1000 / land;
    }

    /// <summary>
    /// A defending dot pressed from several sides (the attack is flowing round it) steps back
    /// along the line: to the neighbouring standing tile least surrounded by the attackers.
    /// -1 if it is not pressed so, or no step makes it better.
    /// </summary>
    private int GiveGround(Dot dot)
    {
        if (_attacking.Count == 0 || dot.Row != 0 || SidesPressed(dot) < 2)
            return -1;
        int w = Map.Width, h = Map.Height, x0 = dot.Tile % w, y0 = dot.Tile / w, best = -1;
        ushort me = dot.Owner.Id;
        int bestShare = AttackedShare(dot.Tile, me);
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = x0 + dx, y = y0 + dy;
                if ((dx | dy) == 0 || x < 0 || y < 0 || x >= w || y >= h)
                    continue;
                int t = y * w + x;
                if (!IsStandTile(t, me, null) || (dx != 0 && dy != 0 && (Owner[y0 * w + x] != me || Owner[y * w + x0] != me)))
                    continue;
                int share = AttackedShare(t, me);
                if (share < bestShare)
                    (best, bestShare) = (t, share);
            }
        return best;
    }

    /// <summary>
    /// Dots move with the line: one whose tile was taken steps back onto its owner's nearest
    /// tile (lost if there is none close: cut off); a standing dot keeps its distance behind
    /// the line, walking after it when it moves away and back when it comes closer (an
    /// attacking dot measured from the land it attacks, any other from anyone's), in the
    /// first row if there's room near it, else in the second (stepping up when room appears);
    /// a defending dot the attack flows round steps back along the line rather than be cut off.
    /// </summary>
    private void FollowLine()
    {
        for (int i = _dots.Count - 1; i >= 0; i--)
        {
            var d = _dots[i];
            if (Owner[d.Tile] == d.Owner.Id)
                continue;
            int back = FallBackTile(d);
            if (back < 0)
            {
                RemoveDot(d);
                continue;
            }
            d.Tile = back;
            d.Progress = 0;
            if (d.IsWalking && Owner[d.Destination] == d.Owner.Id && FindDotPath(d.Owner, back, d.Destination, DotRules.DetourSearchTiles) is { } path)
                SetOff(d, path);
            else
                SetOff(d, new[] { back });
        }

        // Where each player's dots are, so dots keep apart along the line.
        _standing.Clear();
        foreach (var d in _dots)
        {
            if (!_standing.TryGetValue(d.Owner.Id, out var list))
                _standing[d.Owner.Id] = list = new List<Dot>();
            list.Add(d);
        }

        int longSearches = 0, front = Rules.DotStandBack, second = Rules.DotSecondRowBack;
        foreach (var d in _dots)
        {
            if (d.IsWalking)
                continue;
            var push = PushOf(d);
            int dist = LineDistance(d.Tile, d.Owner.Id, push?.TargetId, front);
            if (dist > front && Tick < d.LookAgainAt)
                continue; // (off the line, waiting to look again)
            if (dist > front)
                dist = LineDistance(d.Tile, d.Owner.Id, push?.TargetId, ReachRadiusOf(1));
            d.Row = dist > (front + second) / 2 ? 1 : 0;
            bool onLine = (dist == front || dist == second) && Owner[d.Tile] == d.Owner.Id && !Crowded(d, d.Tile, olderOnly: true);
            if (!onLine && Tick < d.LookAgainAt)
                continue;
            int goal;
            if (!onLine)
            {
                // Room in the first row near it; else in the second; else further along the
                // first. A dot within reach of the line that finds none waits a moment.
                bool near = dist <= ReachRadiusOf(1);
                goal = NearestOnLine(d, push, ReachRadius, 0);
                if (goal < 0 && near)
                    goal = NearestOnLine(d, push, second, 1);
                if (goal < 0 && near && FreePlaceAlong(d, ReachRadius * DotRules.FarRoomReach) is var place and >= 0
                    && FindDotPath(d.Owner, d.Tile, place, DotRules.DetourSearchTiles) is { } way)
                {
                    SetOff(d, way);
                    continue;
                }
                if (goal < 0 && near)
                {
                    d.LookAgainAt = Tick + DotRules.NoRoomWaitTicks;
                    goal = -2;
                }
            }
            else if (dist == second) // in the second row: step up when there's room in front
                goal = (Tick + d.Id) % DotRules.StepUpTicks == 0 ? NearestOnLine(d, push, ReachRadius, 0) : -1;
            else
                goal = push is null ? GiveGround(d) : -1;
            if (goal < 0 && !onLine && dist <= front)
                goal = -2; // nearer the line than it should be, but there's no better tile close by (thin land): stay
            if (goal == -1 && !onLine && push is null)
            {
                // The border ran away from it (old-style attacks spread faster than dots walk):
                // walk to wherever it is now. Its land may have no border at all (a whole
                // island): then look again in a while. Only a few such long searches a tick.
                if (++longSearches > DotRules.LongSearchesPerTick)
                    d.LookAgainAt = Tick + 1;
                else if (WayToFarLine(d) is { } way)
                {
                    SetOff(d, way);
                    d.LookAgainWait = 0;
                }
                else
                {
                    d.LookAgainWait = Math.Clamp(d.LookAgainWait * 2, DotRules.LookAgainTicks, DotRules.LookAgainMaxTicks);
                    d.LookAgainAt = Tick + d.LookAgainWait;
                }
                continue;
            }
            if (goal < 0)
            {
                d.Progress = 0;
                continue;
            }
            int w = Map.Width;
            bool diagonal = d.Tile % w != goal % w && d.Tile / w != goal / w;
            long cost = diagonal ? DotRules.DiagonalStepCost : DotRules.StepCost;
            if (d.Progress < cost)
                d.Progress += Rules.DotWalkSpeedPerTick;
            if (d.Progress >= cost)
            {
                d.Progress -= cost;
                d.Tile = d.Destination = goal;
            }
        }
    }

    /// <summary>
    /// For a dot with no room near it in either row: the nearest free place in the first row
    /// further along its owner's border, following the border up to <paramref name="steps"/>
    /// tiles from the border tile nearest the dot; -1 if none.
    /// </summary>
    private int FreePlaceAlong(Dot dot, int steps)
    {
        var player = dot.Owner;
        int start = BorderTileWithin(player, dot.Tile, ReachRadiusOf(1));
        if (start < 0)
            return -1;
        var depth = new Dictionary<int, int> { [start] = 0 };
        var queue = new Queue<int>();
        queue.Enqueue(start);
        int w = Map.Width, h = Map.Height;
        while (queue.Count > 0)
        {
            int t = queue.Dequeue(), d = depth[t];
            if (!Crowded(dot, t, olderOnly: false) && StandTileNear(player, t) is var spot && spot != t && !Crowded(dot, spot, olderOnly: false))
                return spot;
            if (d == steps)
                continue;
            int x = t % w, y = t / w;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((dx | dy) == 0 || nx < 0 || ny < 0 || nx >= w || ny >= h)
                        continue;
                    int next = ny * w + nx;
                    if (player.Border.Contains(next) && depth.TryAdd(next, d + 1))
                        queue.Enqueue(next);
                }
        }
        return -1;
    }

    /// <summary>
    /// The walk to the line from a dot far behind it: through its own land towards the nearest
    /// border, as far as the first tile the right distance behind it. Null if its land has no
    /// border within reach of the search (a whole island, say).
    /// </summary>
    private int[]? WayToFarLine(Dot dot)
    {
        if (NearestOnLine(dot, null, int.MaxValue, 0, toBorder: true, maxTiles: DotRules.FarLineSearchTiles) < 0)
            return null;
        int stop = _foundPath.FindIndex(t => IsStandTile(t, dot.Owner.Id, null));
        return _foundPath.GetRange(0, stop < 0 ? _foundPath.Count : stop + 1).ToArray();
    }

    /// <summary>The owner's tile nearest a dot whose tile was just taken, within <see cref="ScaledRules.DotFallBack"/> tiles (a border tile on a tie); -1 if none.</summary>
    private int FallBackTile(Dot dot)
    {
        int w = Map.Width, h = Map.Height, r = Rules.DotFallBack, x0 = dot.Tile % w, y0 = dot.Tile / w, best = -1;
        long bestScore = long.MaxValue;
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                int x = x0 + dx, y = y0 + dy;
                if (x < 0 || y < 0 || x >= w || y >= h)
                    continue;
                int t = y * w + x;
                if (Owner[t] != dot.Owner.Id)
                    continue;
                long score = (long)(dx * dx + dy * dy) * 2 + (dot.Owner.Border.Contains(t) ? 0 : 1);
                if (score < bestScore || (score == bestScore && t < best))
                    (best, bestScore) = (t, score);
            }
        return best;
    }

    /// <summary>
    /// The first step towards the nearest free tile where the dot belongs in that row
    /// (standing back from the land it attacks, or from anyone's), through its owner's land up
    /// to <paramref name="radius"/> tiles away; -1 if none. Of the nearest, the one most exposed
    /// to that land (so an attacking dot keeps to the head of its attack rather than drifting
    /// sideways along the old line). With <paramref name="toBorder"/>, the nearest border tile
    /// instead. The whole way there is left in <see cref="_foundPath"/>.
    /// </summary>
    private int NearestOnLine(Dot dot, Push? push, int radius, int row = 0, bool toBorder = false, int maxTiles = DotRules.PathSearchTiles)
    {
        int w = Map.Width, h = Map.Height, r = radius, x0 = dot.Tile % w, y0 = dot.Tile / w, searched = 0, back = BackOf(row);
        ushort me = dot.Owner.Id;
        bool field = false;
        int rings = 0;
        _pathFrom ??= new int[Map.TileCount];
        int stamp = NewPathStamp();
        _pathStamp![dot.Tile] = stamp;
        _pathFrom[dot.Tile] = -1;
        List<int> ring = _ringA, next = _ringB;
        ring.Clear();
        ring.Add(dot.Tile);

        // How many tiles of the land faced (anyone else's, for a dot not attacking) are within
        // a tile past the line around this one.
        int e = Rules.DotStandBack + 1;
        int Exposure(int t)
        {
            int count = 0, x = t % w, y = t / w;
            for (int dy = -e; dy <= e; dy++)
                for (int dx = -e; dx <= e; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((dx | dy) == 0 || nx < 0 || ny < 0 || nx >= w || ny >= h || !Map.IsLand(ny * w + nx))
                        continue;
                    ushort o = Owner[ny * w + nx];
                    if (push is null ? o != me : o == push.TargetId)
                        count++;
                }
            return count;
        }

        while (ring.Count > 0)
        {
            // Close by, each tile is looked at on its own; a search going further (or for the
            // second row, far from the line) works out the whole square's distances at once.
            if (!toBorder && !field && (row > 0 || rings++ == DotRules.TilesBeforeField))
            {
                LineField(x0, y0, r, me, push?.TargetId, back);
                field = true;
            }
            int best = -1, bestExposure = 0;
            foreach (int t in ring)
            {
                if (t == dot.Tile || !(toBorder ? TouchesOthers(t, me) : (field ? FieldAt(t) == back : IsStandTile(t, me, push?.TargetId)) && !Crowded(dot, t, olderOnly: false)))
                    continue;
                int exposure = push is null ? 0 : Exposure(t); // (only an attacking dot looks for the head of its attack)
                if (best < 0 || exposure > bestExposure || (exposure == bestExposure && t < best))
                    (best, bestExposure) = (t, exposure);
            }
            if (best >= 0)
            {
                _foundPath.Clear();
                for (int t = best; t >= 0; t = _pathFrom[t])
                    _foundPath.Add(t);
                _foundPath.Reverse();
                return _foundPath[1];
            }
            if ((searched += ring.Count) > maxTiles)
                return -1;

            next.Clear();
            foreach (int t in ring)
            {
                int x = t % w, y = t / w;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((dx | dy) == 0 || nx < 0 || ny < 0 || nx >= w || ny >= h || Math.Abs(nx - x0) > r || Math.Abs(ny - y0) > r)
                            continue;
                        int n = ny * w + nx;
                        if (Owner[n] != me || _pathStamp[n] == stamp)
                            continue;
                        if (dx != 0 && dy != 0 && (Owner[y * w + nx] != me || Owner[ny * w + x] != me))
                            continue;
                        _pathStamp[n] = stamp;
                        _pathFrom[n] = t;
                        next.Add(n);
                    }
            }
            (ring, next) = (next, ring);
        }
        return -1;
    }
}
