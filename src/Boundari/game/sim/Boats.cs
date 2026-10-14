using System;
using System.Collections.Generic;
using Boundari.Map;

namespace Boundari.Sim;

/// <summary>
/// A transport boat carrying troops across water. It sails a precomputed path; when it
/// reaches the end it lands on <see cref="LandingTile"/> and its troops start an attack
/// from there.
/// </summary>
public sealed class Boat
{
    public readonly Player Owner;
    public readonly long Troops;
    public readonly int LandingTile;

    /// <summary>Water tiles from the launch shore to the landing shore.</summary>
    public readonly int[] Path;

    /// <summary>Distance travelled along <see cref="Path"/>, in thousandths of a step.</summary>
    public long Progress { get; internal set; }

    internal Boat(Player owner, long troops, int landingTile, int[] path)
    {
        Owner = owner;
        Troops = troops;
        LandingTile = landingTile;
        Path = path;
    }

    public bool Arrived => Progress >= (Path.Length - 1) * 1000L;
}

/// <summary>Send a share of troops by boat to land as close as possible to a tile.</summary>
public sealed record BoatIntent(ushort PlayerId, int Tile, int RatioPercent) : Intent(PlayerId);

/// <summary>Water connectivity and shortest sea routes, for boats.</summary>
public sealed class Waterways
{
    public const int NoBody = -1;

    private readonly GameMap _map;
    private int[]? _cost, _cameFrom, _stamp, _closed;
    private int _search;

    /// <summary>Which connected body of water each water tile belongs to (land = <see cref="NoBody"/>).</summary>
    public readonly int[] Body;

    public Waterways(GameMap map)
    {
        _map = map;
        Body = new int[map.TileCount];
        Array.Fill(Body, NoBody);
        var queue = new int[map.TileCount];
        int bodies = 0, w = map.Width, h = map.Height;
        for (int start = 0; start < map.TileCount; start++)
        {
            if (Body[start] != NoBody || !map.IsWater(start))
                continue;
            int head = 0, tail = 0;
            queue[tail++] = start;
            Body[start] = bodies;
            while (head < tail)
            {
                int t = queue[head++], x = t % w, y = t / w;
                if (x > 0) Visit(t - 1);
                if (x < w - 1) Visit(t + 1);
                if (y > 0) Visit(t - w);
                if (y < h - 1) Visit(t + w);
            }
            bodies++;

            void Visit(int n)
            {
                if (Body[n] != NoBody || !map.IsWater(n))
                    return;
                Body[n] = bodies;
                queue[tail++] = n;
            }
        }
        BuildCells();
    }

    /// <summary>A water tile next to this land tile in the given body, or -1.</summary>
    public int ShoreWater(int land, int body)
    {
        int w = _map.Width, x = land % w, y = land / w;
        if (x > 0 && Body[land - 1] == body && body != NoBody) return land - 1;
        if (x < w - 1 && Body[land + 1] == body && body != NoBody) return land + 1;
        if (y > 0 && Body[land - w] == body && body != NoBody) return land - w;
        if (y < _map.Height - 1 && Body[land + w] == body && body != NoBody) return land + w;
        return -1;
    }

    /// <summary>Water bodies touching this land tile (up to 4; duplicates possible).</summary>
    public int TouchingBodies(int land, Span<int> result)
    {
        int w = _map.Width, x = land % w, y = land / w, n = 0;
        if (x > 0 && Body[land - 1] != NoBody) result[n++] = Body[land - 1];
        if (x < w - 1 && Body[land + 1] != NoBody) result[n++] = Body[land + 1];
        if (y > 0 && Body[land - w] != NoBody) result[n++] = Body[land - w];
        if (y < _map.Height - 1 && Body[land + w] != NoBody) result[n++] = Body[land + w];
        return n;
    }

    // Heuristic weight: above 1 makes A* head straight for the goal across open water,
    // expanding far fewer tiles, for routes at most this much longer than the best.
    private const int HeuristicWeightPercent = 150;

    // Routes are planned on a coarse grid of Cell x Cell tiles first, then refined on
    // the real tiles inside a corridor around that rough route. Far cheaper than a
    // full-map search for long voyages.
    private const int Cell = 8;
    private const int CorridorCells = 2;
    private const int WideCorridorCells = 8;
    private const int Mixed = -2;

