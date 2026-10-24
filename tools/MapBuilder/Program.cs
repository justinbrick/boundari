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
    PlayArea? PlayArea = null); // where it can be played (null = all of it); the rest is greyed out

public static class Program
{
    private static readonly MapSpec[] Specs =
    {
        // Plain longitude/latitude grid, like OpenFront's world map. Antarctica is cut off below 58°S.
        new("world", "World", -180, 180, -58, 84, 0, 2400, "50m", 3, MinIslandTiles: 12, MinLakeTiles: 20, LandCoverScale: 0,
            RiverMaxRank: 3, RiverWidth: 2.0),
        // Iceland to the Urals, the Arctic to the Sahara. ~1.7 km per tile: 57 1/7 tiles per degree
        // of longitude, as when it was 4000 tiles over 70° (tile scale 1.33 over the original 3000:
        // finer territory; OpenFront's tile numbers are scaled to match, see Sim.ScaledRules).
        // Islands, lakes and rivers keep their real-world sizes: 45 / 68 tiles and 3.9 wide at 3000.
        // Only Europe's countries are played (EuropePlayArea); the rest is greyed out.
        new("europe", "Europe", -25, 66.875, 27.4, 72, 50, 5250, "10m", 6, MinIslandTiles: 80, MinLakeTiles: 121, LandCoverScale: 2,
            RiverMaxRank: 7, RiverWidth: 5.2, PlayArea: EuropePlayArea),
    };

    /// <summary>
    /// Europe's play area: the countries listed, their borders as the edge (water is all
    /// playable). The rough outline (clockwise from west of Iceland, as (latitude, longitude))
    /// splits Russia and Kazakhstan, down the Ural Mountains and the Ural River to the Caspian,
    /// and decides islands no region covers.
    /// </summary>
    private static PlayArea EuropePlayArea => new( // (a property: the Specs above are set up first)
        new EdgeLeg[]
        {
            new EdgePoints(
                // Arctic
                (66.6, -24.0), (68.2, -20.5), (70.0, -15.5), (71.2, -10.0), (71.35, -4.0), (71.2, 5.0), (71.35, 15.0),
                (71.35, 27.0), (71.3, 37.0), (71.25, 45.0), (70.6, 51.5), (70.0, 56.0), (69.75, 59.8), (69.3, 61.8), (68.8, 64.0),
                // Ural Mountains
                (68.4, 65.8), (67.6, 65.6), (66.5, 64.4), (65.9, 62.6), (65.0, 60.1), (63.9, 59.1), (62.5, 59.3), (61.0, 59.3),
                (59.6, 59.1), (58.2, 59.3), (56.8, 59.8), (55.4, 59.6)),
            new EdgeRiver("Ural", (54.7, 59.4), (46.9, 51.9)),
            new EdgePoints(
                // through the Caspian, keeping its western shore
                (46.4, 50.6), (45.3, 49.6), (44.2, 49.2), (43.0, 49.3), (41.9, 49.9), (40.9, 50.8), (40.0, 50.2), (39.1, 49.5),
                (38.6, 49.1),
                // Iran's border: the Aras, Ararat, the Zagros
                (38.42, 48.88), (38.9, 48.2), (39.5, 48.0), (39.35, 47.0), (38.9, 46.4), (39.2, 45.6), (39.6, 44.8), (39.0, 44.3),
                (38.2, 44.35), (37.15, 44.8), (36.6, 45.3), (35.9, 45.7), (35.1, 46.0), (34.4, 45.6), (33.6, 46.1), (32.9, 46.9),
                (32.1, 47.5), (31.3, 47.7), (30.5, 48.0), (29.95, 48.6),
                // the Syrian desert to the Gulf of Aqaba, across Sinai
                (30.0, 47.9), (30.15, 46.5), (30.4, 45.2), (31.1, 43.4), (31.8, 41.2), (32.2, 39.3), (31.2, 37.6), (30.2, 37.4),
                (29.4, 36.2), (29.53, 35.0), (29.7, 34.0), (29.95, 32.6),
                // the Sahara's edge, Egypt to Morocco
                (29.55, 31.4), (29.2, 30.9), (29.5, 30.0), (30.0, 28.6), (29.75, 27.0), (29.6, 25.4), (29.8, 23.7), (30.0, 22.0),
                (29.65, 20.5), (29.55, 19.0), (29.9, 17.4), (30.4, 15.6), (30.8, 13.6), (30.9, 11.4), (30.6, 9.5), (30.2, 7.8),
                (29.9, 6.0), (30.1, 4.1), (30.4, 2.2), (30.2, 0.2), (30.6, -1.9), (30.3, -3.9), (29.9, -5.9), (29.4, -7.6),
                (28.95, -9.4), (28.68, -11.1),
                // the Atlantic, round the Canaries
                (28.1, -12.7), (28.0, -14.3), (28.0, -16.6), (28.0, -18.6), (28.6, -19.3), (30.5, -21.3), (34.0, -23.0),
                (40.0, -23.5), (46.0, -23.6), (53.0, -23.6), (60.0, -23.8)),
        },
        // The countries that make up Europe (Council of Europe style: Turkey, the Caucasus and
        // Cyprus too); Russia and Kazakhstan only west of the Urals and the Ural River.
        Countries: new[]
        {
            "Akrotiri Sovereign Base Area", "Aland", "Albania", "Andorra", "Armenia", "Austria", "Azerbaijan", "Belarus",
            "Belgium", "Bosnia and Herzegovina", "Bulgaria", "Croatia", "Cyprus", "Czech Republic", "Denmark",
            "Dhekelia Sovereign Base Area", "Estonia", "Faroe Islands", "Finland", "France", "Georgia", "Germany",
            "Gibraltar", "Greece", "Guernsey", "Hungary", "Iceland", "Ireland", "Isle of Man", "Italy", "Jersey",
            "Kosovo", "Latvia", "Liechtenstein", "Lithuania", "Luxembourg", "Macedonia", "Malta", "Moldova", "Monaco",
            "Montenegro", "Netherlands", "Northern Cyprus", "Norway", "Poland", "Portugal", "Republic of Serbia",
            "Romania", "San Marino", "Slovakia", "Slovenia", "Spain", "Sweden", "Switzerland", "Turkey", "Ukraine",
            "United Kingdom", "Vatican",
        },
        SplitCountries: new[] { "Russia", "Kazakhstan" },
        CutByOutline: new[] { "Atyrau" }); // (its land west of the Ural River)

