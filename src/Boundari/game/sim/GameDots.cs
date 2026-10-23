using System;
using System.Collections.Generic;

namespace Boundari.Sim;

/// <summary>
/// Dots (the user's design): troops as single units that stand on borders. Bought with
/// Funds up to a cap, they appear in the middle of their owner's land and walk to the
/// clicked border, spreading out along it; they can be moved (walking again), upgraded
/// and sold. How they hold and push the line is in GameFronts.cs.
/// </summary>
public sealed partial class Game
{
    private readonly List<Dot> _dots = new(); // in id order
    private int _nextDotId = 1;

    // Path search scratch space, one slot per tile (a search's own tiles carry its stamp).
    private long[]? _pathCost;
    private int[]? _pathFrom, _pathStamp;
    private int _pathSearch;
    private const int PathKeyBits = 25; // a queued tile's key: estimate << 25 | tile (maps up to 33M tiles)

    public IReadOnlyList<Dot> Dots => _dots;

    public Dot? DotById(int id)
    {
        foreach (var d in _dots)
            if (d.Id == id)
                return d;
        return null;
    }

    /// <summary>The player's dots, in id order.</summary>
    public List<Dot> DotsOf(Player player) => _dots.FindAll(d => d.Owner == player);

    /// <summary>Funds to raise this dot one strength level (long.MaxValue at the top level).</summary>
    public static long DotUpgradeCost(Dot dot) => dot.Strength >= DotRules.MaxStrength ? long.MaxValue : DotRules.UpgradeCost(dot.Strength);

    /// <summary>Funds selling this dot gives back.</summary>
    public static long DotRefund(Dot dot) => dot.Paid * DotRules.SellRefundPercent / 100;

    /// <summary>How many of <paramref name="wanted"/> new dots the player could buy now (cap and Funds allowing).</summary>
    public int DotsAffordable(Player player, int wanted)
    {
        int n = Math.Min(wanted, player.DotCap - player.DotCount);
        if (!player.InfiniteGold)
            n = (int)Math.Min(n, player.Gold / DotRules.Price);
        return Math.Max(0, n);
    }

    private Dot AddDot(Player player, int tile)
    {
        var dot = new Dot(_nextDotId++, player, tile);
        _dots.Add(dot);
        player.DotCount++;
        return dot;
    }

    private void RemoveDot(Dot dot)
    {
        _dots.Remove(dot);
        dot.Owner.DotCount--;
        foreach (var push in _pushes)
            push.Members.Remove(dot);
        _faced.Remove(dot.Id);
    }

    /// <summary>A new commander's free dots, standing spread out around the start (moving the spawn moves them).</summary>
    private void PlaceStartingDots(Player player, int spawnTile)
    {
        for (int i = _dots.Count - 1; i >= 0; i--)
            if (_dots[i].Owner == player)
                RemoveDot(_dots[i]);
        if (player.IsBot) // Locals hold their land without dots
            return;
        int around = NearestBorderTile(player, spawnTile);
        if (around < 0)
            return;
        foreach (int spot in StandSlots(player, around, DotRules.FreeAtStart, null))
            AddDot(player, spot);
    }

    private void BuyDots(Player player, int count, int borderTile)
    {
        count = DotsAffordable(player, count);
        if (count <= 0 || !player.Border.Contains(borderTile))
            return;
        int home = HomeTile(player, borderTile);
        foreach (int spot in StandSlots(player, borderTile, count, null))
        {
            if (FindDotPath(player, home, spot) is not { } path)
                continue;
            player.Spend(DotRules.Price);
            var dot = AddDot(player, home);
            dot.Paid = DotRules.Price;
            SetOff(dot, path);
        }
    }

    private void SellDot(Player player, int dotId)
    {
        if (DotById(dotId) is not { } dot || dot.Owner != player)
            return;
        player.Gold += DotRefund(dot);
        RemoveDot(dot);
    }

