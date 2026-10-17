using System;
using System.Collections.Generic;
using System.Linq;
using Boundari.Core;
using Boundari.Map;

namespace Boundari.Sim;

/// <summary>
/// Whom a nation attacks and with how many troops, following OpenFront's AiAttackBehavior:
///   - Empty land first. Now and then a boat at a random spot across the water.
///   - Troops are saved up to a reserve (30-39% of the cap, rolled per nation) and usually
///     a trigger level (50-59%) before attacking a player; empty land and tribes only keep
///     an expand reserve (10-19%).
///   - Then a list of strategies, tried in an order that gets smarter with difficulty:
///     take tribes, answer attacks, help allies with their targets, betray an ally, clean
///     up fallout, hit players it hates, traitors, the runaway leader, the very weak, the
///     juiciest, someone already being beaten up, the weakest neighbour, someone overseas.
///   - Hard and Impossible keep enough troops home to match their strongest neighbour,
///     don't bother with attacks under a fifth of the target's troops, take tribes in an
///     order that walls others in (see <see cref="BotAnnexation"/>), and boat a token force
///     that attacks by land the moment it lands.
/// Like the rest of the AI it only submits intents, and uses the nation's own RNG.
/// </summary>
internal sealed class NationAttacks
{
    private readonly Game _game;
    private readonly Player _player;
    private readonly Rng _rng;
    private readonly double _triggerRatio, _reserveRatio, _expandRatio;
    private readonly NationAlliances _alliances;
    private readonly NationEmojis _emojis;
    private readonly NationWarships _warships;
    private Difficulty D => _game.Difficulty;

    private double _botAttackTroopsSent;
    // Our boat landings (attack ids) already followed up by land.
    private readonly List<int> _followedLandings = new();
    // Only set during a MaybeAttack call: the boat route planned to each target.
    private Dictionary<Player, BoatRoute?>? _boatRoutes;
    private List<List<Player>>? _targetGroups;

    /// <summary>A planned boat attack: where it lands, how far it sails, and the warship that would sink it.</summary>
    private sealed record BoatRoute(int Landing, int Length, Warship? Blocker);

    // Landing search for planned boats: the spot itself or a little way along that shore.
    private readonly int PlanSearchTiles;

    public NationAttacks(Game game, Player player, Rng rng, double triggerRatio, double reserveRatio, double expandRatio,
        NationAlliances alliances, NationEmojis emojis, NationWarships warships)
    {
        _game = game;
        _player = player;
        _rng = rng;
        _triggerRatio = triggerRatio;
        _reserveRatio = reserveRatio;
        _expandRatio = expandRatio;
        _alliances = alliances;
        _emojis = emojis;
        _warships = warships;
        PlanSearchTiles = game.Rules.A(300);
    }

    private bool Chance(int n) => _rng.RangeInt(0, n - 1) == 0;
    private int NextInt(int lo, int hi) => _rng.RangeInt(lo, hi - 1);
    private AiTiles.NearbyInfo Nearby => AiTiles.Nearby(_game, _player);
    private bool SharesBorder(Player other) => Nearby.Land.Contains(other.Id);
    private bool Hard => D >= Difficulty.Hard;

    // ------------------------------------------------------------------
    //  Every attack cycle
    // ------------------------------------------------------------------

    public void MaybeAttack()
    {
        var routes = new Dictionary<Player, BoatRoute?>();
        _boatRoutes = routes;
        _targetGroups = null;
        try
        {
            ChooseAttack();
        }
        finally
        {
            _boatRoutes = null;
            _targetGroups = null;
        }
        bool landFront = false;
        foreach (ushort id in Nearby.Land)
            if (!_player.IsAlliedWith(_game.GetPlayer(id)!))
                landFront = true;
        ClearBlockedLane(routes, landFront);
    }

    /// <summary>The very first move after the spawn phase: half our troops into empty land.</summary>
    public void FirstExpansion() =>
        _game.Submit(new AttackIntent(_player.Id, Game.Nobody, 0, Math.Max(1, _player.Troops / 2)));

    /// <summary>
    /// Every tick (Hard and Impossible): a boat is a beachhead. The moment one lands, attack
    /// its target by land from there, once.
    /// </summary>
    public void FollowUpLandings()
    {
        if (!LandsBeachheads)
            return;
        _followedLandings.RemoveAll(id => !_game.Attacks.Any(a => a.Id == id));
        foreach (var attack in _game.Attacks.ToList())
        {
            if (attack.Attacker != _player || attack.SourceTile < 0 || _followedLandings.Contains(attack.Id))
                continue;
            _followedLandings.Add(attack.Id);
            var target = _game.GetPlayer(attack.TargetId);
            if (!HoldsLandNextTo(attack.TargetId, attack.SourceTile))
                continue;
            SendLandAttack(target, null, target);
        }
    }

    private bool HoldsLandNextTo(ushort owner, int tile)
    {
        Span<int> nb = stackalloc int[4];
        int count = _game.LandNeighbours(tile, nb);
        for (int k = 0; k < count; k++)
            if (_game.Owner[nb[k]] == owner)
                return true;
        return false;
    }