    private int[]? _cellBody;       // which body's water a cell holds (Mixed = several, NoBody = none)
    private int[]? _corridor;       // cell -> search id when inside the current corridor
    private int[]? _cellCost, _cellFrom, _cellClosed;
    private byte[]? _cellLinks;

    private static readonly int[] Dx = { 1, 1, 0, -1, -1, -1, 0, 1 };
    private static readonly int[] Dy = { 0, 1, 1, 1, 0, -1, -1, -1 };

    private static int DirIndex(int dx, int dy)
    {
        for (int d = 0; d < 8; d++)
            if (Dx[d] == dx && Dy[d] == dy)
                return d;
        throw new ArgumentException("not a neighbour direction");
    }

    /// <summary>
    /// A short water route (8-directional, never cutting corners of land) between two water
    /// tiles in the same body, or null if there is none.
    /// </summary>
    public int[]? FindPath(int from, int to)
    {
        if (Body[from] == NoBody || Body[from] != Body[to])
            return null;
        if (from == to)
            return new[] { from };
        int search = ++_search;
        if (MarkCorridor(from, to, search, CorridorCells))
        {
            var path = Search(from, to, 3_000_000, search, useCorridor: true);
            if (path is not null)
                return path;
        }
        // Rare: the rough route squeezed through a cell whose water isn't really connected.
        // Try once more with a much wider corridor, then give up on this route.
        search = ++_search;
        if (MarkCorridor(from, to, search, WideCorridorCells))
            return Search(from, to, 3_000_000, search, useCorridor: true);
        return null;
    }

    /// <summary>Coarse-grid data for route planning (done once, when the map loads).</summary>
    private void BuildCells()
    {
        int w = _map.Width, h = _map.Height;
        int cw = (w + Cell - 1) / Cell, ch = (h + Cell - 1) / Cell, cells = cw * ch;

        _cellBody = new int[cells];
        Array.Fill(_cellBody, NoBody);
        for (int t = 0; t < Body.Length; t++)
        {
            if (Body[t] == NoBody)
                continue;
            int c = (t / w / Cell) * cw + (t % w) / Cell;
            _cellBody[c] = _cellBody[c] == NoBody || _cellBody[c] == Body[t] ? Body[t] : Mixed;
        }
        // Which neighbouring cells each cell's water really flows into (bit per direction).
        _cellLinks = new byte[cells];
        for (int t = 0; t < Body.Length; t++)
        {
            if (Body[t] == NoBody)
                continue;
            int x = t % w, y = t / w, c = (y / Cell) * cw + x / Cell;
            for (int d = 0; d < 8; d++)
            {
                int nx = x + Dx[d], ny = y + Dy[d];
                if (nx < 0 || ny < 0 || nx >= w || ny >= h || Body[ny * w + nx] == NoBody)
                    continue;
                if (Dx[d] != 0 && Dy[d] != 0 && (Body[y * w + nx] == NoBody || Body[ny * w + x] == NoBody))
                    continue;
                int nc = (ny / Cell) * cw + nx / Cell;
                if (nc == c)
                    continue;
                int ddx = nx / Cell - x / Cell, ddy = ny / Cell - y / Cell;
                _cellLinks[c] |= (byte)(1 << DirIndex(ddx, ddy));
            }
        }
        _corridor = new int[cells];
        _cellCost = new int[cells];
        _cellFrom = new int[cells];
        _cellClosed = new int[cells];
        }

