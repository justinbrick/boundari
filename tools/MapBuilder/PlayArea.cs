using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Boundari.Map;

namespace Boundari.MapBuilder;

/// <summary>A stretch of a play area's edge, as (latitude, longitude) pairs.</summary>
public abstract record EdgeLeg;

/// <summary>
/// Hand-placed points: the edge runs through them on a smooth curve (so it looks natural,
/// not like straight cuts between them).
/// </summary>
public sealed record EdgePoints(params (double Lat, double Lon)[] Points) : EdgeLeg;

/// <summary>The edge follows a real river (Natural Earth's line by that name) between the points nearest these two.</summary>
public sealed record EdgeRiver(string Name, (double Lat, double Lon) From, (double Lat, double Lon) To) : EdgeLeg;

/// <summary>
/// Where a regional map can be played: everything outside the outline is turned into
/// <see cref="TerrainType.Outside"/> (greyed out in the game, like the world around the map),
/// so the playable edge follows coasts, rivers and mountains instead of the map's straight
/// sides. <paramref name="WaterAreas"/>: boxes whose open water is playable even outside the
/// outline (a sea such as the Caspian, whose far shore is outside), as (lat, lon) corners.
/// </summary>
public sealed record PlayArea(EdgeLeg[] Edge, ((double Lat, double Lon) A, (double Lat, double Lon) B)[] WaterAreas)
{
    // A water area's water counts only this far (in coast-field units) from any shore: its far
    // shores and the rivers running into it stay outside, greyed like the land there.
    private const float OpenWater = 2.5f * Program.FieldPerTile;

    /// <summary>Marks every tile outside the play area as Outside, then counts the landmasses again.</summary>
    public void Apply(GameMap map, MapSpec spec, string dataDir)
    {
        var geo = map.Geo!;
        int w = map.Width, h = map.Height;
        var outline = Outline(Path.Combine(dataDir, "ne_10m_rivers_lake_centerlines.geojson"));
        var ring = new TileRing(outline.Select(p => geo.TileX(p.Lon, w)).ToArray(), outline.Select(p => geo.TileY(p.Lat, h)).ToArray());
        bool[] inside = Rasterizer.Fill(new[] { ring }, w, h);

        int outsideTiles = 0;
        for (int i = 0; i < map.TileCount; i++)
        {
            if (inside[i] || (map.IsWater(i) && map.CoastField[i] < -OpenWater && InWaterArea(geo.LonAt(i % w + 0.5, w), geo.LatAt(i / w + 0.5, h))))
                continue;
            map.Terrain[i] = TerrainType.Outside;
            outsideTiles++;
        }

        // Slivers of land the edge cut off from the rest (smaller than the smallest island kept) go too.
        MapProcessing.LabelLandmasses(map);
        int slivers = 0;
        for (int i = 0; i < map.TileCount; i++)
        {
            int id = map.LandmassId[i];
            if (id != GameMap.NoLandmass && map.LandmassSizes[id] < spec.MinIslandTiles)
            {
                map.Terrain[i] = TerrainType.Outside;
                slivers++;
            }
        }
        MapProcessing.LabelLandmasses(map);
        Console.WriteLine($"  play area: {100.0 * outsideTiles / map.TileCount:F1}% of the map outside, {slivers} sliver tiles dropped");
    }

    private bool InWaterArea(double lon, double lat)
    {
        foreach (var (a, b) in WaterAreas)
            if (lat >= Math.Min(a.Lat, b.Lat) && lat <= Math.Max(a.Lat, b.Lat) && lon >= Math.Min(a.Lon, b.Lon) && lon <= Math.Max(a.Lon, b.Lon))
                return true;
        return false;
    }

