using Boundari.Core;

namespace Boundari.Sim;

/// <summary>
/// A warship. Built from one of your ports, it patrols the water around where you
/// placed it: sinks enemy troop boats, duels enemy warships, and captures other
/// players' trade ships (sailing them home for the gold). Heals slowly near its own
/// ports. Behaviour modelled on OpenFront's warships.
/// </summary>
public sealed class Warship
{
    public readonly int Id;
    public Player Owner { get; internal set; }
    public int Tile { get; internal set; }
    public int PatrolTile { get; internal set; }
    public int Health { get; internal set; } = WarshipRules.MaxHealth;

    internal readonly Rng Rng;
    internal int[]? Path;
    internal int PathIndex;
    internal object? Target;     // Boat, Warship or TradeShip
    internal int LastShotTick = -WarshipRules.ShotCooldownTicks;
    internal int NextRetargetTick;

    /// <summary>Where it was last tick (for smooth drawing).</summary>
    public int PreviousTile { get; internal set; }

    internal Warship(int id, Player owner, int tile, int patrolTile, ulong seed)
    {
        Id = id;
        Owner = owner;
        Tile = PreviousTile = tile;
        PatrolTile = patrolTile;
        Rng = new Rng(seed);
    }

    public bool Alive => Health > 0;
}

/// <summary>A shell fired by a warship, flying towards its target.</summary>
public sealed class Shell
{
    public readonly Warship Shooter;
    public readonly object Target; // Boat or Warship
    public readonly int FromTile;
    public readonly int FiredTick;
    public readonly int HitTick;
    internal readonly int Damage;

    internal Shell(Warship shooter, object target, int fromTile, int firedTick, int hitTick, int damage)
    {
        Shooter = shooter;
        Target = target;
        FromTile = fromTile;
        FiredTick = firedTick;
        HitTick = hitTick;
        Damage = damage;
    }
}

/// <summary>Warship numbers (from OpenFront's config), kept together for balancing.</summary>
public static class WarshipRules
{
    public const int MaxHealth = 1000;
    public const int PatrolRange = 100;        // tiles from its patrol point it wanders
    public const int TargetRange = 130;        // tiles at which it engages
    public const int ShotCooldownTicks = 20;   // between shells at warships
    public const int ShellTilesPerTick = 10;
    public const int MinShellDamage = 200;
    public const int MaxShellDamage = 300;
    public const int TilesPerTick = 1;         // patrol speed
    public const int ChaseTilesPerTick = 2;    // hunting a trade ship
    public const int CaptureRange = 5;         // tiles to board a trade ship
    public const int HealPerTick = 1;          // near one of its own ports
    public const int HealRange = 150;
    public const int MaxDistanceFromPort = 150; // where it can be placed
    public const int RetargetTicks = 5;
}
