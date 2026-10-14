using System;
using System.Collections.Generic;

namespace Boundari.MapBuilder;

/// <summary>A ring already projected into tile coordinates (tile (i, j) has its centre at (i + 0.5, j + 0.5)).</summary>
public sealed record TileRing(double[] X, double[] Y);

/// <summary>Turns vector polygons into per-tile data.</summary>
public static class Rasterizer
{
    /// <summary>
    /// Which tile centres are inside the rings (even-odd rule, so holes inside
    /// polygons work). Scanline fill: one sorted list of edge crossings per row.
    /// </summary>
    public static bool[] Fill(IReadOnlyList<TileRing> rings, int width, int height)
    {
        var crossings = new List<double>[height];
        for (int j = 0; j < height; j++)
            crossings[j] = new List<double>();

        foreach (var ring in rings)
        {
            int n = ring.X.Length;
            for (int k = 0; k < n; k++)
            {
                double x0 = ring.X[k], y0 = ring.Y[k];
                double x1 = ring.X[(k + 1) % n], y1 = ring.Y[(k + 1) % n];
                if (y0 == y1)
                    continue;
                double yMin = Math.Min(y0, y1), yMax = Math.Max(y0, y1);
                int jStart = Math.Max(0, (int)Math.Ceiling(yMin - 0.5));
                int jEnd = Math.Min(height - 1, (int)Math.Ceiling(yMax - 0.5) - 1);
                for (int j = jStart; j <= jEnd; j++)
                {
                    double yc = j + 0.5;
                    crossings[j].Add(x0 + (yc - y0) / (y1 - y0) * (x1 - x0));
                }
            }
        }

        var inside = new bool[width * height];
        for (int j = 0; j < height; j++)
        {
            var xs = crossings[j];
            xs.Sort();
            for (int p = 0; p + 1 < xs.Count; p += 2)
            {
                int iStart = Math.Max(0, (int)Math.Ceiling(xs[p] - 0.5));
                int iEnd = Math.Min(width - 1, (int)Math.Ceiling(xs[p + 1] - 0.5) - 1);
                for (int i = iStart; i <= iEnd; i++)
                    inside[j * width + i] = true;
            }
        }
        return inside;
    }

    /// <summary>
    /// Exact distance from each tile centre to the nearest ring edge, for tiles
    /// within <paramref name="band"/> tiles of an edge. Others get +infinity.
    /// This sub-tile precision is what makes the drawn coastline follow the real one.
    /// </summary>
    public static float[] EdgeDistance(IReadOnlyList<TileRing> rings, int width, int height, double band)
    {
        var dist2 = new float[width * height];
        Array.Fill(dist2, float.PositiveInfinity);
        foreach (var ring in rings)
        {
            int n = ring.X.Length;
            for (int k = 0; k < n; k++)
            {
                double ax = ring.X[k], ay = ring.Y[k];
                double bx = ring.X[(k + 1) % n], by = ring.Y[(k + 1) % n];
                int i0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - band));
                int i1 = Math.Min(width - 1, (int)Math.Ceiling(Math.Max(ax, bx) + band));
                int j0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, by) - band));
                int j1 = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(ay, by) + band));
                double dx = bx - ax, dy = by - ay;
                double len2 = dx * dx + dy * dy;
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                    {
                        double px = i + 0.5 - ax, py = j + 0.5 - ay;
                        double t = len2 > 0 ? Math.Clamp((px * dx + py * dy) / len2, 0, 1) : 0;
                        double ex = px - t * dx, ey = py - t * dy;
                        float d2 = (float)(ex * ex + ey * ey);
                        int idx = j * width + i;
                        if (d2 < dist2[idx])
                            dist2[idx] = d2;
                    }
            }
        }
        for (int i = 0; i < dist2.Length; i++)
            dist2[i] = MathF.Sqrt(dist2[i]);
        return dist2;
    }

    /// <summary>
    /// Signed distance from each tile centre to the nearest river bank, in tiles:
    /// negative inside a river, positive outside. Each line is a river's centre line
    /// with its own half-width. Tiles further than <paramref name="band"/> from any
    /// bank get +infinity.
    /// </summary>
    public static float[] RiverDistance(IReadOnlyList<(TileRing Line, double HalfWidth)> rivers, int width, int height, double band)
    {
        var result = new float[width * height];
        Array.Fill(result, float.PositiveInfinity);
        foreach (var (line, halfWidth) in rivers)
        {
            double reach = halfWidth + band;
            for (int k = 0; k + 1 < line.X.Length; k++)
            {
                double ax = line.X[k], ay = line.Y[k];
                double bx = line.X[k + 1], by = line.Y[k + 1];
                int i0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - reach));
                int i1 = Math.Min(width - 1, (int)Math.Ceiling(Math.Max(ax, bx) + reach));
                int j0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, by) - reach));
                int j1 = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(ay, by) + reach));
                double dx = bx - ax, dy = by - ay;
                double len2 = dx * dx + dy * dy;
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                    {
                        double px = i + 0.5 - ax, py = j + 0.5 - ay;
                        double t = len2 > 0 ? Math.Clamp((px * dx + py * dy) / len2, 0, 1) : 0;
                        double ex = px - t * dx, ey = py - t * dy;
                        float d = (float)(Math.Sqrt(ex * ex + ey * ey) - halfWidth);
                        int idx = j * width + i;
                        if (d < result[idx])
                            result[idx] = d;
                    }
            }
        }
        return result;
    }

    /// <summary>
    /// Euclidean distance (in tiles) from every tile to the nearest tile where
    /// <paramref name="feature"/> is true. Felzenszwalb–Huttenlocher, exact and linear time.
    /// </summary>
    public static float[] DistanceTransform(bool[] feature, int width, int height)
    {
        const double Inf = 1e20;
        var grid = new double[width * height];
        for (int i = 0; i < grid.Length; i++)
            grid[i] = feature[i] ? 0 : Inf;

        int maxDim = Math.Max(width, height);
        var f = new double[maxDim];
        var d = new double[maxDim];
        var v = new int[maxDim];
        var z = new double[maxDim + 1];

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++) f[y] = grid[y * width + x];
            Transform1D(f, height, d, v, z);
            for (int y = 0; y < height; y++) grid[y * width + x] = d[y];
        }
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++) f[x] = grid[y * width + x];
            Transform1D(f, width, d, v, z);
            for (int x = 0; x < width; x++) grid[y * width + x] = d[x];
        }

        var result = new float[grid.Length];
        for (int i = 0; i < grid.Length; i++)
            result[i] = (float)Math.Sqrt(grid[i]);
        return result;
    }

    // Squared-distance transform of a 1D sampled function (lower envelope of parabolas).
    private static void Transform1D(double[] f, int n, double[] d, int[] v, double[] z)
    {
        int k = 0;
        v[0] = 0;
        z[0] = double.NegativeInfinity;
        z[1] = double.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            double s = Intersect(f, q, v[k]);
            while (s <= z[k])
            {
                k--;
                s = Intersect(f, q, v[k]);
            }
            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = double.PositiveInfinity;
        }
        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q)
                k++;
            int p = v[k];
            d[q] = (double)(q - p) * (q - p) + f[p];
        }
    }

    private static double Intersect(double[] f, int q, int p) =>
        ((f[q] + (double)q * q) - (f[p] + (double)p * p)) / (2.0 * (q - p));
}
