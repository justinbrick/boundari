using System;
using Boundari.Map;

namespace Boundari.Sim;

/// <summary>
/// Every gameplay number in one place, so balancing doesn't mean hunting through code.
/// All maths is integer-based so every machine computes exactly the same result.
/// </summary>
public static class Balance
{
    public const int TicksPerSecond = 10;

    // --- Spawning ---
    public const int SpawnPhaseTicks = 10 * TicksPerSecond;
    public const int SpawnRadius = 4;
    public const long StartTroops = 5_000;
    public const int MinSpawnSpacing = 30; // tiles between bot spawns

    // --- Troops ---
    public const long BaseMaxTroops = 10_000;
    public const long MaxTroopsPerTile = 25;

    // Troops grow fastest at half capacity and slow down as they fill up.
    // Bigger divisor = slower growth.
    public const long GrowthDivisor = 110;

    // --- Gold ---
    public const long BaseGoldPerSecond = 100;
    public const long GoldPerTilePerSecond = 1;

    // --- Attacking ---
    // Troops it costs to take one plains tile of empty land.
    public const long NeutralTileCost = 5;

    // Extra cost per tile when attacking a player, per troop of their density
    // (troops per tile), in tenths: 12 = 1.2x their density.
    public const long DefenderDensityCostTenths = 12;

    // Troops the defender loses per tile taken, in tenths of their density.
    public const long DefenderLossTenths = 8;

    // How many tiles an attack takes per tick: grows with the length of the front.
    public const int MinTilesPerTick = 2;
    public const int FrontTilesPerExtraTile = 12;
    public const int MaxTilesPerTick = 800;

    /// <summary>Terrain cost multiplier, in tenths (10 = normal).</summary>
    public static long TerrainCostTenths(TerrainType terrain) => terrain switch
    {
        TerrainType.Highlands => 15,
        TerrainType.Mountains => 25,
        _ => 10,
    };

    public static long MaxTroops(int tiles) => BaseMaxTroops + tiles * MaxTroopsPerTile;

    /// <summary>Troops gained this tick (or lost, when over capacity).</summary>
    public static long TroopGrowth(long troops, int tiles)
    {
        long max = MaxTroops(tiles);
        if (troops >= max)
            return -Math.Max(1, (troops - max) / 100); // slowly shed troops above capacity
        long growth = troops * (max - troops) / (max * GrowthDivisor);
        return Math.Max(growth, 1);
    }

    public static long GoldPerTick(int tiles) =>
        (BaseGoldPerSecond + tiles * GoldPerTilePerSecond) / TicksPerSecond;
}
