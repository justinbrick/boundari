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
        foreach (int slot in BorderSlots(player, around, DotRules.FreeAtStart, null))
            AddDot(player, StandTileNear(player, slot));
    }

    private void BuyDots(Player player, int count, int borderTile)
    {
        count = DotsAffordable(player, count);
        if (count <= 0 || !player.Border.Contains(borderTile))
            return;
        int home = HomeTile(player, borderTile);
        foreach (int slot in BorderSlots(player, borderTile, count, null))
        {
            if (FindDotPath(player, home, StandTileNear(player, slot)) is not { } path)
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
        var slots = BorderSlots(player, borderTile, moving.Count, moving);
        // Nearest slot to the click first, each taking the closest dot still free.
        int w = Map.Width;
        foreach (int slot in slots)
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
            if (FindDotPath(player, best!.Tile, StandTileNear(player, slot)) is { } path)
                SetOff(best, path);
        }
    }

    /// <summary>
    /// Spreads the named dots evenly along the border from one point to the other (each
    /// snapped to the player's nearest border tile), keeping the order they stand in along
    /// it; each walks to its new spot. Points on border pieces that don't join: they gather
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
        var plan = new List<(Dot, int)>();
        for (int i = 0; i < n; i++)
            plan.Add((moving[i], StandTileNear(player, line[n == 1 ? line.Count / 2 : (int)((long)i * (line.Count - 1) / (n - 1))])));
        return plan;
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
        var spots = new List<int>();
        foreach (int slot in BorderSlots(player, borderTile, DotsAffordable(player, count), null))
            spots.Add(StandTileNear(player, slot));
        return spots;
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
    /// Up to <paramref name="count"/> border tiles near <paramref name="around"/>, spread
    /// along the border <see cref="ScaledRules.DotSpacing"/> apart (and as far from the
    /// player's other dots), nearest first. Closer together if the stretch is too short,
    /// sharing tiles as a last resort.
    /// </summary>
    private List<int> BorderSlots(Player player, int around, int count, List<Dot>? ignore)
    {
        var slots = new List<int>();
        if (count <= 0 || !player.Border.Contains(around))
            return slots;

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

        for (int spacing = Rules.DotSpacing; ; spacing /= 2)
        {
            slots.Clear();
            foreach (int t in order)
            {
                if (slots.Count == count)
                    break;
                if (Clear(t, slots, spacing, w) && Clear(t, others, spacing, w))
                    slots.Add(t);
            }
            if (slots.Count == count || spacing <= 1)
                break;
        }
        for (int i = 0; slots.Count < count; i++) // more dots than border tiles: they share
            slots.Add(order[i % order.Count]);
        return slots;
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
