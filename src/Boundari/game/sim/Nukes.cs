using System;
using System.Collections.Generic;

namespace Boundari.Sim;

/// <summary>
/// A bomb in flight: launched from a silo, it flies a high arc to its target and goes off
/// there, unless a SAM shoots it down on the way. A Tsar Bomba (MIRV) doesn't explode
/// itself: high above the map it splits into a swarm of warheads aimed at the target's land.
/// </summary>
public sealed class Nuke
{
    public readonly int Id;
    public readonly Player Owner;
    public readonly NukeType Type;
    public readonly int From, To; // silo (or split point) and target tiles

    /// <summary>Where the bomb is after each tick of flight (map coordinates, tile units); the last point is the target.</summary>
    public readonly float[] PathX, PathY;

    /// <summary>Whether SAMs can shoot at it at each point (near its silo or its target, as in OpenFront).</summary>
    internal readonly bool[] Targetable;

    /// <summary>Index into the path (-1 = still on the pad).</summary>
    public int Index { get; internal set; } = -1;

    /// <summary>Ticks it sits before setting off (a silo fires one bomb per tick; warheads trickle out).</summary>
    public int WaitTicks { get; internal set; }

    /// <summary>A SAM missile is on its way to it.</summary>
    public bool TargetedBySam { get; internal set; }

    /// <summary>Exploded, shot down, or (a Tsar Bomba) split up.</summary>
    public bool Gone { get; internal set; }

    /// <summary>For a Tsar Bomba and its warheads: whose land it is after.</summary>
    internal readonly ushort TargetPlayer;
    internal bool Split;

    internal Nuke(int id, Player owner, NukeType type, int from, int to, float[] x, float[] y, bool[] targetable, ushort targetPlayer)
    {
        Id = id;
        Owner = owner;
        Type = type;
        From = from;
        To = to;
        PathX = x;
        PathY = y;
        Targetable = targetable;
        TargetPlayer = targetPlayer;
    }

    public int TicksLeft => PathX.Length - 1 - Index + WaitTicks;
}

/// <summary>A SAM's interceptor, flying straight at the point where it meets a bomb.</summary>
public sealed class SamMissile
{
    public readonly Player Owner;
    public readonly int From, To;
    public readonly Nuke Target;
    public readonly int FlightTicks;
    public int Age { get; internal set; }

    internal SamMissile(Player owner, int from, int to, Nuke target, int flightTicks)
    {
        Owner = owner;
        From = from;
        To = to;
        Target = target;
        FlightTicks = Math.Max(1, flightTicks);
    }
}

/// <summary>A detonation, kept briefly for the renderer.</summary>
public readonly record struct Explosion(int Tile, NukeType Type, int Tick, bool Intercepted = false);

/// <summary>Bomb and SAM rules, modelled on OpenFront (Config.nukeMagnitudes, nukeSpeed, NukeExecution, MIRVExecution, SAMLauncherExecution).</summary>
public static class NukeRules
{
    /// <summary>Blast radii in tiles: everything inside Inner is destroyed, about half of the ring out to Outer.</summary>
    public static (int Inner, int Outer) Blast(NukeType type) => type switch
    {
        NukeType.Moab => (12, 30),
        NukeType.FatMan => (80, 100),
        NukeType.Warhead => (12, 18),
        _ => (0, 0), // a Tsar Bomba itself never lands
    };

    /// <summary>Tiles flown per tick.</summary>
    public static double Speed(NukeType type) => type switch
    {
        NukeType.Moab or NukeType.FatMan => 10,
        NukeType.TsarBomba => 15,
        _ => 22,
    };

    public const int TargetableRange = 150;   // SAMs can only hit a bomb this close to its silo or target
    public const int SamMissileSpeed = 12;    // tiles per tick (counted along x + y)
    public const int MaxSamRange = 150;
    public const int SpawnImmunityTicks = 5 * Balance.TicksPerSecond; // no bombs right after the start

    // Tsar Bomba: up to this many warheads over the target's land within this range,
    // no two closer than MirvSpread (counted along x + y).
    public const int MirvRange = 1500;
    public const int MirvSpread = 55;
    public const int MirvWarheads = 350;
    public const int MirvFlightTicks = 14; // its climb to the split point takes about this long

