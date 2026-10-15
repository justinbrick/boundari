using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
    public long Troops { get; internal set; }
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

    public int Tile => Path[(int)System.Math.Min(Progress / 1000, Path.Length - 1)];

    /// <summary>Sunk by a warship (troops lost).</summary>
    public bool Sunk { get; internal set; }
}

/// <summary>Send a share of troops by boat to land as close as possible to a tile.</summary>
public sealed record BoatIntent(ushort PlayerId, int Tile, int RatioPercent) : Intent(PlayerId);

/// <summary>Water connectivity and shortest sea routes, for boats.</summary>
public sealed class Waterways
{
    public const int NoBody = -1;

    private readonly GameMap _map;

    /// <summary>Which connected body of water each water tile belongs to (land = <see cref="NoBody"/>).</summary>
    public readonly int[] Body;

    /// <summary>Tiles in each body of water.</summary>
    public readonly int[] BodySize;

    public Waterways(GameMap map)
    {
        _map = map;
        Body = new int[map.TileCount];
        Array.Fill(Body, NoBody);
        var queue = new int[map.TileCount];
        int bodies = 0, w = map.Width, h = map.Height;
        var sizes = new System.Collections.Generic.List<int>();
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
            sizes.Add(tail);

            void Visit(int n)
            {
                if (Body[n] != NoBody || !map.IsWater(n))
                    return;
                Body[n] = bodies;
                queue[tail++] = n;
            }
        }
        BodySize = sizes.ToArray();
        BuildCells();
        // Work memory, allocated now so the first boat doesn't stall a tick. One set for
        // instant searches (troop boats, warships), one for background trade routes.
        _instant = new Workspace(map.TileCount, _cellsWide * _cellsHigh);
        _background = new Workspace(map.TileCount, _cellsWide * _cellsHigh);
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
    // full-map search for long voyages. If the narrow corridor fails (it squeezed
    // through a cell whose water isn't really connected), a wide one is tried.
    private const int Cell = 8;
    private const int NarrowCorridorCells = 2;
    private const int WideCorridorCells = 8;
    private const int NarrowSearchLimit = 150_000;
    private const int WideSearchLimit = 250_000;
    private const int Mixed = -2;

    private int[] _cellBody = Array.Empty<int>();   // which body's water a cell holds (Mixed = several)
    private byte[] _cellLinks = Array.Empty<byte>(); // which neighbouring cells its water flows into
    private int _cellsWide, _cellsHigh;

    private readonly Workspace _instant;
    private readonly Workspace _background;
    private RouteJob? _backgroundJob;

    private static readonly int[] Dx = { 1, 1, 0, -1, -1, -1, 0, 1 };
    private static readonly int[] Dy = { 0, 1, 1, 1, 0, -1, -1, -1 };

    /// <summary>
    /// Scratch memory for one route search. A tile's entries only count when its Mark is
    /// this search's (2 * id = seen, 2 * id + 1 = done), so nothing needs clearing.
    /// </summary>
    internal sealed class Workspace
    {
        public readonly int[] Cost, CameFrom, Mark;
        public readonly int[] Corridor, CellCost, CellFrom, CellMark;
        public int Search;

        public Workspace(int tiles, int cells)
        {
            Cost = new int[tiles];
            CameFrom = new int[tiles];
            Mark = new int[tiles];
            Corridor = new int[cells];
            CellCost = new int[cells];
            CellFrom = new int[cells];
            CellMark = new int[cells];
        }
    }

    /// <summary>A short water route between two water tiles in the same body, planned right now. Null if none.</summary>
    public int[]? FindPath(int from, int to)
    {
        if (Body[from] == NoBody || Body[from] != Body[to])
            return null;
        if (from == to)
            return new[] { from };
        var job = new RouteJob(this, _instant, from, to);
        while (!job.Done)
            job.Step(int.MaxValue);
        return job.Result;
    }

    /// <summary>
    /// Plans a long route a slice at a time (see <see cref="StepBackground"/>), so it never
    /// stalls a tick. Only one at a time; returns null if the planner is busy.
    /// </summary>
    public RouteJob? StartBackground(int from, int to)
    {
        if (_backgroundJob is { Done: false })
            return null;
        return _backgroundJob = new RouteJob(this, _background, from, to);
    }

