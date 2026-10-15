using System;
using System.Collections.Generic;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// Territory colours, picked from OpenFront's palettes: bright ones for people, softer
/// ones for nations, and dull greyish ones for bots (OpenFront's classic bot colours), so
/// the filler players fade into the background. Each pool hands out its most distinct
/// colour first; once a pool runs out it starts over, slightly darker or lighter each round.
/// </summary>
public static class PlayerColors
{
    private static readonly string[] Human =
    {
        "a3e635", "84cc16", "10b981", "34d399", "2dd4bf", "4ade80", "6ee7b7", "86efac", "22c55e", "52b788",
        "30b2b4", "93c5fd", "7dd3fc", "63cafd", "38bdf8", "60a5fa", "3b82f6", "4f46e5", "7c3aed", "9333ea",
        "b388ff", "a78bfa", "d946ef", "a855f7", "c084fc", "f0abfc", "f472b6", "ec4899", "dc2626", "ef4444",
        "f87171", "fb7185", "fda4af", "fcd34d", "fbbf24", "eab308", "f59e0b", "fb923c", "f97316", "ea580c",
    };

    private static readonly string[] Nation =
    {
        "d2d264", "b4d278", "aabe64", "50c878", "82c882", "8cb48c", "a0bea0", "a0b48c", "64a050", "648c6e",
        "64b4a0", "82b4aa", "aabeb4", "648296", "78a0c8", "8c96b4", "64d2d2", "8cb4dc", "82aabe", "64b4e6",
        "5082be", "7878be", "966ebe", "a078a0", "aa8cbe", "b482b4", "be8c96", "b464e6", "b4a0b4", "aa96aa",
        "968296", "e6b4b4", "d2a0c8", "e682b4", "d264a0", "be6482", "dc7878", "c8826e", "e68c8c", "e66464",
        "e69664", "d28c50", "e6b450", "c8a06e", "be9682", "b4aa8c", "c8c88c", "beaa64",
    };

    private static readonly string[] Bot =
    {
        "96a08c", "a0a096", "aaaa8c", "aaaa78", "96a078", "96aa82", "96aa96", "82aa82", "8ca08c", "789664",
        "788c78", "64aa82", "78a096", "82a096", "78aaaa", "78a0be", "8296aa", "8296a0", "8c96a0", "8ca0aa",
        "96a0a0", "6478a0", "78828c", "8282a0", "8c828c", "8c78a0", "968296", "968ca0", "a082a0", "aa96aa",
        "a078be", "a07882", "aa788c", "aa8278", "aa8282", "b48c8c", "be82a0", "be7878", "be8c78", "bea064",
        "aa8c64", "a08c82", "aa9682", "a09678", "a0968c", "a08c96", "a096a0", "968c96", "b4a0a0",
    };

    // Past this many picks, choosing the most distinct colour isn't worth the time (OpenFront).
    private const int DistinctPicks = 50;

    /// <summary>A colour for every player id (index 0, nobody, is unused).</summary>
    public static Color[] For(IReadOnlyList<Player> players, ulong seed)
    {
        int count = 1;
        foreach (var p in players)
            count = Math.Max(count, p.Id + 1);
        var colors = new Color[count];
        var pools = new Dictionary<PlayerKind, Pool>
        {
            [PlayerKind.Human] = new(Human, seed),
            [PlayerKind.Nation] = new(Nation, seed + 1),
            [PlayerKind.Bot] = new(Bot, seed + 2),
        };
        foreach (var p in players)
            colors[p.Id] = pools[p.Kind].Next();
        return colors;
    }

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

        // Round 0 as is, then darker, lighter, darker still...
        private static Color Shade(Color c, int round) => round switch
        {
            0 => c,
            _ when round % 2 == 1 => c.Darkened(0.12f * ((round + 1) / 2)),
            _ => c.Lightened(0.12f * (round / 2)),
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
