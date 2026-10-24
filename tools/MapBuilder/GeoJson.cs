using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;

namespace Boundari.MapBuilder;

/// <summary>A closed polygon ring (or a line) as (longitude, latitude) pairs, with its bounding box.</summary>
public sealed record Ring(double[] Lon, double[] Lat)
{
    public double MinLon { get; } = Lon.Length > 0 ? Lon.Min() : 0;
    public double MaxLon { get; } = Lon.Length > 0 ? Lon.Max() : 0;
    public double MinLat { get; } = Lat.Length > 0 ? Lat.Min() : 0;
    public double MaxLat { get; } = Lat.Length > 0 ? Lat.Max() : 0;

    /// <summary>True if any of it may lie in this box.</summary>
    public bool Touches(double lonMin, double lonMax, double latMin, double latMax) =>
        MaxLon >= lonMin && MinLon <= lonMax && MaxLat >= latMin && MinLat <= latMax;
}

public static class GeoJson
{
    // Files read once per run (the world backdrop builds hundreds of pieces from them).
    private static readonly ConcurrentDictionary<string, List<Ring>> RingCache = new();
    private static readonly ConcurrentDictionary<(string, int), List<(Ring, int)>> RiverCache = new();

    /// <summary>Every ring (outer boundaries and holes) of every Polygon/MultiPolygon in the file.</summary>
    public static List<Ring> LoadRings(string path) => RingCache.GetOrAdd(path, ReadRings);

    private static List<Ring> ReadRings(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var rings = new List<Ring>();
        foreach (var feature in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            var geometry = feature.GetProperty("geometry");
            if (geometry.ValueKind != JsonValueKind.Object)
                continue;
            string type = geometry.GetProperty("type").GetString()!;
            var coords = geometry.GetProperty("coordinates");
            if (type == "Polygon")
                AddPolygon(coords, rings);
            else if (type == "MultiPolygon")
                foreach (var polygon in coords.EnumerateArray())
                    AddPolygon(polygon, rings);
        }
        return rings;
    }

    /// <summary>
    /// River lines from Natural Earth's rivers file, with their importance rank
    /// (0 = most important). Intermittent rivers are skipped.
    /// </summary>
    public static List<(Ring Line, int Rank)> LoadRivers(string path, int maxRank) =>
        RiverCache.GetOrAdd((path, maxRank), key => ReadRivers(key.Item1, key.Item2));

    private static List<(Ring Line, int Rank)> ReadRivers(string path, int maxRank)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var rivers = new List<(Ring, int)>();
        foreach (var feature in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            var props = feature.GetProperty("properties");
            string kind = props.GetProperty("featurecla").GetString() ?? "";
            if (kind != "River" && kind != "Lake Centerline")
                continue;
            int rank = (int)props.GetProperty("scalerank").GetDouble();
            var geometry = feature.GetProperty("geometry");
            if (rank > maxRank || geometry.ValueKind != JsonValueKind.Object)
                continue;
            var coords = geometry.GetProperty("coordinates");
            if (geometry.GetProperty("type").GetString() == "LineString")
                rivers.Add((ReadLine(coords), rank));
            else
                foreach (var line in coords.EnumerateArray())
                    rivers.Add((ReadLine(line), rank));
        }
        return rivers;
    }

    private static Ring ReadLine(JsonElement points)
    {
        int n = points.GetArrayLength();
        var lon = new double[n];
        var lat = new double[n];
        int i = 0;
        foreach (var point in points.EnumerateArray())
        {
            lon[i] = point[0].GetDouble();
            lat[i] = point[1].GetDouble();
            i++;
        }
        return new Ring(lon, lat);
    }

    private static void AddPolygon(JsonElement polygon, List<Ring> rings)
    {
        foreach (var ring in polygon.EnumerateArray())
        {
            int n = ring.GetArrayLength();
            var lon = new double[n];
            var lat = new double[n];
            int i = 0;
            foreach (var point in ring.EnumerateArray())
            {
                lon[i] = point[0].GetDouble();
                lat[i] = point[1].GetDouble();
                i++;
            }
            rings.Add(new Ring(lon, lat));
        }
    }
}
