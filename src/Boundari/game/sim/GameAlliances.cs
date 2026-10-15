using System;
using System.Collections.Generic;

namespace Boundari.Sim;

/// <summary>Two players who agreed not to fight. Ends when it expires or one side breaks it.</summary>
public sealed class Alliance
{
    public readonly Player A, B;
    public readonly int CreatedTick;
    public int ExpiresTick { get; internal set; }

    internal Alliance(Player a, Player b, int createdTick)
    {
        A = a;
        B = b;
        CreatedTick = createdTick;
        ExpiresTick = createdTick + AllianceRules.DurationTicks;
    }

    public Player Other(Player p) => p == A ? B : A;
}

/// <summary>A pending "shall we ally?" from one player to another.</summary>
public sealed class AllianceRequest
{
    public readonly Player From, To;
    public readonly int CreatedTick;

    internal AllianceRequest(Player from, Player to, int createdTick)
    {
        From = from;
        To = to;
        CreatedTick = createdTick;
    }
}

/// <summary>OpenFront's alliance numbers (ticks are 1/10 s).</summary>
public static class AllianceRules
{
    public const int RequestTicks = 20 * 10;          // a request lapses after 20 s
    public const int RequestCooldownTicks = 30 * 10;  // before asking the same player again
    public const int DurationTicks = 300 * 10;        // alliances last 5 minutes
    public const int TraitorTicks = 30 * 10;          // breaking one brands you a traitor for 30 s
    public const int TraitorTroopLossPercent = 50;    // attacking a traitor costs half the troops...
    public const int TraitorTravelPercent = 80;       // ...and goes faster
    public const int NukeBreakTiles = 100;            // a bomb hitting this much of an ally's land ends the alliance

    // Relations ("how much a player likes another"), in hundredths of OpenFront's -100..100.
    public const int RelationMax = 10_000;
    public const int RelationDecayPerTick = 5;        // drifts back to neutral by 0.05 a tick
    public const int HostileBelow = -5_000;
    public const int FriendlyFrom = 5_000;
}

/// <summary>How a player feels about another (OpenFront's four bands).</summary>
public enum Relation
{
    Hostile,
    Distrustful,
    Neutral,
    Friendly,
}

// Alliances, alliance requests and relations, the way OpenFront does them: ask, and the
// other side accepts (by asking back) or lets it lapse; allies can't attack each other;
// breaking an alliance makes you a traitor, cheaper and faster to attack for 30 s.
public sealed partial class Game
{
    private readonly List<Alliance> _alliances = new();
    private readonly List<AllianceRequest> _allianceRequests = new();
    private readonly Dictionary<(ushort From, ushort To), int> _lastRequestTick = new();
    private readonly List<Player> _withRelations = new();

    public IReadOnlyList<Alliance> Alliances => _alliances;
    public IReadOnlyList<AllianceRequest> AllianceRequests => _allianceRequests;

    public bool AreAllied(ushort a, ushort b) => GetPlayer(a) is { } p && p.Allies.Contains(b);

    public bool IsTraitor(Player p) => p.TraitorUntilTick > Tick;

    /// <summary>The pending request from one player to another, or null.</summary>
    public AllianceRequest? PendingRequest(Player from, Player to) =>
        _allianceRequests.Find(r => r.From == from && r.To == to);

    /// <summary>Whether <paramref name="from"/> may ask <paramref name="to"/> for an alliance (or accept theirs) now.</summary>
    public bool CanRequestAlliance(Player from, Player to)
    {
        if (from == to || Phase != GamePhase.Running || !from.IsAlive || !from.HasSpawned || !to.IsAlive || !to.HasSpawned)
            return false;
        if (from.IsAlliedWith(to) || PendingRequest(from, to) is not null)
            return false;
        if (PendingRequest(to, from) is not null)
            return true; // that's accepting
        return !_lastRequestTick.TryGetValue((from.Id, to.Id), out int last) || Tick - last >= AllianceRules.RequestCooldownTicks;
    }

    private void RequestAlliance(Player from, Player to)
    {
        if (!CanRequestAlliance(from, to))
            return;
        if (PendingRequest(to, from) is { } incoming)
        {
            AcceptAlliance(incoming);
            return;
        }
        _allianceRequests.Add(new AllianceRequest(from, to, Tick));
        _lastRequestTick[(from.Id, to.Id)] = Tick;
    }

