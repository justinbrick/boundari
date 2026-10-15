using System;
using System.Collections.Generic;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// Territory colours. People and nations get dull, muted colours of every hue: the palette
/// is spread evenly around the colour wheel at one low colourfulness (in CIE Lab, so every
/// hue looks equally muted) and three lightness levels. Each new player gets the colour most
/// distinct from those already handed out; once the palette runs out it starts over a shade
/// lighter or darker. Tribes are all one cream white, like OpenFront's current bot colour.
/// </summary>
public static class PlayerColors
{
    // How dull: chroma in CIE Lab (about what the earlier hand-picked military tones averaged),
    // and the lightness levels used.
    private const double Chroma = 20;
    private static readonly double[] Lightness = { 38, 48, 58 };
    private const int HueSteps = 24; // every 15 degrees

    private static readonly Color[] Dull = MakeDull();

    private static Color[] MakeDull()
    {
        var colors = new List<Color>();
        foreach (double l in Lightness)
            for (int i = 0; i < HueSteps; i++)
            {
                double hue = (i + (l == Lightness[1] ? 0.5 : 0)) * 2 * Math.PI / HueSteps; // stagger the middle row
                colors.Add(FromLab(l, Chroma * Math.Cos(hue), Chroma * Math.Sin(hue)));
            }
        return colors.ToArray();
    }

    /// <summary>CIE Lab (D65) to an sRGB colour.</summary>
    private static Color FromLab(double l, double a, double b)
    {
        double fy = (l + 16) / 116, fx = fy + a / 500, fz = fy - b / 200;
        static double Inv(double t) => t * t * t > 0.008856 ? t * t * t : (t - 16.0 / 116) / 7.787;
        double x = Inv(fx) * 0.95047, y = Inv(fy), z = Inv(fz) * 1.08883;
        double r = 3.2406 * x - 1.5372 * y - 0.4986 * z;
        double g = -0.9689 * x + 1.8758 * y + 0.0415 * z;
        double bl = 0.0557 * x - 0.2040 * y + 1.0570 * z;
        static float Gamma(double v) => (float)Math.Clamp(v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055, 0, 1);
        return new Color(Gamma(r), Gamma(g), Gamma(bl));
    }

    /// <summary>Tribes: all the same cream white, see-through like everyone else.</summary>
    public static readonly Color Tribe = new(0.96f, 0.93f, 0.84f);

    // Past this many picks, choosing the most distinct colour isn't worth the time (OpenFront).
    private const int DistinctPicks = 50;

    /// <summary>A colour for every player id (index 0, nobody, is unused).</summary>
    public static Color[] For(IReadOnlyList<Player> players, ulong seed)
    {
        int count = 1;
        foreach (var p in players)
            count = Math.Max(count, p.Id + 1);
        var colors = new Color[count];
        var pool = new Pool(Dull, seed);
        foreach (var p in players)
            colors[p.Id] = p.IsBot ? Tribe : pool.Next();
        return colors;
    }

    /// <summary>The same colours, all fully opaque (for icons, ships and labels).</summary>
    public static Color[] Solid(Color[] colors) => Array.ConvertAll(colors, c => new Color(c, 1f));

    private sealed class Pool
    {
        private readonly Color[] _all;
        private readonly List<Color> _left = new();
        private readonly List<Color> _given = new();
        private readonly Core.Rng _rng;
        private int _round = -1;

        public Pool(Color[] colors, ulong seed)
        {
            _all = colors;
            _rng = new Core.Rng(seed);
        }

        public Color Next()
        {
            if (_left.Count == 0)
            {
                _round++;
                foreach (var c in _all)
                    _left.Add(Shade(c, _round));
            }
            int pick;
            if (_given.Count == 0 || _given.Count > DistinctPicks)
                pick = _rng.RangeInt(0, _left.Count - 1);
            else
            {
                pick = 0;
                double bestGap = -1;
                for (int i = 0; i < _left.Count; i++)
                {
                    double gap = double.MaxValue;
                    foreach (var g in _given)
                        gap = Math.Min(gap, Distance(_left[i], g));
                    if (gap > bestGap)
                        (bestGap, pick) = (gap, i);
                }
            }
            var color = _left[pick];
            _left.RemoveAt(pick);
            _given.Add(color);
            return color;
        }

        // Round 0 as is, then lighter, darker, lighter still...
        private static Color Shade(Color c, int round) => round switch
        {
            0 => c,
            _ when round % 2 == 1 => c.Lightened(0.15f * ((round + 1) / 2)),
            _ => c.Darkened(0.15f * (round / 2)),
        };
    }

    /// <summary>How different two colours look (distance in CIE Lab).</summary>
    private static double Distance(Color a, Color b)
    {
        var (l1, a1, b1) = Lab(a);
        var (l2, a2, b2) = Lab(b);
        return Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
    }

    private static (double L, double A, double B) Lab(Color c)
    {
        static double Linear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        double r = Linear(c.R), g = Linear(c.G), b = Linear(c.B);
        double x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047;
        double y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        double z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883;
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
        double fx = F(x), fy = F(y), fz = F(z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }
}
