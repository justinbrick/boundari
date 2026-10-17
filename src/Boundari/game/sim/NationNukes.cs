using System;
using System.Collections.Generic;
using Boundari.Core;

namespace Boundari.Sim;

/// <summary>
/// When and where nations bomb, following OpenFront's nation nuke and MIRV rules:
///   - Targets, in order: whoever attacks them hardest, whoever has a bomb flying at them,
///     a strong player they're already fighting, and a runaway leader. Never bots.
///   - Aim: the spot inside the target's land with the most buildings in the blast and the
///     least wasted on water or empty land, not near a SAM, not where a bomb is already
///     landing, preferably close to their own silo, and never touching their own land.
///   - A Fat Man if they can afford it, else a MOAB; the "felt" price of each rises after
///     every launch while they'd rather save up for a Tsar Bomba.
///   - Tsar Bombas go at whoever is about to win, has far more cities than anyone, or has
///     one flying at them, if enough warheads would get past that player's SAMs.
/// </summary>
internal sealed class NationNukes
{
    private readonly Game _game;
    private readonly Player _player;
    private readonly Rng _rng;
    private readonly Difficulty _difficulty;

    private readonly List<(int Tick, int Tile, NukeType Type)> _recent = new();
    private long _moabFeltCost, _fatManFeltCost;

    // OpenFront's tuning.
    private const double StrikeMinTroopRatio = 0.5;
    private const double CrownNearWinShare = 0.5;
    private const int FullCoverageValue = 50_000;
    private readonly int StrikeBorderMargin;   // 10 tiles on the reference map
    private const int RecentNukeMemoryTicks = 600;
    private const int MirvCooldownTicks = 300;
    private const int MirvCoverageSamples = 40;

    private readonly NationEmojis _emojis;

    // Hard and up: bombs sent to soften a neighbour up, followed by a land attack the moment they land.
    private readonly List<(Player Target, int Tile, int Tick, Nuke? Bomb)> _strikes = new();
    private const int StrikeSpawnTicks = 10; // how long to look for the bomb after ordering it

    public NationNukes(Game game, Player player, Rng rng, NationEmojis emojis)
    {
        _game = game;
        _player = player;
        _rng = rng;
        _emojis = emojis;
        _difficulty = game.Difficulty;
        StrikeBorderMargin = game.Rules.D(10);
    }

    private bool Chance(int n) => _rng.RangeInt(0, n - 1) == 0;

    private static double MinCoverage(NukeType type) => type == NukeType.Moab ? 0.25 : 0.4;

    // ------------------------------------------------------------------
    //  MOABs and Fat Men
    // ------------------------------------------------------------------

    public void MaybeSendNuke()
    {
        var silos = _game.BuildingsOf(_player, BuildingType.Silo);
        if (silos.Count == 0 || _game.NukesLocked)
            return;
        var (target, strike) = ChooseTarget();
        if (target is null || target.IsBot || _player.IsAlliedWith(target) || !ShouldAttack(target))
            return;

        bool saving = IsSavingForTsarBomba();
        if (saving && !IsHittingUs(target))
            return; // Hard & up: only bomb back at players hitting them while saving up

        var structures = _game.BuildingsOf(target);
        var candidates = new List<int>();
        var seen = new HashSet<int>();
        foreach (int t in AiTiles.RandomTerritory(_game, target, _rng, _difficulty == Difficulty.Impossible ? 30 : 10))
            if (seen.Add(t))
                candidates.Add(t);
        foreach (var b in structures)
            if (seen.Add(b.Tile))
                candidates.Add(b.Tile);

        _recent.RemoveAll(r => _game.Tick - r.Tick > RecentNukeMemoryTicks);
        foreach (var type in TypesToTry(saving))
        {
            int tile = BestTile(type, target, candidates, silos, structures, strike);
            if (tile >= 0)
            {
                Send(type, tile, target);
                if (strike && _difficulty >= Difficulty.Hard)
                    _strikes.Add((target, tile, _game.Tick, null));
                return;
            }
        }
    }

    /// <summary>Whom to bomb, and whether it's a "strike" to soften them up for our attack.</summary>
    private (Player? Target, bool Strike) ChooseTarget()
    {
        var target = BestTarget();
        if (target is null)
            return (null, false);
        bool strike = _difficulty switch
        {
            Difficulty.Easy => false,
            Difficulty.Medium => TroopsAttacking(target) > 0,
            _ => target.IsBot ? false : Borders(target),
        };
        return (target, strike);
    }

