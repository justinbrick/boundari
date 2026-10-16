using System;
using System.Collections.Generic;
using Boundari.Core;

namespace Boundari.Sim;

/// <summary>
/// A nation's diplomacy, following OpenFront's nation alliance rules: which requests it
/// accepts, when it asks its neighbours, and when it betrays an ally. Decisions depend on
/// difficulty, relations (how much it likes the other player), relative strength, and how
/// far into the game we are. Like the rest of the AI it only submits intents.
/// </summary>
internal sealed class NationAlliances
{
    private readonly Game _game;
    private readonly Player _player;
    private readonly Rng _rng;
    private Difficulty D => _game.Difficulty;

    public NationAlliances(Game game, Player player, Rng rng)
    {
        _game = game;
        _player = player;
        _rng = rng;
    }

    /// <summary>OpenFront's chance(n): true one time in n.</summary>
    private bool Chance(int n) => _rng.RangeInt(0, n - 1) == 0;

    /// <summary>OpenFront's nextInt(lo, hi): lo inclusive, hi exclusive.</summary>
    private int NextInt(int lo, int hi) => _rng.RangeInt(lo, hi - 1);

    /// <summary>Answers every request waiting for us: yes (by asking back) or no.</summary>
    public void HandleRequests()
    {
        foreach (var request in _game.AllianceRequests)
        {
            if (request.To != _player)
                continue;
            _game.Submit(Decide(request.From, isResponse: true)
                ? new AllianceIntent(_player.Id, request.From.Id)
                : new RejectAllianceIntent(_player.Id, request.From.Id));
        }
    }

    /// <summary>
    /// OpenFront: stops trading with players it's hostile toward, and resumes once it feels
    /// neutral again (Hard: only once friendly; Impossible: never).
    /// </summary>
    public void HandleEmbargoes()
    {
        foreach (var other in _game.Players)
        {
            if (other == _player || !other.HasSpawned || !other.IsAlive)
                continue;
            var relation = _game.RelationOf(_player, other);
            bool embargo = _game.HasEmbargo(_player, other);
            if (relation == Relation.Hostile && !embargo)
                _game.Submit(new EmbargoIntent(_player.Id, other.Id, true));
            else if (embargo && ((relation >= Relation.Neutral && D != Difficulty.Hard && D != Difficulty.Impossible)
                || (relation >= Relation.Friendly && D != Difficulty.Impossible)))
                _game.Submit(new EmbargoIntent(_player.Id, other.Id, false));
        }
    }

    /// <summary>Agrees to renew an alliance its ally asked to renew, if it would still ally with them (OpenFront).</summary>
    public void HandleRenewals()
    {
        foreach (var alliance in _game.Alliances)
        {
            if (alliance.A != _player && alliance.B != _player)
                continue;
            var ally = alliance.Other(_player);
            if (alliance.AgreedToRenew(ally) && !alliance.AgreedToRenew(_player) && Decide(ally, isResponse: true))
                _game.Submit(new AllianceIntent(_player.Id, ally.Id));
        }
    }

    /// <summary>Now and then asks a bordering non-ally (never a tribe, except on Easy) to ally.</summary>
    public void MaybeSendRequests(List<Player> borderingEnemies)
    {
        foreach (var enemy in borderingEnemies)
        {
            if (!Chance(30))
                continue;
            if (enemy.IsBot && D != Difficulty.Easy)
                continue;
            if (_game.CanRequestAlliance(_player, enemy) && Decide(enemy, isResponse: false))
                _game.Submit(new AllianceIntent(_player.Id, enemy.Id));
        }
    }

    /// <summary>
    /// Breaks the alliance with this ally if it's worth it (OpenFront's rules, minus the
    /// Hard/Impossible "juiciest ally" one): far weaker than us, a weak traitor, or our only
    /// neighbour and much weaker. Returns true if it did.
    /// </summary>
    public bool MaybeBetray(Player ally, int borderingPlayers)
    {
        if (!_player.IsAlliedWith(ally))
            return false;
        bool betray =
            ((D == Difficulty.Easy || D == Difficulty.Medium)
                && !(D == Difficulty.Easy && ally.Kind == PlayerKind.Human)
                && _player.Troops >= ally.Troops * 10)
            || (D != Difficulty.Easy && _game.IsTraitor(ally) && ally.Troops * 10 < _player.Troops * 12)
            || (D != Difficulty.Easy && borderingPlayers == 1 && ally.Troops * 3 < _player.Troops);
        if (betray)
            _game.Submit(new BreakAllianceIntent(_player.Id, ally.Id));
        return betray;
    }