    /// <summary>
    /// Troops a player loses for each tile a bomb takes from them (OpenFront's nukeDeathFactor):
    /// a share of their troops per tile for big bombs; warheads hurt only big armies much.
    /// </summary>
    public static double Deaths(NukeType type, double troops, int tilesLeft, long maxTroops)
    {
        if (type != NukeType.Warhead)
            return 5 * troops / Math.Max(1, tilesLeft);
        double excess = Math.Max(0, troops - 0.03 * maxTroops);
        return 500 * (1 - Math.Exp(-2 * excess / Math.Max(1, maxTroops)));
    }

    /// <summary>
    /// The arc a bomb flies (OpenFront's parabola): a cubic curve bulging "up" the map by a
    /// third of the distance (at least 50 tiles), walked <paramref name="step"/> tiles at a time.
    /// </summary>
    public static (float[] X, float[] Y) Arc(int fromX, int fromY, int toX, int toY, int mapHeight, double step, bool clampToMap = true)
    {
        var (p1, p2) = ControlPoints(fromX, fromY, toX, toY, mapHeight, clampToMap);
        double length = ArcLength(fromX, fromY, p1, p2, toX, toY, out var samples);
        var xs = new List<float>();
        var ys = new List<float>();
        int s = 1;
        for (double d = step; d < length; d += step)
        {
            while (s < samples.Count - 1 && samples[s].Dist < d)
                s++;
            var (ax, ay, ad) = samples[s - 1];
            var (bx, by, bd) = samples[s];
            double f = bd > ad ? (d - ad) / (bd - ad) : 0;
            xs.Add((float)(ax + (bx - ax) * f));
            ys.Add((float)(ay + (by - ay) * f));
        }
        xs.Add(toX + 0.5f);
        ys.Add(toY + 0.5f);
        return (xs.ToArray(), ys.ToArray());
    }

    /// <summary>Length of the arc between two tiles (as a bomb would fly it without the map's top edge in the way).</summary>
    public static double ArcLength(int fromX, int fromY, int toX, int toY)
    {
        var (p1, p2) = ControlPoints(fromX, fromY, toX, toY, 0, clamp: false);
        return ArcLength(fromX, fromY, p1, p2, toX, toY, out _);
    }

    private static ((double X, double Y), (double X, double Y)) ControlPoints(int fx, int fy, int tx, int ty, int mapHeight, bool clamp)
    {
        double dx = tx - fx, dy = ty - fy;
        double height = Math.Max(Math.Sqrt(dx * dx + dy * dy) / 3, 50);
        double p1y = fy + dy / 4 - height, p2y = fy + dy * 3 / 4 - height;
        if (clamp)
        {
            p1y = Math.Clamp(p1y, 0, mapHeight - 1);
            p2y = Math.Clamp(p2y, 0, mapHeight - 1);
        }
        return ((fx + dx / 4, p1y), (fx + dx * 3 / 4, p2y));
    }

    private static double ArcLength(double x0, double y0, (double X, double Y) p1, (double X, double Y) p2, double x3, double y3,
        out List<(double X, double Y, double Dist)> samples)
    {
        int n = (int)Math.Clamp((Math.Abs(x3 - x0) + Math.Abs(y3 - y0)) * 2, 16, 4000);
        samples = new List<(double, double, double)>(n + 1);
        double total = 0, px = x0 + 0.5, py = y0 + 0.5;
        samples.Add((px, py, 0));
        for (int i = 1; i <= n; i++)
        {
            double t = (double)i / n, u = 1 - t;
            double x = u * u * u * x0 + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * x3 + 0.5;
            double y = u * u * u * y0 + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * y3 + 0.5;
            total += Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
            samples.Add((x, y, total));
            (px, py) = (x, y);
        }
        return total;
    }
}

/// <summary>Launch a bomb at a tile (from your nearest ready silo).</summary>
public sealed record NukeIntent(ushort PlayerId, NukeType Type, int Tile) : Intent(PlayerId);
