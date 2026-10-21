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
    public int LandingTile { get; internal set; }

    /// <summary>Unique for the match (so a player can name the boat to call it back).</summary>
    public int Id { get; internal set; }

    /// <summary>The land tile it set off from (where it heads back to if called back).</summary>
    public readonly int SourceTile;

    /// <summary>Called back: sailing back along its path to where it set off.</summary>
    public bool Retreating { get; internal set; }

    /// <summary>Water tiles from the launch shore to the landing shore.</summary>
    public readonly int[] Path;

    /// <summary>Distance travelled along <see cref="Path"/>, in thousandths of a step.</summary>
    public long Progress { get; internal set; }

    internal Boat(Player owner, long troops, int landingTile, int sourceTile, int[] path)
    {
        Owner = owner;
        Troops = troops;
        LandingTile = landingTile;
        SourceTile = sourceTile;
        Path = path;
    }

    public bool Arrived => Progress >= (Path.Length - 1) * 1000L;

    public int Tile => Path[(int)System.Math.Min(Progress / 1000, Path.Length - 1)];

    /// <summary>Sunk by a warship (troops lost).</summary>
    public bool Sunk { get; internal set; }

    /// <summary>Whose warship or bomb sank it (if it was sunk).</summary>
    public Player? SunkBy { get; internal set; }
}

/// <summary>Send a share of troops (or exactly <paramref name="Troops"/>, if set) by boat to land as close as possible to a tile.</summary>
public sealed record BoatIntent(ushort PlayerId, int Tile, int RatioPercent, long Troops = 0) : Intent(PlayerId);

/// <summary>Send one of your warships to patrol somewhere else (water tile).</summary>
public sealed record MoveWarshipIntent(ushort PlayerId, int WarshipId, int Tile) : Intent(PlayerId);

/// <summary>Call one of your boats back (it sails home; a quarter of its troops are lost, as in OpenFront).</summary>
public sealed record BoatRetreatIntent(ushort PlayerId, int BoatId) : Intent(PlayerId);

/// <summary>Water connectivity and shortest sea routes, for boats.</summary>
public sealed class Waterways
{
    public const int NoBody = -1;

    private readonly GameMap _map;

    /// <summary>Which connected body of water each water tile belongs to (land = <see cref="NoBody"/>).</summary>
    public readonly int[] Body;

    /// <summary>Tiles in each body of water.</summary>
    public readonly int[] BodySize;