    private void AcceptAlliance(AllianceRequest request)
    {
        _allianceRequests.Remove(request);
        var (a, b) = (request.From, request.To);
        if (a.IsAlliedWith(b))
            return;
        _alliances.Add(new Alliance(a, b, Tick));
        a.Allies.Add(b.Id);
        b.Allies.Add(a.Id);
        UpdateRelation(a, b, AllianceRules.RelationMax);
        UpdateRelation(b, a, AllianceRules.RelationMax);
        CancelNukesBetween(a, b);
        // Fighting stops: attacks between them pull back (see AdvanceAttacks) and boats turn home on landing.
    }

    private void RejectAlliance(AllianceRequest request) => _allianceRequests.Remove(request);

    /// <summary>Ends an alliance on purpose: the breaker becomes a traitor (unless the other already was) and is disliked for it.</summary>
    private void BreakAlliance(Player breaker, Player other)
    {
        var alliance = _alliances.Find(x => (x.A == breaker && x.B == other) || (x.A == other && x.B == breaker));
        if (alliance is null)
            return;
        EndAlliance(alliance);
        if (!IsTraitor(other))
            breaker.TraitorUntilTick = Tick + AllianceRules.TraitorTicks;
        UpdateRelation(other, breaker, -AllianceRules.RelationMax);
        foreach (var neighbour in BorderingPlayers(breaker))
            if (neighbour != other)
                UpdateRelation(neighbour, breaker, -4_000);
    }

    private void EndAlliance(Alliance alliance)
    {
        _alliances.Remove(alliance);
        alliance.A.Allies.Remove(alliance.B.Id);
        alliance.B.Allies.Remove(alliance.A.Id);
    }

    /// <summary>Every tick: requests and alliances run out, tribes say yes to everyone, feelings cool off.</summary>
    private void RunAlliances()
    {
        for (int i = _allianceRequests.Count - 1; i >= 0; i--)
        {
            var r = _allianceRequests[i];
            if (Tick - r.CreatedTick > AllianceRules.RequestTicks || !r.From.IsAlive || !r.To.IsAlive)
                _allianceRequests.RemoveAt(i);
            else if (r.To.IsBot)
                AcceptAlliance(r); // OpenFront's tribes accept every request
        }
        for (int i = _alliances.Count - 1; i >= 0; i--)
        {
            var a = _alliances[i];
            if (Tick >= a.ExpiresTick || !a.A.IsAlive || !a.B.IsAlive)
                EndAlliance(a);
        }
        foreach (var p in _withRelations)
        {
            var rel = p.Relations!;
            for (int i = 0; i < rel.Length; i++)
            {
                int r = rel[i];
                if (r == 0)
                    continue;
                r -= Math.Sign(r) * AllianceRules.RelationDecayPerTick;
                rel[i] = Math.Abs(r) < AllianceRules.RelationDecayPerTick * 2 ? 0 : r;
            }
        }
    }

    // ------------------------------------------------------------------
    //  Relations
    // ------------------------------------------------------------------

    /// <summary>How <paramref name="of"/> feels about <paramref name="toward"/>, in hundredths (-10000..10000).</summary>
    public int RelationValue(Player of, Player toward) =>
        of.Relations is { } rel && toward.Id < rel.Length ? rel[toward.Id] : 0;

    public Relation RelationOf(Player of, Player toward)
    {
        int v = RelationValue(of, toward);
        return v < AllianceRules.HostileBelow ? Relation.Hostile
            : v < 0 ? Relation.Distrustful
            : v < AllianceRules.FriendlyFrom ? Relation.Neutral
            : Relation.Friendly;
    }

    private void UpdateRelation(Player of, Player toward, int delta)
    {
        if (of == toward)
            return;
        if (of.Relations is null)
        {
            of.Relations = new int[_players.Count];
            _withRelations.Add(of);
        }
        ref int r = ref of.Relations[toward.Id];
        r = Math.Clamp(r + delta, -AllianceRules.RelationMax, AllianceRules.RelationMax);
    }

