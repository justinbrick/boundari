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

    // --- Terrain difficulty ---
    // Cost multipliers in tenths (10 = normal). A tile's cost is its land cover
    // (averaged over its cover cells) times its elevation band.

    /// <summary>Land-cover multiplier. Farmland is the baseline.</summary>
    public static int CoverCostTenths(CoverType cover) => cover switch
    {
        CoverType.Meadow => 12,
        CoverType.Forest => 17,
        CoverType.Water => 20,  // small lakes and marsh
        CoverType.Rock => 22,
        CoverType.Snow => 32,
        _ => 10,                // farmland, or no data
    };

    /// <summary>Elevation multiplier.</summary>
    public static int ElevationCostTenths(TerrainType terrain) => terrain switch
    {
        TerrainType.Highlands => 13,
        TerrainType.Mountains => 18,
        _ => 10,
    };

    /// <summary>How hard each tile is to take, in tenths (10 = plain farmland).</summary>
    public static short[] TileCostTenths(GameMap map)
    {
        var costs = new short[map.TileCount];
        var cover = map.Cover;
        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                int tile = map.Index(x, y);
                if (!map.IsLand(tile))
                    continue;
                int coverCost = 10;
                if (cover is not null)
                {
                    int sum = 0, n = 0, s = cover.Scale;
                    for (int cy = y * s; cy < (y + 1) * s; cy++)
                        for (int cx = x * s; cx < (x + 1) * s; cx++)
                        {
                            var c = cover.Cells[cover.Index(cx, cy)];
                            if (c == CoverType.None)
                                continue;
                            sum += CoverCostTenths(c);
                            n++;
                        }
                    if (n > 0)
                        coverCost = (sum + n / 2) / n;
                }
                costs[tile] = (short)(coverCost * ElevationCostTenths(map.Terrain[tile]) / 10);
            }
        }
        return costs;
    }

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
