using System;
using System.Collections.Generic;
using Boundari.Core;

namespace Boundari.Sim;

/// <summary>
/// Spawning for the dots game (the user's design): commanders start on random spots of
/// the mainland, spaced apart, and Locals cover most of the map from the start: each grows
/// out from a seed scattered over the land until it meets its neighbours, bigger or smaller
/// by chance, its edges bent by the land (forest and rivers are slower to grow through) and
/// by a lumpy noise. Only a patch of wilderness round each commander stays free, so a match
/// opens with a short expansion and then grows by taking Locals. Locals have no dots and
/// never attack: their land just holds (see <see cref="DotRules.LocalsForce"/>).
/// </summary>
public sealed partial class Game
{
    private readonly List<int> _commanderSpawns = new();

    /// <summary>
    /// Starts a commander on a random free spot of the biggest landmass, at least
    /// <see cref="LocalsRules.CommanderSpacing"/> from the others if there is room (else as
    /// far as can be found). False if there is no free land.
    /// </summary>
    public bool SpawnAtRandom(Player player)
    {
        int mainland = 0;
        for (int i = 1; i < Map.LandmassSizes.Length; i++)
            if (Map.LandmassSizes[i] > Map.LandmassSizes[mainland])
                mainland = i;
        long wanted = (long)Rules.D(LocalsRules.CommanderSpacing) * Rules.D(LocalsRules.CommanderSpacing);
        int margin = Rules.D(LocalsRules.WildernessRadius), w = Map.Width, h = Map.Height;
        int best = -1;
        long bestDist = -1;
        for (int tries = 0; tries < 2000 && bestDist < wanted; tries++)
        {
            int t = _rng.RangeInt(0, Map.TileCount - 1), x = t % w, y = t / w;
            if (!Map.IsLand(t) || Owner[t] != Nobody || Map.LandmassId[t] != mainland)
                continue;
            if (tries < 1000 && (x < margin || y < margin || x >= w - margin || y >= h - margin))
                continue; // (keep off the edge of the map while there's a choice: the wilderness round it would be cut short)
            long d = long.MaxValue;
            foreach (int s in _commanderSpawns)
            {
                long dx = x - s % w, dy = y - s / w;
                d = Math.Min(d, dx * dx + dy * dy);
            }
            if (d > bestDist)
                (best, bestDist) = (t, d);
        }
        if (best < 0)
            return false;
        _commanderSpawns.Add(best);
        _aiSpawns.Add(best);
        Spawn(player, best);
        return player.HasSpawned;
    }

