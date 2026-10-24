using System;

namespace Boundari.Map;

/// <summary>How a real-world map's latitudes are laid out down the map.</summary>
public enum MapProjection : byte
{
    /// <summary>y is latitude; x is longitude squeezed by cos(StandardParallel).</summary>
    Equirectangular,

    /// <summary>
    /// Tiles are square everywhere (true shapes) and so get smaller on the ground towards
    /// the poles: rows are spaced by ln(tan(45° + lat/2)). StandardParallel is unused.
    /// </summary>
    Mercator,
}

/// <summary>
/// Where a real-world map sits on the Earth: x is longitude, y depends on the
/// <see cref="Projection"/>. Positions past the map's edges carry on in the same projection.
/// </summary>
public sealed record MapGeo(double LonMin, double LonMax, double LatMin, double LatMax, double StandardParallel,
                            MapProjection Projection = MapProjection.Equirectangular)
{
    private const double KmPerDegree = 111.32;
    private const double Rad = Math.PI / 180;

    private bool IsMercator => Projection == MapProjection.Mercator;

    /// <summary>A latitude's height on the projection (north up).</summary>
    private double Y(double lat) => IsMercator ? Math.Log(Math.Tan(Math.PI / 4 + lat * Rad / 2)) : lat;

    private double LatOfY(double y) => IsMercator ? (2 * Math.Atan(Math.Exp(y)) - Math.PI / 2) / Rad : y;

    public double LatAt(double y, int height)
    {
        double top = Y(LatMax);
        return LatOfY(top - y / height * (top - Y(LatMin)));
    }

    public double LonAt(double x, int width) => LonMin + x / width * (LonMax - LonMin);

    public double TileX(double lon, int width) => (lon - LonMin) / (LonMax - LonMin) * width;

    public double TileY(double lat, int height)
    {
        double top = Y(LatMax);
        return (top - Y(lat)) / (top - Y(LatMin)) * height;
    }

    /// <summary>Map height in tiles for a given width, so tiles come out square.</summary>
    public int HeightFor(int width) => IsMercator
        ? (int)Math.Round(width * (Y(LatMax) - Y(LatMin)) / ((LonMax - LonMin) * Rad))
        : (int)Math.Round(width * (LatMax - LatMin) / ((LonMax - LonMin) * Math.Cos(StandardParallel * Rad)));

    /// <summary>
    /// Size of one tile in kilometres on row <paramref name="y"/>: Mercator exact (smaller
    /// towards the poles); equirectangular north to south, the same on every row.
    /// </summary>
    public double KmPerTile(double y, int width, int height) => IsMercator
        ? (LonMax - LonMin) / width * KmPerDegree * Math.Cos(LatAt(y, height) * Rad)
        : (LatMax - LatMin) / height * KmPerDegree;
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