    public Waterways(GameMap map, double tileScale = 1)
    {
        _map = map;
        ShoreNear = Math.Max(1, (int)Math.Round(BaseShoreNear * tileScale));
        ShoreFar = Math.Max(ShoreNear, (int)Math.Round(BaseShoreFar * tileScale));
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
        BuildShoreDistance();
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

    // Boats sail a few tiles off the coast, like OpenFront's (AStar.WaterBounded): a step
    // closer than ShoreNear tiles to land costs 3 extra steps, ShoreNear..ShoreFar is free,
    // and open water further out costs 1 extra step, so routes neither hug the coast nor
    // swing far out to sea. Distances are worked out once, when the map loads.
    private const int BaseShoreNear = 3;       // tiles, on the reference map (scaled by the tile scale)
    private const int BaseShoreFar = 10;
    private readonly int ShoreNear, ShoreFar;
    private const int NearShorePenalty = 30;   // per step (a straight step costs 10)
    private const int OpenSeaPenalty = 10;
    private int ShoreMargin => ShoreFar + 1;
    private byte[] _shoreDist = Array.Empty<byte>(); // water tiles: tiles to the nearest land (capped at ShoreMargin)

    /// <summary>Extra cost of a step onto a water tile this many tiles from land.</summary>
    private int ShorePenalty(int distance) => distance < ShoreNear ? NearShorePenalty : distance <= ShoreFar ? 0 : OpenSeaPenalty;

    private void BuildShoreDistance()
    {
        int w = _map.Width, h = _map.Height;
        _shoreDist = new byte[_map.TileCount];
        var queue = new Queue<int>();
        for (int t = 0; t < _map.TileCount; t++)
        {
            if (Body[t] == NoBody)
                continue;
            int x = t % w, y = t / w;
            bool shore = (x > 0 && Body[t - 1] == NoBody) || (x < w - 1 && Body[t + 1] == NoBody)
                || (y > 0 && Body[t - w] == NoBody) || (y < h - 1 && Body[t + w] == NoBody);
            _shoreDist[t] = (byte)(shore ? 1 : ShoreMargin);
            if (shore)
                queue.Enqueue(t);
        }
        while (queue.Count > 0)
        {
            int t = queue.Dequeue(), d = _shoreDist[t] + 1;
            if (d >= ShoreMargin)
                continue;
            int x = t % w, y = t / w;
            void Visit(int n)
            {
                if (Body[n] != NoBody && _shoreDist[n] > d)
                {
                    _shoreDist[n] = (byte)d;
                    queue.Enqueue(n);
                }
            }
            if (x > 0) Visit(t - 1);
            if (x < w - 1) Visit(t + 1);
            if (y > 0) Visit(t - w);
            if (y < h - 1) Visit(t + w);
        }
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
    private byte[] _cellCoast = Array.Empty<byte>(); // cells to the nearest cell with land in it (capped at CellMargin)

    // The rough route follows the same rule on cells: cells with land in them cost as much
    // as the coast, the ring of cells next to them is free, open sea beyond costs a little.
    private const int CellMargin = 2;
    private static int CellPenalty(int coast) => coast == 0 ? NearShorePenalty : coast == 1 ? 0 : OpenSeaPenalty;
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
            var shore = _w._shoreDist;
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
                    int ng = g + (dx != 0 && dy != 0 ? 14 : 10) + _w.ShorePenalty(shore[n]);
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
        _cellCoast = new byte[cells];
        Array.Fill(_cellCoast, (byte)CellMargin);
        var coastQueue = new Queue<int>();
        for (int t = 0; t < Body.Length; t++)
        {
            if (Body[t] == NoBody)
            {
                int lc = (t / w / Cell) * cw + (t % w) / Cell;
                if (_cellCoast[lc] != 0)
                {
                    _cellCoast[lc] = 0;
                    coastQueue.Enqueue(lc);
                }
                continue;
            }
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
        while (coastQueue.Count > 0)
        {
            int c = coastQueue.Dequeue(), d = _cellCoast[c] + 1;
            if (d >= CellMargin)
                continue;
            int cx = c % cw, cy = c / cw;
            for (int k = 0; k < 8; k++)
            {
                int nx = cx + Dx[k], ny = cy + Dy[k];
                if (nx < 0 || ny < 0 || nx >= cw || ny >= ch || _cellCoast[ny * cw + nx] <= d)
                    continue;
                _cellCoast[ny * cw + nx] = (byte)d;
                coastQueue.Enqueue(ny * cw + nx);
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
        if (!CoarseSearch(ws, from, to, search, out int start, out int goal))
            return false;
        int cw = _cellsWide, ch = _cellsHigh;
        var cameFrom = ws.CellFrom;
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

    private readonly Dictionary<(int Start, int Goal, int Body), int[]?> _roughRoutes = new();
    private const int MaxRoughRoutes = 8192;
    private const int RoughBlockCells = 4;          // rough routes are remembered per 4x4 cells (32x32 tiles) at each end
    private const int RoughHeuristicPercent = 200;  // and found greedily: they only need to be roughly right

    /// <summary>
    /// A rough sea route between two water tiles: one tile per coarse cell along the way, the
    /// ends exact. Far cheaper than <see cref="FindPath"/> (and remembered), for the AI to see
    /// whether warships lie along a voyage and roughly how long it is. Null if there's none
    /// shorter than about <paramref name="maxTiles"/>.
    /// </summary>
    public int[]? RoughRoute(int from, int to, int maxTiles)
    {
        if (Body[from] == NoBody || Body[from] != Body[to])
            return null;
        int w = _map.Width, cw = _cellsWide;
        int bw = (cw + RoughBlockCells - 1) / RoughBlockCells, b = Cell * RoughBlockCells;
        var key = ((from / w / b) * bw + (from % w) / b, (to / w / b) * bw + (to % w) / b, Body[from] * 64 + Math.Min(63, maxTiles / 64));
        if (!_roughRoutes.TryGetValue(key, out var cells))
        {
            if (_roughRoutes.Count >= MaxRoughRoutes)
                _roughRoutes.Clear();
            var ws = _instant;
            cells = null;
            // A step costs 10 per cell (8 tiles), more near coasts: allow double that.
            int maxCost = maxTiles >= int.MaxValue / 4 ? int.MaxValue : maxTiles / Cell * 20;
            if (CoarseSearch(ws, from, to, ++ws.Search, out int start, out int goal, RoughHeuristicPercent, maxCost))
            {
                var list = new List<int>();
                for (int c = goal; ; c = ws.CellFrom[c])
                {
                    list.Add(c);
                    if (c == start)
                        break;
                }
                list.Reverse();
                cells = list.ToArray();
            }
            _roughRoutes[key] = cells;
        }
        if (cells is null)
            return null;
        var route = new int[cells.Length + 1];
        route[0] = from;
        for (int i = 1; i < cells.Length; i++)
        {
            int cx = cells[i] % cw, cy = cells[i] / cw;
            route[i] = Math.Min(cy * Cell + Cell / 2, _map.Height - 1) * w + Math.Min(cx * Cell + Cell / 2, w - 1);
        }
        route[^1] = to;
        return route;
    }

    /// <summary>A* over the coarse cells from one water tile's cell to another's (results in the workspace's cell arrays).</summary>
    private bool CoarseSearch(Workspace ws, int from, int to, int search, out int start, out int goal, int heuristicPercent = 100, int maxCost = int.MaxValue)
    {
        int w = _map.Width, cw = _cellsWide, ch = _cellsHigh, body = Body[from];
        var cost = ws.CellCost;
        var cameFrom = ws.CellFrom;
        var mark = ws.CellMark;
        int seen = 2 * search, closed = 2 * search + 1;
        start = (from / w / Cell) * cw + (from % w) / Cell;
        goal = (to / w / Cell) * cw + (to % w) / Cell;
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
            if (g > maxCost)
                continue; // further than the caller cares about
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
                int ng = g + (Dx[d] != 0 && Dy[d] != 0 ? 14 : 10) + CellPenalty(_cellCoast[n]);
                if (mark[n] == seen && cost[n] <= ng)
                    continue;
                mark[n] = seen;
                cost[n] = ng;
                cameFrom[n] = c;
                int hx = Math.Abs(nx - gx), hy = Math.Abs(ny - gy);
                open.Enqueue(n, ng + (10 * Math.Max(hx, hy) + 4 * Math.Min(hx, hy)) * heuristicPercent / 100);
            }
        }
        return found;
    }
}