    /// <summary>Whether to ally with this player (accepting their request, or asking them).</summary>
    private bool Decide(Player other, bool isResponse)
    {
        // Dumb nations sometimes just flip a coin.
        if (IsConfused())
            return Chance(2);
        // Nearly always turn down traitors.
        if (_game.IsTraitor(other) && NextInt(0, 100) >= 10)
            return false;
        if (HasTooManyAlliances(other) || IsRunawayLeader(other))
            return false;
        // Better a friend than an enemy: a threat gets a yes.
        if (IsThreat(other))
            return true;
        if (_game.RelationOf(_player, other) < Relation.Neutral)
            return false;
        if (IsFriendly(other))
            return true;
        if (HaveEnoughAlliances(other))
            return false;
        if (IsEarlyGame())
            return true;
        return IsSimilarlyStrong(other);
    }

    private bool IsConfused() => D switch
    {
        Difficulty.Easy => Chance(10),
        Difficulty.Medium => Chance(20),
        Difficulty.Hard => Chance(40),
        _ => false,
    };

    private bool HasTooManyAlliances(Player other)
    {
        if (D < Difficulty.Hard)
            return false;
        int people = 0;
        foreach (var p in _game.Players)
            if (!p.IsBot)
                people++;
        return D == Difficulty.Hard
            ? other.AllianceCount * 2 >= people
            : other.AllianceCount * 4 >= people;
    }

    /// <summary>The non-tribe player with the most land, if it has this many times the runner-up's (Medium 3x, Hard 2x, Impossible 1.5x).</summary>
    private bool IsRunawayLeader(Player other)
    {
        int factorTenths = D switch { Difficulty.Medium => 30, Difficulty.Hard => 20, Difficulty.Impossible => 15, _ => 0 };
        if (factorTenths == 0)
            return false;
        Player? leader = null, runnerUp = null;
        foreach (var p in _game.Players)
        {
            if (p.IsBot || !p.IsAlive)
                continue;
            if (leader is null || p.TileCount > leader.TileCount)
            {
                runnerUp = leader;
                leader = p;
            }
            else if (runnerUp is null || p.TileCount > runnerUp.TileCount)
                runnerUp = p;
        }
        return leader == other && runnerUp is not null && leader.TileCount * 10L >= runnerUp.TileCount * (long)factorTenths;
    }

    private bool IsThreat(Player other) => D switch
    {
        Difficulty.Easy => false,
        Difficulty.Medium => other.Troops * 10 > _player.Troops * 25,
        Difficulty.Hard => other.Troops > _player.Troops && other.MaxTroops > _player.MaxTroops * 2,
        _ => other.Troops * 10 > _player.Troops * 15
             || (other.Troops > _player.Troops && other.MaxTroops * 10 > _player.MaxTroops * 15)
             || (other.Troops > _player.Troops && other.TileCount * 10L > _player.TileCount * 15L),
    };

    private bool IsFriendly(Player other)
    {
        if (_game.RelationOf(_player, other) != Relation.Friendly)
            return false;
        return D switch
        {
            Difficulty.Hard => NextInt(0, 100) >= 17,
            Difficulty.Impossible => NextInt(0, 100) >= 33,
            _ => true,
        };
    }

    private bool HaveEnoughAlliances(Player other)
    {
        switch (D)
        {
            case Difficulty.Easy:
                return false;
            case Difficulty.Medium:
                return _player.AllianceCount >= NextInt(4, 6);
            default:
                // Hard and up: never ally with all our neighbours.
                int bordering = 0, friends = 0;
                bool touchesOther = false;
                foreach (var n in _game.BorderingPlayers(_player))
                {
                    if (n.IsBot)
                        continue;
                    bordering++;
                    if (_player.IsAlliedWith(n))
                        friends++;
                    touchesOther |= n == other;
                }
                if (bordering >= 2 && touchesOther)
                    return bordering <= friends + 1;
                return D == Difficulty.Hard
                    ? _player.AllianceCount >= NextInt(3, 5)
                    : _player.AllianceCount >= NextInt(2, 4);
        }
    }

    private bool IsEarlyGame()
    {
        int t = _game.Tick - _game.SpawnPhaseTicks;
        return D switch
        {
            Difficulty.Easy => t < 3000 && NextInt(0, 100) >= 10,
            Difficulty.Medium => t < 1800 && NextInt(0, 100) >= 30,
            Difficulty.Hard => t < 1800 && NextInt(0, 100) >= 50,
            _ => t < 600 && NextInt(0, 100) >= 70,
        };
    }

    private bool IsSimilarlyStrong(Player other)
    {
        var (t0, t1, l0, l1) = D switch
        {
            Difficulty.Easy => (60, 70, 70, 80),
            Difficulty.Medium => (70, 80, 80, 90),
            Difficulty.Hard => (75, 85, 85, 95),
            _ => (80, 90, 90, 100),
        };
        long mine = _player.Troops + _game.TroopsAttacking(_player);
        long theirs = other.Troops + _game.TroopsAttacking(other);
        long troopThreshold = mine * NextInt(t0, t1) / 100;
        long tileThreshold = (long)_player.TileCount * NextInt(l0, l1) / 100;
        return theirs > troopThreshold || (other.TileCount > tileThreshold && theirs * 2 > mine);
    }
}
