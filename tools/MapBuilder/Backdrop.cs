using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Boundari.Map;

namespace Boundari.MapBuilder;

/// <summary>
/// The greyed-out world drawn around a regional map, at the map's own detail: the whole world
/// on the map's tile grid (its projection carried round the globe), cut into square pieces the
/// game loads only where the camera is zoomed in (src/Boundari/maps/world/{x}_{y}.wchunk).
/// Each piece is a PNG, grey + alpha: per tile the coast field (128 + 16 per tile from the
/// coast, land above 128) and the hill shading (0.45..1.4 as 0..255), with a one-tile border
/// from its neighbours so they join seamlessly. Pieces of open ocean, and pieces the map itself
/// covers, aren't written.
/// </summary>
public static class Backdrop
{
    public const int Chunk = 1024;        // tiles per piece side (without its border)
    private const int Margin = 64;        // built this far past each side, so islands and lakes at the seams come out whole
    private const double WorldLatMax = 84, WorldLatMin = -58; // as the world map

    public static void Build(MapSpec region, string dataDir, string outDir)
    {
        var timer = Stopwatch.StartNew();
        var geo = new MapGeo(region.LonMin, region.LonMax, region.LatMin, region.LatMax, region.StandardParallel);
        int width = region.Width, height = geo.HeightFor(width);
        double kx = width / (region.LonMax - region.LonMin), ky = height / (region.LatMax - region.LatMin);
        double metresPerTile = geo.KmPerTile(height) * 1000;

        int cx0 = (int)Math.Floor((-180 - region.LonMin) * kx / Chunk), cx1 = (int)Math.Floor((180 - region.LonMin) * kx / Chunk);
        int cy0 = (int)Math.Floor((region.LatMax - WorldLatMax) * ky / Chunk), cy1 = (int)Math.Floor((region.LatMax - WorldLatMin) * ky / Chunk);
        string folder = Path.Combine(outDir, "world");
        Directory.CreateDirectory(folder);
        foreach (string old in Directory.GetFiles(folder, "*.wchunk"))
            File.Delete(old);

        var land = GeoJson.LoadRings(Path.Combine(dataDir, $"ne_{region.Detail}_land.geojson"));
        GeoJson.LoadRings(Path.Combine(dataDir, $"ne_{region.Detail}_lakes.geojson"));
        GeoJson.LoadRivers(Path.Combine(dataDir, "ne_10m_rivers_lake_centerlines.geojson"), region.RiverMaxRank);

        var jobs = new List<(int X, int Y)>();
        for (int cy = cy0; cy <= cy1; cy++)
            for (int cx = cx0; cx <= cx1; cx++)
            {
                int x0 = cx * Chunk, y0 = cy * Chunk;
                if (x0 >= 0 && y0 >= 0 && x0 + Chunk <= width && y0 + Chunk <= height)
                    continue; // all under the map itself
                double lonMin = region.LonMin + (x0 - Margin) / kx, lonMax = region.LonMin + (x0 + Chunk + Margin) / kx;
                double latMax = region.LatMax - (y0 - Margin) / ky, latMin = region.LatMax - (y0 + Chunk + Margin) / ky;
                if (land.Any(r => r.Touches(lonMin, lonMax, latMin, latMax)))
                    jobs.Add((cx, cy));
            }
        Console.WriteLine($"World backdrop: {jobs.Count} pieces of {Chunk}x{Chunk} tiles to build");

        int done = 0;
        long bytes = 0;
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = 4 }, job =>
        {
            int x0 = job.X * Chunk - Margin, y0 = job.Y * Chunk - Margin, size = Chunk + 2 * Margin;
            var spec = region with
            {
                Id = $"world_{job.X}_{job.Y}",
                LonMin = region.LonMin + x0 / kx,
                LonMax = region.LonMin + (x0 + size) / kx,
                LatMax = region.LatMax - y0 / ky,
                LatMin = region.LatMax - (y0 + size) / ky,
                Width = size,
                LandCoverScale = 0,
                PlayArea = null,
            };
            var map = Program.Build(spec, dataDir, height: size);
            byte[] png = Encode(map, metresPerTile);
            File.WriteAllBytes(Path.Combine(folder, $"{job.X}_{job.Y}.wchunk"), png);
            Interlocked.Add(ref bytes, png.Length);
            int n = Interlocked.Increment(ref done);
            if (n % 10 == 0 || n == jobs.Count)
                Console.WriteLine($"  {n}/{jobs.Count} pieces, {Interlocked.Read(ref bytes) / 1024 / 1024} MB, {timer.Elapsed.TotalMinutes:F1} min");
        });
        Console.WriteLine($"World backdrop: {jobs.Count} pieces, {bytes / 1024 / 1024} MB, {timer.Elapsed.TotalMinutes:F1} min -> {folder}");
    }

    /// <summary>The piece's own tiles plus a one-tile border, as a grey + alpha PNG (see the class notes).</summary>
    private static byte[] Encode(GameMap map, double metresPerTile)
    {
        int side = Chunk + 2, w = map.Width, h = map.Height;
        var pixels = new byte[side * side * 2];
        for (int j = 0; j < side; j++)
            for (int i = 0; i < side; i++)
            {
                int x = Margin - 1 + i, y = Margin - 1 + j, t = y * w + x;
                double tiles = map.CoastField[t] / Program.FieldPerTile;
                int l = y * w + Math.Max(x - 1, 0), r = y * w + Math.Min(x + 1, w - 1);
                int u = Math.Max(y - 1, 0) * w + x, d = Math.Min(y + 1, h - 1) * w + x;
                double shade = HillShade(map, l, r, u, d, metresPerTile);
                int p = (j * side + i) * 2;
                pixels[p] = (byte)Math.Clamp((int)Math.Round(128 + tiles * 16), 0, 255);
                pixels[p + 1] = (byte)Math.Clamp((int)Math.Round((shade - 0.45) / (1.4 - 0.45) * 255), 0, 255);
            }
        return Png.EncodeGreyAlpha(side, side, pixels);
    }

    // As MapView's hill shading (the light from the north-west, relief exaggerated 5 times).
    private static double HillShade(GameMap map, int left, int right, int up, int down, double metresPerTile)
    {
        const double Exaggeration = 5;
        double gx = (ElevationScale.ToMetres(map.Elevation[right]) - ElevationScale.ToMetres(map.Elevation[left])) * 0.5 / metresPerTile * Exaggeration;
        double gy = (ElevationScale.ToMetres(map.Elevation[down]) - ElevationScale.ToMetres(map.Elevation[up])) * 0.5 / metresPerTile * Exaggeration;
        double lx = -1, ly = -1, lz = 1.5, ll = Math.Sqrt(lx * lx + ly * ly + lz * lz);
        double nx = -gx, ny = -gy, nz = 1, nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        double dot = (nx * lx + ny * ly + nz * lz) / (nl * ll);
        return Math.Clamp(dot / (lz / ll), 0.45, 1.4);
    }
}