    private void UpgradeDot(Player player, int dotId)
    {
        if (DotById(dotId) is not { } dot || dot.Owner != player || dot.Strength >= DotRules.MaxStrength)
            return;
        long cost = DotUpgradeCost(dot);
        if (!player.CanAfford(cost))
            return;
        player.Spend(cost);
        dot.Paid += cost;
        dot.Strength++;
    }

    /// <summary>Sends the player's named dots to a border tile: they spread along it, each walking through own land.</summary>
    private void MoveDots(Player player, int[] dotIds, int borderTile)
    {
        if (!player.Border.Contains(borderTile))
            return;
        var moving = new List<Dot>();
        foreach (var d in _dots)
            if (d.Owner == player && Array.IndexOf(dotIds, d.Id) >= 0)
                moving.Add(d);
        if (moving.Count == 0)
            return;
        var spots = StandSlots(player, borderTile, moving.Count, moving);
        // Nearest spot to the click first, each taking the closest dot still free.
        int w = Map.Width;
        foreach (int slot in spots)
        {
            Dot? best = null;
            long bestDist = long.MaxValue;
            foreach (var d in moving)
            {
                long dx = d.Tile % w - slot % w, dy = d.Tile / w - slot / w, dist = dx * dx + dy * dy;
                if (dist < bestDist)
                    (best, bestDist) = (d, dist);
            }
            moving.Remove(best!);
            if (FindDotPath(player, best!.Tile, slot) is { } path)
                SetOff(best, path);
        }
    }

    /// <summary>
    /// Spreads the named dots evenly along the border from one point to the other (each
    /// snapped to the player's nearest border tile), keeping the order they stand in along
    /// it; each walks to its new spot. More than fit a dot's width apart form a second row
    /// behind, between those in front. Points on border pieces that don't join: they gather
    /// at the second point instead, as a move there would.
    /// </summary>
    private void SpreadDots(Player player, int[] dotIds, int fromTile, int toTile)
    {
        if (SpreadPlan(player, dotIds, fromTile, toTile, out int to) is { } plan)
        {
            foreach (var (dot, spot) in plan)
                if (FindDotPath(player, dot.Tile, spot) is { } path)
                    SetOff(dot, path);
        }
        else if (to >= 0)
            MoveDots(player, dotIds, to);
    }

