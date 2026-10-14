using System;
using System.Threading.Tasks;
using Boundari.Core;
using Boundari.Map;

namespace Boundari.View;

/// <summary>
/// Purely visual per-tile data, worked out from a map when it's loaded:
/// lighting with cast shadows, vegetation and snow. Not saved in map files,
/// so the look can be tuned without rebuilding maps. No Godot types.
/// </summary>
public sealed class MapVisuals
{
    /// <summary>Light multiplier: 1 on flat ground, higher on slopes facing the light, low in shadow.</summary>
    public readonly float[] Shade;

    /// <summary>0 = dry/barren, 0.5 = grassland, 1 = dense forest.</summary>
    public readonly float[] Vegetation;

    /// <summary>0..1 snow cover.</summary>
    public readonly float[] Snow;

    // Sun from the top-left (north-west), fairly low so mountains cast long shadows.
    private const double SunAltitudeDeg = 32;
    private const float ShadowStrength = 0.6f;
    private const int MaxShadowSteps = 80;

    // Used for random maps, which have no real location.
    private const double DefaultKmPerTile = 2.5;
    private const double DefaultLatitude = 45;

    private MapVisuals(int tiles)
    {
        Shade = new float[tiles];
        Vegetation = new float[tiles];
        Snow = new float[tiles];
    }

