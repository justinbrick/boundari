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
    int LandCoverScale,   // land-cover cells per tile side (0 = no land cover)
    int RiverMaxRank,     // include rivers up to this Natural Earth rank (0 = biggest; -1 = none)
    double RiverWidth,    // width in tiles of the biggest rivers; smaller ones are narrower
    bool CarveLakes = true,     // land-cover lakes become real (unclaimable) water
    string? OsmWater = null,    // OpenStreetMap rivers/lakes in tools/data, drawn as claimable water
    double RiverMetres = 0,     // drawn width of those rivers
    string? CoverFrom = null,   // take the land cover from this (bigger) map's cached grid instead of fetching it
    bool RiversAsCover = false); // Natural Earth rivers only drawn (land cover water): crossable and claimable, not cut into the map

public static class Program
{
    private static readonly MapSpec[] Specs =
    {
        // Plain longitude/latitude grid, like OpenFront's world map. Antarctica is cut off below 58°S.
        new("world", "World", -180, 180, -58, 84, 0, 2400, "50m", 3, MinIslandTiles: 12, MinLakeTiles: 20, LandCoverScale: 0,
            RiverMaxRank: 3, RiverWidth: 2.0),
        // Iceland to the Urals' foothills, Scandinavia's tip to North Africa. ~1.7 km per tile.
        // 4000 wide (tile scale 1.33 over the original 3000: finer territory; OpenFront's tile
        // numbers are scaled to match, see Sim.ScaledRules). Islands, lakes and rivers keep their
        // real-world sizes: 45 / 68 tiles and 3.9 wide at 3000.
        new("europe", "Europe", -25, 45, 33, 72, 50, 4000, "10m", 6, MinIslandTiles: 80, MinLakeTiles: 121, LandCoverScale: 2,
            RiverMaxRank: 7, RiverWidth: 5.2),
        // Western and central Europe, the dots game's main map: Ireland to the edge of Ukraine,
        // southern Scandinavia to the North African coast. Europe's detail (~1.7 km per tile,
        // same tile shape), its land cover cut out of Europe's cached grid (no download).
        // Rivers are only drawn, as on the Ardennes: they can be crossed and claimed (slowly).
        new("westeurope", "Western Europe", -11, 34, 35, 62, 50, 2571, "10m", 6, MinIslandTiles: 80, MinLakeTiles: 121, LandCoverScale: 2,
            RiverMaxRank: 7, RiverWidth: 3.0, CoverFrom: "europe", RiversAsCover: true),
        // The Ardennes around Bastogne and Houffalize, ~36 km square, 30 m per tile. The
        // Ourthe, Sure, Wiltz and Clerve come from OpenStreetMap and are only drawn: they can
        // be crossed and claimed like land. No sea or lakes. Land cover at WorldCover's full
        // 10 m detail (15 m cells), elevation from zoom-12 terrain tiles (~25 m).
        new("ardennes", "Ardennes", 5.50, 6.00, 49.93, 50.25, 50.09, 1200, "10m", 12, MinIslandTiles: 0, MinLakeTiles: 0, LandCoverScale: 2,
            RiverMaxRank: -1, RiverWidth: 0, CarveLakes: false, OsmWater: "osm/ardennes_water.json", RiverMetres: 60),
    };

    // Coast field units per tile of distance. Matches the scale of the random
    // generator's field, which the shader's water shading is tuned for.
    internal const float FieldPerTile = 0.004f;

    // Within this many tiles of a coastline, distances are exact (from the vector edges).
    internal const double ExactBand = 3.0;

    // Rivers narrower than this (in tiles) could break into separate puddles on the tile
    // grid, which boats couldn't use. 1.6 keeps even diagonal stretches connected.
    private const double MinRiverWidth = 1.6;

    internal static MapSpec Spec(string id) => Specs.First(s => s.Id == id);

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
            {
                // The land cover cuts its lakes into the map as real water (see
                // LandCoverBuilder.CarveLakes): classify again, keeping every lake however small.
                map.Cover = LandCoverBuilder.Build(map, spec, root);
                RefreshFarField(map);
                Classify(map, spec, minLakeTiles: 1);
            }
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

