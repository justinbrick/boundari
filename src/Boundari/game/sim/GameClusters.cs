using System;
using System.Collections.Generic;
using Boundari.Map;

namespace Boundari.Sim;

// Pockets of land cut off and walled in by enemies are handed over, the way OpenFront's
// PlayerExecution.removeClusters does it. A player's border tiles (next to anyone else's
// land, or water) are grouped into connected clusters (corners count). The cluster with
// the most tiles is the main body: it's only taken if one single enemy walls it in
// completely (no coast, map edge or unclaimed land). Any other cluster is taken when
// enemies box it in. Before land changes hands, a walk through our land and any unclaimed
// land must find no way out to water or the map edge; then that whole connected piece goes
// to the neighbour attacking us hardest (or, with no attacks, the one with the longest
// shared border). Each player is checked every 20 ticks (every tick under 100 tiles), and
// only if their land changed.
public sealed partial class Game
{
    private const int TicksPerClusterCalc = 20;
    private int[]? _clusterStamp;
    private int _clusterGen;
    private readonly Stack<int> _clusterStack = new();

    private void RemoveClusters()
    {
        _clusterStamp ??= new int[Map.TileCount];
        for (int id = 1; id < _players.Count; id++)
        {
            var p = _players[id];
            if (p.TileCount == 0)
                continue;
            if (p.LastClusterCalc == int.MinValue)
                p.LastClusterCalc = id % TicksPerClusterCalc - TicksPerClusterCalc; // as if set at the start; staggers players, like OpenFront
            if (Tick - p.LastClusterCalc <= TicksPerClusterCalc && p.TileCount >= Rules.ConquerBelowTiles)
                continue;
            if (p.LastLandChange < p.LastClusterCalc)
                continue;
            p.LastClusterCalc = Tick;
            RemoveClusters(p);
        }
    }

    private readonly record struct Box(int MinX, int MinY, int MaxX, int MaxY)
    {
        public bool Contains(Box o) => MinX <= o.MinX && MinY <= o.MinY && MaxX >= o.MaxX && MaxY >= o.MaxY;
    }

