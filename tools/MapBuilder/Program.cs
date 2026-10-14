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
    double StandardParallel, // latitude where tiles are exactly square (0 = plain lon/lat grid)
    int Width,
    string Detail,        // Natural Earth scale: "50m" (world) or "10m" (regional, more detailed)
    int ElevationZoom,    // terrain tile zoom level in tools/data/terrarium{zoom}
    int MinIslandTiles,   // smaller islands are dropped
    int MinLakeTiles,     // smaller lakes are filled in
    int LandCoverScale);  // land-cover cells per tile side (0 = no land cover)

public static class Program
{
    private static readonly MapSpec[] Specs =
    {
        // Plain longitude/latitude grid, like OpenFront's world map. Antarctica is cut off below 58°S.
        new("world", "World", -180, 180, -58, 84, 0, 2400, "50m", 3, MinIslandTiles: 12, MinLakeTiles: 20, LandCoverScale: 0),
        // Iceland to the Urals' foothills, Scandinavia's tip to North Africa. ~2.5 km per tile.
        new("europe", "Europe", -25, 45, 33, 72, 50, 2000, "10m", 6, MinIslandTiles: 20, MinLakeTiles: 30, LandCoverScale: 4),
    };

    // Coast field units per tile of distance. Matches the scale of the random
    // generator's field, which the shader's water shading is tuned for.
    private const float FieldPerTile = 0.004f;

    // Within this many tiles of a coastline, distances are exact (from the vector edges).
    private const double ExactBand = 3.0;

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
            if (spec.LandCoverScale > 0)
                map.Cover = LandCoverBuilder.Build(map, spec, root);
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
        var geo = new MapGeo(spec.LonMin, spec.LonMax, spec.LatMin, spec.LatMax, spec.StandardParallel);
        int width = spec.Width;
        int height = geo.HeightFor(width);
        var map = new GameMap(width, height, seed: 0) { Name = spec.Name, Geo = geo };

        List<TileRing> Project(List<Ring> rings) => rings
            .Select(r => new TileRing(r.Lon.Select(lon => geo.TileX(lon, width)).ToArray(),
                                      r.Lat.Select(lat => geo.TileY(lat, height)).ToArray()))
            .ToList();

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
        // Average several samples across each tile rather than reading one point: one
        // point per tile picks up random noise, which shows as blotchy patches.
        const int Sub = 3;
        System.Threading.Tasks.Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                float sum = 0;
                for (int sy = 0; sy < Sub; sy++)
                    for (int sx = 0; sx < Sub; sx++)
                        sum += elevation.Sample(geo.LonAt(x + (sx + 0.5) / Sub, width), geo.LatAt(y + (sy + 0.5) / Sub, height));
                map.Elevation[map.Index(x, y)] = ElevationScale.ToElevation(sum / (Sub * Sub));
            }
        });
        if (elevation.MissingTiles > 0)
            Console.WriteLine($"  warning: {elevation.MissingTiles} elevation tiles missing (treated as sea level)");

        // 4. Same clean-up and classification as random maps.
        MapProcessing.RemoveSmallLandmasses(map, spec.MinIslandTiles);
        MapProcessing.ClassifyWater(map, spec.MinLakeTiles);
        DropRemovedFeatures(map);
        MapProcessing.AssignTerrain(map, ElevationScale.PlainsEnd, ElevationScale.HighlandsEnd);
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

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Boundari.sln")))
                return dir.FullName;
        throw new DirectoryNotFoundException("Run this from inside the Boundari repository.");
    }
}
