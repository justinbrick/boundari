using System;
using System.Collections.Generic;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// Territory colours, military style: people and nations get muted service colours (olive
/// drab, khaki, field grey, navy, rust...), each picked to be as distinct as possible from
/// those already handed out; once the list runs out it starts over a shade darker or lighter.
/// Tribes are all one cream white, like OpenFront's current bot colour.
/// </summary>
public static class PlayerColors
{
    private static readonly string[] Military =
    {
        "6b6b3a", "9a8f5e", "6d7366", "3f4a5c", "6e3b3b", "8a5a3c", "5a6772", "a39a78", "5e6b45", "4f5d6b",
        "5e4a5c", "4d6662", "7f4b3e", "9a8a45", "8a7550", "455a40", "51677a", "6b3f4b", "66778c", "7d8a6a",
        "6e5a45", "3e4651", "a0864e", "535a60", "78805a", "8c7a5b", "8c5550", "b0a07a", "7a7550", "3f5a5e",
        "7a6070", "3a5550", "5c4a3a", "7a8085", "5a4050", "8a9070", "8f6a4a", "4a4f66", "704a48", "5a6e5e",
    };

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
        var pool = new Pool(Military, seed);
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

        public Pool(string[] hex, ulong seed)
        {
            _all = Array.ConvertAll(hex, h => Color.FromHtml(h));
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