    private void RemoveClusters(Player p)
    {
        // Border tiles in OpenFront's sense: next to anything that isn't ours, water included.
        var border = new int[p.Border.Count + p.Coast.Count];
        p.Border.CopyTo(border, 0);
        p.Coast.CopyTo(border, p.Border.Count);
        Array.Sort(border); // HashSet order isn't guaranteed; keep every machine identical
        int borderGen = ++_clusterGen, doneGen = ++_clusterGen;
        var stamp = _clusterStamp!;
        foreach (int t in border)
            stamp[t] = borderGen;

        int w = Map.Width, h = Map.Height;
        var clusters = new List<List<int>>();
        var boxes = new List<Box>();
        foreach (int start in border)
        {
            if (stamp[start] != borderGen)
                continue; // already in a cluster (or a duplicate: coast and border both)
            var cluster = new List<int>();
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            _clusterStack.Clear();
            _clusterStack.Push(start);
            stamp[start] = doneGen;
            while (_clusterStack.Count > 0)
            {
                int t = _clusterStack.Pop();
                cluster.Add(t);
                int x = t % w, y = t / w;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= w || ny >= h)
                            continue;
                        int n = ny * w + nx;
                        if (stamp[n] == borderGen)
                        {
                            stamp[n] = doneGen;
                            _clusterStack.Push(n);
                        }
                    }
            }
            clusters.Add(cluster);
            boxes.Add(new Box(minX, minY, maxX, maxY));
        }
        if (clusters.Count == 0)
            return;

        int largest = 0;
        for (int i = 1; i < clusters.Count; i++)
            if (clusters[i].Count > clusters[largest].Count)
                largest = i;
        largest = AvoidHole(p, clusters, boxes, largest);

        // Judge every cluster first, then hand land over (OpenFront's order).
        var surrounder = SurroundedBySamePlayer(p, clusters[largest], boxes[largest]);
        var surrounded = new bool[clusters.Count];
        for (int i = 0; i < clusters.Count; i++)
            if (i != largest)
                surrounded[i] = IsBoxedIn(p, clusters[i], boxes[i]);

        if (surrounder is not null && !p.IsAlliedWith(surrounder))
            RemoveCluster(p, clusters[largest]);
        for (int i = 0; i < clusters.Count; i++)
            if (surrounded[i])
                RemoveCluster(p, clusters[i]);
    }

    /// <summary>
    /// A hole in our land (an enemy enclave, a crater) gives our territory an extra, inner
    /// border cluster. If the biggest cluster is such a ring around a hole, the main body is
    /// the biggest cluster that isn't one.
    /// </summary>
    private int AvoidHole(Player p, List<List<int>> clusters, List<Box> boxes, int largest)
    {
        bool IsHole(int i)
        {
            for (int j = 0; j < clusters.Count; j++)
                if (j != i && boxes[j].Contains(boxes[i]) && SameTerritory(p, clusters[i][0], clusters[j][0]))
                    return true;
            return false;
        }
        if (!IsHole(largest))
            return largest;
        int best = -1;
        for (int i = 0; i < clusters.Count; i++)
            if (i != largest && (best < 0 || clusters[i].Count > clusters[best].Count) && !IsHole(i))
                best = i;
        return best >= 0 ? best : largest;
    }

    /// <summary>Whether two of our tiles are joined through our own land.</summary>
    private bool SameTerritory(Player p, int a, int b)
    {
        var seen = new HashSet<int> { a };
        var queue = new Queue<int>();
        queue.Enqueue(a);
        Span<int> nb = stackalloc int[4];
        while (queue.Count > 0)
        {
            int t = queue.Dequeue();
            if (t == b)
                return true;
            int count = LandNeighbours(t, nb);
            for (int k = 0; k < count; k++)
                if (Owner[nb[k]] == p.Id && seen.Add(nb[k]))
                    queue.Enqueue(nb[k]);
        }
        return false;
    }

    private bool OnMapEdge(int t)
    {
        int x = t % Map.Width, y = t / Map.Width;
        return x == 0 || y == 0 || x == Map.Width - 1 || y == Map.Height - 1;
    }

    /// <summary>The tile's up to four neighbours (water included).</summary>
    private int Neighbours4(int t, Span<int> result)
    {
        int w = Map.Width, x = t % w, y = t / w, n = 0;
        if (x > 0) result[n++] = t - 1;
        if (x < w - 1) result[n++] = t + 1;
        if (y > 0) result[n++] = t - w;
        if (y < Map.Height - 1) result[n++] = t + w;
        return n;
    }

    /// <summary>
    /// The one enemy walling this cluster in on every side, or null: no tile on the
    /// ocean shore or map edge, no unclaimed land or water next to it, exactly one other
    /// owner around it, and that owner's tiles spanning the cluster's whole box.
    /// </summary>
    private Player? SurroundedBySamePlayer(Player p, List<int> cluster, Box box)
    {
        ushort enemy = Nobody;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        Span<int> nb = stackalloc int[4];
        foreach (int t in cluster)
        {
            if (OnMapEdge(t))
                return null;
            int count = Neighbours4(t, nb);
            for (int k = 0; k < count; k++)
            {
                int n = nb[k];
                if (Map.Terrain[n] == TerrainType.Ocean)
                    return null; // on the ocean shore
                ushort o = Owner[n];
                if (o == Nobody)
                    return null; // unclaimed land or a lake: not walled in
                if (o == p.Id)
                    continue;
                if (enemy != Nobody && o != enemy)
                    return null; // more than one enemy
                enemy = o;
                int x = n % Map.Width, y = n / Map.Width;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        }
        return enemy != Nobody && new Box(minX, minY, maxX, maxY).Contains(box) ? _players[enemy] : null;
    }

    /// <summary>OpenFront's CLUSTER_SURROUNDED: off the shore and map edge, with enemy land spanning the cluster's whole box.</summary>
    private bool IsBoxedIn(Player p, List<int> cluster, Box box)
    {
        bool enemy = false;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        Span<int> nb = stackalloc int[4];
        foreach (int t in cluster)
        {
            if (OnMapEdge(t) || _coastal![t])
                return false;
            int count = Neighbours4(t, nb);
            for (int k = 0; k < count; k++)
            {
                ushort o = Owner[nb[k]];
                if (o == Nobody || o == p.Id)
                    continue;
                enemy = true;
                int x = nb[k] % Map.Width, y = nb[k] / Map.Width;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        }
        return enemy && new Box(minX, minY, maxX, maxY).Contains(box);
    }

    /// <summary>Hands the walled-in piece of land this cluster borders to the right neighbour.</summary>
    private void RemoveCluster(Player p, List<int> cluster)
    {
        foreach (int t in cluster)
            if (Owner[t] != p.Id)
                return; // an earlier hand-over this pass already changed it
        var capturing = CapturingPlayer(p, cluster);
        if (capturing is null || !IsEnclosed(p, cluster[0]))
            return;

        var tiles = new List<int> { cluster[0] };
        var seen = new HashSet<int> { cluster[0] };
        Span<int> nb = stackalloc int[4];
        for (int i = 0; i < tiles.Count; i++)
        {
            int count = LandNeighbours(tiles[i], nb);
            for (int k = 0; k < count; k++)
                if (Owner[nb[k]] == p.Id && seen.Add(nb[k]))
                    tiles.Add(nb[k]);
        }
        if (tiles.Count == p.TileCount)
            TakeGold(capturing, p); // that was everything they had
        foreach (int t in tiles)
            SetOwner(t, capturing.Id);
    }

    /// <summary>
    /// Whether our land around this tile is sealed in by other players: walking through
    /// our land and any unclaimed land never reaches water or the map edge.
    /// </summary>
    private bool IsEnclosed(Player p, int start)
    {
        var seen = new HashSet<int> { start };
        _clusterStack.Clear();
        _clusterStack.Push(start);
        Span<int> nb = stackalloc int[4];
        while (_clusterStack.Count > 0)
        {
            int t = _clusterStack.Pop();
            if (OnMapEdge(t))
                return false;
            int count = Neighbours4(t, nb);
            for (int k = 0; k < count; k++)
            {
                int n = nb[k];
                if (seen.Contains(n))
                    continue;
                ushort o = Owner[n];
                if (o != Nobody && o != p.Id)
                    continue; // someone else's land: part of the wall
                if (o == Nobody && !Map.IsLand(n))
                    return false; // water: a way out
                seen.Add(n);
                _clusterStack.Push(n);
            }
        }
        return true;
    }

    /// <summary>The non-allied neighbour attacking us with the most troops, or else the one touching the cluster most.</summary>
    private Player? CapturingPlayer(Player p, List<int> cluster)
    {
        var counts = new Dictionary<ushort, int>();
        var order = new List<ushort>();
        Span<int> nb = stackalloc int[4];
        foreach (int t in cluster)
        {
            int count = Neighbours4(t, nb);
            for (int k = 0; k < count; k++)
            {
                ushort o = Owner[nb[k]];
                if (o == Nobody || o == p.Id || p.Allies.Contains(o))
                    continue;
                if (!counts.ContainsKey(o))
                    order.Add(o);
                counts[o] = counts.GetValueOrDefault(o) + 1;
            }
        }
        if (order.Count == 0)
            return null;
        Player? strongest = null;
        double most = 0;
        foreach (ushort id in order)
            foreach (var a in _attacks)
                if (a.Attacker.Id == id && a.TargetId == p.Id && a.TroopsExact > most)
                {
                    most = a.TroopsExact;
                    strongest = a.Attacker;
                }
        if (strongest is not null)
            return strongest;
        ushort mode = order[0];
        foreach (ushort id in order)
            if (counts[id] > counts[mode])
                mode = id;
        return _players[mode];
    }
}
