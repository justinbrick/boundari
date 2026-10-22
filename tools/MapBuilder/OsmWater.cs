using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Boundari.MapBuilder;

/// <summary>
/// Rivers and lakes from an OpenStreetMap Overpass export ("out geom" JSON, see
/// tools/download-data.ps1). Natural Earth only has big rivers; small maps use these.
/// </summary>
public static class OsmWater
{
    // Ponds smaller than this (m²) are left out: at map scale they'd be specks.
    private const double MinAreaSquareMetres = 10_000;

    /// <summary>
    /// River centre lines (waterway=river, not streams) and lake/pond outlines
    /// (natural=water, not riverbanks or water treatment basins).
    /// </summary>
    public static (List<Ring> Rivers, List<Ring> Lakes) Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var rivers = new List<Ring>();
        var lakes = new List<Ring>();
        foreach (var element in doc.RootElement.GetProperty("elements").EnumerateArray())
        {
            if (element.GetProperty("type").GetString() != "way"
                || !element.TryGetProperty("tags", out var tags)
                || !element.TryGetProperty("geometry", out var geometry))
                continue;
            var line = ReadLine(geometry);
            if (Tag(tags, "waterway") == "river")
                rivers.Add(line);
            else if (Tag(tags, "natural") == "water" && Tag(tags, "water") is not ("river" or "stream" or "wastewater")
                     && line.Lon.Length > 3 && AreaSquareMetres(line) >= MinAreaSquareMetres)
                lakes.Add(line);
        }
        return (rivers, lakes);
    }

    private static string? Tag(JsonElement tags, string key) =>
        tags.TryGetProperty(key, out var v) ? v.GetString() : null;

    private static Ring ReadLine(JsonElement points)
    {
        int n = points.GetArrayLength();
        var lon = new double[n];
        var lat = new double[n];
        int i = 0;
        foreach (var p in points.EnumerateArray())
        {
            lon[i] = p.GetProperty("lon").GetDouble();
            lat[i] = p.GetProperty("lat").GetDouble();
            i++;
        }
        return new Ring(lon, lat);
    }

    /// <summary>Area of a small closed ring (shoelace on local metres).</summary>
    private static double AreaSquareMetres(Ring ring)
    {
        double mPerLat = 111_320, mPerLon = mPerLat * Math.Cos(ring.Lat[0] * Math.PI / 180);
        double sum = 0;
        int n = ring.Lon.Length;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            sum += ring.Lon[i] * mPerLon * ring.Lat[j] * mPerLat - ring.Lon[j] * mPerLon * ring.Lat[i] * mPerLat;
        }
        return Math.Abs(sum) / 2;
    }
}