    private Player? BestTarget()
    {
        var alive = AlivePlayers();
        if (_difficulty >= Difficulty.Hard && alive.Count == 2)
            return alive[0] == _player ? alive[1] : alive[0];

        if (BiggestIncomingAttacker() is { } attacker)
            return attacker;
        if (IncomingNukeSender() is { } bomber)
            return bomber;

        long landLeft = Math.Max(1, _game.Map.LandTileCount - _game.FalloutTiles);
        var crown = Crown();
        if (_difficulty == Difficulty.Impossible && crown is not null && crown != _player && crown.TileCount > landLeft * CrownNearWinShare)
            return crown;

        if (StrikeTarget() is { } strike)
            return strike;

        return CrownTarget();
    }

    /// <summary>A player not much weaker than us: Medium picks one it's already attacking, Hard and up the strongest neighbour.</summary>
    private Player? StrikeTarget()
    {
        bool Candidate(Player? p) => p is not null && p != _player && !p.IsBot && p.IsAlive && p.Troops >= _player.Troops * StrikeMinTroopRatio;
        switch (_difficulty)
        {
            case Difficulty.Easy:
                return null;
            case Difficulty.Medium:
                foreach (var a in _game.Attacks)
                    if (a.Attacker == _player && Candidate(_game.GetPlayer(a.TargetId)))
                        return _game.GetPlayer(a.TargetId);
                return null;
            default:
                Player? best = null;
                foreach (var n in AiTiles.Neighbours(_game, _player))
                    if (Candidate(n) && (best is null || n.Troops > best.Troops))
                        best = n;
                return best;
        }
    }

    /// <summary>The land leader, if they hold over half the land or have run away from the pack.</summary>
    private Player? CrownTarget()
    {
        var first = Crown();
        if (first is null || first == _player)
            return null;
        long landLeft = Math.Max(1, _game.Map.LandTileCount - _game.FalloutTiles);
        double firstShare = (double)first.TileCount / landLeft;
        if (firstShare > CrownNearWinShare)
            return first;
        if (RunawayLeader() != first)
            return null;
        double threshold = _difficulty switch
        {
            Difficulty.Easy => double.MaxValue,
            Difficulty.Medium => 0.3,
            Difficulty.Hard => 0.2,
            _ => 0.1,
        };
        if (!Borders(first))
            threshold *= 2; // its neighbours react first
        return firstShare - (double)_player.TileCount / landLeft > threshold ? first : null;
    }

    private Player? Crown()
    {
        Player? best = null;
        foreach (var p in AlivePlayers())
            if (best is null || p.TileCount > best.TileCount)
                best = p;
        return best;
    }

    /// <summary>The biggest non-bot, if it has several times the land of the runner-up.</summary>
    private Player? RunawayLeader()
    {
        double factor = _difficulty switch { Difficulty.Easy => 0, Difficulty.Medium => 3, Difficulty.Hard => 2, _ => 1.5 };
        if (factor == 0)
            return null;
        Player? leader = null, runnerUp = null;
        foreach (var p in AlivePlayers())
        {
            if (p.IsBot)
                continue;
            if (leader is null || p.TileCount > leader.TileCount)
                (runnerUp, leader) = (leader, p);
            else if (runnerUp is null || p.TileCount > runnerUp.TileCount)
                runnerUp = p;
        }
        return leader is not null && runnerUp is not null && leader.TileCount >= runnerUp.TileCount * factor ? leader : null;
    }

    /// <summary>Lower difficulties sometimes leave people alone (OpenFront).</summary>
    private bool ShouldAttack(Player target)
    {
        if (target.Kind != PlayerKind.Human)
            return true;
        return _difficulty switch
        {
            Difficulty.Easy => _rng.RangeInt(0, 3) == 0,
            Difficulty.Medium => _rng.RangeInt(0, 3) != 0,
            _ => true,
        };
    }

    private List<NukeType> TypesToTry(bool moabOnly)
    {
        var types = new List<NukeType>();
        if (!moabOnly && _player.Gold >= FeltCost(NukeType.FatMan))
            types.Add(NukeType.FatMan);
        if ((types.Count == 0 || _difficulty >= Difficulty.Hard) && _player.Gold >= FeltCost(NukeType.Moab))
            types.Add(NukeType.Moab);
        return types;
    }

