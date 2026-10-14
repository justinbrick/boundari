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
    public const int SpawnRadius = 6;
    public const long StartTroops = 11_000;
    public const int MinSpawnSpacing = 45; // tiles between bot spawns

    // --- Troops ---
    public const long BaseMaxTroops = 22_000;
    public const long MaxTroopsPerTile = 25;

    // Troops grow fastest at half capacity and slow down as they fill up.
    // Bigger divisor = slower growth.
    public const long GrowthDivisor = 110;

    // --- Gold ---
    // Like OpenFront: a flat income (bots earn half), with trade as the main way to get rich.
    public const long PlayerGoldPerTick = 100;
    public const long BotGoldPerTick = 50;

    // Trade ships sail at this speed (thousandths of a tile per tick).
    public const long TradeShipSpeedPerTick = 1500;

    // --- Attacking ---
    // Troops it costs to take one plains tile of empty land.
    public const long NeutralTileCost = 5;

    // Extra cost per tile when attacking a player, per troop of their density
    // (troops per tile), in tenths: 12 = 1.2x their density.
    public const long DefenderDensityCostTenths = 12;

    // Troops the defender loses per tile taken, in tenths of their density.
    public const long DefenderLossTenths = 8;

    // --- Attack speed ---
    // Time to cross one farmland tile, in travel units. Other terrain scales this by
    // its cost multiplier, and each tile also gets a random factor.
    public const long TravelUnit = 1000;

    // How far an attack's wave advances per tick, in travel units.
    public const long WaveSpeedPerTick = 230;

    // Random spread of crossing times, in percent of normal (min..max). Wide spread
    // gives ragged, natural-looking fronts instead of geometric shapes.
    public const int TravelRandomMinPercent = 40;
    public const int TravelRandomMaxPercent = 170;

    // Tiles already touched by several of the attacker's tiles are crossed faster
    // (percent of normal, by number of touching tiles). This fills in pockets so
    // territories stay solid instead of growing spiky fingers.
    public static readonly int[] SurroundedTravelPercent = { 100, 100, 55, 25, 8 };

    // Attacks slow down as they run out of troops, easing into a crawl before they stop
    // (like OpenFront). Below this share of the troops sent (percent), speed drops in
    // proportion to what's left, down to the minimum speed (permille of full speed).
    public const int SlowdownBelowPercent = 40;
    public const int MinSpeedPermille = 60;

    // How much terrain affects the troops a tile costs, in percent of its effect on speed.
    // 100 = forest costs as many extra troops as it adds time; 0 = terrain only slows you.
    public const int TerrainTroopCostPercent = 50;

    // Safety cap so one huge attack can't stall a tick.
    public const int MaxTilesPerTick = 6000;

    // --- Boats ---
    public const int MaxBoatsPerPlayer = 3;
    public const long BoatSpeedPerTick = 1500;      // thousandths of a tile: 15 tiles a second
    public const int BoatLandingSearchTiles = 200_000;

    // --- Buildings ---
    public const int MinBuildingSpacing = 12;       // tiles between any two buildings

    /// <summary>
    /// What the next one costs, given how many the player already has (OpenFront's
    /// prices). <paramref name="owned"/> counts only ones the player built themselves
    /// and still owns, so capturing buildings never makes yours dearer. Ports and
    /// factories share one count.
    /// </summary>
    public static long BuildingCost(BuildingType type, int owned) => type switch
    {
        BuildingType.Port or BuildingType.Factory or BuildingType.City => Math.Min(1_000_000, 125_000L << Math.Min(owned, 10)),
        BuildingType.Defense => Math.Min(250_000, (owned + 1) * 50_000L),
        BuildingType.Silo => 1_000_000,
        BuildingType.Sam => Math.Min(3_000_000, (owned + 1) * 1_500_000L),
        BuildingType.Warship => Math.Min(1_000_000, (owned + 1) * 250_000L),
        _ => long.MaxValue,
    };

    // --- Terrain difficulty ---
    // Multipliers in tenths (10 = normal) for both the troops it takes to capture a tile
    // and how long the attack wave takes to cross it. A tile's value is its land cover
    // (averaged over its cover cells) times its elevation band.

    /// <summary>Land-cover multiplier. Farmland is the baseline.</summary>
    public static int CoverCostTenths(CoverType cover) => cover switch
    {
        CoverType.Meadow => 12,
        CoverType.Forest => 20,
        CoverType.Water => 25,  // small lakes and marsh
        CoverType.Rock => 25,
        CoverType.Snow => 40,
        _ => 10,                // farmland, or no data
    };

    /// <summary>Elevation multiplier.</summary>
    public static int ElevationCostTenths(TerrainType terrain) => terrain switch
    {
        TerrainType.Highlands => 14,
        TerrainType.Mountains => 20,
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

    public static long GoldPerTick(Player player) => player.IsBot ? BotGoldPerTick : PlayerGoldPerTick;
}