    /// <summary>Advances the background route by about this many tile expansions.</summary>
    public void StepBackground(int budget)
    {
        if (_backgroundJob is { Done: false } job)
            job.Step(budget);
    }

    /// <summary>One route search that can be paused and resumed between ticks.</summary>
    public sealed class RouteJob
    {
        private readonly Waterways _w;
        private readonly Workspace _ws;
        public readonly int From, To;
        public bool Done { get; private set; }
        public int[]? Result { get; private set; }

        private int _phase;          // 0 narrow corridor, 1 wide corridor
        private bool _searching;
        private PriorityQueue<int, int> _open = new();
        private int _expanded, _limit, _search;

        internal RouteJob(Waterways w, Workspace ws, int from, int to)
        {
            _w = w;
            _ws = ws;
            From = from;
            To = to;
            if (w.Body[from] == NoBody || w.Body[from] != w.Body[to])
                Done = true;
            else if (from == to)
            {
                Result = new[] { from };
                Done = true;
            }
        }

        /// <summary>Does up to <paramref name="budget"/> expansions of work.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Step(int budget)
        {
            while (!Done && budget > 0)
            {
                if (!_searching)
                {
                    if (_phase > 1)
                    {
                        Done = true; // no route
                        return;
                    }
                    _search = ++_ws.Search;
                    if (!_w.MarkCorridor(_ws, From, To, _search, _phase == 0 ? NarrowCorridorCells : WideCorridorCells))
                    {
                        Done = true; // not even a rough route: unreachable
                        return;
                    }
                    _limit = _phase == 0 ? NarrowSearchLimit : WideSearchLimit;
                    _expanded = 0;
                    _open = new PriorityQueue<int, int>();
                    _ws.Mark[From] = 2 * _search;
                    _ws.Cost[From] = 0;
                    _ws.CameFrom[From] = -1;
                    _open.Enqueue(From, 0);
                    _searching = true;
                    return; // the coarse pass is this tick's work; the detailed search starts next tick
                }
                int used = Expand(budget);
                budget -= Math.Max(used, 1);
            }
        }

        private int Expand(int budget)
        {
            var body = _w.Body;
            var cost = _ws.Cost;
            var cameFrom = _ws.CameFrom;
            var mark = _ws.Mark;
            var corridor = _ws.Corridor;
            int w = _w._map.Width, h = _w._map.Height, cw = _w._cellsWide;
            int tx = To % w, ty = To / w, seen = 2 * _search, closed = 2 * _search + 1;
            int used = 0;

            while (used < budget && _open.TryDequeue(out int current, out _))
            {
                if (mark[current] == closed)
                    continue; // already expanded via a better entry
                mark[current] = closed;
                if (current == To)
                {
                    Result = Rebuild(cameFrom, current);
                    Done = true;
                    return used;
                }
                if (++_expanded > _limit)
                    break;
                used++;
                int cx = current % w, cy = current / w, g = cost[current];
                for (int d = 0; d < 8; d++)
                {
                    int dx = Dx[d], dy = Dy[d], nx = cx + dx, ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                        continue;
                    int n = ny * w + nx;
                    if (body[n] == NoBody || corridor[(ny / Cell) * cw + nx / Cell] != _search)
                        continue;
                    if (dx != 0 && dy != 0 && (body[cy * w + nx] == NoBody || body[ny * w + cx] == NoBody))
                        continue; // don't squeeze diagonally between two land tiles
                    if (mark[n] == closed)
                        continue;
                    int ng = g + (dx != 0 && dy != 0 ? 14 : 10);
                    if (mark[n] == seen && cost[n] <= ng)
                        continue;
                    mark[n] = seen;
                    cost[n] = ng;
                    cameFrom[n] = current;
                    int hx = Math.Abs(nx - tx), hy = Math.Abs(ny - ty);
                    _open.Enqueue(n, ng + (10 * Math.Max(hx, hy) + 4 * Math.Min(hx, hy)) * HeuristicWeightPercent / 100);
                }
            }
            if (!Done && (_open.Count == 0 || _expanded > _limit))
            {
                _searching = false; // this corridor failed: try the next phase
                _phase++;
            }
            return used;
        }