    private int BestTile(NukeType type, Player target, List<int> candidates, List<Building> silos, List<Building> structures, bool strike)
    {
        int outer = _game.Rules.Blast(type).Outer;
        var scored = new List<(int Tile, double Score)>();
        foreach (int tile in candidates)
        {
            if (!BoxIsValid(tile, outer, target) || !BoxIsValid(tile, outer / 2, target))
                continue;
            if (_game.ReadySilo(_player, tile) is null)
                continue;
            double coverage = 0;
            if (_difficulty != Difficulty.Easy)
            {
                if (AlreadyLandingNear(tile, type))
                    continue;
                coverage = EnemyCoverage(tile, type);
                if (coverage < MinCoverage(type))
                    continue;
            }
            if (_difficulty >= Difficulty.Hard && InEnemySamRange(tile))
                continue;
            double value = TileValue(tile, silos, structures, type);
            if (_difficulty == Difficulty.Impossible && !strike ? value <= 0 : value < 0)
                continue;
            scored.Add((tile, value + coverage * FullCoverageValue));
        }
        // Stable sort, best first: ties keep the earlier candidate.
        var order = new List<int>(scored.Count);
        for (int i = 0; i < scored.Count; i++)
            order.Add(i);
        order.Sort((a, b) => scored[b].Score != scored[a].Score ? scored[b].Score.CompareTo(scored[a].Score) : a.CompareTo(b));
        int margin = strike ? StrikeBorderMargin : 0;
        foreach (int i in order)
            if (!BlastHitsOwnLand(scored[i].Tile, type, margin))
                return scored[i].Tile;
        return -1;
    }

    /// <summary>The square around a spot: Easy/Medium want it all the target's (deep inside their land), Hard and up just not friendly.</summary>
    private bool BoxIsValid(int center, int radius, Player target)
    {
        var map = _game.Map;
        int w = map.Width, cx = center % w, cy = center / w;
        bool Ok(int x, int y)
        {
            if (!map.InBounds(x, y))
                return true;
            ushort owner = _game.Owner[map.Index(x, y)];
            if (owner == target.Id)
                return true;
            return _difficulty >= Difficulty.Hard && owner != _player.Id;
        }
        for (int x = cx - radius; x <= cx + radius; x++)
            if (!Ok(x, cy - radius) || !Ok(x, cy + radius))
                return false;
        for (int y = cy - radius + 1; y < cy + radius; y++)
            if (!Ok(cx - radius, y) || !Ok(cx + radius, y))
                return false;
        return true;
    }

    /// <summary>Worth of the buildings in the blast, less a little for distance from our silo and a lot for re-bombing a spot.</summary>
    private double TileValue(int tile, List<Building> silos, List<Building> structures, NukeType type)
    {
        int outer = _game.Rules.Blast(type).Outer;
        double value = 0;
        foreach (var b in structures)
            if (AiTiles.DistSq(_game, b.Tile, tile) <= (long)outer * outer)
                value += b.Level * b.Type switch
                {
                    BuildingType.City => 25_000,
                    BuildingType.Defense => 5_000,
                    BuildingType.Silo => 50_000,
                    BuildingType.Port or BuildingType.Factory => 15_000,
                    _ => 0,
                };

        if (_difficulty == Difficulty.Medium)
            foreach (var b in structures)
                if (b.Type == BuildingType.Sam && AiTiles.DistSq(_game, b.Tile, tile) <= 50 * 50)
                    return -1; // a SAM right there would just shoot it down

        long nearest = long.MaxValue;
        foreach (var s in silos)
            nearest = Math.Min(nearest, AiTiles.DistSq(_game, s.Tile, tile));
        double penalty = Math.Sqrt(nearest) * 30;
        value = Math.Max(value * 0.2, value - penalty);

        foreach (var (_, recentTile, recentType) in _recent)
        {
            int inner = _game.Rules.Blast(recentType).Inner;
            if (AiTiles.DistSq(_game, tile, recentTile) <= (long)inner * inner)
                value -= 1_000_000;
        }
        return value;
    }

