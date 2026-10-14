using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Boundari.Core;

namespace Boundari.Map;

public sealed class MapGenSettings
{
    public int Width = 2000;
    public int Height = 1250;

    public int MinContinents = 3;
    public int MaxContinents = 5;

    /// <summary>Roughly what fraction of the map is land.</summary>
    public double LandFraction = 0.36;

    /// <summary>Clusters of small islands placed out in open ocean.</summary>
    public int MinIslandClusters = 5;
    public int MaxIslandClusters = 10;

    /// <summary>Landmasses smaller than this are sunk (too small to matter).</summary>
    public int MinIslandTiles = 40;

    /// <summary>Enclosed lakes smaller than this are filled in.</summary>
    public int MinLakeTiles = 150;

    /// <summary>Share of land tiles that are plains / highlands; the rest is mountains.</summary>
    public double PlainsShare = 0.55;
    public double HighlandsShare = 0.30;
}

/// <summary>
/// Generates a random map of several continents plus scattered islands.
/// Deterministic: the same seed and settings always give the same map.
/// </summary>
public static class MapGenerator
{
    // A landmass holding at least this share of all land counts as a continent;
    // anything smaller is just an island.
    private const double ContinentMinShareOfLand = 0.04;

    private const int MaxAttempts = 6;

    private struct Blob
    {
        public double X, Y;
        public double DirX, DirY; // unit vector of the major axis
        public double MajorRadius, MinorRadius;
        public double Peak;
    }

