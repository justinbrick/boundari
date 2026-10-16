using System.Collections.Generic;

namespace Boundari.Sim;

/// <summary>OpenFront's numbers for embargoes and targets.</summary>
public static class EmbargoRules
{
    public const int EmbargoRelationPenalty = -2_000; // a nation embargoed by someone likes them 0.2 less (OpenFront -20)
    public const int TargetDurationTicks = 10 * 10;   // a mark lasts 10 s
    public const int TargetCooldownTicks = 15 * 10;   // before marking again
    public const int TargetRelationPenalty = -4_000;  // the marked player likes the marker 0.4 less (OpenFront -40)
}

/// <summary>Asks to stop (<paramref name="Start"/>) or resume trade with another player.</summary>
public sealed record EmbargoIntent(ushort PlayerId, ushort TargetId, bool Start) : Intent(PlayerId);

/// <summary>Marks a player as a target for you and your allies for a few seconds.</summary>
public sealed record TargetIntent(ushort PlayerId, ushort TargetId) : Intent(PlayerId);

// Embargoes and targets, as in OpenFront. An embargo stops trade between the two players
// either way: no trade ships between their ports (ships already on the way turn back) and
// trains stop at the other's stations. A nation embargoed by someone trusts them less
// until it's lifted. Marking a target shows it to you and your allies for 10 s and makes
// the target like you less.
public sealed partial class Game
{
    // Nations whose relation toward an embargoer currently carries the embargo penalty.
    private readonly HashSet<(ushort Nation, ushort Embargoer)> _embargoPenalty = new();

    /// <summary>Whether <paramref name="from"/> has stopped trading with <paramref name="against"/>.</summary>
    public bool HasEmbargo(Player from, Player against) => from.Embargoes.Contains(against.Id);

    /// <summary>Whether the two may trade (no embargo either way).</summary>
    public bool CanTrade(Player a, Player b) => a != b && !a.Embargoes.Contains(b.Id) && !b.Embargoes.Contains(a.Id);

    private bool CanTrade(ushort a, ushort b) => GetPlayer(a) is { } pa && GetPlayer(b) is { } pb && CanTrade(pa, pb);

    private void SetEmbargo(Player from, Player against, bool on)
    {
        if (from == against)
            return;
        if (on ? !from.Embargoes.Add(against.Id) : !from.Embargoes.Remove(against.Id))
            return;
        Report(on ? GameEventKind.EmbargoStarted : GameEventKind.EmbargoStopped, from, against);
        // OpenFront's nations hold an embargo against them against the embargoer while it lasts.
        if (on && against.IsNation && _embargoPenalty.Add((against.Id, from.Id)))
            UpdateRelation(against, from, EmbargoRules.EmbargoRelationPenalty);
        else if (!on && _embargoPenalty.Remove((against.Id, from.Id)))
            UpdateRelation(against, from, -EmbargoRules.EmbargoRelationPenalty);
    }

    /// <summary>Whether this player may mark the other as a target now (not an ally, not too soon after the last mark).</summary>
    public bool CanTarget(Player me, Player other) =>
        me != other && me.IsAlive && other.IsAlive && !me.IsAlliedWith(other)
        && (me.LastTargetTick < 0 || Tick - me.LastTargetTick >= EmbargoRules.TargetCooldownTicks);

    private void MarkTarget(Player me, Player other)
    {
        if (!CanTarget(me, other))
            return;
        me.LastTargetTick = Tick;
        me.Targets.RemoveAll(t => Tick - t.Tick >= EmbargoRules.TargetDurationTicks);
        me.Targets.Add((Tick, other.Id));
        UpdateRelation(other, me, EmbargoRules.TargetRelationPenalty);
        Report(GameEventKind.Targeted, me, other);
        // Allies are told too (they see the mark).
        if (EventsFor != Nobody && me.Allies.Contains(EventsFor) && other.Id != EventsFor)
            _events.Add(new GameEvent(GameEventKind.Targeted, me.Id, other.Id));
    }

    /// <summary>Players this one has marked in the last 10 s.</summary>
    public bool IsTargetOf(Player marker, Player target)
    {
        for (int i = marker.Targets.Count - 1; i >= 0; i--)
        {
            var (tick, id) = marker.Targets[i];
            if (Tick - tick >= EmbargoRules.TargetDurationTicks)
                break; // older ones have run out
            if (id == target.Id)
                return true;
        }
        return false;
    }

    /// <summary>Marked by this player or by one of their allies (what they see on the map), like OpenFront's transitive targets.</summary>
    public bool IsMarkedFor(Player viewer, Player target)
    {
        if (IsTargetOf(viewer, target))
            return true;
        foreach (ushort ally in viewer.Allies)
            if (GetPlayer(ally) is { } a && IsTargetOf(a, target))
                return true;
        return false;
    }
}
