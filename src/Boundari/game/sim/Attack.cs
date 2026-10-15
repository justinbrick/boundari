using System.Collections.Generic;
using Boundari.Core;

namespace Boundari.Sim;

/// <summary>
/// An ongoing push into someone's land (or empty land), run exactly like OpenFront's
/// attacks: tiles next to the attacker's land wait in a queue, ordered by a randomised
/// priority that favours tiles already half-surrounded and flat ground; every tick the
/// attack takes tiles off the queue until that tick's budget is spent. How much budget a
/// tile eats depends on terrain, the length of the front, and above all on how
/// outnumbered the attack is, so a big army pours through thinly held land in fast,
/// branching "veins" while a weak one crawls. See <see cref="Game"/>.AdvanceAttacks.
/// </summary>
public sealed class Attack
{
    public readonly Player Attacker;
    public readonly ushort TargetId; // 0 = empty land

    /// <summary>Troops still in the attack (OpenFront keeps fractions here).</summary>
    internal double TroopsExact;

    public long Troops
    {
        get => (long)TroopsExact;
        internal set => TroopsExact = value;
    }

    /// <summary>Where a boat landed (the attack spreads from there), or -1 for a land attack.</summary>
    internal readonly int SourceTile;

    /// <summary>The target's tiles currently next to this attack (OpenFront's attack border; its size sets the pace).</summary>
    internal readonly HashSet<int> Border = new();

    /// <summary>Tiles waiting to be taken, by priority (lowest first). A tile can be queued more than once.</summary>
    internal readonly PriorityQueue<int, long> ToConquer = new();

    internal readonly Rng Rng;

    internal Attack(Player attacker, ushort targetId, long troops, int sourceTile, Rng rng)
    {
        Attacker = attacker;
        TargetId = targetId;
        TroopsExact = troops;
        SourceTile = sourceTile;
        Rng = rng;
    }
}