    /// <summary>
    /// Where each of the named dots would stand if spread from one point to the other (see
    /// <see cref="SpreadDots"/>), in their order along the border; null if the points
    /// aren't on one piece of border (<paramref name="to"/> is the second point's border
    /// tile, -1 if there is none) or none of the dots is the player's.
    /// </summary>
    public List<(Dot Dot, int Spot)>? SpreadPlan(Player player, int[] dotIds, int fromTile, int toTile, out int to)
    {
        to = -1;
        if (fromTile < 0 || toTile < 0 || fromTile >= Map.TileCount || toTile >= Map.TileCount)
            return null;
        var moving = new List<Dot>();
        foreach (var d in _dots)
            if (d.Owner == player && Array.IndexOf(dotIds, d.Id) >= 0)
                moving.Add(d);
        int from = BorderTileNear(player, fromTile);
        to = BorderTileNear(player, toTile);
        if (moving.Count == 0 || from < 0 || to < 0 || BorderLine(player, from, to) is not { } line)
            return null;
        // Each dot's place along the line: the nearest of its tiles (ties: the dot's id).
        int w = Map.Width, n = moving.Count;
        var place = new Dictionary<int, int>();
        foreach (var d in moving)
        {
            int best = 0, bestDist = int.MaxValue;
            for (int i = 0; i < line.Count; i++)
            {
                int dist = Math.Max(Math.Abs(d.Tile % w - line[i] % w), Math.Abs(d.Tile / w - line[i] / w));
                if (dist < bestDist)
                    (best, bestDist) = (i, dist);
            }
            place[d.Id] = best;
        }
        moving.Sort((a, b) => place[a.Id] != place[b.Id] ? place[a.Id].CompareTo(place[b.Id]) : a.Id.CompareTo(b.Id));

        // Places along the line: as many as fit a dot's width apart in front, the rest
        // evenly in the second row (in between); the dots take them in order along the line.
        int last = line.Count - 1, first = Math.Min(n, last / Rules.DotSpacing + 1), second = n - first;
        var places = new List<(long At, int Row)>(); // At: thousandths of the way along, x the line's last index
        for (int i = 0; i < first; i++)
            places.Add((first == 1 ? last * 500L : (long)i * last * 1000 / (first - 1), 0));
        for (int j = 0; j < second; j++)
            places.Add(((2L * j + 1) * last * 1000 / (2 * second), 1));
        places.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Row.CompareTo(b.Row));
        var plan = new List<(Dot, int)>();
        for (int i = 0; i < n; i++)
        {
            int at = line[(int)(places[i].At / 1000)], spot = StandTileNear(player, at, places[i].Row);
            if (spot == at && places[i].Row == 1)
                spot = StandTileNear(player, at); // too thin for a second row here
            plan.Add((moving[i], spot));
        }
        return plan;
    }

    /// <summary>
    /// The two ends of the stretch of the player's border reaching <paramref name="half"/>
    /// tiles each way along it from a border tile (shorter where the border ends), for
    /// spreading dots over it; (-1, -1) if the tile isn't on their border.
    /// </summary>
    public (int From, int To) BorderStretch(Player player, int borderTile, int half)
    {
        if (!player.Border.Contains(borderTile))
            return (-1, -1);
        var depth = new Dictionary<int, int> { [borderTile] = 0 };
        var queue = new Queue<int>();
        queue.Enqueue(borderTile);
        var ends = new List<int>();
        int w = Map.Width, h = Map.Height;
        while (queue.Count > 0 && depth.Count < DotRules.SpreadSearchTiles)
        {
            int t = queue.Dequeue(), x = t % w, y = t / w, d = depth[t];
            if (d == half)
            {
                ends.Add(t);
                continue;
            }
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
        if (ends.Count == 0)
            return (borderTile, borderTile);
        ends.Sort();
        int a = ends[0], b = borderTile;
        long far = -1;
        foreach (int t in ends)
        {
            long dist = Math.Max(Math.Abs(t % w - a % w), Math.Abs(t / w - a / w));
            if (dist > far)
                (far, b) = (dist, t);
        }
        return far * 2 > half ? (a, b) : (a, borderTile); // (both ends one way: the border stops short the other)
    }

    /// <summary>
    /// The shortest way along the player's border tiles (tile to tile, diagonals included)
    /// from one border tile to another, both included; null if they aren't on one piece of
    /// border (or it is too long to follow).
    /// </summary>
    private List<int>? BorderLine(Player player, int from, int to)
    {
        var cameFrom = new Dictionary<int, int> { [from] = -1 };
        var queue = new Queue<int>();
        queue.Enqueue(from);
        int w = Map.Width, h = Map.Height;
        while (queue.Count > 0 && !cameFrom.ContainsKey(to) && cameFrom.Count < DotRules.SpreadSearchTiles)
        {
            int t = queue.Dequeue(), x = t % w, y = t / w;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((dx | dy) == 0 || nx < 0 || ny < 0 || nx >= w || ny >= h)
                        continue;
                    int next = ny * w + nx;
                    if (player.Border.Contains(next) && cameFrom.TryAdd(next, t))
                        queue.Enqueue(next);
                }
        }
        if (!cameFrom.ContainsKey(to))
            return null;
        var line = new List<int>();
        for (int t = to; t >= 0; t = cameFrom[t])
            line.Add(t);
        line.Reverse();
        return line;
    }

    /// <summary>
    /// The player's border tile nearest this tile: looked for within two dot spacings, then
    /// along the whole border (-1 if they have none).
    /// </summary>
    public int BorderTileNear(Player player, int tile)
    {
        int near = BorderTileWithin(player, tile, 2 * Rules.DotSpacing);
        return near >= 0 ? near : NearestBorderTile(player, tile);
    }

    /// <summary>The player's border tile nearest this tile within <paramref name="radius"/> tiles (-1 if none).</summary>
    public int BorderTileWithin(Player player, int tile, int radius)
    {
        if (tile < 0 || tile >= Map.TileCount)
            return -1;
        if (player.Border.Contains(tile))
            return tile;
        int w = Map.Width, h = Map.Height, x0 = tile % w, y0 = tile / w, best = -1;
        long bestDist = long.MaxValue;
        for (int y = Math.Max(0, y0 - radius); y <= Math.Min(h - 1, y0 + radius); y++)
            for (int x = Math.Max(0, x0 - radius); x <= Math.Min(w - 1, x0 + radius); x++)
            {
                long dist = (long)(x - x0) * (x - x0) + (long)(y - y0) * (y - y0);
                if (dist < bestDist && player.Border.Contains(y * w + x))
                    (best, bestDist) = (y * w + x, dist);
            }
        return best;
    }

    /// <summary>
    /// Where dots bought at this border tile would stand (as many as the player can buy of
    /// <paramref name="count"/>), for showing before buying.
    /// </summary>
    public List<int> BuySpotsPreview(Player player, int borderTile, int count)
    {
        return StandSlots(player, borderTile, DotsAffordable(player, count), null);
    }

    /// <summary>
    /// The standing dots of this dot's owner chained to it, each within a stretch's link
    /// distance of the next (the dot itself included), in id order: the group whose handles
    /// spread or slide it.
    /// </summary>
    public List<Dot> DotGroup(Dot dot)
    {
        var group = new List<Dot> { dot };
        if (dot.IsWalking)
            return group;
        int w = Map.Width, link = Rules.DotLink;
        for (int i = 0; i < group.Count; i++)
            foreach (var d in _dots)
                if (d.Owner == dot.Owner && !d.IsWalking && !group.Contains(d)
                    && Math.Max(Math.Abs(d.Tile % w - group[i].Tile % w), Math.Abs(d.Tile / w - group[i].Tile / w)) <= link)
                    group.Add(d);
        group.Sort((a, b) => a.Id.CompareTo(b.Id));
        return group;
    }

    private static void SetOff(Dot dot, int[] path)
    {
        dot.Destination = path[^1];
        dot.Path = path.Length > 1 ? path : null;
        dot.PathIndex = 0;
        dot.Progress = 0;
    }

    /// <summary>Walks every walking dot on and keeps standing dots on the line; dots of a player with no land left are gone.</summary>
    private void RunDots()
    {
        for (int i = _dots.Count - 1; i >= 0; i--)
            if (_dots[i].Owner.TileCount == 0)
                RemoveDot(_dots[i]);

        int w = Map.Width;
        foreach (var dot in _dots)
        {
            if (dot.Path is null)
                continue;
            dot.Progress += Rules.DotWalkSpeedPerTick;
            while (dot.Path is { } path)
            {
                if (dot.PathIndex >= path.Length - 1)
                {
                    dot.Path = null;
                    dot.Progress = 0;
                    break;
                }
                int next = path[dot.PathIndex + 1];
                bool diagonal = dot.Tile % w != next % w && dot.Tile / w != next / w;
                long cost = diagonal ? DotRules.DiagonalStepCost : DotRules.StepCost;
                if (dot.Progress < cost)
                    break;
                if (Owner[next] != dot.Owner.Id)
                {
                    // The way was lost under its feet: find another, or stop where it is.
                    dot.Progress = 0;
                    if (Owner[dot.Destination] == dot.Owner.Id && FindDotPath(dot.Owner, dot.Tile, dot.Destination, DotRules.DetourSearchTiles) is { } detour)
                        SetOff(dot, detour);
                    else
                        SetOff(dot, new[] { dot.Tile });
                    break;
                }
                dot.Progress -= cost;
                dot.PathIndex++;
                dot.Tile = next;
            }
        }
        FollowLine();
    }

    /// <summary>
    /// Where up to <paramref name="count"/> dots sent to the border tile <paramref name="around"/>
    /// stand, nearest first: along the border <see cref="ScaledRules.DotSpacing"/> apart (and
    /// as far from the player's other dots) in the first row; any that don't fit, in a second
    /// row behind, each between two in front; if even that is short (thin land), closer
    /// together, sharing places as a last resort.
    /// </summary>
    private List<int> StandSlots(Player player, int around, int count, List<Dot>? ignore)
    {
        var spots = new List<int>();
        if (count <= 0 || !player.Border.Contains(around))
            return spots;

        // The border, nearest first, following it tile to tile (diagonals included).
        var order = new List<int> { around };
        var seen = new HashSet<int> { around };
        int w = Map.Width, h = Map.Height;
        for (int i = 0; i < order.Count && order.Count < DotRules.SpreadSearchTiles; i++)
        {
            int t = order[i], x = t % w, y = t / w;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((dx | dy) == 0 || nx < 0 || ny < 0 || nx >= w || ny >= h)
                        continue;
                    int n = ny * w + nx;
                    if (player.Border.Contains(n) && seen.Add(n))
                        order.Add(n);
                }
        }

        var others = new List<int>();
        foreach (var d in _dots)
            if (d.Owner == player && (ignore is null || !ignore.Contains(d)))
                others.Add(d.Destination);

        static bool Clear(int t, List<int> taken, int spacing, int w)
        {
            foreach (int o in taken)
                if (Math.Max(Math.Abs(t % w - o % w), Math.Abs(t / w - o / w)) < spacing)
                    return false;
            return true;
        }

        int spacing = Rules.DotSpacing;
        var front = new List<int>(); // the first row's border tiles
        foreach (int t in order)
        {
            if (front.Count == count)
                break;
            if (Clear(t, front, spacing, w) && Clear(t, others, spacing, w))
                front.Add(t);
        }
        foreach (int t in front)
            spots.Add(StandTileNear(player, t));

        // The second row: behind the gaps between the first row's dots (looked for only along
        // the stretch the first row covers, and a little past it).
        var backBorder = new List<int>();
        var back = new List<int>();
        int look = (front.Count + 2) * spacing * 2;
        for (int i = 0; i < order.Count && i < look && spots.Count < count; i++)
        {
            int t = order[i];
            if (!Clear(t, front, spacing / 2, w) || !Clear(t, backBorder, spacing, w))
                continue;
            int s = StandTileNear(player, t, 1);
            if (s == t || !Clear(s, back, spacing, w) || !Clear(s, others, spacing, w))
                continue; // (no second row here: the land is too thin)
            backBorder.Add(t);
            back.Add(s);
            spots.Add(s);
        }

        // Still short: closer together in the first row, then sharing places.
        for (int sp = spacing / 2; sp >= 1 && spots.Count < count; sp /= 2)
            foreach (int t in order)
            {
                if (spots.Count == count)
                    break;
                if (Clear(t, front, sp, w) && Clear(t, others, sp, w))
                {
                    front.Add(t);
                    spots.Add(StandTileNear(player, t));
                }
            }
        for (int i = 0; spots.Count < count; i++)
            spots.Add(StandTileNear(player, order[i % order.Count]));
        return spots;
    }

    /// <summary>
    /// Where new dots appear: the player's tile nearest the middle of their land, among the
    /// land joined to <paramref name="borderTile"/> (so they can always walk there).
    /// </summary>
    private int HomeTile(Player player, int borderTile)
    {
        int w = Map.Width;
        long cx = player.SumX / player.TileCount, cy = player.SumY / player.TileCount;
        int best = borderTile;
        long bestDist = long.MaxValue;
        var queue = new Queue<int>();
        int stamp = NewPathStamp();
        _pathStamp![borderTile] = stamp;
        queue.Enqueue(borderTile);
        Span<int> nb = stackalloc int[4];
        while (queue.Count > 0)
        {
            int t = queue.Dequeue();
            long dx = t % w - cx, dy = t / w - cy, d = dx * dx + dy * dy;
            if (d < bestDist)
                (best, bestDist) = (t, d);
            int count = LandNeighbours(t, nb);
            for (int k = 0; k < count; k++)
                if (Owner[nb[k]] == player.Id && _pathStamp[nb[k]] != stamp)
                {
                    _pathStamp[nb[k]] = stamp;
                    queue.Enqueue(nb[k]);
                }
        }
        return best;
    }

    /// <summary>A fresh mark for <see cref="_pathStamp"/>: tiles carrying it belong to the search just begun.</summary>
    private int NewPathStamp()
    {
        _pathStamp ??= new int[Map.TileCount];
        return ++_pathSearch;
    }

    /// <summary>The player's border tile nearest this tile (-1 if they have none).</summary>
    private int NearestBorderTile(Player player, int tile)
    {
        int w = Map.Width, best = -1;
        long bestDist = long.MaxValue;
        foreach (int t in player.Border)
        {
            long dx = t % w - tile % w, dy = t / w - tile / w, d = dx * dx + dy * dy;
            if (d < bestDist || (d == bestDist && t < best))
                (best, bestDist) = (t, d);
        }
        return best;
    }

    /// <summary>
    /// The shortest walk from one tile to another through the player's own land (8 ways,
    /// no cutting corners past someone else's land), first and last tile included; null if
    /// there is none.
    /// </summary>
    internal int[]? FindDotPath(Player player, int from, int to, int maxTiles = DotRules.PathSearchTiles)
    {
        if (from == to)
            return new[] { from };
        if (Owner[to] != player.Id)
            return null;
        int n = Map.TileCount, w = Map.Width, h = Map.Height;
        _pathCost ??= new long[n];
        _pathFrom ??= new int[n];
        int stamp = NewPathStamp();
        int tx = to % w, ty = to / w;
        long Guess(int t)
        {
            int dx = Math.Abs(t % w - tx), dy = Math.Abs(t / w - ty);
            return DotRules.StepCost * Math.Max(dx, dy) + (DotRules.DiagonalStepCost - DotRules.StepCost) * Math.Min(dx, dy);
        }
        // Ordered by estimated total, then tile, so ties always break the same way.
        var open = new PriorityQueue<int, long>();
        _pathStamp[from] = stamp;
        _pathCost[from] = 0;
        _pathFrom[from] = -1;
        open.Enqueue(from, Guess(from) << PathKeyBits | (uint)from);
        int expanded = 0;
        while (open.TryDequeue(out int t, out long key))
        {
            if (t == to)
                break;
            if (key >> PathKeyBits > _pathCost[t] + Guess(t))
                continue; // queued before a shorter way here was found
            if (++expanded > maxTiles)
                return null;
            int x = t % w, y = t / w;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((dx | dy) == 0 || nx < 0 || ny < 0 || nx >= w || ny >= h)
                        continue;
                    int next = ny * w + nx;
                    if (Owner[next] != player.Id)
                        continue;
                    bool diagonal = dx != 0 && dy != 0;
                    if (diagonal && (Owner[y * w + nx] != player.Id || Owner[ny * w + x] != player.Id))
                        continue;
                    long cost = _pathCost[t] + (diagonal ? DotRules.DiagonalStepCost : DotRules.StepCost);
                    if (_pathStamp[next] == stamp && _pathCost[next] <= cost)
                        continue;
                    _pathStamp[next] = stamp;
                    _pathCost[next] = cost;
                    _pathFrom[next] = t;
                    open.Enqueue(next, (cost + Guess(next)) << PathKeyBits | (uint)next);
                }
        }
        if (_pathStamp[to] != stamp)
            return null;
        var path = new List<int>();
        for (int t = to; t >= 0; t = _pathFrom[t])
            path.Add(t);
        path.Reverse();
        return path.ToArray();
    }
}
