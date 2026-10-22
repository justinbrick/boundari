namespace Boundari.Sim;

/// <summary>
/// Numbers for dots (the user's design, not OpenFront's): one dot = one troop that stands
/// on a border. Distances are tiles of the Ardennes map (30 m); <see cref="ScaledRules"/>
/// adjusts them for other maps.
/// </summary>
public static class DotRules
{
    /// <summary>Most dots a commander may own (Barracks will raise it later).</summary>
    public const int StartCap = 10;

    /// <summary>Dots a commander spawns with, already spread around the starting border.</summary>
    public const int FreeAtStart = 10;

    public const int MaxStrength = 4;
    public const int MaxStamina = 100;

    /// <summary>Funds for one new dot.</summary>
    public const long Price = 25_000;

    /// <summary>Funds to raise a dot from this strength level to the next: 25K, 50K, 100K.</summary>
    public static long UpgradeCost(int fromLevel) => 25_000L << (fromLevel - 1);

    /// <summary>Selling a dot gives back this share of everything paid for it.</summary>
    public const int SellRefundPercent = 50;

    /// <summary>Tiles between dots spread along a border (closer when the stretch is short).</summary>
    public const int Spacing = 6;

    /// <summary>Walking speed, thousandths of a tile per tick: 10 tiles a second.</summary>
    public const long WalkSpeedPerTick = 1000;
    public const long StepCost = 1000, DiagonalStepCost = 1414;

    /// <summary>Most tiles one path search may look at (a whole big territory fits).</summary>
    public const int PathSearchTiles = 1_500_000;

    /// <summary>Most border tiles looked at when spreading dots out along a border.</summary>
    public const int SpreadSearchTiles = 20_000;
}

/// <summary>
/// One troop. It stands on one of its owner's border tiles, or walks there through its
/// owner's land, a tile at a time along <see cref="Path"/>.
/// </summary>
public sealed class Dot
{
    public readonly int Id;
    public readonly Player Owner;

    /// <summary>The tile it is on (while walking: the last tile it reached).</summary>
    public int Tile { get; internal set; }

    /// <summary>Where it is going, or <see cref="Tile"/> when standing.</summary>
    public int Destination { get; internal set; }

    /// <summary>1..<see cref="DotRules.MaxStrength"/>: how hard it pushes and how stiff it is when pushed.</summary>
    public int Strength { get; internal set; } = 1;

    /// <summary>0..<see cref="DotRules.MaxStamina"/> (drains while pushing, from step 5 on).</summary>
    public int Stamina { get; internal set; } = DotRules.MaxStamina;

    /// <summary>Funds paid for it and its upgrades (selling gives back a share).</summary>
    public long Paid { get; internal set; }

    /// <summary>Tiles from where it set off to <see cref="Destination"/>; null when standing. Path[PathIndex] = Tile.</summary>
    internal int[]? Path;
    internal int PathIndex;

    /// <summary>Thousandths of a tile walked towards <see cref="NextTile"/>.</summary>
    internal long Progress;

    public bool IsWalking => Path is not null;

    /// <summary>The tile it is stepping onto (its own tile when standing).</summary>
    public int NextTile => Path is not null && PathIndex + 1 < Path.Length ? Path[PathIndex + 1] : Tile;

    /// <summary>How far it is between <see cref="Tile"/> and <see cref="NextTile"/>, 0..1 (for drawing).</summary>
    public double StepFraction(int width)
    {
        if (Path is null || NextTile == Tile)
            return 0;
        bool diagonal = Tile % width != NextTile % width && Tile / width != NextTile / width;
        return System.Math.Min(1.0, Progress / (double)(diagonal ? DotRules.DiagonalStepCost : DotRules.StepCost));
    }

    internal Dot(int id, Player owner, int tile)
    {
        Id = id;
        Owner = owner;
        Tile = tile;
        Destination = tile;
    }

    public override string ToString() => $"dot {Id} of {Owner.Name} at {Tile}";
}
