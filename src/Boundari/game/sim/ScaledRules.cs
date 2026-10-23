using System;

namespace Boundari.Sim;

/// <summary>
/// OpenFront's numbers that are measured in tiles, adjusted to the map's tile size so a
/// finer map plays the same. <see cref="Linear"/> is how many of this map's tiles span what
/// one tile of the reference map did (Europe at 3000 tiles wide is 1; the 4000-wide Europe
/// is 1.33). Distances, ranges and speeds scale by <see cref="Linear"/>; things that count
/// tiles (troop caps, per-tile attack costs, "under 100 tiles") by <see cref="Area"/>.
/// At 1 every number is exactly the plain OpenFront one.
/// </summary>
public sealed class ScaledRules
{
    public readonly double Linear;
    public readonly double Area;

    public ScaledRules(double linear)
    {
        Linear = linear > 0 ? linear : 1;
        Area = Linear * Linear;
        SpawnRadius = D(Balance.SpawnRadius);
        MinSpawnSpacing = D(Balance.MinSpawnSpacing);
        NationSpawnSpread = D(25);
        MinBuildingSpacing = D(Balance.MinBuildingSpacing);
        DefensePostRange = D(Balance.DefensePostRange);
        ConquerBelowTiles = A(Balance.ConquerBelowTiles);
        BoatSpeedPerTick = (long)Math.Round(Balance.BoatSpeedPerTick * Linear);
        TradeShipSpeedPerTick = (long)Math.Round(Balance.TradeShipSpeedPerTick * Linear);
        BoatLandingSearchRadius = D(Balance.BoatLandingSearchRadius);
        BoatLandingSearchTiles = A(Balance.BoatLandingSearchTiles);
        ShortRangeDebuffTiles = D(TradeRules.ShortRangeDebuffTiles);
        StationMaxRange = D(RailRules.StationMaxRange);
        StationMinRange = D(RailRules.StationMinRange);
        MaxRailLength = D(RailRules.MaxRailLength);
        MaxBridgeTiles = D(RailRules.MaxBridgeTiles);
        RailTurnCost = D(RailRules.TurnCost);
        TrainSpeedPerTick = (long)Math.Round(RailRules.TrainSpeedPerTick * Linear);
        WarshipPatrolRange = D(WarshipRules.PatrolRange);
        WarshipTargetRange = D(WarshipRules.TargetRange);
        WarshipShellTilesPerTick = D(WarshipRules.ShellTilesPerTick);
        WarshipTilesPerTick = D(WarshipRules.TilesPerTick);
        WarshipChaseTilesPerTick = D(WarshipRules.ChaseTilesPerTick);
        WarshipCaptureRange = D(WarshipRules.CaptureRange);
        WarshipHealRange = D(WarshipRules.HealRange);
        WarshipMaxDistanceFromPort = D(WarshipRules.MaxDistanceFromPort);
        NukeTargetableRange = D(NukeRules.TargetableRange);
        SamMissileSpeed = D(NukeRules.SamMissileSpeed);
        MaxSamRange = D(NukeRules.MaxSamRange);
        MirvRange = D(NukeRules.MirvRange);
        MirvSpread = D(NukeRules.MirvSpread);
        MirvClimb = D(500);
        MirvClimbFloor = D(50);
        NukeArcMinHeight = 50 * Linear;
        NukeBreakTiles = A(AllianceRules.NukeBreakTiles);
        LargeTerritoryMidpoint = AttackRules.LargeTerritoryMidpoint * Area;
        DotSpacing = D(DotRules.Spacing);
        DotWalkSpeedPerTick = (long)Math.Round(DotRules.WalkSpeedPerTick * Linear);
        DotReach = D(DotRules.Reach);
        DotStandBack = D(DotRules.StandBack);
        DotSecondRowBack = DotStandBack + D(DotRules.RowGap);
        DotFallBack = D(DotRules.FallBackTiles);
        DotLink = D(DotRules.LinkTiles);
        DotSidesRing = D(DotRules.SidesRing);
        DotPushSpeedPerTick = (long)Math.Round(DotRules.PushSpeedPerTick * Linear);
        DotMaxPushPerTick = (long)Math.Round(DotRules.MaxPushPerTick * Linear);
    }

    /// <summary>A distance in tiles, scaled (never below 1).</summary>
    public int D(int tiles) => Math.Max(1, (int)Math.Round(tiles * Linear));

    /// <summary>A count of tiles, scaled.</summary>
    public int A(int tiles) => Math.Max(1, (int)Math.Round(tiles * Area));

    public readonly int SpawnRadius, MinSpawnSpacing, NationSpawnSpread, MinBuildingSpacing, DefensePostRange, ConquerBelowTiles;
    public readonly long BoatSpeedPerTick, TradeShipSpeedPerTick, TrainSpeedPerTick;
    public readonly int BoatLandingSearchRadius, BoatLandingSearchTiles, ShortRangeDebuffTiles;
    public readonly int StationMaxRange, StationMinRange, MaxRailLength, MaxBridgeTiles, RailTurnCost;
    public readonly int WarshipPatrolRange, WarshipTargetRange, WarshipShellTilesPerTick, WarshipTilesPerTick,
        WarshipChaseTilesPerTick, WarshipCaptureRange, WarshipHealRange, WarshipMaxDistanceFromPort;
    public readonly int NukeTargetableRange, SamMissileSpeed, MaxSamRange, MirvRange, MirvSpread, MirvClimb, MirvClimbFloor;
    public readonly double NukeArcMinHeight;
    public readonly int NukeBreakTiles;
    public readonly double LargeTerritoryMidpoint;
    public readonly int DotSpacing;
    public readonly long DotWalkSpeedPerTick;
    public readonly int DotReach, DotStandBack, DotSecondRowBack, DotFallBack, DotLink, DotSidesRing;
    public readonly long DotPushSpeedPerTick, DotMaxPushPerTick;

    /// <summary>A SAM's reach at this level.</summary>
    public int SamRange(int level) => D(Balance.SamRange(level));

    /// <summary>A bomb's blast radii.</summary>
    public (int Inner, int Outer) Blast(NukeType type)
    {
        var (inner, outer) = NukeRules.Blast(type);
        return inner == 0 && outer == 0 ? (0, 0) : (D(inner), D(outer));
    }

    /// <summary>A bomb's speed, in tiles per tick.</summary>
    public double NukeSpeed(NukeType type) => NukeRules.Speed(type) * Linear;
}
