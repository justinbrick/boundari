using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Boundari.Map;

namespace Boundari.MapBuilder;

/// <summary>One real-world map: which part of the Earth, at what size and detail.</summary>
public sealed record MapSpec(
    string Id,
    string Name,
    double LonMin, double LonMax,
    double LatMin, double LatMax,
    int Width,
    string Detail,        // Natural Earth scale: "50m" (world) or "10m" (regional, more detailed)
    int ElevationZoom,    // terrain tile zoom level in tools/data/terrarium{zoom}
    int MinIslandTiles,   // smaller islands are dropped
    int MinLakeTiles);    // smaller lakes are filled in

public static class Program
{
    private static readonly MapSpec[] Specs =
    {
        // Equirectangular (plain longitude/latitude grid), like OpenFront's world map.
        // Antarctica is cut off below 58°S.
        new("world", "World", -180, 180, -58, 84, 2400, "50m", 3, MinIslandTiles: 12, MinLakeTiles: 20),
    };

    // Coast field units per tile of distance. Matches the scale of the random
    // generator's field, which the shader's water shading is tuned for.
    private const float FieldPerTile = 0.004f;

    // Within this many tiles of a coastline, distances are exact (from the vector edges).
    private const double ExactBand = 3.0;

    // Elevation (metres) to the 0..1 scale the game uses. Terrain types split at
    // 0.55 (plains/highlands) and 0.85 (highlands/mountains).
    private static readonly (float Metres, float Value)[] ElevationCurve =
    {
        (0, 0f), (350, 0.55f), (2000, 0.85f), (5500, 1f),
    };

    public static int Main(string[] args)
    {
        string root = FindRepoRoot();
        string dataDir = Path.Combine(root, "tools", "data");
        string outDir = Path.Combine(root, "src", "Boundari", "maps");

        var wanted = args.Length == 0 ? Specs : Specs.Where(s => args.Contains(s.Id)).ToArray();
        if (wanted.Length == 0)
        {
            Console.Error.WriteLine($"Unknown map. Available: {string.Join(", ", Specs.Select(s => s.Id))}");
            return 1;
        }

        foreach (var spec in wanted)
        {
            var timer = Stopwatch.StartNew();
            var map = Build(spec, dataDir);
            string path = Path.Combine(outDir, spec.Id + ".bmap");
            using (var file = File.Create(path))
                MapFile.Write(map, file);

            int continents = map.CountLandmasses((int)(map.LandTileCount * 0.01));
            Console.WriteLine($"{spec.Name}: {map.Width}x{map.Height}, {map.LandmassSizes.Length} landmasses " +
                              $"({continents} large), {100.0 * map.LandTileCount / map.TileCount:F1}% land, " +
                              $"{new FileInfo(path).Length / 1024} KB, {timer.ElapsedMilliseconds} ms -> {path}");
        }
        return 0;
    }