    public static GameMap Generate(ulong seed, MapGenSettings? settings = null)
    {
        settings ??= new MapGenSettings();

        // A seed can occasionally produce continents that merge together. Retry with
        // derived seeds (still deterministic) until we get enough separate ones.
        var seeds = new Rng(seed);
        GameMap map = null!;
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            ulong attemptSeed = attempt == 0 ? seed : seeds.Fork();
            map = GenerateOnce(seed, attemptSeed, settings, out int wantedContinents);
            int minContinentTiles = (int)(map.LandTileCount * ContinentMinShareOfLand);
            if (map.CountLandmasses(minContinentTiles) >= Math.Min(wantedContinents, settings.MinContinents))
                break;
        }
        return map;
    }

    private static GameMap GenerateOnce(ulong mapSeed, ulong seed, MapGenSettings s, out int continentCount)
    {
        var rng = new Rng(seed);
        var map = new GameMap(s.Width, s.Height, mapSeed);
        double aspect = (double)s.Width / s.Height;

        var shapeNoise = new Noise(rng.Fork());
        var warpNoiseX = new Noise(rng.Fork());
        var warpNoiseY = new Noise(rng.Fork());
        var ridgeNoise = new Noise(rng.Fork());
        var rangeNoise = new Noise(rng.Fork());

        continentCount = rng.RangeInt(s.MinContinents, s.MaxContinents);
        var continents = PlaceContinents(rng, continentCount, aspect, s.LandFraction);
        var islands = PlaceIslands(rng, s, continents, aspect);

        // --- Pass 1: continuous fields for every tile (parallel, each tile independent) ---
        float[] coast = map.CoastField;
        float[] rawElevation = map.Elevation;
        Parallel.For(0, s.Height, y =>
        {
            for (int x = 0; x < s.Width; x++)
            {
                // Normalised coordinates: y in [0, 1], x in [0, aspect].
                double nx = (x + 0.5) / s.Height;
                double ny = (y + 0.5) / s.Height;

                // Two layers of domain warp bend the blob outlines into peninsulas and bays.
                double wx = nx + 0.13 * warpNoiseX.Fbm(nx * 1.8, ny * 1.8, 4);
                double wy = ny + 0.13 * warpNoiseY.Fbm(nx * 1.8 + 5.2, ny * 1.8 + 1.3, 4);
                wx += 0.035 * warpNoiseX.Fbm(wx * 7 + 20, wy * 7 + 20, 3);
                wy += 0.035 * warpNoiseY.Fbm(wx * 7 + 30, wy * 7 + 30, 3);

                double shape = Math.Max(BlobField(continents, wx, wy), BlobField(islands, wx, wy));

                double field = shape
                    + 0.34 * shapeNoise.Fbm(nx * 3.2, ny * 3.2, 7, 2.0, 0.55)
                    + 0.07 * shapeNoise.Fbm(nx * 20 + 40, ny * 20 + 40, 4);

                // Keep a band of ocean around the map edge.
                double edge = Math.Min(Math.Min(nx, aspect - nx), Math.Min(ny, 1 - ny));
                field = Math.Min(field, (edge - 0.025) * 10.0);

                // Elevation: rolling hills everywhere, ridged mountain ranges in places,
                // and always low near the coast.
                double lowCoast = SmoothStep(-0.05, 0.35, field);
                double hills = 0.5 + 0.5 * shapeNoise.Fbm(nx * 2.6 + 100, ny * 2.6 + 100, 5);
                double ranges = SmoothStep(0.0, 0.4, rangeNoise.Fbm(nx * 2.4, ny * 2.4, 3));
                double ridge = ridgeNoise.Ridged(wx * 4.5, wy * 4.5, 5);
                double elevation = lowCoast * (0.55 * hills + 0.85 * ridge * ridge * ranges)
                    + 0.06 * shapeNoise.Fbm(nx * 12 + 200, ny * 12 + 200, 3);

                int i = y * s.Width + x;
                coast[i] = (float)field;
                rawElevation[i] = (float)elevation;
            }
        });

        // --- Pass 2: classify, clean up tiny islands and lakes, label landmasses ---
        MapProcessing.RemoveSmallLandmasses(map, s.MinIslandTiles);
        MapProcessing.ClassifyWater(map, s.MinLakeTiles);
        NormaliseElevation(map);
        MapProcessing.AssignTerrain(map, (float)s.PlainsShare, (float)(s.PlainsShare + s.HighlandsShare));
        MapProcessing.LabelLandmasses(map);
        return map;
    }

    // ------------------------------------------------------------------
    //  Placement
    // ------------------------------------------------------------------

    private static List<Blob> PlaceContinents(Rng rng, int count, double aspect, double landFraction)
    {
        var list = new List<Blob>();
        double mapArea = aspect;
        double baseRadius = Math.Sqrt(landFraction * mapArea / (count * Math.PI));

        var centres = new List<(double X, double Y, double Radius)>();
        for (int c = 0; c < count; c++)
        {
            double radius = baseRadius * rng.Range(0.8, 1.2);

            // Best-candidate sampling: try several spots, keep the one furthest from the rest.
            double bestX = 0, bestY = 0, bestScore = double.NegativeInfinity;
            double margin = radius * 0.8 + 0.04;
            for (int tries = 0; tries < 40; tries++)
            {
                double x = rng.Range(margin, aspect - margin);
                double y = rng.Range(margin, 1 - margin);
                double score = double.PositiveInfinity;
                foreach (var other in centres)
                {
                    double d = Math.Sqrt((x - other.X) * (x - other.X) + (y - other.Y) * (y - other.Y));
                    score = Math.Min(score, d - (radius + other.Radius) * 0.85);
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    bestX = x;
                    bestY = y;
                }
            }
            centres.Add((bestX, bestY, radius));

            // A continent is a cluster of overlapping lobes, which gives it an
            // irregular outline instead of a single oval.
            int lobes = rng.RangeInt(3, 6);
            double lx = bestX, ly = bestY;
            for (int l = 0; l < lobes; l++)
            {
                double lobeRadius = radius * (l == 0 ? rng.Range(0.6, 0.75) : rng.Range(0.35, 0.6));
                double stretch = rng.Range(1.0, 2.0);
                var (dx, dy) = RandomDirection(rng);
                list.Add(new Blob
                {
                    X = lx,
                    Y = ly,
                    DirX = dx,
                    DirY = dy,
                    MajorRadius = lobeRadius * Math.Sqrt(stretch),
                    MinorRadius = lobeRadius / Math.Sqrt(stretch),
                    Peak = 1.0,
                });

                // Next lobe branches off from the centre or the previous lobe.
                var (sx, sy) = RandomDirection(rng);
                double step = radius * rng.Range(0.45, 0.7);
                bool fromCentre = rng.NextDouble() < 0.5;
                double fromX = fromCentre ? bestX : lx;
                double fromY = fromCentre ? bestY : ly;
                lx = Math.Clamp(fromX + sx * step, margin * 0.7, aspect - margin * 0.7);
                ly = Math.Clamp(fromY + sy * step, margin * 0.7, 1 - margin * 0.7);
            }
        }
        return list;
    }

    private static List<Blob> PlaceIslands(Rng rng, MapGenSettings s, List<Blob> continents, double aspect)
    {
        var list = new List<Blob>();
        int clusters = rng.RangeInt(s.MinIslandClusters, s.MaxIslandClusters);
        for (int c = 0; c < clusters; c++)
        {
            // Find a spot in open ocean, away from the continents.
            double cx = 0, cy = 0;
            bool found = false;
            for (int tries = 0; tries < 60 && !found; tries++)
            {
                cx = rng.Range(0.08, aspect - 0.08);
                cy = rng.Range(0.08, 0.92);
                found = BlobField(continents, cx, cy) < -0.25;
            }
            if (!found)
                continue;

            int islandCount = rng.RangeInt(1, 6);
            for (int i = 0; i < islandCount; i++)
            {
                var (dx, dy) = RandomDirection(rng);
                double radius = rng.Range(0.012, 0.04);
                double stretch = rng.Range(1.0, 2.2);
                list.Add(new Blob
                {
                    X = cx + rng.Range(-0.09, 0.09),
                    Y = cy + rng.Range(-0.09, 0.09),
                    DirX = dx,
                    DirY = dy,
                    MajorRadius = radius * Math.Sqrt(stretch),
                    MinorRadius = radius / Math.Sqrt(stretch),
                    Peak = 0.55,
                });
            }
        }
        return list;
    }

    /// <summary>Random unit vector without trig functions (keeps results bit-identical everywhere).</summary>
    private static (double X, double Y) RandomDirection(Rng rng)
    {
        while (true)
        {
            double x = rng.Range(-1, 1);
            double y = rng.Range(-1, 1);
            double len2 = x * x + y * y;
            if (len2 > 0.01 && len2 <= 1)
            {
                double len = Math.Sqrt(len2);
                return (x / len, y / len);
            }
        }
    }

    /// <summary>Highest blob value at a point: Peak at a blob's centre, 0 at its edge, negative outside.</summary>
    private static double BlobField(List<Blob> blobs, double x, double y)
    {
        double best = -10;
        foreach (var b in blobs)
        {
            double lx = x - b.X;
            double ly = y - b.Y;
            double u = (lx * b.DirX + ly * b.DirY) / b.MajorRadius;
            double v = (lx * -b.DirY + ly * b.DirX) / b.MinorRadius;
            double d = Math.Sqrt(u * u + v * v);
            best = Math.Max(best, (1.0 - d) * b.Peak);
        }
        return best;
    }

    // ------------------------------------------------------------------
    //  Elevation
    // ------------------------------------------------------------------

    /// <summary>
    /// Remaps land elevation so terrain shares are the same on every map
    /// (e.g. always ~15% mountains). The remap is monotonic and continuous, so
    /// the smooth shading isn't broken.
    /// </summary>
    private static void NormaliseElevation(GameMap map)
    {
        var land = new List<float>(map.TileCount / 2);
        for (int i = 0; i < map.TileCount; i += 7)
            if (map.CoastField[i] > 0)
                land.Add(map.Elevation[i]);
        if (land.Count < 2)
            return;
        land.Sort();

        const int Steps = 256;
        var quantiles = new float[Steps + 1];
        for (int q = 0; q <= Steps; q++)
            quantiles[q] = land[(int)((long)q * (land.Count - 1) / Steps)];

        Parallel.For(0, map.Height, y =>
        {
            for (int x = 0; x < map.Width; x++)
            {
                int i = y * map.Width + x;
                map.Elevation[i] = Quantile(quantiles, map.Elevation[i]);
            }
        });
    }

    private static float Quantile(float[] q, float v)
    {
        if (v <= q[0])
            return 0;
        if (v >= q[^1])
            return 1;
        int lo = 0, hi = q.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (q[mid] <= v)
                lo = mid;
            else
                hi = mid;
        }
        float span = q[hi] - q[lo];
        float t = span > 0 ? (v - q[lo]) / span : 0;
        return (lo + t) / (q.Length - 1);
    }

    private static double SmoothStep(double edge0, double edge1, double x)
    {
        double t = Math.Clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
        return t * t * (3 - 2 * t);
    }
}