    private void ChooseAttack()
    {
        var nearby = Nearby;
        var bordering = nearby.Players.OrderBy(p => p.Troops).ToList(); // weakest first (ties: id order)
        var friends = bordering.Where(p => _player.IsAlliedWith(p)).ToList();
        var enemies = bordering.Where(p => !_player.IsAlliedWith(p)).ToList();

        // Empty land (not fallout) next to us or across a river.
        if (nearby.EmptyLand && SendAttack(null))
            return;

        bool holdBoats = HoldsBoatsUnderAttack();
        if (enemies.Count == 0)
        {
            if (!holdBoats && Chance(5))
                AttackWithRandomBoat(enemies);
        }
        else
        {
            if (!holdBoats && Chance(10))
            {
                AttackWithRandomBoat(enemies);
                return;
            }
            _alliances.MaybeSendRequests(enemies);
        }
        AttackBestTarget(friends, enemies);
    }

    private void AttackWithRandomBoat(List<Player> enemies)
    {
        if (_game.BoatsAtSea(_player) >= Balance.MaxBoatsPerPlayer || _player.Coast.Count == 0)
            return;
        var shores = SortedCoast(_player);
        int src = shores[_rng.RangeInt(0, shores.Count - 1)];
        var found = FindRandomBoatTarget(src, enemies, highInterestOnly: true)
            ?? FindRandomBoatTarget(src, enemies, highInterestOnly: false);
        if (found is not { } f || Sail(f.Plan).Blocker is not null)
            return;

        var owner = _game.GetPlayer(_game.Owner[f.Tile]);
        if (LandsBeachheads)
        {
            SendBeachhead(owner, f.Tile);
            return;
        }
        double cap = owner is not null ? TroopSendCap() : TroopSendCapForExpansion();
        double troops = Math.Min(_player.Troops / 5.0, cap);
        if (troops < 1)
            return;
        if (owner is not null && IsAttackTooWeak(troops, owner))
            return;
        _game.Submit(new BoatIntent(_player.Id, f.Tile, 0, (long)troops));
    }

    /// <summary>A random spot within 150 tiles we could boat to: empty land or a tribe first, then anyone we may attack.</summary>
    private (int Tile, Game.BoatPlan Plan)? FindRandomBoatTarget(int from, List<Player> enemies, bool highInterestOnly)
    {
        var map = _game.Map;
        int x = from % map.Width, y = from / map.Width, range = _game.Rules.D(150);
        var unreachable = new HashSet<ushort>();
        for (int i = 0; i < 500; i++)
        {
            int rx = NextInt(x - range, x + range), ry = NextInt(y - range, y + range);
            if (!map.InBounds(rx, ry))
                continue;
            int tile = map.Index(rx, ry);
            if (!map.IsLand(tile))
                continue;
            ushort o = _game.Owner[tile];
            if (o == _player.Id || unreachable.Contains(o))
                continue;
            var owner = _game.GetPlayer(o);
            if (owner is not null && enemies.Contains(owner))
                continue; // boating someone we border looks silly
            if (owner is not null && owner.Troops > _player.Troops)
                continue; // don't spam boats at stronger players
            bool wanted = highInterestOnly
                ? owner is null || owner.IsBot
                : owner is null || !_player.IsAlliedWith(owner);
            if (!wanted)
                continue;
            if (_game.PlanBoat(_player, tile, PlanSearchTiles) is not { } plan)
            {
                if (owner is not null)
                    unreachable.Add(o);
                continue;
            }
            return (tile, plan);
        }
        return null;
    }

    // ------------------------------------------------------------------
    //  Picking a target
    // ------------------------------------------------------------------

    /// <summary>Friends and enemies are sorted by troops, weakest first.</summary>
    private void AttackBestTarget(List<Player> friends, List<Player> enemies)
    {
        // Hard and Impossible answer attacks right away, even below the reserve and trigger.
        if (Hard && Retaliate())
            return;

        // A walled-in tribe costs next to nothing to annex, so don't save up for it.
        AnnexWalledInBots();

        // Tribes holding our buildings are taken back before the troop checks.
        if (HasNeighbouringBotWithStructures() && AttackBots())
            return;

        if (!HasReserveRatioTroops())
            return;
        // Medium answers attacks without saving up to the trigger first.
        if (D == Difficulty.Medium && Retaliate())
            return;
        // Usually save up to the trigger.
        if (!HasTriggerRatioTroops() && !Chance(10))
            return;

        foreach (var strategy in Strategies(friends, enemies))
            if (strategy())
                return;
    }

    private List<Func<bool>> Strategies(List<Player> friends, List<Player> enemies)
    {
        bool HitBack() => Retaliate();
        bool Bots() => AttackBots();
        bool Assist() => AssistAllies();
        bool Betray() => MaybeBetrayAndAttack(friends, enemies);
        bool Nuked() => Nearby.BorderFallout && SendAttack(null);
        bool Traitor() => Pick(enemies, FindTraitor) is { } t && SendAttack(t);
        bool Victim() => Pick(enemies, FindVictim) is { } t && SendAttack(t);
        bool Juicy() => Pick(enemies, FindJuicyTarget) is { } t && SendAttack(t);
        bool VeryWeak() => Pick(enemies, FindVeryWeakEnemy) is { } t && SendAttack(t);
        bool Weakest() => Pick(enemies, pool => pool.FirstOrDefault(e => e.Troops < _player.Troops)) is { } t && SendAttack(t);
        bool Hated() => AttackHated();
        bool Island() => Pick(enemies, pool => pool.FirstOrDefault()) is null && FindNearestIslandEnemy() is { } t && SendAttack(t);
        bool Crown() => FindCrownTarget(enemies) is { } leader && ShouldAttack(leader) && SendLandAttack(leader, null, leader);

        // Easy gets the dumbest order, Impossible the smartest. (OpenFront's "AFK player" and
        // "donate to teammates" steps need disconnects and teams, which we don't have yet.)
        return D switch
        {
            Difficulty.Easy => new() { Nuked, Bots, HitBack, Assist, Betray, Hated, Weakest },
            Difficulty.Medium => new() { Bots, Nuked, Assist, Betray, Hated, Traitor, Crown, Weakest, Island },
            Difficulty.Hard => new() { Bots, Assist, Betray, Nuked, Traitor, Hated, VeryWeak, Juicy, Victim, Crown, Weakest, Island },
            _ => new() { Bots, VeryWeak, Betray, Assist, Victim, Crown, Traitor, Juicy, Nuked, Hated, Weakest, Island },
        };
    }

