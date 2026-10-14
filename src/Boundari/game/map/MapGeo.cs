using System;

namespace Boundari.Map;

/// <summary>
/// Where a real-world map sits on the Earth. Equirectangular projection with a
/// standard parallel: x is longitude squeezed by cos(StandardParallel), y is latitude.
/// </summary>
public sealed record MapGeo(double LonMin, double LonMax, double LatMin, double LatMax, double StandardParallel)
{
    private const double KmPerDegree = 111.32;

    public double LatAt(double y, int height) => LatMax - y / height * (LatMax - LatMin);

    public double LonAt(double x, int width) => LonMin + x / width * (LonMax - LonMin);

    public double TileX(double lon, int width) => (lon - LonMin) / (LonMax - LonMin) * width;

    public double TileY(double lat, int height) => (LatMax - lat) / (LatMax - LatMin) * height;

    /// <summary>Map height in tiles for a given width, so tiles come out square.</summary>
    public int HeightFor(int width) =>
        (int)Math.Round(width * (LatMax - LatMin) / ((LonMax - LonMin) * Math.Cos(StandardParallel * Math.PI / 180)));

    /// <summary>Approximate size of one tile in kilometres (exact at the standard parallel).</summary>
    public double KmPerTile(int height) => (LatMax - LatMin) / height * KmPerDegree;
}

/// <summary>
/// Converts between real heights and the game's 0..1 elevation scale. Terrain
/// types split at 0.55 (plains/highlands, 350 m) and 0.85 (highlands/mountains, 2000 m).
/// </summary>
public static class ElevationScale
{
    public const float PlainsEnd = 0.55f;
    public const float HighlandsEnd = 0.85f;

    private static readonly (float Metres, float Value)[] Curve =
    {
        (0, 0f), (350, PlainsEnd), (2000, HighlandsEnd), (5500, 1f),
    };

    public static float ToElevation(float metres)
    {
        if (metres <= Curve[0].Metres)
            return 0;
        for (int i = 1; i < Curve.Length; i++)
        {
            var (m0, v0) = Curve[i - 1];
            var (m1, v1) = Curve[i];
            if (metres <= m1)
                return v0 + (metres - m0) / (m1 - m0) * (v1 - v0);
        }
        return 1;
    }

    public static float ToMetres(float elevation)
    {
        if (elevation <= 0)
            return 0;
        for (int i = 1; i < Curve.Length; i++)
        {
            var (m0, v0) = Curve[i - 1];
            var (m1, v1) = Curve[i];
            if (elevation <= v1)
                return m0 + (elevation - v0) / (v1 - v0) * (m1 - m0);
        }
        return Curve[^1].Metres;
    }
}
