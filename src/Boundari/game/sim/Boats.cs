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
    private int[]? _cost, _cameFrom;

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

    /// <summary>
    /// Shortest water route (A*, 8-directional, never cutting corners of land) between two
    /// water tiles in the same body. Returns null if none is found within the search limit.
    /// </summary>
    public int[]? FindPath(int from, int to, int maxExpanded = 4_000_000)
    {
        if (Body[from] == NoBody || Body[from] != Body[to])
            return null;
        int w = _map.Width, h = _map.Height;
        int tx = to % w, ty = to / w;
        // Flat arrays (allocated once, reset per search) are far faster than dictionaries here.
        _cost ??= new int[_map.TileCount];
        _cameFrom ??= new int[_map.TileCount];
        var cost = _cost;
        var cameFrom = _cameFrom;
        Array.Fill(cost, int.MaxValue);
        cost[from] = 0;
        cameFrom[from] = -1;
        var open = new PriorityQueue<int, int>();
        open.Enqueue(from, Heuristic(from));
        int expanded = 0;

        while (open.TryDequeue(out int current, out _))
        {
            if (current == to)
                return Rebuild(current);
            if (++expanded > maxExpanded)
                return null;
            int cx = current % w, cy = current / w, g = cost[current];
            if (g == int.MaxValue)
                continue;
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
                    if (dx != 0 && dy != 0 && (Body[cy * w + nx] == NoBody || Body[ny * w + cx] == NoBody))
                        continue; // don't squeeze diagonally between two land tiles
                    int step = dx != 0 && dy != 0 ? 14 : 10;
                    int ng = g + step;
                    if (cost[n] <= ng)
                        continue;
                    cost[n] = ng;
                    cameFrom[n] = current;
                    open.Enqueue(n, ng + Heuristic(n));
                }
        }
        return null;

        int Heuristic(int t)
        {
            int dx = Math.Abs(t % w - tx), dy = Math.Abs(t / w - ty);
            return 10 * Math.Max(dx, dy) + 4 * Math.Min(dx, dy);
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
