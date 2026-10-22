using System;

namespace Boundari.Sim;

/// <summary>
/// Light fog of war: Boundari's own rule, not OpenFront's. What you can see of other
/// players' troops and gold (display only: the simulation and the AI use the real numbers).
/// You and your allies are shown exactly and live. Neighbours (players whose land touches
/// yours) show approximate troops, live. Everyone further away shows rougher troops that
/// only refresh every <see cref="FarRefreshTicks"/>. Gold is hidden unless you're allied.
/// </summary>
public static class FogRules
{
    /// <summary>How often the troops of players further away (not neighbours) refresh.</summary>
    public const int FarRefreshTicks = 20 * Balance.TicksPerSecond;

    // Approximate troops are rounded to the nearest of these steps (each times a power of ten):
    // about 10% apart for neighbours, about 25-35% apart for players further away.
    private static readonly double[] NearSteps = { 1, 1.2, 1.4, 1.6, 1.8, 2, 2.5, 3, 3.5, 4, 4.5, 5, 6, 7, 8, 9 };
    private static readonly double[] FarSteps = { 1, 1.5, 2, 3, 4, 5, 7 };

    /// <summary>A troop count rounded the way a neighbour's (<paramref name="near"/>) or a further player's is shown.</summary>
    public static long Approximate(long troops, bool near)
    {
        if (troops <= 0)
            return 0;
        var steps = near ? NearSteps : FarSteps;
        double decade = Math.Pow(10, Math.Floor(Math.Log10(troops)));
        double best = decade, bestRatio = double.MaxValue;
        foreach (double s in steps)
            foreach (double value in new[] { s * decade, s * decade * 10 })
            {
                double ratio = Math.Max(value, troops) / Math.Min(value, troops); // nearest on a log scale
                if (ratio < bestRatio)
                {
                    bestRatio = ratio;
                    best = value;
                }
            }
        return (long)Math.Round(best);
    }

    /// <summary>
    /// Where a further player's refresh cycle is: 0 just refreshed .. 1 about to. Each player
    /// is offset by their id, so they don't all refresh at once.
    /// </summary>
    public static double FarCycle(ushort playerId, int tick, double tickFraction = 0) =>
        ((tick + playerId * 37) % FarRefreshTicks + tickFraction) / FarRefreshTicks;

    /// <summary>The tick a further player's shown troops were last refreshed at.</summary>
    public static int LastFarRefresh(ushort playerId, int tick) => tick - (tick + playerId * 37) % FarRefreshTicks;
}

public sealed partial class Game
{
    /// <summary>Match option: light fog of war (see <see cref="FogRules"/>; on by default; off = classic OpenFront, everything shown).</summary>
    public bool FogOfWar { get; set; } = true;
}
