using System.Collections.Generic;

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

    // ---- The front (how dots move the line) ----

    /// <summary>How far a dot presses or holds the line: full force on its own tile, fading to nothing just past this many tiles.</summary>
    public const int Reach = 8;

    /// <summary>Force of a full-stamina level-1 dot, in thousandths (force = strength x stamina).</summary>
    public const int DotForce = 1000;

    /// <summary>A commander's land with no dot near still holds this much (a quarter of a level-1 dot).</summary>
    public const int UndefendedForce = 250;

    /// <summary>Locals have no dots: all their land holds like a level-2 dot.</summary>
    public const int LocalsForce = 2000;

    /// <summary>
    /// How fast the line moves, thousandths of a tile per tick for each level-1 dot of force
    /// more than the other side: a lone dot in open farmland wilderness moves it 3 tiles a second.
    /// </summary>
    public const long PushSpeedPerTick = 300;

    /// <summary>The line never moves faster than dots walk.</summary>
    public const long MaxPushPerTick = WalkSpeedPerTick;

    /// <summary>A dot whose tile is taken steps back onto its owner's nearest tile within this many; with none, it is cut off and lost.</summary>
    public const int FallBackTiles = 3;

    /// <summary>A dot that found no line to stand on (its land has no border) looks again after this long.</summary>
    public const int LookAgainTicks = 50;

    /// <summary>Most dots a tick that search their whole land for a line to stand on (the rest wait a tick).</summary>
    public const int LongSearchesPerTick = 2;

    /// <summary>Dots this close (twice the spacing) belong to the same stretch of border: one attack moves them all.</summary>
    public const int LinkTiles = 2 * Spacing;

    /// <summary>
    /// Radius of the ring looked at to tell how many sides a dot is pressed from: if more
    /// than 5/8 of the land on it is held by attacking enemies, 2 sides (half force); more than 7/8, 3 (a third).
    /// </summary>
    public const int SidesRing = 4;
}

/// <summary>
/// An attack: a stretch of border whose dots all push into one neighbour's land (or the
/// wilderness) until stopped. Every dot standing on that stretch takes part, including dots
/// that walk onto it later; a dot that walks away leaves it.
/// </summary>
public sealed class Push
{
    public readonly int Id;
    public readonly Player Attacker;

    /// <summary>Whose land is pushed into (<see cref="Game.Nobody"/> = wilderness).</summary>
    public readonly ushort TargetId;

    internal readonly List<Dot> Members = new(); // in id order
    internal readonly Dictionary<int, long> Progress = new(); // tile -> thousandths of a tile pushed so far

    /// <summary>The dots pushing, in id order.</summary>
    public IReadOnlyList<Dot> Dots => Members;

    internal Push(int id, Player attacker, ushort targetId)
    {
        Id = id;
        Attacker = attacker;
        TargetId = targetId;
    }

    public override string ToString() => $"push {Id} of {Attacker.Name} into {TargetId}";
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

    /// <summary>Off the line with no line found: the tick to look for one again.</summary>
    internal long LookAgainAt;

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