    /// <summary>
    /// Target groups, tried in order, each sorted weakest first. Hard and Impossible without a
    /// land front weigh enemies across the water (within boat range) alongside the bordering
    /// ones; Impossible with a land front turns to them once it finds nothing there.
    /// </summary>
    private List<List<Player>> TargetGroups(List<Player> enemies)
    {
        if (_targetGroups is not null)
            return _targetGroups;
        bool landFront = enemies.Any(SharesBorder);
        if (D == Difficulty.Impossible && landFront)
            _targetGroups = new() { enemies, OverseasTargets(enemies).OrderBy(p => p.Troops).ToList() };
        else if (Hard && !landFront)
            _targetGroups = new() { enemies.Concat(OverseasTargets(enemies)).OrderBy(p => p.Troops).ToList() };
        else
            _targetGroups = new() { enemies };
        return _targetGroups;
    }

    /// <summary>What <paramref name="find"/> picks among the targets we can reach right now, from the first group where it picks anyone (Easy doesn't check reach).</summary>
    private Player? Pick(List<Player> enemies, Func<List<Player>, Player?> find)
    {
        foreach (var group in TargetGroups(enemies))
        {
            var pool = group;
            while (find(pool) is { } target)
            {
                if (D == Difficulty.Easy || CanReach(target))
                    return target;
                pool = pool.Where(e => e != target).ToList();
            }
        }
        return null;
    }

    private bool HasNeighbouringBotWithStructures()
    {
        foreach (var n in Nearby.Players)
            if (n.IsBot && !_player.IsAlliedWith(n) && _game.BuildingsOf(n).Count > 0)
                return true;
        return false;
    }

    private bool HasReserveRatioTroops() => _player.Troops >= _player.MaxTroops * _reserveRatio;
    private bool HasTriggerRatioTroops() => _player.Troops >= _player.MaxTroops * _triggerRatio;

    /// <summary>Whoever attacks us hardest (not allies; a nation ignores tribes).</summary>
    private Player? FindIncomingAttacker()
    {
        long largest = 0;
        Player? attacker = null;
        foreach (var a in _game.Attacks)
        {
            if (a.TargetId != _player.Id || _player.IsAlliedWith(a.Attacker) || a.Attacker.IsBot)
                continue;
            if (a.Troops <= largest)
                continue;
            largest = a.Troops;
            attacker = a.Attacker;
        }
        return attacker;
    }

    private bool Retaliate()
    {
        if (FindIncomingAttacker() is not { } attacker)
            return false;
        if (Hard && SharesBorder(attacker))
            return SendRetaliation(attacker);
        return SendAttack(attacker, force: true);
    }

    /// <summary>Hard and Impossible keep their boats home while under attack, Medium only sometimes.</summary>
    private bool HoldsBoatsUnderAttack()
    {
        if (FindIncomingAttacker() is null)
            return false;
        return D switch
        {
            Difficulty.Easy => false,
            Difficulty.Medium => Chance(2),
            _ => true,
        };
    }

    /// <summary>
    /// Cancels the attacker's incoming troops. If we can also match its home army while
    /// keeping enough back for every other neighbour, pushes into its land with at least that
    /// much. Otherwise only cancels, as far as the reserve allows. Never dips below the
    /// expand reserve: troop growth nearly stops near zero.
    /// </summary>
    private bool SendRetaliation(Player attacker)
    {
        double incoming = 0, pressing = 0;
        foreach (var a in _game.Attacks)
        {
            if (a.Attacker == attacker && a.TargetId == _player.Id)
                incoming += a.Troops;
            if (a.Attacker == _player && a.TargetId == attacker.Id)
                pressing += a.Troops;
        }
        double troops = _player.Troops, max = _player.MaxTroops;
        double spare = Math.Min(troops - max * _expandRatio, NeighbourTroopCap(attacker));
        double aboveReserve = troops - max * _reserveRatio;
        double counter = incoming + Math.Max(0, attacker.Troops - pressing);
        double send = spare >= counter
            ? Math.Max(counter, Math.Min(aboveReserve, spare))
            : Math.Min(incoming, Math.Max(spare, aboveReserve));
        return SendLandAttack(attacker, _ => send, attacker);
    }

    /// <summary>Attacks a player our bomb just hit, while its troops are down (skips the trigger).</summary>
    public bool Strike(Player target)
    {
        if (_player.IsAlliedWith(target) || !target.IsAlive || !SharesBorder(target))
            return false;
        return SendLandAttack(target, null, target);
    }