    /// <summary>Share of the blast area (inner circle counted double) that is enemy land.</summary>
    private double EnemyCoverage(int center, NukeType type)
    {
        var (inner, outer) = _game.Rules.Blast(type);
        var map = _game.Map;
        int step = Math.Max(1, outer / 10), cx = center % map.Width, cy = center / map.Width;
        double total = 0, enemy = 0;
        for (int dy = -outer; dy <= outer; dy += step)
            for (int dx = -outer; dx <= outer; dx += step)
            {
                int d2 = dx * dx + dy * dy;
                if (d2 > outer * outer)
                    continue;
                int weight = d2 <= inner * inner ? 2 : 1;
                total += weight;
                if (!map.InBounds(cx + dx, cy + dy))
                    continue;
                ushort owner = _game.Owner[map.Index(cx + dx, cy + dy)];
                if (owner != Game.Nobody && owner != _player.Id)
                    enemy += weight;
            }
        return total > 0 ? enemy / total : 0;
    }

    private bool BlastHitsOwnLand(int center, NukeType type, int margin)
    {
        var map = _game.Map;
        int outer = _game.Rules.Blast(type).Outer + margin, cx = center % map.Width, cy = center / map.Width;
        for (int dy = -outer; dy <= outer; dy++)
            for (int dx = -outer; dx <= outer; dx++)
                if (dx * dx + dy * dy <= outer * outer && map.InBounds(cx + dx, cy + dy)
                    && _game.Owner[map.Index(cx + dx, cy + dy)] == _player.Id)
                    return true;
        return false;
    }

    private bool AlreadyLandingNear(int tile, NukeType type)
    {
        int inner = _game.Rules.Blast(type).Inner;
        foreach (var n in _game.Nukes)
        {
            if (n.Gone || n.Type == NukeType.TsarBomba)
                continue;
            long sum = inner + _game.Rules.Blast(n.Type).Inner;
            if (AiTiles.DistSq(_game, tile, n.To) <= sum * sum)
                return true;
        }
        return false;
    }

    private bool InEnemySamRange(int tile)
    {
        foreach (var b in _game.Buildings)
            if (b.Type == BuildingType.Sam && b.Owner != _player.Id
                && AiTiles.DistSq(_game, b.Tile, tile) <= (long)_game.Rules.SamRange(b.Level) * _game.Rules.SamRange(b.Level))
                return true;
        return false;
    }

    /// <summary>Every tick (Hard and up): attacks a strike's target the moment its bomb lands (not if it was shot down).</summary>
    public void FollowUpStrikes(NationAttacks attacks)
    {
        for (int i = 0; i < _strikes.Count; i++)
        {
            var (target, tile, tick, bomb) = _strikes[i];
            if (bomb is null)
            {
                foreach (var n in _game.Nukes)
                    if (n.Owner == _player && n.To == tile && !n.Gone)
                        bomb = n;
                if (bomb is null)
                {
                    if (_game.Tick - tick > StrikeSpawnTicks)
                        _strikes.RemoveAt(i--);
                    continue;
                }
                _strikes[i] = (target, tile, tick, bomb);
            }
            if (!bomb.Gone)
                continue;
            _strikes.RemoveAt(i--);
            if (!bomb.TargetedBySam) // intercepted bombs leave the target's troops alone
                attacks.Strike(target);
        }
    }

    private void Send(NukeType type, int tile, Player target)
    {
        _recent.Add((_game.Tick, tile, type));
        long felt = FeltCost(type);
        if (type == NukeType.Moab)
            _moabFeltCost = felt * 150 / 100; // dearer each time, to save up for a Tsar Bomba
        else
            _fatManFeltCost = felt * 125 / 100;
        _game.Submit(new NukeIntent(_player.Id, type, tile));
        _emojis.MaybeSend(target, NationMoods.Nuke);
    }

    /// <summary>The price a nation acts on: the real one, or an inflated one while a Tsar Bomba is worth saving for.</summary>
    private long FeltCost(NukeType type)
    {
        long real = _game.NukeCost(type);
        if (PaysRealPrice())
            return real;
        bool forgets = _difficulty >= Difficulty.Hard; // Hard & up let the price ease back after a while
        if (!forgets && !IsTsarBombaWorthSavingFor(_game, _player, _rng))
            return real;
        long felt = type == NukeType.Moab ? _moabFeltCost : _fatManFeltCost;
        return Math.Max(real, felt);
    }