    // Coast field units per tile of distance. Matches the scale of the random
    // generator's field, which the shader's water shading is tuned for.
    internal const float FieldPerTile = 0.004f;

    // Within this many tiles of a coastline, distances are exact (from the vector edges).
    internal const double ExactBand = 3.0;

    // Rivers narrower than this (in tiles) could break into separate puddles on the tile
    // grid, which boats couldn't use. 1.6 keeps even diagonal stretches connected.
    private const double MinRiverWidth = 1.6;

    public static int Main(string[] args)
    {
        string root = FindRepoRoot();
        string dataDir = Path.Combine(root, "tools", "data");
        string outDir = Path.Combine(root, "src", "Boundari", "maps");

        // worldbackdrop: the full-detail greyed-out world around Europe, in pieces (see Backdrop).
        if (args.Contains("worldbackdrop"))
        {
            Backdrop.Build(Specs.First(s => s.Id == "europe"), dataDir, outDir);
            return 0;
        }

        var wanted = args.Length == 0 ? Specs : Specs.Where(s => args.Contains(s.Id)).ToArray();
        if (wanted.Length == 0)
        {
            Console.Error.WriteLine($"Unknown map. Available: {string.Join(", ", Specs.Select(s => s.Id))}");
            return 1;
        }

        // --preview: a quarter-size picture of each map's play area (tools/data/preview_<id>.ppm),
        // with no land cover (nothing downloaded) and no map file written.
        bool preview = args.Contains("--preview");
        foreach (var full in wanted)
        {
            var spec = preview ? full with { Width = full.Width / 4, LandCoverScale = 0, MinIslandTiles = full.MinIslandTiles / 16 } : full;
            var timer = Stopwatch.StartNew();
            var map = Build(spec, dataDir);
            if (preview)
            {
                spec.PlayArea?.Apply(map, spec, dataDir);
                WritePreview(map, Path.Combine(dataDir, $"preview_{spec.Id}.ppm"));
                continue;
            }
            if (spec.LandCoverScale > 0)
            {
                // The land cover cuts its lakes into the map as real water (see
                // LandCoverBuilder.CarveLakes): classify again, keeping every lake however small.
                map.Cover = LandCoverBuilder.Build(map, spec, root);
                RefreshFarField(map);
                Classify(map, spec, minLakeTiles: 1);
            }
            spec.PlayArea?.Apply(map, spec, dataDir);
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

    /// <summary>Builds the map (<paramref name="height"/>: its height in tiles, if not the one that makes tiles square).</summary>
    public static GameMap Build(MapSpec spec, string dataDir, int? height = null)
    {
        var geo = new MapGeo(spec.LonMin, spec.LonMax, spec.LatMin, spec.LatMax, spec.StandardParallel);
        int width = spec.Width;
        int tall = height ?? geo.HeightFor(width);
        var map = new GameMap(width, tall, seed: 0) { Name = spec.Name, Geo = geo };
        return BuildInto(map, spec, dataDir);
    }

    private static GameMap BuildInto(GameMap map, MapSpec spec, string dataDir)
    {
        var geo = map.Geo!;
        int width = map.Width, height = map.Height;

        // (only what reaches near the map: the world backdrop builds small pieces of the world)
        List<TileRing> Project(List<Ring> rings) => rings
            .Where(r => r.Touches(geo.LonMin - 1, geo.LonMax + 1, geo.LatMin - 1, geo.LatMax + 1))
            .Select(r => new TileRing(r.Lon.Select(lon => geo.TileX(lon, width)).ToArray(),
                                      r.Lat.Select(lat => geo.TileY(lat, height)).ToArray()))
            .ToList();

        var land = Project(GeoJson.LoadRings(Path.Combine(dataDir, $"ne_{spec.Detail}_land.geojson")));
        var lakes = Project(GeoJson.LoadRings(Path.Combine(dataDir, $"ne_{spec.Detail}_lakes.geojson")));

        // Rivers are real water: boats can use them and they split the land.
        var rivers = spec.RiverMaxRank < 0
            ? new List<(TileRing, double)>()
            : GeoJson.LoadRivers(Path.Combine(dataDir, "ne_10m_rivers_lake_centerlines.geojson"), spec.RiverMaxRank)
                .Where(r => r.Line.Touches(geo.LonMin - 1, geo.LonMax + 1, geo.LatMin - 1, geo.LatMax + 1))
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
    private static double RiverHalfWidth(MapSpec spec, int rank)
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

    /// <summary>A plain picture of the map: land green, water blue, outside the play area grey.</summary>
    private static void WritePreview(GameMap map, string path)
    {
        using var file = File.Create(path);
        file.Write(System.Text.Encoding.ASCII.GetBytes($"P6 {map.Width} {map.Height} 255\n"));
        var pixels = new byte[map.TileCount * 3];
        for (int i = 0; i < map.TileCount; i++)
        {
            bool land = map.CoastField[i] > 0;
            (byte r, byte g, byte b) = map.IsOutside(i) ? (land ? ((byte)150, (byte)150, (byte)140) : ((byte)95, (byte)100, (byte)110))
                : land ? ((byte)110, (byte)150, (byte)70) : ((byte)70, (byte)140, (byte)200);
            pixels[i * 3] = r;
            pixels[i * 3 + 1] = g;
            pixels[i * 3 + 2] = b;
        }
        file.Write(pixels);
        Console.WriteLine($"preview -> {path}");
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Boundari.sln")))
                return dir.FullName;
        throw new DirectoryNotFoundException("Run this from inside the Boundari repository.");
    }
}
