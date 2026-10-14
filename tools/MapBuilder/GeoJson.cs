using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Boundari.MapBuilder;

/// <summary>A closed polygon ring as (longitude, latitude) pairs.</summary>
public sealed record Ring(double[] Lon, double[] Lat);

public static class GeoJson
{
    /// <summary>Every ring (outer boundaries and holes) of every Polygon/MultiPolygon in the file.</summary>
    public static List<Ring> LoadRings(string path)
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