    /// <summary>The whole closed outline: point legs as smooth curves, river legs as the river runs.</summary>
    private List<(double Lat, double Lon)> Outline(string riversPath)
    {
        // Each leg's points, rivers resolved to their real course.
        var legs = Edge.Select(leg => leg switch
        {
            EdgePoints p => (Points: p.Points.ToList(), Smooth: true),
            EdgeRiver r => (Points: RiverCourse(riversPath, r), Smooth: false),
            _ => throw new InvalidOperationException(),
        }).ToList();

        var outline = new List<(double Lat, double Lon)>();
        for (int l = 0; l < legs.Count; l++)
        {
            var (points, smooth) = legs[l];
            if (!smooth)
            {
                outline.AddRange(points);
                continue;
            }
            // A Catmull-Rom curve through the points, steered at each end by the neighbouring legs.
            // It runs on to the next leg's first point; the stretch from the previous leg's last
            // point is drawn here only when that leg is a river (a curve leg drew it already).
            var (prev, prevSmooth) = legs[(l - 1 + legs.Count) % legs.Count];
            var next = legs[(l + 1) % legs.Count].Points;
            var all = new List<(double Lat, double Lon)> { prev[^1] };
            all.AddRange(points);
            all.Add(next[0]);
            var before = prev.Count > 1 ? prev[^2] : prev[^1];
            var after = next.Count > 1 ? next[1] : next[0];
            for (int i = prevSmooth ? 1 : 0; i < all.Count - 1; i++)
            {
                var p0 = i == 0 ? before : all[i - 1];
                var p1 = all[i];
                var p2 = all[i + 1];
                var p3 = i + 2 < all.Count ? all[i + 2] : after;
                double length = Math.Sqrt((p2.Lat - p1.Lat) * (p2.Lat - p1.Lat) + (p2.Lon - p1.Lon) * (p2.Lon - p1.Lon));
                int steps = Math.Max(2, (int)(length / 0.05)); // a point every ~5 km
                for (int s = i == 0 ? 1 : 0; s < steps; s++)
                {
                    double t = (double)s / steps;
                    outline.Add((CatmullRom(p0.Lat, p1.Lat, p2.Lat, p3.Lat, t), CatmullRom(p0.Lon, p1.Lon, p2.Lon, p3.Lon, t)));
                }
            }
        }
        return outline;
    }

    private static double CatmullRom(double p0, double p1, double p2, double p3, double t) =>
        0.5 * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t + (-p0 + 3 * p1 - 3 * p2 + p3) * t * t * t);

    /// <summary>The named river's course between the points nearest <see cref="EdgeRiver.From"/> and <see cref="EdgeRiver.To"/>.</summary>
    private static List<(double Lat, double Lon)> RiverCourse(string path, EdgeRiver river)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var lines = new List<List<(double Lat, double Lon)>>();
        foreach (var feature in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            var props = feature.GetProperty("properties");
            if (!props.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != river.Name)
                continue;
            var geometry = feature.GetProperty("geometry");
            if (geometry.ValueKind != JsonValueKind.Object)
                continue;
            var coords = geometry.GetProperty("coordinates");
            if (geometry.GetProperty("type").GetString() == "LineString")
                lines.Add(ReadLine(coords));
            else
                foreach (var line in coords.EnumerateArray())
                    lines.Add(ReadLine(line));
        }
        if (lines.Count == 0)
            throw new InvalidDataException($"No river called {river.Name} in {path}.");

        // The longest line is the main course; Natural Earth may split it, so join pieces end to end first.
        var course = Join(lines).OrderByDescending(l => l.Count).First();
        int a = Nearest(course, river.From), b = Nearest(course, river.To);
        var part = course.GetRange(Math.Min(a, b), Math.Abs(b - a) + 1);
        if (a > b)
            part.Reverse();
        Console.WriteLine($"  edge follows the {river.Name}: {part.Count} points, from ({part[0].Lat:F2}, {part[0].Lon:F2}) to ({part[^1].Lat:F2}, {part[^1].Lon:F2})");
        return part;
    }

    /// <summary>Joins lines that end where another starts (within ~1 km) into longer ones.</summary>
    private static List<List<(double Lat, double Lon)>> Join(List<List<(double Lat, double Lon)>> lines)
    {
        static bool Same((double Lat, double Lon) p, (double Lat, double Lon) q) => Math.Abs(p.Lat - q.Lat) < 0.01 && Math.Abs(p.Lon - q.Lon) < 0.01;
        var pool = lines.Select(l => l.ToList()).ToList();
        bool joined = true;
        while (joined)
        {
            joined = false;
            for (int i = 0; i < pool.Count && !joined; i++)
                for (int j = 0; j < pool.Count && !joined; j++)
                {
                    if (i == j)
                        continue;
                    if (Same(pool[i][^1], pool[j][0]))
                    {
                        pool[i].AddRange(pool[j].Skip(1));
                        pool.RemoveAt(j);
                        joined = true;
                    }
                }
        }
        return pool;
    }

    private static int Nearest(List<(double Lat, double Lon)> line, (double Lat, double Lon) p)
    {
        int best = 0;
        double bestD = double.MaxValue;
        for (int i = 0; i < line.Count; i++)
        {
            double d = (line[i].Lat - p.Lat) * (line[i].Lat - p.Lat) + (line[i].Lon - p.Lon) * (line[i].Lon - p.Lon);
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best;
    }

    private static List<(double Lat, double Lon)> ReadLine(JsonElement points)
    {
        var line = new List<(double Lat, double Lon)>();
        foreach (var point in points.EnumerateArray())
            line.Add((point[1].GetDouble(), point[0].GetDouble()));
        return line;
    }
}
