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
    // Like OpenFront: humans start with 25K troops, bots with 10K, nations by difficulty.
    public const long StartTroops = 25_000;
    public const long BotStartTroops = 10_000;
    public const int MinSpawnSpacing = 45; // tiles between AI spawns

    // --- Troops (OpenFront's formulas) ---
    // Cap: 2 x (tiles^0.6 x 1000 + 50,000), plus 250K per city level; bots get a third,
    // nations a share by difficulty.
    public const long CityTroopIncrease = 250_000;

    // --- Gold ---
    // Like OpenFront: a flat income (bots earn half), with trade as the main way to get rich.
    public const long PlayerGoldPerTick = 100;
    public const long BotGoldPerTick = 50;

    // Trade ships sail at this speed (thousandths of a tile per tick).
    public const long TradeShipSpeedPerTick = 1500;

    // --- Attacking ---
    // Troops it costs to take one farmland tile of empty land (OpenFront: 80/5 for
    // players, 80/10 for bots, scaled by terrain).
    public const long NeutralTileCost = 16;
    public const long BotNeutralTileCost = 8;

    // Bots are soft targets: people and nations lose 30% fewer troops attacking them (OpenFront).
    public const long VsBotTroopCostPercent = 70;

    // A player pushed below this many tiles by an attack is finished off (OpenFront):
    // the attacker takes the scraps and their gold.
    public const int ConquerBelowTiles = 100;

    // Tearing down a building takes 30 s, and you can start one every 30 s (OpenFront).
    public const int DemolishTicks = 30 * TicksPerSecond;
    public const int DemolishCooldownTicks = 30 * TicksPerSecond;

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
    // A boat lands on the clicked owner's own coast within this many tiles of the click (or not at all).
    public const int BoatLandingSearchRadius = 150;

    // --- Buildings ---
    public const int MinBuildingSpacing = 15;       // tiles between any two buildings (OpenFront)

    /// <summary>How long a new building takes before it works (OpenFront's times).</summary>
    public static int ConstructionTicks(BuildingType type) => type switch
    {
        BuildingType.City or BuildingType.Factory => 2 * TicksPerSecond,
        BuildingType.Port or BuildingType.Defense => 5 * TicksPerSecond,
        BuildingType.Silo => 10 * TicksPerSecond,
        BuildingType.Sam => 30 * TicksPerSecond,
        _ => 0,
    };

    /// <summary>Buildings that level up when you build the same thing on top of them.</summary>
    public static bool Stackable(BuildingType type) =>
        type is BuildingType.Port or BuildingType.City or BuildingType.Factory or BuildingType.Silo or BuildingType.Sam;

    // --- Defense posts ---
    // Land within this range of a defender's post costs attackers 5x the troops and
    // takes them 3x as long to take.
    public const int DefensePostRange = 30;
    public const int DefensePostTroopFactor = 5;
    public const int DefensePostSpeedFactor = 3;

    // --- SAMs and silos (for when nukes arrive) ---
    public const int SamCooldownTicks = 90;
    public const int SiloCooldownTicks = 90;

    /// <summary>SAM reach in tiles: 70 at level 1, growing towards 150.</summary>
    public static int SamRange(int level) => 150 - 480 / (level + 5);

    // --- Nukes (OpenFront prices) ---
    public const long MoabCost = 750_000;         // OpenFront's atom bomb
    public const long FatManCost = 5_000_000;     // OpenFront's hydrogen bomb
    public const long TsarBombaBaseCost = 25_000_000; // OpenFront's MIRV ...
    public const long TsarBombaCostPerLaunch = 15_000_000; // ... plus this per one launched in the game

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

    /// <summary>Starting troops, as in OpenFront (Hard nations start like people).</summary>
    public static long StartTroopsFor(PlayerKind kind, Difficulty difficulty) => kind switch
    {
        PlayerKind.Bot => BotStartTroops,
        PlayerKind.Nation => difficulty switch
        {
            Difficulty.Easy => 12_500,
            Difficulty.Medium => 18_750,
            Difficulty.Hard => 25_000,
            _ => 31_250,
        },
        _ => StartTroops,
    };

    /// <summary>Troop cap, as in OpenFront.</summary>
    public static long MaxTroops(int tiles, int cityLevels, PlayerKind kind = PlayerKind.Human, Difficulty difficulty = Difficulty.Medium)
    {
        double cap = 2 * (Math.Pow(tiles, 0.6) * 1000 + 50_000) + cityLevels * (double)CityTroopIncrease;
        return (long)(kind switch
        {
            PlayerKind.Bot => cap / 3,
            PlayerKind.Nation => cap * difficulty switch
            {
                Difficulty.Easy => 0.5,
                Difficulty.Medium => 0.75,
                Difficulty.Hard => 1,
                _ => 1.25,
            },
            _ => cap,
        });
    }

    /// <summary>
    /// Troops gained this tick, as in OpenFront: (10 + troops^0.73 / 4), scaled by how
    /// much room is left under the cap (bots grow at half speed, nations a little slower
    /// or faster by difficulty). Over the cap you drop to it.
    /// </summary>
    public static long TroopGrowth(long troops, long max, PlayerKind kind = PlayerKind.Human, Difficulty difficulty = Difficulty.Medium)
    {
        double toAdd = (10 + Math.Pow(troops, 0.73) / 4) * (1 - (double)troops / Math.Max(1, max));
        toAdd *= kind switch
        {
            PlayerKind.Bot => 0.5,
            PlayerKind.Nation => difficulty switch
            {
                Difficulty.Easy => 0.9,
                Difficulty.Medium => 0.95,
                Difficulty.Hard => 1,
                _ => 1.05,
            },
            _ => 1,
        };
        return Math.Min(troops + (long)toAdd, max) - troops;
    }

    public static long GoldPerTick(Player player) => player.IsBot ? BotGoldPerTick : PlayerGoldPerTick;
}