    private bool PaysRealPrice()
    {
        if (AlivePlayers().Count == 2)
            return true;
        if (_player.Gold > TsarBombaSaveUp())
            return true;
        return _difficulty >= Difficulty.Hard && IsUnderHeavyAttack();
    }

    private long TsarBombaSaveUp() => _game.NukeCost(NukeType.TsarBomba) + _game.NukeCost(NukeType.FatMan);

    private bool IsSavingForTsarBomba() =>
        _difficulty >= Difficulty.Hard && !PaysRealPrice() && IsTsarBombaWorthSavingFor(_game, _player, _rng);

    private bool IsUnderHeavyAttack()
    {
        long incoming = 0;
        foreach (var a in _game.Attacks)
            if (a.TargetId == _player.Id)
                incoming += a.Troops;
        return incoming >= _player.Troops;
    }

    private bool IsHittingUs(Player other)
    {
        foreach (var a in _game.Attacks)
            if (a.TargetId == _player.Id && a.Attacker == other)
                return true;
        foreach (var n in _game.Nukes)
            if (n.Owner == other && !n.Gone && (_game.Owner[n.To] == _player.Id || n.TargetPlayer == _player.Id))
                return true;
        return false;
    }

    private Player? BiggestIncomingAttacker()
    {
        Attack? biggest = null;
        foreach (var a in _game.Attacks)
            if (a.TargetId == _player.Id && !a.Attacker.IsBot && a.Attacker.IsAlive && (biggest is null || a.Troops > biggest.Troops))
                biggest = a;
        return biggest?.Attacker;
    }

    private Player? IncomingNukeSender()
    {
        foreach (var n in _game.Nukes)
            if (!n.Gone && n.Type is NukeType.Moab or NukeType.FatMan && n.Owner != _player && _game.Owner[n.To] == _player.Id)
                return n.Owner;
        return null;
    }

    private long TroopsAttacking(Player target)
    {
        long troops = 0;
        foreach (var a in _game.Attacks)
            if (a.Attacker == _player && a.TargetId == target.Id)
                troops += a.Troops;
        return troops;
    }

    private bool Borders(Player other)
    {
        foreach (var n in AiTiles.Neighbours(_game, _player))
            if (n == other)
                return true;
        return false;
    }

    private List<Player> AlivePlayers()
    {
        var list = new List<Player>();
        foreach (var p in _game.Players)
            if (p.HasSpawned && p.TileCount > 0)
                list.Add(p);
        return list;
    }

    // ------------------------------------------------------------------
    //  Tsar Bombas
    // ------------------------------------------------------------------

    private int HesitationOdds => _difficulty switch { Difficulty.Easy => 2, Difficulty.Medium => 4, Difficulty.Hard => 8, _ => 16 };
    private int VictoryDenialPercent => _difficulty switch { Difficulty.Easy => 75, Difficulty.Medium => 65, Difficulty.Hard => 55, _ => 40 };
    private double SteamrollGap => _difficulty switch { Difficulty.Easy => 2, Difficulty.Medium => 1.5, Difficulty.Hard => 1.25, _ => 1.15 };
    private int SteamrollMinCities => _difficulty switch { Difficulty.Easy => 20, Difficulty.Medium or Difficulty.Hard => 10, _ => 8 };

    /// <summary>Returns true if it launched one.</summary>
    public bool ConsiderTsarBomba()
    {
        if (_game.NukesLocked || _game.BuildingsOf(_player, BuildingType.Silo).Count == 0)
            return false;
        if (_player.Gold < _game.NukeCost(NukeType.TsarBomba))
            return false;
        if (_rng.RangeInt(0, HesitationOdds - 1) == 0)
            return false;

        foreach (var target in new[] { CounterTsarTarget(), VictoryDenialTarget(), SteamrollTarget(), RivalTarget() })
            if (target is not null && WorthTsarBomba(target))
            {
                SendTsarBomba(target);
                return true;
            }
        return false;
    }

    private List<Player> TsarTargets()
    {
        var list = new List<Player>();
        foreach (var p in AlivePlayers())
            if (p != _player && !p.IsBot)
                list.Add(p);
        return list;
    }

    private Player? CounterTsarTarget()
    {
        Player? best = null;
        foreach (var n in _game.Nukes)
            if (n.Type == NukeType.TsarBomba && !n.Gone && n.TargetPlayer == _player.Id && n.Owner != _player && !n.Owner.IsBot
                && (best is null || n.Owner.TileCount > best.TileCount))
                best = n.Owner;
        return best;
    }