    public static GameMap Build(MapSpec spec, string dataDir)
    {
        int width = spec.Width;
        int height = (int)Math.Round(width * (spec.LatMax - spec.LatMin) / (spec.LonMax - spec.LonMin));
        var map = new GameMap(width, height, seed: 0) { Name = spec.Name };

        double TileX(double lon) => (lon - spec.LonMin) / (spec.LonMax - spec.LonMin) * width;
        double TileY(double lat) => (spec.LatMax - lat) / (spec.LatMax - spec.LatMin) * height;
        List<TileRing> Project(List<Ring> rings) =>
            rings.Select(r => new TileRing(r.Lon.Select(TileX).ToArray(), r.Lat.Select(TileY).ToArray())).ToList();

        var land = Project(GeoJson.LoadRings(Path.Combine(dataDir, $"ne_{spec.Detail}_land.geojson")));
        var lakes = Project(GeoJson.LoadRings(Path.Combine(dataDir, $"ne_{spec.Detail}_lakes.geojson")));

        // 1. Which tile centres are land.
        bool[] inLand = Rasterizer.Fill(land, width, height);
        bool[] inLake = Rasterizer.Fill(lakes, width, height);
        var isLand = new bool[map.TileCount];
        var isWater = new bool[map.TileCount];
        for (int i = 0; i < map.TileCount; i++)
        {
            isLand[i] = inLand[i] && !inLake[i];
            isWater[i] = !isLand[i];
        }

        // 2. Signed distance to the coast: exact near the shore, approximate further away.
        float[] edge = Rasterizer.EdgeDistance(land.Concat(lakes).ToList(), width, height, ExactBand);
        float[] toLand = Rasterizer.DistanceTransform(isLand, width, height);
        float[] toWater = Rasterizer.DistanceTransform(isWater, width, height);
        for (int i = 0; i < map.TileCount; i++)
        {
            float far = Math.Max((isLand[i] ? toWater[i] : toLand[i]) - 0.5f, (float)ExactBand);
            float d = edge[i] < ExactBand ? edge[i] : far;
            d = Math.Max(d, 0.05f); // a tile centre exactly on the line still needs a side
            map.CoastField[i] = (isLand[i] ? d : -d) * FieldPerTile;
        }

        // 3. Real elevation.
        var elevation = new ElevationSource(Path.Combine(dataDir, $"terrarium{spec.ElevationZoom}"), spec.ElevationZoom);
        for (int y = 0; y < height; y++)
        {
            double lat = spec.LatMax - (y + 0.5) / height * (spec.LatMax - spec.LatMin);
            for (int x = 0; x < width; x++)
            {
                double lon = spec.LonMin + (x + 0.5) / width * (spec.LonMax - spec.LonMin);
                map.Elevation[map.Index(x, y)] = MetresToElevation(elevation.Sample(lon, lat));
            }
        }

        // 4. Same clean-up and classification as random maps.
        MapProcessing.RemoveSmallLandmasses(map, spec.MinIslandTiles);
        MapProcessing.ClassifyWater(map, spec.MinLakeTiles);
        DropRemovedFeatures(map);
        MapProcessing.AssignTerrain(map, 0.55f, 0.85f);
        MapProcessing.LabelLandmasses(map);
        return map;
    }

    /// <summary>
    /// Dropped islands and filled lakes still pull the coast field towards 0 (it
    /// was measured to their outlines), which would draw shallow-water blobs where
    /// they used to be. Push each tile's value out to at least its distance from
    /// the coastline that actually remains.
    /// </summary>
    private static void DropRemovedFeatures(GameMap map)
    {
        var land = new bool[map.TileCount];
        var water = new bool[map.TileCount];
        for (int i = 0; i < map.TileCount; i++)
        {
            land[i] = map.CoastField[i] > 0;
            water[i] = !land[i];
        }
        float[] toLand = Rasterizer.DistanceTransform(land, map.Width, map.Height);
        float[] toWater = Rasterizer.DistanceTransform(water, map.Width, map.Height);
        for (int i = 0; i < map.TileCount; i++)
        {
            float remaining = ((land[i] ? toWater[i] : toLand[i]) - 0.5f) * FieldPerTile;
            float v = map.CoastField[i];
            map.CoastField[i] = land[i] ? Math.Max(v, remaining) : Math.Min(v, -remaining);
        }
    }

    private static float MetresToElevation(float metres)
    {
        if (metres <= ElevationCurve[0].Metres)
            return 0;
        for (int i = 1; i < ElevationCurve.Length; i++)
        {
            var (m0, v0) = ElevationCurve[i - 1];
            var (m1, v1) = ElevationCurve[i];
            if (metres <= m1)
                return v0 + (metres - m0) / (m1 - m0) * (v1 - v0);
        }
        return 1;
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Boundari.sln")))
                return dir.FullName;
        throw new DirectoryNotFoundException("Run this from inside the Boundari repository.");
    }
}