    /// <summary>
    /// An attack on a player angers them (OpenFront's amounts by difficulty), unless it's
    /// hitting back at someone the attacker already hates. It also turns down any alliance
    /// request the target had sent the attacker.
    /// </summary>
    private void OnAttacked(Player attacker, Player target)
    {
        if (!attacker.IsBot && !target.IsBot && PendingRequest(target, attacker) is { } asked)
            RejectAlliance(asked);
        if (Difficulty != Difficulty.Easy && RelationOf(attacker, target) == Relation.Hostile)
            return;
        int change = Difficulty switch
        {
            Difficulty.Easy => -6_000,
            Difficulty.Medium => -7_000,
            Difficulty.Hard => -8_000,
            _ => -10_000,
        };
        UpdateRelation(target, attacker, change);
    }

    /// <summary>
    /// A bomb (not a warhead) aimed where it would hit 100+ tiles of someone's land (inner
    /// ring counting double), or any of an ally's buildings: that player is angered, their
    /// pending request to the launcher is withdrawn, or the alliance with them is broken.
    /// </summary>
    private void OnBombLaunched(Player launcher, NukeType type, int target)
    {
        if (type == NukeType.Warhead)
            return;
        var (inner, outer) = NukeRules.Blast(type);
        var weight = new Dictionary<ushort, int>(); // in halves: inner tiles 2, outer 1
        int w = Map.Width, cx = target % w, cy = target / w;
        for (int y = Math.Max(0, cy - outer); y <= Math.Min(Map.Height - 1, cy + outer); y++)
            for (int x = Math.Max(0, cx - outer); x <= Math.Min(w - 1, cx + outer); x++)
            {
                int dx = x - cx, dy = y - cy, d2 = dx * dx + dy * dy;
                ushort o = Owner[y * w + x];
                if (o == Nobody || d2 > outer * outer)
                    continue;
                weight[o] = weight.GetValueOrDefault(o) + (d2 <= inner * inner ? 2 : 1);
            }
        var hit = new SortedSet<ushort>();
        foreach (var (id, halves) in weight)
            if (halves >= AllianceRules.NukeBreakTiles * 2)
                hit.Add(id);
        long outer2 = (long)outer * outer;
        foreach (var b in _buildings.Values)
            if (launcher.Allies.Contains(b.Owner) && DistSq(b.Tile, target) <= outer2)
                hit.Add(b.Owner);

        foreach (ushort id in hit)
        {
            var victim = GetPlayer(id);
            if (victim is null || victim == launcher)
                continue;
            if (PendingRequest(launcher, victim) is { } mine)
            {
                RejectAlliance(mine);
                continue;
            }
            if (launcher.IsAlliedWith(victim))
                BreakAlliance(launcher, victim);
            UpdateRelation(victim, launcher, -AllianceRules.RelationMax);
        }
    }

    /// <summary>A Tsar Bomba is an act of war on its target: any alliance breaks and both sides hate each other.</summary>
    private void OnTsarBombaLaunched(Player launcher, Player target)
    {
        if (launcher == target)
            return;
        if (launcher.IsAlliedWith(target))
            BreakAlliance(launcher, target);
        UpdateRelation(target, launcher, -AllianceRules.RelationMax);
        UpdateRelation(launcher, target, -AllianceRules.RelationMax);
    }

    /// <summary>New allies' bombs already flying at each other fizzle out.</summary>
    private void CancelNukesBetween(Player a, Player b)
    {
        foreach (var n in _nukes)
        {
            if (n.Gone || (n.Owner != a && n.Owner != b))
                continue;
            var other = n.Owner == a ? b : a;
            if (n.TargetPlayer == other.Id || Owner[n.To] == other.Id)
                n.Gone = true;
        }
    }

    /// <summary>Players (not nobody) whose land touches this player's border, in id order.</summary>
    public List<Player> BorderingPlayers(Player player)
    {
        var ids = new SortedSet<ushort>();
        Span<int> nb = stackalloc int[4];
        foreach (int tile in player.Border)
        {
            int count = LandNeighbours(tile, nb);
            for (int k = 0; k < count; k++)
            {
                ushort o = Owner[nb[k]];
                if (o != Nobody && o != player.Id)
                    ids.Add(o);
            }
        }
        var result = new List<Player>(ids.Count);
        foreach (ushort id in ids)
            result.Add(_players[id]);
        return result;
    }

    /// <summary>Troops a player has out attacking right now.</summary>
    public long TroopsAttacking(Player player)
    {
        long sum = 0;
        foreach (var a in _attacks)
            if (a.Attacker == player)
                sum += a.Troops;
        return sum;
    }
}