    private Player? VictoryDenialTarget()
    {
        long threshold = (long)_game.Map.LandTileCount * VictoryDenialPercent;
        Player? best = null;
        foreach (var p in TsarTargets())
            if ((long)p.TileCount * 100 >= threshold && (best is null || p.TileCount > best.TileCount))
                best = p;
        return best;
    }

    private Player? SteamrollTarget()
    {
        var byCities = AlivePlayers().ConvertAll(p => (P: p, Cities: _game.CountBuildings(p, BuildingType.City)));
        if (byCities.Count < 2)
            return null;
        byCities.Sort((a, b) => b.Cities != a.Cities ? b.Cities.CompareTo(a.Cities) : a.P.Id.CompareTo(b.P.Id));
        var top = byCities[0];
        if (top.Cities <= SteamrollMinCities || top.Cities < byCities[1].Cities * SteamrollGap)
            return null;
        return top.P != _player && !top.P.IsBot ? top.P : null;
    }

    /// <summary>Hard and up: the biggest neighbour at least half our size.</summary>
    private Player? RivalTarget()
    {
        if (_difficulty < Difficulty.Hard)
            return null;
        Player? best = null;
        foreach (var p in AiTiles.Neighbours(_game, _player))
            if (!p.IsBot && p.TileCount >= _player.TileCount / 2 && (best is null || p.TileCount > best.TileCount))
                best = p;
        return best;
    }

    private bool WorthTsarBomba(Player target)
    {
        if (_player.IsAlliedWith(target))
            return false;
        if (_game.NationMirvTargets.TryGetValue(target.Id, out int last) && _game.Tick - last < MirvCooldownTicks)
            return false;
        return GetsThrough(_game, _player, target, _rng);
    }

    private void SendTsarBomba(Player target)
    {
        int center = TerritoryCenter(target);
        _emojis.MaybeSendAttackEmoji(target);
        if (center < 0 || !_game.CanLaunch(_player, NukeType.TsarBomba, center))
            return;
        _game.Submit(new NukeIntent(_player.Id, NukeType.TsarBomba, center));
        _game.NationMirvTargets[target.Id] = _game.Tick;
        _emojis.Send(Game.Nobody, NationMoods.Nuke);
        // OpenFront: a nation on the receiving end sometimes cries for help to everyone.
        if (target.IsNation && Chance(8) && _game.CanSendEmoji(target, Game.Nobody))
            _game.Submit(new EmojiIntent(target.Id, Game.Nobody, NationMoods.Overwhelmed[_rng.RangeInt(0, NationMoods.Overwhelmed.Length - 1)]));
    }

    /// <summary>The middle of their territory's bounding box, or their nearest border tile if they don't own it.</summary>
    private int TerritoryCenter(Player target)
    {
        var map = _game.Map;
        int w = map.Width, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        foreach (int t in target.Border)
        {
            minX = Math.Min(minX, t % w); maxX = Math.Max(maxX, t % w);
            minY = Math.Min(minY, t / w); maxY = Math.Max(maxY, t / w);
        }
        if (maxX < 0)
            return -1;
        int center = map.Index((minX + maxX) / 2, (minY + maxY) / 2);
        if (_game.Owner[center] == target.Id)
            return center;
        int best = -1;
        long bestDist = long.MaxValue;
        var border = new List<int>(target.Border);
        border.Sort();
        foreach (int t in border)
        {
            long d = AiTiles.DistSq(_game, t, center);
            if (d < bestDist)
                (best, bestDist) = (t, d);
        }
        return best;
    }

    /// <summary>Whether enough of a Tsar Bomba's warheads would get past the target's SAMs (Easy doesn't check).</summary>
    internal static bool GetsThrough(Game game, Player attacker, Player target, Rng rng)
    {
        double min = game.Difficulty switch { Difficulty.Easy => 0, Difficulty.Medium => 0.1, Difficulty.Hard => 0.2, _ => 0.25 };
        return min == 0 || LeakShare(game, attacker, target, rng) >= min;
    }

