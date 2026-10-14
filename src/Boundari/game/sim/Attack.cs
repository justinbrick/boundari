using System.Collections.Generic;

namespace Boundari.Sim;

/// <summary>
/// An ongoing push into someone's land (or empty land), modelled as a spreading wave.
/// Every tile on the front has an arrival time: when its neighbour fell, plus how long
/// this tile takes to cross (terrain, with some randomness). The attack's clock runs
/// forward each tick and every tile whose time has come is taken, as long as troops last.
/// Slow terrain (forest, snow, mountains) makes the wave visibly lag and flow around it.
/// </summary>
public sealed class Attack
{
    public readonly Player Attacker;
    public readonly ushort TargetId; // 0 = empty land
    public long Troops { get; internal set; }

    /// <summary>All troops ever sent into this attack (it slows down as Troops runs low).</summary>
    internal long TroopsSent;

    // Front tiles by arrival time. A tile may sit in the queue more than once (re-added
    // with an earlier time as more of its neighbours fall); stale copies are skipped.
    internal readonly PriorityQueue<int, long> Front = new();

    /// <summary>How far the wave has advanced, in <see cref="Balance.TravelUnit"/>s.</summary>
    internal long Clock;

    internal Attack(Player attacker, ushort targetId, long troops)
    {
        Attacker = attacker;
        TargetId = targetId;
        Troops = troops;
    }
}
