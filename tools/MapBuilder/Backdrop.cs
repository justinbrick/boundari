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
/// The world drawn around a regional map (its land dark grey, outside the play area), at the
/// map's own detail: the whole world on the map's tile grid (its projection carried round the
/// globe), cut into square pieces the game loads only where the camera is zoomed in
/// (src/Boundari/maps/world/{x}_{y}.wchunk). Each piece is a greyscale PNG of the coast field
/// (128 + 16 per tile from the coast, land above 128), with a one-tile border from its
/// neighbours so they join seamlessly. Pieces of open ocean, and pieces the map itself covers,
/// aren't written. Zoomed out, the game draws a world overview instead
/// (src/Boundari/maps/world_overview.bmap: the same, at a sixth of the detail, without rivers).
/// </summary>
public static class Backdrop
{
    public const int Chunk = 1024;        // tiles per piece side (without its border)
    private const int Margin = 64;        // built this far past each side, so islands and lakes at the seams come out whole
    private const double WorldLatMax = 84, WorldLatMin = -58; // as the world map
    private const int OverviewDivisor = 6;  // the overview's tiles are this many of the map's across

    public static void Build(MapSpec region, string dataDir, string outDir)
    {
        var timer = Stopwatch.StartNew();
        var geo = new MapGeo(region.LonMin, region.LonMax, region.LatMin, region.LatMax, region.StandardParallel);
        int width = region.Width, height = geo.HeightFor(width);
        double kx = width / (region.LonMax - region.LonMin), ky = height / (region.LatMax - region.LatMin);

        int cx0 = (int)Math.Floor((-180 - region.LonMin) * kx / Chunk), cx1 = (int)Math.Floor((180 - region.LonMin) * kx / Chunk);
        int cy0 = (int)Math.Floor((region.LatMax - WorldLatMax) * ky / Chunk), cy1 = (int)Math.Floor((region.LatMax - WorldLatMin) * ky / Chunk);
        string folder = Path.Combine(outDir, "world");
        Directory.CreateDirectory(folder);
        foreach (string old in Directory.GetFiles(folder, "*.wchunk"))
            File.Delete(old);

        var land = GeoJson.LoadRings(Path.Combine(dataDir, $"ne_{region.Detail}_land.geojson"));
        GeoJson.LoadRings(Path.Combine(dataDir, $"ne_{region.Detail}_lakes.geojson"));
        GeoJson.LoadRivers(Path.Combine(dataDir, "ne_10m_rivers_lake_centerlines.geojson"), region.RiverMaxRank);

        BuildOverview(region, kx, dataDir, outDir);

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
            byte[] png = Encode(map);
            File.WriteAllBytes(Path.Combine(folder, $"{job.X}_{job.Y}.wchunk"), png);
            Interlocked.Add(ref bytes, png.Length);
            int n = Interlocked.Increment(ref done);
            if (n % 10 == 0 || n == jobs.Count)
                Console.WriteLine($"  {n}/{jobs.Count} pieces, {Interlocked.Read(ref bytes) / 1024 / 1024} MB, {timer.Elapsed.TotalMinutes:F1} min");
        });
        Console.WriteLine($"World backdrop: {jobs.Count} pieces, {bytes / 1024 / 1024} MB, {timer.Elapsed.TotalMinutes:F1} min -> {folder}");
    }

    /// <summary>The whole world at a sixth of the map's detail, for when the camera is zoomed out.</summary>
    private static void BuildOverview(MapSpec region, double kx, string dataDir, string outDir)
    {
        var timer = Stopwatch.StartNew();
        var spec = region with
        {
            Id = "world_overview",
            Name = "World overview",
            LonMin = -180,
            LonMax = 180,
            LatMin = WorldLatMin,
            LatMax = WorldLatMax,
            Width = (int)Math.Round(360 * kx / OverviewDivisor),
            MinIslandTiles = Math.Max(2, region.MinIslandTiles / (OverviewDivisor * OverviewDivisor)),
            MinLakeTiles = Math.Max(2, region.MinLakeTiles / (OverviewDivisor * OverviewDivisor)),
            LandCoverScale = 0,
            RiverMaxRank = -1, // (rivers would come out far too wide at this size)
            PlayArea = null,
        };
        var map = Program.Build(spec, dataDir);
        string path = Path.Combine(outDir, "world_overview.bmap");
        using (var file = File.Create(path))
            MapFile.Write(map, file);
        Console.WriteLine($"World overview: {map.Width}x{map.Height}, {new FileInfo(path).Length / 1024} KB, {timer.ElapsedMilliseconds} ms -> {path}");
    }

    /// <summary>The piece's own tiles plus a one-tile border, as a greyscale PNG of the coast field.</summary>
    private static byte[] Encode(GameMap map)
    {
        int side = Chunk + 2, w = map.Width;
        var pixels = new byte[side * side];
        for (int j = 0; j < side; j++)
            for (int i = 0; i < side; i++)
            {
                int t = (Margin - 1 + j) * w + Margin - 1 + i;
                double tiles = map.CoastField[t] / Program.FieldPerTile;
                pixels[j * side + i] = (byte)Math.Clamp((int)Math.Round(128 + tiles * 16), 0, 255);
            }
        return Png.EncodeGrey(side, side, pixels);
    }
}
