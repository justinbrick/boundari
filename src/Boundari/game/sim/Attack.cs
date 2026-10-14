using System.Collections.Generic;

namespace Boundari.Sim;

/// <summary>
/// An ongoing push into someone's land (or empty land). It holds the troops sent and
/// a queue of target tiles along the front, taken a few per tick in a slightly random
/// order so fronts advance organically rather than in straight lines.
/// </summary>
public sealed class Attack
{
    public readonly Player Attacker;
    public readonly ushort TargetId; // 0 = empty land
    public long Troops { get; internal set; }

    // A tile may sit in the queue more than once (re-added with a better priority as
    // more of its neighbours fall); stale copies are skipped when they come up.
    internal readonly PriorityQueue<int, long> Front = new();
    internal int Step;

    internal Attack(Player attacker, ushort targetId, long troops)
    {
        Attacker = attacker;
        TargetId = targetId;
        Troops = troops;
    }
}