    /// <summary>A bordering runaway leader. Hard and up always consider it; Medium only joins attacks already under way.</summary>
    private Player? FindCrownTarget(List<Player> enemies)
    {
        var leader = AiTiles.RunawayLeader(_game);
        if (leader is null || !enemies.Contains(leader) || !SharesBorder(leader))
            return null;
        if (Hard)
            return leader;
        return AiTiles.IncomingTroops(_game, leader) >= leader.Troops * 0.1 ? leader : null;
    }

    // ------------------------------------------------------------------
    //  Tribes
    // ------------------------------------------------------------------

    /// <summary>
    /// Attacks several neighbouring tribes at once (cheapest land first; any holding buildings
    /// before the rest), as many as the difficulty allows. Hard and up order them to wall
    /// other tribes in (see <see cref="BotAnnexation"/>).
    /// </summary>
    private bool AttackBots()
    {
        var bots = NeighbouringBots();
        if (bots.Count == 0)
            return false;
        _botAttackTroopsSent = 0;
        var ordered = AnnexesBots
            ? BotAnnexation.PlanAttacks(_game, _player, bots)
            : bots.OrderBy(b => (double)b.Troops / Math.Max(1, b.TileCount)).ToList();
        var chosen = ordered
            .OrderByDescending(b => _game.BuildingsOf(b).Count > 0)
            .Take(BotAttackParallelism())
            .ToList();
        foreach (var bot in chosen)
            SendAttack(bot);
        // Only skip the other strategies if troops really went at tribes (Hard and up only top up running attacks, so those count too).
        return _botAttackTroopsSent > 0
            || (AnnexesBots && _game.Attacks.Any(a => a.Attacker == _player && chosen.Any(b => b.Id == a.TargetId)));
    }

    private List<Player> NeighbouringBots() =>
        Nearby.Players.Where(n => n.IsBot && !_player.IsAlliedWith(n)).ToList();

    private bool AnnexesBots => Hard;

    /// <summary>A tribe walled in by us alone is annexed as soon as its land changes, so poke it with 1% of our troops.</summary>
    private void AnnexWalledInBots()
    {
        if (!AnnexesBots)
            return;
        var bots = NeighbouringBots();
        if (bots.Count == 0)
            return;
        long troops = _player.Troops / 100;
        if (troops < 1)
            return;
        foreach (var bot in BotAnnexation.WalledIn(_game, _player, bots))
            _game.Submit(new AttackIntent(_player.Id, bot.Id, 0, troops));
    }

    private int BotAttackParallelism() => D switch
    {
        Difficulty.Easy => 1,
        Difficulty.Medium => Chance(2) ? 1 : 2,
        Difficulty.Hard => 3,
        _ => 100,
    };

    // ------------------------------------------------------------------
    //  Other strategies
    // ------------------------------------------------------------------

    /// <summary>Joins an ally's marked target (if we like the ally enough), at a small cost to how much we like them.</summary>
    private bool AssistAllies()
    {
        foreach (var ally in _game.Players)
        {
            if (!_player.IsAlliedWith(ally))
                continue;
            var targets = ActiveTargets(ally);
            if (targets.Count == 0)
                continue;
            if (_game.RelationOf(_player, ally) < Relation.Friendly)
            {
                _emojis.Send(ally.Id, NationMoods.AssistRelationTooLow);
                continue;
            }
            foreach (var target in targets)
            {
                if (target == _player)
                {
                    _emojis.Send(ally.Id, NationMoods.AssistTargetMe);
                    continue;
                }
                if (_player.IsAlliedWith(target))
                {
                    _emojis.Send(ally.Id, NationMoods.AssistTargetAlly);
                    continue;
                }
                if (!SendAttack(target))
                    continue;
                _game.UpdateRelation(_player, ally, -2000);
                _emojis.Send(ally.Id, NationMoods.AssistAccept);
                return true;
            }
        }
        return false;
    }

    /// <summary>Players this one has marked as targets in the last 10 s, oldest first.</summary>
    private List<Player> ActiveTargets(Player marker)
    {
        var list = new List<Player>();
        foreach (var (tick, id) in marker.Targets)
            if (_game.Tick - tick < EmbargoRules.TargetDurationTicks && _game.GetPlayer(id) is { IsAlive: true } p)
                list.Add(p);
        return list;
    }

    /// <summary>A traitor not much stronger than us.</summary>
    private Player? FindTraitor(List<Player> enemies) =>
        enemies.FirstOrDefault(e => _game.IsTraitor(e) && e.Troops < _player.Troops * 1.2);

    private bool MaybeBetrayAndAttack(List<Player> friends, List<Player> enemies)
    {
        if (friends.Count == 0)
            return false;
        var juiciest = _alliances.FindJuiciestAlly(friends);
        foreach (var friend in friends)
            if (_alliances.MaybeBetray(friend, juiciest, friends, enemies))
                return SendAttack(friend, force: true);
        return false;
    }

    /// <summary>Someone not much stronger than us that others are already attacking with over half its troops.</summary>
    private Player? FindVictim(List<Player> enemies) =>
        enemies.FirstOrDefault(e => !(e.Troops > _player.Troops * 1.2) && AiTiles.IncomingTroops(_game, e) > e.Troops * 0.5);

    /// <summary>Someone down to under 15% of their troop cap (e.g. just bombed), not much stronger than us.</summary>
    private Player? FindVeryWeakEnemy(List<Player> enemies) =>
        enemies.FirstOrDefault(e => e.Troops < e.MaxTroops * 0.15 && e.Troops < _player.Troops * 1.2);