        // Rivers are real water: boats can use them and they split the land.
        var rivers = spec.RiverMaxRank < 0 || spec.RiversAsCover
            ? new List<(TileRing, double)>()
            : GeoJson.LoadRivers(Path.Combine(dataDir, "ne_10m_rivers_lake_centerlines.geojson"), spec.RiverMaxRank)
                .Select(r => (Project(new List<Ring> { r.Line })[0], RiverHalfWidth(spec, r.Rank)))
                .ToList();
        float[] river = Rasterizer.RiverDistance(rivers, width, height, ExactBand);

        // 1. Which tile centres are land.
        bool[] inLand = Rasterizer.Fill(land, width, height);
        bool[] inLake = Rasterizer.Fill(lakes, width, height);
        var isLand = new bool[map.TileCount];
        var isWater = new bool[map.TileCount];
        for (int i = 0; i < map.TileCount; i++)
        {
            isLand[i] = inLand[i] && !inLake[i] && river[i] >= 0;
            isWater[i] = !isLand[i];
        }

        // 2. Signed distance to the coast and river banks: exact near the shore,
        //    approximate further away.
        float[] edge = Rasterizer.EdgeDistance(land.Concat(lakes).ToList(), width, height, ExactBand);
        float[] toLand = Rasterizer.DistanceTransform(isLand, width, height);
        float[] toWater = Rasterizer.DistanceTransform(isWater, width, height);
        for (int i = 0; i < map.TileCount; i++)
        {
            bool inRiver = river[i] < 0;
            bool coastLand = inLand[i] && !inLake[i];
            float near = isLand[i] ? Math.Min(edge[i], river[i])
                : coastLand ? -river[i]                    // in a river: measure to its banks
                : inRiver ? Math.Max(edge[i], -river[i])   // river running out into the sea or a lake
                : edge[i];
            float far = Math.Max((isLand[i] ? toWater[i] : toLand[i]) - 0.5f, (float)ExactBand);
            float d = near < ExactBand ? near : far;
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
        Classify(map, spec, spec.MinLakeTiles);
        return map;
    }

    /// <summary>Drops tiny islands and lakes, then sorts tiles into sea, lake and land heights.</summary>
    private static void Classify(GameMap map, MapSpec spec, int minLakeTiles)
    {
        MapProcessing.RemoveSmallLandmasses(map, spec.MinIslandTiles);
        MapProcessing.ClassifyWater(map, minLakeTiles);
        DropRemovedFeatures(map);
        MapProcessing.AssignTerrain(map, ElevationScale.PlainsEnd, ElevationScale.HighlandsEnd);
        MapProcessing.LabelLandmasses(map);
    }

    /// <summary>Half a river's width in tiles: the most important rivers are widest.</summary>
    internal static double RiverHalfWidth(MapSpec spec, int rank)
    {
        double share = rank <= 3 ? 1.0 : rank <= 5 ? 0.85 : 0.7;
        return Math.Max(spec.RiverWidth * share, MinRiverWidth) / 2;
    }

    /// <summary>
    /// Dropped islands and filled lakes (and lake slivers too thin to make a water
    /// tile) still pull the coast field towards 0 (it was measured to their outlines),
    /// which would draw shallow-water blobs or phantom shores where they used to be. Push each tile's value out to at least its distance from
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

    /// <summary>
    /// Measures the coast field again beyond the exact band, on the tile grid as in Build,
    /// after lakes were cut in. Otherwise it would jump where a lake's measured distances
    /// stop, and the shader draws such a jump as a phantom coastline (a ring round the lake).
    /// </summary>
    private static void RefreshFarField(GameMap map)
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
        float band = (float)ExactBand * FieldPerTile;
        for (int i = 0; i < map.TileCount; i++)
        {
            if (Math.Abs(map.CoastField[i]) < band)
                continue; // near a shore: already exact
            float far = Math.Max((land[i] ? toWater[i] : toLand[i]) - 0.5f, (float)ExactBand) * FieldPerTile;
            map.CoastField[i] = land[i] ? far : -far;
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