    /// <summary>Estimated share of warheads that SAMs (each shooting its level in warheads) would miss.</summary>
    private static double LeakShare(Game game, Player attacker, Player target, Rng rng)
    {
        double warheads = Math.Min(NukeRules.MirvWarheads, Math.Max(1, target.TileCount / (double)game.Rules.A(2000))); // 2000 reference tiles a warhead
        var samples = AiTiles.RandomTerritory(game, target, rng, MirvCoverageSamples);
        if (samples.Count == 0)
            return 1;
        var sams = new List<Building>();
        foreach (var b in game.Buildings)
            if (b.Type == BuildingType.Sam && b.Owner != attacker.Id && b.IsReady(game.Tick))
                sams.Add(b);
        sams.Sort((a, b) => a.Tile.CompareTo(b.Tile));
        double perSample = warheads / samples.Count;
        var inRange = new Dictionary<Building, double>();
        int covered = 0;
        foreach (int tile in samples)
        {
            bool any = false;
            foreach (var sam in sams)
            {
                long r = game.Rules.SamRange(sam.Level);
                if (AiTiles.DistSq(game, tile, sam.Tile) > r * r)
                    continue;
                inRange[sam] = inRange.GetValueOrDefault(sam) + perSample;
                any = true;
            }
            if (any)
                covered++;
        }
        double shots = 0;
        foreach (var (sam, n) in inRange)
            shots += Math.Min(sam.Level, n);
        return 1 - Math.Min(covered * perSample, shots) / warheads;
    }

    /// <summary>Whether a Tsar Bomba would get through at the land leader or the city leader (who nations aim them at).</summary>
    internal static bool IsTsarBombaWorthSavingFor(Game game, Player player, Rng rng)
    {
        Player? landLeader = null, cityLeader = null;
        int leaderCities = -1;
        foreach (var p in game.Players)
        {
            if (p == player || p.IsBot || p.TileCount == 0)
                continue;
            if (landLeader is null || p.TileCount > landLeader.TileCount)
                landLeader = p;
            int cities = game.CountBuildings(p, BuildingType.City);
            if (cities > leaderCities)
                (cityLeader, leaderCities) = (p, cities);
        }
        if (landLeader is not null && GetsThrough(game, player, landLeader, rng))
            return true;
        return cityLeader is not null && cityLeader != landLeader && GetsThrough(game, player, cityLeader, rng);
    }
}

/// <summary>Small map helpers shared by the AIs.</summary>
internal static partial class AiTiles
{
    public static long DistSq(Game game, int a, int b)
    {
        int w = game.Map.Width;
        long dx = a % w - b % w, dy = a / w - b / w;
        return dx * dx + dy * dy;
    }

    /// <summary>Up to <paramref name="count"/> random tiles the player owns, sampled inside their bounding box.</summary>
    public static List<int> RandomTerritory(Game game, Player player, Rng rng, int count)
    {
        var map = game.Map;
        int w = map.Width, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        foreach (int t in player.Border)
        {
            minX = Math.Min(minX, t % w); maxX = Math.Max(maxX, t % w);
            minY = Math.Min(minY, t / w); maxY = Math.Max(maxY, t / w);
        }
        foreach (int t in player.Coast)
        {
            minX = Math.Min(minX, t % w); maxX = Math.Max(maxX, t % w);
            minY = Math.Min(minY, t / w); maxY = Math.Max(maxY, t / w);
        }
        var tiles = new List<int>(count);
        if (maxX < 0)
            return tiles;
        for (int i = 0; i < count; i++)
            for (int tries = 0; tries < 100; tries++)
            {
                int t = map.Index(rng.RangeInt(minX, maxX), rng.RangeInt(minY, maxY));
                if (game.Owner[t] == player.Id)
                {
                    tiles.Add(t);
                    break;
                }
            }
        return tiles;
    }

    /// <summary>Players touching the player's land, in id order.</summary>
    public static List<Player> Neighbours(Game game, Player player)
    {
        var ids = new SortedSet<ushort>();
        Span<int> nb = stackalloc int[4];
        foreach (int t in player.Border)
        {
            int count = game.LandNeighbours(t, nb);
            for (int k = 0; k < count; k++)
            {
                ushort o = game.Owner[nb[k]];
                if (o != Game.Nobody && o != player.Id)
                    ids.Add(o);
            }
        }
        var list = new List<Player>(ids.Count);
        foreach (ushort id in ids)
            list.Add(game.GetPlayer(id)!);
        return list;
    }
}