    /// <summary>The juiciest enemy we could plausibly beat (troops at most 75% of ours).</summary>
    private Player? FindJuicyTarget(List<Player> enemies) =>
        AiTiles.Juiciest(_game, enemies.Where(e => e.Troops <= _player.Troops * 0.75).ToList());

    /// <summary>The player we hate most, if not far stronger than us and within reach.</summary>
    private bool AttackHated()
    {
        var hated = _game.Players
            .Where(p => p != _player && p.IsAlive && _game.RelationOf(_player, p) == Relation.Hostile)
            .OrderBy(p => _game.RelationValue(_player, p))
            .ToList();
        foreach (var other in hated)
        {
            if (_player.IsAlliedWith(other) || other.Troops > _player.Troops * 3)
                continue;
            if (D != Difficulty.Easy && !CanReach(other))
                continue;
            return SendAttack(other);
        }
        return false;
    }

    /// <summary>The nearest player weaker than us that a boat can reach (a third of the time the second nearest).</summary>
    private Player? FindNearestIslandEnemy()
    {
        if (_game.BoatsAtSea(_player) >= Balance.MaxBoatsPerPlayer || _player.Coast.Count == 0)
            return null;
        int center = AiTiles.BoxCenter(_game, _player);
        var sorted = _game.Players
            .Where(p => p != _player && p.IsAlive && p.HasSpawned && !_player.IsAlliedWith(p) && p.Troops < _player.Troops)
            .OrderBy(p => AiTiles.Manhattan(_game, center, AiTiles.BoxCenter(_game, p)))
            .ToList();
        var reachable = new List<Player>();
        foreach (var p in sorted)
        {
            // Beachhead boats sail any distance, even past a warship: nothing nearer is left to attack.
            bool ok = LandsBeachheads ? SharesBorder(p) || BoatRouteTo(p) is not null : CanReach(p);
            if (!ok)
                continue;
            reachable.Add(p);
            if (reachable.Count >= 2)
                break;
        }
        if (reachable.Count == 0)
            return null;
        return reachable.Count >= 2 && Chance(3) ? reachable[1] : reachable[0];
    }

    // ------------------------------------------------------------------
    //  Sending troops
    // ------------------------------------------------------------------

    /// <summary>Attacks a player (or empty land, if null): by land if we border them, otherwise by boat.</summary>
    public bool SendAttack(Player? target, bool force = false)
    {
        if (!force && !ShouldAttack(target))
            return false;
        if (target is not null)
            return SharesBorder(target) ? SendLandAttack(target) : SendBoatAttack(target);
        return Nearby.BorderAnyEmptyLand ? SendLandAttack(null) : SendBoatToNearbyEmptyLand();
    }

    /// <summary>Lower difficulties often leave people alone (not traitors).</summary>
    private bool ShouldAttack(Player? other)
    {
        if (other is null || other.Kind != PlayerKind.Human || _game.IsTraitor(other))
            return true;
        if (D == Difficulty.Easy && NextInt(0, 4) != 0)
            return false;
        if (D == Difficulty.Medium && Chance(4))
            return false;
        return true;
    }

    /// <summary>Hard and up: not worth it if we'd send under a fifth of the target's troops (unless we're under attack).</summary>
    private bool IsAttackTooWeak(double troops, Player target)
    {
        if (AiTiles.UnderAttack(_game, _player))
            return false;
        return Hard && troops < target.Troops * 0.2;
    }

    /// <summary>
    /// Hard and up: the most we can send while keeping 75% (Impossible 90%) of our strongest
    /// non-allied, non-tribe neighbour's troops at home. Under attack, at least the incoming
    /// troops. <paramref name="ignore"/> is the player we're taking on on purpose.
    /// </summary>
    private double TroopSendCap(Player? ignore = null)
    {
        double cap = NeighbourTroopCap(ignore);
        long incoming = AiTiles.IncomingTroops(_game, _player);
        if (incoming > 0)
            cap = Math.Max(cap, incoming);
        return cap;
    }

    private double NeighbourTroopCap(Player? ignore)
    {
        double retain = D switch { Difficulty.Hard => 0.75, Difficulty.Impossible => 0.9, _ => -1 };
        if (retain < 0)
            return double.PositiveInfinity;
        long strongest = 0;
        foreach (var n in Nearby.Players)
            if (n != ignore && !_player.IsAlliedWith(n) && !n.IsBot && n.Troops > strongest)
                strongest = n.Troops;
        if (strongest == 0)
            return double.PositiveInfinity;
        return Math.Max(0, _player.Troops - Math.Ceiling(strongest * retain));
    }

    /// <summary>Like <see cref="TroopSendCap"/>, but never zero: empty land can't fight back, so it's slowed, not stopped.</summary>
    private double TroopSendCapForExpansion()
    {
        double cap = TroopSendCap();
        return cap > 0 ? cap : Math.Ceiling(_player.Troops * 0.05);
    }