    /// <summary>Coarse A* between the two cells; marks the cells around the route as the corridor.</summary>
    private bool MarkCorridor(int from, int to, int search, int radius)
    {
        int w = _map.Width, h = _map.Height;
        int cw = (w + Cell - 1) / Cell, ch = (h + Cell - 1) / Cell, cells = cw * ch;
        int body = Body[from];
        if (_cellBody is null)
            BuildCells();
        var cellBody = _cellBody!;

        var cost = _cellCost!;
        var cameFrom = _cellFrom!;
        var closed = _cellClosed!;
        int start = (from / w / Cell) * cw + (from % w) / Cell;
        int goal = (to / w / Cell) * cw + (to % w) / Cell;
        int gx = goal % cw, gy = goal / cw;

        // Reuse the corridor stamps as "seen this search" for the coarse costs.
        var seen = new Dictionary<int, int>();
        cost[start] = 0;
        cameFrom[start] = -1;
        seen[start] = 0;
        var open = new PriorityQueue<int, int>();
        open.Enqueue(start, 0);
        bool found = false;
        while (open.TryDequeue(out int c, out _))
        {
            if (closed[c] == search)
                continue;
            closed[c] = search;
            if (c == goal)
            {
                found = true;
                break;
            }
            int cx = c % cw, cy = c / cw, g = cost[c];
            for (int d = 0; d < 8; d++)
                {
                    if ((_cellLinks![c] & (1 << d)) == 0)
                        continue; // no water actually flows that way
                    int dx = Dx[d], dy = Dy[d];
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= cw || ny >= ch)
                        continue;
                    int n = ny * cw + nx;
                    if (cellBody[n] != body && cellBody[n] != Mixed)
                        continue;
                    int ng = g + (dx != 0 && dy != 0 ? 14 : 10);
                    if (seen.TryGetValue(n, out int old) && old <= ng)
                        continue;
                    seen[n] = ng;
                    cost[n] = ng;
                    cameFrom[n] = c;
                    int hx = Math.Abs(nx - gx), hy = Math.Abs(ny - gy);
                    open.Enqueue(n, ng + 10 * Math.Max(hx, hy) + 4 * Math.Min(hx, hy));
                }
        }
        if (!found)
            return false;

        var corridor = _corridor!;
        for (int c = goal; c >= 0; c = cameFrom[c])
        {
            int cx = c % cw, cy = c / cw;
            for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int nx = cx + dx, ny = cy + dy;
                    if (nx >= 0 && ny >= 0 && nx < cw && ny < ch)
                        corridor[ny * cw + nx] = search;
                }
            if (c == start)
                break;
        }
        return true;
    }

    /// <summary>Weighted A* over water tiles, optionally only inside the marked corridor.</summary>
    private int[]? Search(int from, int to, int maxExpanded, int search, bool useCorridor)
    {
        int w = _map.Width, h = _map.Height, cw = (w + Cell - 1) / Cell;
        int tx = to % w, ty = to / w;
        // Flat arrays, allocated once; a tile's entry only counts if stamped by this search.
        _cost ??= new int[_map.TileCount];
        _cameFrom ??= new int[_map.TileCount];
        _stamp ??= new int[_map.TileCount];
        _closed ??= new int[_map.TileCount];
        var cost = _cost;
        var cameFrom = _cameFrom;
        var stamp = _stamp;
        var closed = _closed;
        var corridor = _corridor;
        stamp[from] = search;
        cost[from] = 0;
        cameFrom[from] = -1;
        var open = new PriorityQueue<int, int>();
        open.Enqueue(from, Heuristic(from));
        int expanded = 0;

        while (open.TryDequeue(out int current, out _))
        {
            if (closed[current] == search)
                continue; // already expanded via a better entry
            closed[current] = search;
            if (current == to)
                return Rebuild(current);
            if (++expanded > maxExpanded)
                return null;
            int cx = current % w, cy = current / w, g = cost[current];
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                        continue;
                    int nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                        continue;
                    int n = ny * w + nx;
                    if (Body[n] == NoBody)
                        continue;
                    if (useCorridor && corridor![(ny / Cell) * cw + nx / Cell] != search)
                        continue;
                    if (dx != 0 && dy != 0 && (Body[cy * w + nx] == NoBody || Body[ny * w + cx] == NoBody))
                        continue; // don't squeeze diagonally between two land tiles
                    int ng = g + (dx != 0 && dy != 0 ? 14 : 10);
                    if (stamp[n] == search && cost[n] <= ng)
                        continue;
                    stamp[n] = search;
                    cost[n] = ng;
                    cameFrom[n] = current;
                    open.Enqueue(n, ng + Heuristic(n));
                }
        }
        return null;

        int Heuristic(int t)
        {
            int dx = Math.Abs(t % w - tx), dy = Math.Abs(t / w - ty);
            return (10 * Math.Max(dx, dy) + 4 * Math.Min(dx, dy)) * HeuristicWeightPercent / 100;
        }

        int[] Rebuild(int end)
        {
            var path = new List<int> { end };
            while (cameFrom[end] >= 0)
            {
                end = cameFrom[end];
                path.Add(end);
            }
            path.Reverse();
            return path.ToArray();
        }
    }
}