    public static MapVisuals Compute(GameMap map)
    {
        var v = new MapVisuals(map.TileCount);
        int w = map.Width, h = map.Height;
        double kmPerTile = map.Geo?.KmPerTile(h) ?? DefaultKmPerTile;

        // Height in "tile units" for lighting. Exaggerated (real mountains are far too
        // flat at map scale to cast visible shadows), more so on coarse maps.
        double metresPerHeightTile = 400 + 60 * kmPerTile;
        var height = new float[map.TileCount];
        float maxHeight = 0;
        for (int i = 0; i < map.TileCount; i++)
        {
            if (!map.IsLand(i))
                continue;
            height[i] = (float)(ElevationScale.ToMetres(map.Elevation[i]) / metresPerHeightTile);
            maxHeight = Math.Max(maxHeight, height[i]);
        }

        // Lowland relief uses a blurred copy: the source elevation data is stitched from
        // patches of different smoothness, which shows up as faint rectangles in flat areas.
        float[] smooth = Blur(height, w, h, radius: 2);

        double alt = SunAltitudeDeg * Math.PI / 180;
        double sinA = Math.Sin(alt), cosA = Math.Cos(alt), tanA = Math.Tan(alt);
        double lx = -cosA / Math.Sqrt(2), ly = -cosA / Math.Sqrt(2), lz = sinA;
        double stepX = -1 / Math.Sqrt(2), stepY = -1 / Math.Sqrt(2);

        var forestNoise = new Noise(1001);
        var detailNoise = new Noise(1002);

        Parallel.For(0, h, y =>
        {
            double lat = Math.Abs(map.Geo?.LatAt(y + 0.5, h) ?? DefaultLatitude);
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!map.IsLand(i))
                {
                    v.Shade[i] = 1;
                    continue;
                }

                // --- Lighting: slope facing the sun, then shadows cast by higher ground ---
                float elev = map.Elevation[i];
                float mountain = (float)SmoothStep(0.5, 0.82, elev);
                float At(int j) => smooth[j] + (height[j] - smooth[j]) * mountain;
                float hl = At(x > 0 ? i - 1 : i), hr = At(x < w - 1 ? i + 1 : i);
                float hu = At(y > 0 ? i - w : i), hd = At(y < h - 1 ? i + w : i);
                double nx = -(hr - hl) * 0.5, ny = -(hd - hu) * 0.5;
                double len = Math.Sqrt(nx * nx + ny * ny + 1);
                double lambert = Math.Max(0, (nx * lx + ny * ly + lz) / len) / sinA;
                lambert = 1 + (lambert - 1) * (0.45 + 0.55 * mountain); // gentler in the lowlands

                double h0 = height[i], excess = 0;
                for (int s = 1; s <= MaxShadowSteps; s++)
                {
                    double ray = h0 + s * tanA;
                    if (ray > maxHeight)
                        break;
                    double px = x + s * stepX, py = y + s * stepY;
                    if (px < 0 || py < 0)
                        break;
                    excess = Math.Max(excess, SampleBilinear(height, w, h, px, py) - ray);
                }
                double shadow = SmoothStep(0, 0.25, excess); // hard-edged shadows
                v.Shade[i] = (float)Math.Clamp(lambert * (1 - ShadowStrength * shadow), 0.2, 1.7);

                // --- Vegetation: large forest regions (~200 km) with ragged edges, more on hills ---
                double fx = x * kmPerTile / 200, fy = y * kmPerTile / 200;
                double forest = forestNoise.Fbm(fx, fy, 6, 2.0, 0.55) + 0.15 * SmoothStep(0.45, 0.75, elev);
                double veg = 0.5 + 0.45 * SmoothStep(0.08, 0.3, forest)
                    + 0.08 * detailNoise.Fbm(fx * 6, fy * 6, 3);
                veg *= 1 - 0.6 * SmoothStep(0.86, 0.92, elev);        // above the tree line
                double taiga = SmoothStep(54, 60, lat) * (1 - SmoothStep(64, 68, lat));
                veg += 0.3 * taiga * SmoothStep(-0.25, 0.1, forest);  // northern forest belt
                veg = Lerp(veg, 0.35, SmoothStep(67, 71, lat));       // tundra
                // Rough dry belt (North Africa, Middle East). Placeholder until real land-cover data.
                double dry = SmoothStep(38, 33.5, lat) * (1 - SmoothStep(14, 10, lat));
                veg = Lerp(veg, 0.08 + 0.1 * forest, dry * 0.9);
                v.Vegetation[i] = (float)Math.Clamp(veg, 0, 1);

                // --- Snow: above a snow line that drops towards the poles ---
                double metres = ElevationScale.ToMetres(elev);
                double snowLine = Math.Clamp(2400 - Math.Max(lat - 45, 0) * 75 + Math.Max(40 - lat, 0) * 80, 400, 5200);
                v.Snow[i] = (float)SmoothStep(snowLine - 250, snowLine + 250, metres);
            }
        });
        return v;
    }

    /// <summary>Two passes of a separable box blur (close to a Gaussian).</summary>
    private static float[] Blur(float[] src, int w, int h, int radius)
    {
        var a = (float[])src.Clone();
        var b = new float[src.Length];
        for (int pass = 0; pass < 2; pass++)
        {
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    float sum = 0;
                    int n = 0;
                    for (int k = Math.Max(0, x - radius); k <= Math.Min(w - 1, x + radius); k++, n++)
                        sum += a[y * w + k];
                    b[y * w + x] = sum / n;
                }
            });
            Parallel.For(0, w, x =>
            {
                for (int y = 0; y < h; y++)
                {
                    float sum = 0;
                    int n = 0;
                    for (int k = Math.Max(0, y - radius); k <= Math.Min(h - 1, y + radius); k++, n++)
                        sum += b[k * w + x];
                    a[y * w + x] = sum / n;
                }
            });
        }
        return a;
    }

    private static float SampleBilinear(float[] f, int w, int h, double x, double y)
    {
        int x0 = (int)x, y0 = (int)y;
        int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float tx = (float)(x - x0), ty = (float)(y - y0);
        float top = f[y0 * w + x0] * (1 - tx) + f[y0 * w + x1] * tx;
        float bottom = f[y1 * w + x0] * (1 - tx) + f[y1 * w + x1] * tx;
        return top * (1 - ty) + bottom * ty;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double SmoothStep(double edge0, double edge1, double x)
    {
        double t = Math.Clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
        return t * t * (3 - 2 * t);
    }
}