    /// <summary>
    /// Adds Locals that share out all the free land (wilderness is left only round the
    /// commanders, and on landmasses too small for a seed). Call after placing the commanders.
    /// </summary>
    private void AddLocals(int count)
    {
        var rng = new Rng(_rng.Fork());
        var names = new Rng(_rng.Fork());
        int n = Map.TileCount, w = Map.Width, h = Map.Height;

        // A lumpy noise over the map (thousandths, LocalsRules.NoiseLow..NoiseHigh): a lattice
        // of random values, blended between its points.
        int cell = Math.Max(4, Rules.D(LocalsRules.NoiseCell)), cw = w / cell + 2, ch = h / cell + 2;
        var lattice = new int[cw * ch];
        for (int i = 0; i < lattice.Length; i++)
            lattice[i] = rng.RangeInt(LocalsRules.NoiseLow, LocalsRules.NoiseHigh);
        int Noise(int t)
        {
            int x = t % w, y = t / w, gx = x / cell, gy = y / cell, fx = x % cell, fy = y % cell;
            long top = (long)lattice[gy * cw + gx] * (cell - fx) + (long)lattice[gy * cw + gx + 1] * fx;
            long bottom = (long)lattice[(gy + 1) * cw + gx] * (cell - fx) + (long)lattice[(gy + 1) * cw + gx + 1] * fx;
            return (int)((top * (cell - fy) + bottom * fy) / ((long)cell * cell));
        }

        // The wilderness kept round each commander (its edge wobbles with the noise).
        var blocked = new bool[n];
        int radius = Rules.D(LocalsRules.WildernessRadius), far = radius * LocalsRules.NoiseHigh / 1000 + 1;
        foreach (int s in _commanderSpawns)
        {
            int sx = s % w, sy = s / w;
            for (int y = Math.Max(0, sy - far); y <= Math.Min(h - 1, sy + far); y++)
                for (int x = Math.Max(0, sx - far); x <= Math.Min(w - 1, sx + far); x++)
                {
                    int t = y * w + x;
                    long r = (long)radius * Noise(t) / 1000, dx = x - sx, dy = y - sy;
                    if (dx * dx + dy * dy <= r * r)
                        blocked[t] = true;
                }
        }
        bool Free(int t) => Map.IsLand(t) && Owner[t] == Nobody && !blocked[t];

        // Seeds scattered over the land: of a few random free spots each, the one furthest from the others.
        int minLandmass = Rules.A(LocalsRules.MinLandmassTiles);
        var seeds = new List<int>();
        var spread = new List<int>(_commanderSpawns);
        for (int i = 0; i < count; i++)
        {
            int best = -1;
            long bestDist = -1;
            for (int tries = 0; tries < LocalsRules.SeedTries * 4 && (tries < LocalsRules.SeedTries || best < 0); tries++)
            {
                int t = rng.RangeInt(0, n - 1);
                if (!Free(t) || Map.LandmassSizes[Map.LandmassId[t]] < minLandmass)
                    continue;
                long d = long.MaxValue;
                foreach (int s in spread)
                {
                    long dx = t % w - s % w, dy = t / w - s / w;
                    d = Math.Min(d, dx * dx + dy * dy);
                }
                if (d > bestDist)
                    (best, bestDist) = (t, d);
            }
            if (best < 0)
                break; // no free land left
            seeds.Add(best);
            spread.Add(best);
        }

        // Every Local grows at once from its seed (cheapest first: Dial's buckets), at its own
        // pace, so each free tile goes to whichever reaches it first.
        var region = new int[n];
        Array.Fill(region, -1);
        var cost = new int[n];
        const int Buckets = 2048; // more than one step's dearest cost (cover 40 x 14 x noise / pace)
        var bucket = new List<int>[Buckets];
        for (int b = 0; b < Buckets; b++)
            bucket[b] = new List<int>();
        var pace = new int[seeds.Count];
        for (int i = 0; i < seeds.Count; i++)
        {
            pace[i] = rng.RangeInt(LocalsRules.PaceLow, LocalsRules.PaceHigh);
            region[seeds[i]] = i;
            bucket[0].Add(seeds[i]);
        }
        var done = new bool[n];
        int queued = seeds.Count;
        for (long at = 0; queued > 0; at++)
        {
            var list = bucket[at % Buckets];
            for (int k = 0; k < list.Count; k++)
            {
                int t = list[k];
                queued--;
                if (done[t] || cost[t] != at)
                    continue; // (queued again later, cheaper)
                done[t] = true;
                int x = t % w, y = t / w, owner = region[t];
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((dx | dy) == 0 || nx < 0 || ny < 0 || nx >= w || ny >= h)
                            continue;
                        int next = ny * w + nx;
                        if (done[next] || !Free(next))
                            continue;
                        long step = (long)CoverTenths[next] * (dx != 0 && dy != 0 ? 14 : 10) * Noise(next) / pace[owner];
                        int c = (int)(at + Math.Clamp(step, 1, Buckets - 1));
                        if (region[next] >= 0 && cost[next] <= c)
                            continue;
                        region[next] = owner;
                        cost[next] = c;
                        bucket[c % Buckets].Add(next);
                        queued++;
                    }
            }
            list.Clear();
        }

        var locals = new Player[seeds.Count];
        for (int i = 0; i < seeds.Count; i++)
        {
            var local = AddPlayer(BotNames.Random(names), PlayerKind.Bot);
            local.HasSpawned = true;
            locals[i] = local;
            _aiSpawns.Add(seeds[i]);
        }
        for (int t = 0; t < n; t++)
            if (region[t] >= 0)
                SetOwner(t, locals[region[t]].Id);
    }
}

/// <summary>Numbers for spawning in the dots game (the user's design; tiles of the Ardennes map, scaled for others).</summary>
public static class LocalsRules
{
    /// <summary>Commanders start at least this far apart, if the mainland has room.</summary>
    public const int CommanderSpacing = 250;

    /// <summary>Wilderness left round each commander's start (give or take the noise).</summary>
    public const int WildernessRadius = 45;

    /// <summary>Tiles between the points of the noise that bends the Locals' borders.</summary>
    public const int NoiseCell = 40;

    /// <summary>The noise's range, thousandths: land grows this much slower or faster.</summary>
    public const int NoiseLow = 600, NoiseHigh = 1400;

    /// <summary>Each Local's pace of growth, thousandths (faster grows bigger).</summary>
    public const int PaceLow = 600, PaceHigh = 1600;

    /// <summary>Random spots tried for each seed (the one furthest from the others wins).</summary>
    public const int SeedTries = 30;

    /// <summary>Landmasses smaller than this get no Locals (they stay wilderness).</summary>
    public const int MinLandmassTiles = 1100;
}