    /// <summary>
    /// How many troops to send at a target (null = empty land), or null if not worth it.
    /// <paramref name="opponent"/> is a player we take on on purpose (retaliation, the
    /// runaway leader): it doesn't count as a threat to keep troops home for, and troops
    /// already attacking it count toward the too-weak check.
    /// </summary>
    private double? CalculateAttackTroops(Player? target, Func<double, double> nonBotTroops, Player? opponent)
    {
        bool botWithStructures = target is { IsBot: true } && _game.BuildingsOf(target).Count > 0;
        // Tribes holding our buildings are worth dipping into the reserve for.
        bool useReserve = target is not null && !botWithStructures;
        double keep = _player.MaxTroops * (useReserve ? _reserveRatio : _expandRatio);

        bool botAttack = target is { IsBot: true };
        double troops = botAttack
            ? BotAttackTroops(target!, _player.Troops - keep - _botAttackTroopsSent)
            : nonBotTroops(keep);
        troops = Math.Min(troops, target is not null ? TroopSendCap(opponent) : TroopSendCapForExpansion());
        if (troops < 1)
            return null;

        double pileOn = target is not null && target == opponent ? AiTiles.IncomingTroops(_game, target) : 0;
        if (target is not null && IsAttackTooWeak(troops + pileOn, target))
            return null;
        if (target is not null)
            _emojis.MaybeSendAttackEmoji(target);
        if (botAttack)
            _botAttackTroopsSent += troops;
        return troops;
    }

    private bool SendLandAttack(Player? target, Func<double, double>? nonBotTroops = null, Player? opponent = null)
    {
        var troops = CalculateAttackTroops(target, nonBotTroops ?? (keep => _player.Troops - keep), opponent);
        if (troops is not { } t)
            return false;
        _game.Submit(new AttackIntent(_player.Id, target?.Id ?? Game.Nobody, 0, (long)t));
        return true;
    }

    private bool SendBoatAttack(Player target)
    {
        if (BoatRouteTo(target) is not { } route)
            return false;
        // A beachhead boat is cheap enough to risk past a warship, one at a time.
        if (route.Blocker is not null && (!LandsBeachheads || _game.BoatsAtSea(_player) > 0))
            return false;
        if (LandsBeachheads)
            return SendBeachhead(target, route.Landing);
        var troops = CalculateAttackTroops(target, _ => _player.Troops / 5.0, null);
        if (troops is not { } t)
            return false;
        _game.Submit(new BoatIntent(_player.Id, route.Landing, 0, (long)t));
        return true;
    }

    /// <summary>Hard and up boat a token force, then attack by land the moment it lands (see <see cref="FollowUpLandings"/>).</summary>
    private bool LandsBeachheads => Hard;

    /// <summary>A 1% boat, if the land attack following its landing would be worth making now.</summary>
    private bool SendBeachhead(Player? target, int landing)
    {
        var troops = CalculateAttackTroops(target, keep => _player.Troops - keep, target);
        if (troops is not { } t)
            return false;
        long boatTroops = Math.Max(1, _player.Troops / 100);
        // Only the boat leaves now: the land attack is budgeted once it lands.
        if (target is { IsBot: true })
            _botAttackTroopsSent += boatTroops - t;
        _game.Submit(new BoatIntent(_player.Id, landing, 0, boatTroops));
        return true;
    }

