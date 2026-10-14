using System;

namespace Boundari.Core;

/// <summary>
/// Seeded 2D gradient (Perlin) noise. Uses only basic arithmetic so results
/// are identical across machines.
/// </summary>
public sealed class Noise
{
    private readonly int[] _perm = new int[512];

    public Noise(ulong seed)
    {
        var rng = new Rng(seed);
        var p = new int[256];
        for (int i = 0; i < 256; i++)
            p[i] = i;
        for (int i = 255; i > 0; i--)
        {
            int j = rng.RangeInt(0, i);
            (p[i], p[j]) = (p[j], p[i]);
        }
        for (int i = 0; i < 512; i++)
            _perm[i] = p[i & 255];
    }

    private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);

    private static double Lerp(double a, double b, double t) => a + t * (b - a);

    private static double Grad(int hash, double x, double y) =>
        (hash & 7) switch
        {
            0 => x + y,
            1 => -x + y,
            2 => x - y,
            3 => -x - y,
            4 => x,
            5 => -x,
            6 => y,
            _ => -y,
        };

    /// <summary>Single octave, roughly in [-1, 1].</summary>
    public double Sample(double x, double y)
    {
        double fx = Math.Floor(x);
        double fy = Math.Floor(y);
        int xi = (int)fx & 255;
        int yi = (int)fy & 255;
        double xf = x - fx;
        double yf = y - fy;
        double u = Fade(xf);
        double v = Fade(yf);

        int aa = _perm[_perm[xi] + yi];
        int ab = _perm[_perm[xi] + yi + 1];
        int ba = _perm[_perm[xi + 1] + yi];
        int bb = _perm[_perm[xi + 1] + yi + 1];

        double x1 = Lerp(Grad(aa, xf, yf), Grad(ba, xf - 1, yf), u);
        double x2 = Lerp(Grad(ab, xf, yf - 1), Grad(bb, xf - 1, yf - 1), u);
        return Lerp(x1, x2, v);
    }

    /// <summary>Fractal (layered) noise, roughly in [-1, 1].</summary>
    public double Fbm(double x, double y, int octaves, double lacunarity = 2.0, double gain = 0.5)
    {
        double sum = 0, amp = 1, norm = 0;
        for (int i = 0; i < octaves; i++)
        {
            sum += Sample(x, y) * amp;
            norm += amp;
            amp *= gain;
            x = x * lacunarity + 17.31;
            y = y * lacunarity + 9.73;
        }
        return sum / norm;
    }

    /// <summary>Ridged noise in [0, 1]: sharp crests, good for mountain ranges.</summary>
    public double Ridged(double x, double y, int octaves, double lacunarity = 2.0, double gain = 0.5)
    {
        double sum = 0, amp = 1, norm = 0, weight = 1;
        for (int i = 0; i < octaves; i++)
        {
            double n = 1.0 - Math.Abs(Sample(x, y));
            n *= n * weight;
            weight = Math.Clamp(n * 2.0, 0.0, 1.0);
            sum += n * amp;
            norm += amp;
            amp *= gain;
            x = x * lacunarity + 31.7;
            y = y * lacunarity + 47.1;
        }
        return sum / norm;
    }
}
