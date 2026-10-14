using System;
using System.Collections.Generic;
using Boundari.Core;
using Boundari.Map;

namespace Boundari.DevTools;

/// <summary>
/// Fake nations for previewing how territories and borders look before the real
/// expansion simulation exists. Not used by gameplay.
/// </summary>
public static class DemoTerritories
{
    public static ushort[] Generate(GameMap map, ulong seed, int nations)
    {
        var rng = new Rng(seed);
        var noise = new Noise(rng.Fork());
        int n = map.TileCount, w = map.Width;
        var owners = new ushort[n];
        var cost = new float[n];
        Array.Fill(cost, float.PositiveInfinity);
        var budget = new float[nations + 1];
        var queue = new PriorityQueue<int, float>();
        var spawns = new List<int>();

        for (int id = 1; id <= nations; id++)
        {
            // Pick a land tile far from the other spawns.
            int best = -1;
            double bestDist = -1;
            for (int tries = 0; tries < 60; tries++)
            {
                int t = rng.RangeInt(0, n - 1);
                if (!map.IsLand(t))
                    continue;
                double d = double.MaxValue;
                foreach (int s in spawns)
                {
                    int dx = t % w - s % w, dy = t / w - s / w;
                    d = Math.Min(d, dx * dx + dy * dy);
                }
                if (d > bestDist)
                {
                    bestDist = d;
                    best = t;
                }
            }
            if (best < 0)
                continue;
            spawns.Add(best);
            owners[best] = (ushort)id;
            cost[best] = 0;
            budget[id] = (float)rng.Range(60, 260);
            queue.Enqueue(best, 0);
        }

        // Grow all nations at once; noisy step costs give organic, wiggly borders.
        while (queue.TryDequeue(out int t, out float c))
        {
            if (c > cost[t])
                continue;
            ushort id = owners[t];
            int x = t % w, y = t / w;
            bool left = x > 0, right = x < w - 1, up = y > 0, down = y < map.Height - 1;
            if (left) Step(t - 1, 1f);
            if (right) Step(t + 1, 1f);
            if (up) Step(t - w, 1f);
            if (down) Step(t + w, 1f);
            // Diagonals keep shapes round instead of diamond-shaped.
            if (left && up) Step(t - w - 1, 1.414f);
            if (right && up) Step(t - w + 1, 1.414f);
            if (left && down) Step(t + w - 1, 1.414f);
            if (right && down) Step(t + w + 1, 1.414f);

            void Step(int nb, float distance)
            {
                if (!map.IsLand(nb))
                    return;
                float terrain = map.Terrain[nb] switch
                {
                    TerrainType.Mountains => 3f,
                    TerrainType.Highlands => 1.6f,
                    _ => 1f,
                };
                double wiggle = 1.0 + 0.7 * noise.Fbm(nb % w * 0.02, nb / w * 0.02, 3);
                float nc = c + distance * terrain * (float)wiggle;
                if (nc > budget[id] || nc >= cost[nb])
                    return;
                cost[nb] = nc;
                owners[nb] = id;
                queue.Enqueue(nb, nc);
            }
        }
        return owners;
    }
}