    /// <summary>Empty land just across a narrow strait (5 tiles), with a fifth of the troops.</summary>
    private bool SendBoatToNearbyEmptyLand()
    {
        if (_game.BoatsAtSea(_player) >= Balance.MaxBoatsPerPlayer)
            return false;
        var map = _game.Map;
        int w = map.Width, reach = _game.Rules.D(5);
        var coast = SortedCoast(_player);
        ReadOnlySpan<(int Dx, int Dy)> dirs = stackalloc (int, int)[] { (0, -1), (0, 1), (-1, 0), (1, 0) };
        for (int i = 0; i < coast.Count; i += 10)
        {
            int x = coast[i] % w, y = coast[i] / w;
            foreach (var (dx, dy) in dirs)
            {
                if (!map.InBounds(x + dx, y + dy) || !map.IsWater(map.Index(x + dx, y + dy)))
                    continue;
                int fx = x + dx * reach, fy = y + dy * reach;
                if (!map.InBounds(fx, fy))
                    continue;
                int far = map.Index(fx, fy);
                if (!map.IsLand(far) || _game.Owner[far] != Game.Nobody || _game.Fallout[far])
                    continue;
                if (_game.PlanBoat(_player, far, PlanSearchTiles) is not { } plan || Sail(plan).Blocker is not null)
                    continue;
                if (LandsBeachheads)
                    return SendBeachhead(null, far);
                double troops = Math.Min(_player.Troops / 5.0, TroopSendCapForExpansion());
                if (troops < 1)
                    return false;
                _game.Submit(new BoatIntent(_player.Id, far, 0, (long)troops));
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Tribes: Easy throws everything above the reserve; Medium sends 4x the tribe's troops (or
    /// all it can spare, if that's at least 2x); Hard and up top up what already attacks it to
    /// what keeps the attack at its cheapest loss rate until the tribe collapses.
    /// </summary>
    private double BotAttackTroops(Player bot, double available)
    {
        switch (D)
        {
            case Difficulty.Easy:
                return available;
            case Difficulty.Medium:
                double troops = bot.Troops * 4.0;
                if (troops > available)
                    troops = available < bot.Troops * 2.0 ? 0 : available;
                return troops;
            default:
                double needed = BotAttackTroopsNeeded(bot) * 1.3 - OurStackAgainst(bot);
                return needed <= available ? needed : 0;
        }
    }

    private double BotAttackTroopsNeeded(Player bot)
    {
        double stack = Math.Max(1, bot.Troops / AttackRules.CheapestTroopRatio);
        int tiles = Math.Max(0, bot.TileCount - _game.Rules.ConquerBelowTiles);
        return Math.Max(stack, tiles * BotTileLoss(bot, stack));
    }

    /// <summary>Troops lost per tile at that stack: Impossible reads the ground on a 6x6 grid over the tribe's land, Hard assumes highlands.</summary>
    private double BotTileLoss(Player bot, double stack)
    {
        double sum = 0;
        int n = 0;
        if (D == Difficulty.Impossible)
        {
            var box = AiTiles.Box(_game, bot);
            if (box.MaxX >= 0)
            {
                int width = box.MaxX - box.MinX + 1, height = box.MaxY - box.MinY + 1;
                for (int i = 0; i < 6; i++)
                    for (int j = 0; j < 6; j++)
                    {
                        int tile = _game.Map.Index(box.MinX + (2 * i + 1) * width / 12, box.MinY + (2 * j + 1) * height / 12);
                        if (_game.Owner[tile] != bot.Id)
                            continue;
                        sum += _game.EstimateTileLoss(_player, bot, _game.Map.Terrain[tile], _game.CoverTenths[tile], stack);
                        n++;
                    }
            }
        }
        if (n == 0)
            return _game.EstimateTileLoss(_player, bot, TerrainType.Highlands, 10, stack);
        return sum / n;
    }

    /// <summary>Our land attacks on the target (they merge with the next we send), less its attacks on us (they cancel out).</summary>
    private double OurStackAgainst(Player target)
    {
        double troops = 0;
        foreach (var a in _game.Attacks)
        {
            if (a.Attacker == _player && a.TargetId == target.Id && a.SourceTile < 0)
                troops += a.Troops;
            if (a.Attacker == target && a.TargetId == _player.Id)
                troops -= a.Troops;
        }
        return troops;
    }

    // ------------------------------------------------------------------
    //  Boats and warships in the way
    // ------------------------------------------------------------------

    /// <summary>By land, or by a boat that passes no enemy warship and doesn't sail too far.</summary>
    private bool CanReach(Player target)
    {
        if (SharesBorder(target))
            return true;
        return BoatRouteTo(target) is { Blocker: null } route && route.Length <= MaxBoatRoute;
    }

    private BoatRoute? BoatRouteTo(Player target)
    {
        if (_boatRoutes is not null && _boatRoutes.TryGetValue(target, out var known))
            return known;
        var route = PlanBoatRoute(target);
        _boatRoutes?.Add(target, route);
        return route;
    }

    /// <summary>The direct crossing, or if a warship blocks it, the shortest safe one landing elsewhere on their coast.</summary>
    private BoatRoute? PlanBoatRoute(Player target)
    {
        if (_game.BoatsAtSea(_player) >= Balance.MaxBoatsPerPlayer)
            return null;
        var ours = SortedCoast(_player);
        var theirs = SortedCoast(target);
        if (ours.Count == 0 || theirs.Count == 0)
            return null;
        int closest = ClosestTo(ours, theirs);
        var warships = HostileWarships();
        var direct = RouteTo(closest, warships);
        if (direct is { Blocker: null })
            return direct;
        BoatRoute? best = null;
        foreach (int landing in OtherLandings(ours, theirs, closest, warships))
            if (RouteTo(landing, warships) is { Blocker: null } route && (best is null || route.Length < best.Length))
                best = route;
        return best ?? direct;
    }

    /// <summary>Their coast tile closest to ours (both sampled down to about 100 tiles).</summary>
    private int ClosestTo(List<int> ours, List<int> theirs)
    {
        int ourStep = Math.Max(1, ours.Count / 100), theirStep = Math.Max(1, theirs.Count / 100);
        int best = theirs[0], bestDist = int.MaxValue;
        for (int i = 0; i < theirs.Count; i += theirStep)
            for (int j = 0; j < ours.Count; j += ourStep)
            {
                int d = AiTiles.Manhattan(_game, theirs[i], ours[j]);
                if (d < bestDist)
                    (best, bestDist) = (theirs[i], d);
            }
        return best;
    }

    private BoatRoute? RouteTo(int tile, List<Warship> warships)
    {
        if (_game.PlanBoat(_player, tile, PlanSearchTiles) is not { } plan)
            return null;
        var (length, blocker) = Sail(plan, warships);
        return new BoatRoute(plan.Landing, length, blocker);
    }

    /// <summary>Landing spots on their coast apart from the one tried, clear of enemy warships, nearest us first: Medium 1, Hard 2, Impossible 4.</summary>
    private List<int> OtherLandings(List<int> ours, List<int> theirs, int tried, List<Warship> warships)
    {
        int count = D switch { Difficulty.Easy => 0, Difficulty.Medium => 1, Difficulty.Hard => 2, _ => 4 };
        if (count == 0)
            return new();
        long range = DangerRange;
        int ourStep = Math.Max(1, ours.Count / 32), theirStep = Math.Max(1, theirs.Count / 32);
        var candidates = new List<(int Tile, int Dist)>();
        for (int i = 0; i < theirs.Count; i += theirStep)
        {
            int tile = theirs[i];
            if (warships.Any(w => AiTiles.DistSq(_game, w.Tile, tile) <= range * range))
                continue;
            int dist = int.MaxValue;
            for (int j = 0; j < ours.Count; j += ourStep)
                dist = Math.Min(dist, AiTiles.Manhattan(_game, ours[j], tile));
            candidates.Add((tile, dist));
        }
        int apart = _game.Rules.D(50);
        var picked = new List<int> { tried };
        foreach (var (tile, _) in candidates.OrderBy(c => c.Dist))
        {
            if (picked.Count > count)
                break;
            if (picked.Any(p => AiTiles.Manhattan(_game, p, tile) < apart))
                continue;
            picked.Add(tile);
        }
        picked.RemoveAt(0);
        return picked;
    }

    private List<Warship> HostileWarships() =>
        _game.Warships.Where(w => w.Owner != _player && w.Alive && !_player.IsAlliedWith(w.Owner)).ToList();

    /// <summary>A warship's range, plus what it may move while our boat approaches.</summary>
    private int DangerRange => _game.Rules.WarshipTargetRange + _game.Rules.D(20);

    /// <summary>
    /// How far a boat sails, and the enemy warship that would sink it: one fires once the boat
    /// comes in range, so a boat that lands before the shell can catch it is safe. Easy doesn't
    /// look, Medium only checks the two shores, Hard and up follow the route (its rough version,
    /// one point per 8 tiles: plenty next to a warship's 130-tile reach, and far cheaper).
    /// </summary>
    private (int Length, Warship? Blocker) Sail(Game.BoatPlan plan, List<Warship>? warships = null)
    {
        int direct = AiTiles.Manhattan(_game, plan.Source, plan.Landing);
        if (D == Difficulty.Easy)
            return (direct, null);
        warships ??= HostileWarships();
        if (warships.Count == 0)
            return (direct, null);
        bool follow = D != Difficulty.Medium && direct > _game.Rules.D(20);
        int[]? path = follow ? _game.Waterways.RoughRoute(plan.LaunchWater, plan.LandingWater, MaxBoatRoute) : null;
        if (follow && path is null)
            return (Math.Max(direct, MaxBoatRoute + 1), null); // further than we'd sail to attack anyway
        int[] route = path ?? new[] { plan.Source, plan.Landing };
        // Tiles sailed to reach each point of the route (without a route, assume a warship can fire as soon as we launch).
        var sailed = new int[route.Length];
        for (int i = 1; i < route.Length && path is not null; i++)
            sailed[i] = sailed[i - 1] + Chebyshev(route[i - 1], route[i]);
        int length = path is null ? direct : sailed[^1];
        int range = DangerRange;
        int w = _game.Map.Width;
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        foreach (int t in route)
        {
            minX = Math.Min(minX, t % w); maxX = Math.Max(maxX, t % w);
            minY = Math.Min(minY, t / w); maxY = Math.Max(maxY, t / w);
        }
        double boatSpeed = Math.Max(1, _game.Rules.BoatSpeedPerTick) / 1000.0;
        double closing = _game.Rules.WarshipShellTilesPerTick + boatSpeed; // shell and boat close in this fast
        int spare = _game.Rules.D(10); // the warship's reaction time
        foreach (var ship in warships)
        {
            int wx = ship.Tile % w, wy = ship.Tile / w;
            if (wx < minX - range || wx > maxX + range || wy < minY - range || wy > maxY + range)
                continue;
            for (int i = 0; i < route.Length; i++)
            {
                if (AiTiles.DistSq(_game, ship.Tile, route[i]) > (long)range * range)
                    continue;
                double ticksLeft = (length - sailed[i]) / boatSpeed;
                if (closing * ticksLeft >= AiTiles.Manhattan(_game, ship.Tile, route[i]) - spare)
                    return (length, ship);
                break;
            }
        }
        return (length, null);
    }

    private int Chebyshev(int a, int b)
    {
        int w = _game.Map.Width;
        return Math.Max(Math.Abs(a % w - b % w), Math.Abs(a / w - b / w));
    }

    /// <summary>Hard and Impossible don't sail further than this to attack someone.</summary>
    private int MaxBoatRoute => D switch
    {
        Difficulty.Hard => _game.Rules.D(300),
        Difficulty.Impossible => _game.Rules.D(500),
        _ => int.MaxValue,
    };

    /// <summary>Enemies across the water (not bordering us) whose land is within boat range of ours; whether a boat gets there is left to <see cref="CanReach"/>.</summary>
    private List<Player> OverseasTargets(List<Player> enemies)
    {
        if (_game.BoatsAtSea(_player) >= Balance.MaxBoatsPerPlayer)
            return new();
        int range = MaxBoatRoute;
        return _game.Players
            .Where(p => p != _player && p.IsAlive && p.HasSpawned && !p.IsBot && !_player.IsAlliedWith(p)
                && !enemies.Contains(p) && p.Troops < _player.Troops && AiTiles.BoxGap(_game, _player, p) <= range)
            .ToList();
    }

    /// <summary>Hard and Impossible send warships at whatever blocks the juiciest target they couldn't reach (any distance without a land front).</summary>
    private void ClearBlockedLane(Dictionary<Player, BoatRoute?> routes, bool landFront)
    {
        if (!Hard)
            return;
        int maxRoute = MaxBoatRoute;
        var blocked = new Dictionary<Player, Warship>();
        foreach (var (target, route) in routes)
            if (route?.Blocker is { } blocker && (!landFront || route.Length <= maxRoute) && target.Troops < _player.Troops)
                blocked[target] = blocker;
        if (AiTiles.Juiciest(_game, blocked.Keys.ToList()) is { } juiciest)
            _warships.ClearSeaLane(blocked[juiciest]);
    }

    private static List<int> SortedCoast(Player p)
    {
        var list = new List<int>(p.Coast);
        list.Sort(); // HashSet order isn't guaranteed; keep every machine identical
        return list;
    }
}
