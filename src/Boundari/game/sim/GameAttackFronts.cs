using System;
using System.Collections.Generic;

namespace Boundari.Sim;

/// <summary>Where an attack's troop count is drawn on the map (OpenFront's AttackImpl.clusteredPositions).</summary>
public sealed partial class Game
{
    /// <summary>OpenFront drops a second front piece smaller than this many tiles.</summary>
    private const int FrontClusterMinTiles = 30;
    private const int FrontClusterMax = 2;

    /// <summary>
    /// One tile on each of the attack's biggest front lines (at most two, largest first): the
    /// attack's border splits into pieces that don't touch (diagonals count), and each piece
    /// is marked by its tile nearest the piece's centre. Pieces under
    /// <see cref="FrontClusterMinTiles"/> are left out unless there's nothing bigger. With no
    /// front yet, a boat attack's landing tile. A thin directed attack has one spot, on the
    /// lead of its push. Read-only; only for drawing.
    /// </summary>
    public List<int> AttackFrontTiles(Attack attack)
    {
        var result = new List<int>();
        if (attack.Border.Count == 0)
        {
            if (attack.SourceTile >= 0)
                result.Add(attack.SourceTile);
            return result;
        }
        if (attack.Arrow is { } arrow && arrow.LeadTile(attack.Border, Map.Width) is { } lead and >= 0)
        {
            result.Add(lead);
            return result;
        }

        int w = Map.Width, h = Map.Height;
        var border = new List<int>(attack.Border);
        border.Sort();
        var visited = new HashSet<int>();
        var clusters = new List<(int Tile, int Size)>();
        var queue = new List<int>();
        foreach (int start in border)
        {
            if (!visited.Add(start))
                continue;
            queue.Clear();
            queue.Add(start);
            long sumX = 0, sumY = 0;
            for (int i = 0; i < queue.Count; i++)
            {
                int t = queue[i], x = t % w, y = t / w;
                sumX += x;
                sumY += y;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((dx | dy) == 0 || nx < 0 || ny < 0 || nx >= w || ny >= h)
                            continue;
                        int n = ny * w + nx;
                        if (attack.Border.Contains(n) && visited.Add(n))
                            queue.Add(n);
                    }
            }
            // The centre may not be a front tile, so the front tile nearest it stands in.
            double cx = (double)sumX / queue.Count, cy = (double)sumY / queue.Count;
            int best = queue[0];
            double bestDist = double.MaxValue;
            foreach (int t in queue)
            {
                double ddx = t % w - cx, ddy = t / w - cy, d = ddx * ddx + ddy * ddy;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = t;
                }
            }
            clusters.Add((best, queue.Count));
        }

        clusters.Sort((a, b) => b.Size != a.Size ? b.Size.CompareTo(a.Size) : a.Tile.CompareTo(b.Tile));
        int minSize = Rules.A(FrontClusterMinTiles);
        foreach (var c in clusters)
            if (c.Size >= minSize && result.Count < FrontClusterMax)
                result.Add(c.Tile);
        if (result.Count == 0)
            result.Add(clusters[0].Tile);
        return result;
    }
}