        private static int[] Rebuild(int[] cameFrom, int end)
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

    /// <summary>Coarse-grid data for route planning (done once, when the map loads).</summary>
    private void BuildCells()
    {
        int w = _map.Width, h = _map.Height;
        int cw = (w + Cell - 1) / Cell, ch = (h + Cell - 1) / Cell, cells = cw * ch;
        _cellsWide = cw;
        _cellsHigh = ch;
        _cellBody = new int[cells];
        Array.Fill(_cellBody, NoBody);
        _cellLinks = new byte[cells];
        for (int t = 0; t < Body.Length; t++)
        {
            if (Body[t] == NoBody)
                continue;
            int x = t % w, y = t / w, c = (y / Cell) * cw + x / Cell;
            _cellBody[c] = _cellBody[c] == NoBody || _cellBody[c] == Body[t] ? Body[t] : Mixed;
            for (int d = 0; d < 8; d++)
            {
                int nx = x + Dx[d], ny = y + Dy[d];
                if (nx < 0 || ny < 0 || nx >= w || ny >= h || Body[ny * w + nx] == NoBody)
                    continue;
                if (Dx[d] != 0 && Dy[d] != 0 && (Body[y * w + nx] == NoBody || Body[ny * w + x] == NoBody))
                    continue;
                int ddx = nx / Cell - x / Cell, ddy = ny / Cell - y / Cell;
                if (ddx != 0 || ddy != 0)
                    _cellLinks[c] |= (byte)(1 << DirIndex(ddx, ddy));
            }
        }
    }

    private static int DirIndex(int dx, int dy)
    {
        for (int d = 0; d < 8; d++)
            if (Dx[d] == dx && Dy[d] == dy)
                return d;
        throw new ArgumentException("not a neighbour direction");
    }

    /// <summary>Coarse A* between the two cells; marks the cells around the route as the corridor.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool MarkCorridor(Workspace ws, int from, int to, int search, int radius)
    {
        int w = _map.Width, cw = _cellsWide, ch = _cellsHigh, body = Body[from];
        var cost = ws.CellCost;
        var cameFrom = ws.CellFrom;
        var mark = ws.CellMark;
        int seen = 2 * search, closed = 2 * search + 1;
        int start = (from / w / Cell) * cw + (from % w) / Cell;
        int goal = (to / w / Cell) * cw + (to % w) / Cell;
        int gx = goal % cw, gy = goal / cw;

        cost[start] = 0;
        cameFrom[start] = -1;
        mark[start] = seen;
        var open = new PriorityQueue<int, int>();
        open.Enqueue(start, 0);
        bool found = false;
        while (open.TryDequeue(out int c, out _))
        {
            if (mark[c] == closed)
                continue;
            mark[c] = closed;
            if (c == goal)
            {
                found = true;
                break;
            }
            int cx = c % cw, cy = c / cw, g = cost[c];
            for (int d = 0; d < 8; d++)
            {
                if ((_cellLinks[c] & (1 << d)) == 0)
                    continue; // no water actually flows that way
                int nx = cx + Dx[d], ny = cy + Dy[d];
                if (nx < 0 || ny < 0 || nx >= cw || ny >= ch)
                    continue;
                int n = ny * cw + nx;
                if ((_cellBody[n] != body && _cellBody[n] != Mixed) || mark[n] == closed)
                    continue;
                int ng = g + (Dx[d] != 0 && Dy[d] != 0 ? 14 : 10);
                if (mark[n] == seen && cost[n] <= ng)
                    continue;
                mark[n] = seen;
                cost[n] = ng;
                cameFrom[n] = c;
                int hx = Math.Abs(nx - gx), hy = Math.Abs(ny - gy);
                open.Enqueue(n, ng + 10 * Math.Max(hx, hy) + 4 * Math.Min(hx, hy));
            }
        }
        if (!found)
            return false;

        var corridor = ws.Corridor;
        for (int c = goal; ; c = cameFrom[c])
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
}
