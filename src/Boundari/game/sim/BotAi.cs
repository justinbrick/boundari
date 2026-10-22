using System;
using System.Collections.Generic;
using Boundari.Core;

namespace Boundari.Sim;

/// <summary>An AI player: looked at once per tick, and only ever submits intents.</summary>
internal interface IPlayerAi
{
    void Think();
}

/// <summary>
/// A bot: OpenFront's weak filler player (its "tribes"). Every few seconds it either goes
/// after a nearby traitor (a third of the time), grabs empty land, hits back at whoever
/// attacks it hardest, or picks a random neighbour, sparing people and nations half the
/// time. Neighbours just across a river are reached by boat. It never builds, and tears
/// down anything it captures. Same behaviour and numbers as OpenFront's tribes, in our own code.
/// </summary>
internal sealed class BotAi : IPlayerAi
{
    private readonly Game _game;
    private readonly Player _player;
    private readonly Rng _rng;

    // Each bot gets its own rhythm and nerve, rolled once (OpenFront's ranges).
    private readonly int _attackRate;     // ticks between decisions (40-79)
    private readonly int _attackTick;     // offset within that, so bots don't all act at once
    private readonly int _triggerPercent; // troops (of max) before attacking anyone: 50-59%
    private readonly int _reservePercent; // troops (of max) kept home when attacking a player: 30-39%
    private readonly int _expandPercent;  // kept home when grabbing empty land: 10-19%

    private bool _started;
    private bool _emptyLandNearby = true; // until a look finds none left

    // OpenFront: a boat grabs empty land at most this many tiles out to sea, with a fifth of the troops.
    private readonly int BoatReach; // tiles across a strait (5 on the reference map)
    private const int BoatTroopsPercent = 20;
    private const int CoastSampleStep = 10;

    public BotAi(Game game, Player player, Rng rng)
    {
        _game = game;
        _player = player;
        _rng = rng;
        BoatReach = game.Rules.D(5);
        _attackRate = rng.RangeInt(40, 79);
        _attackTick = rng.RangeInt(0, _attackRate - 1);
        _triggerPercent = rng.RangeInt(50, 59);
        _reservePercent = rng.RangeInt(30, 39);
        _expandPercent = rng.RangeInt(10, 19);
    }

    public void Think()
    {
        if (_game.Tick % _attackRate != _attackTick || !_player.HasSpawned || _player.TileCount == 0)
            return;

        if (!_started) // first move of the match: straight into empty land
        {
            _started = true;
            SendAttack(null);
            return;
        }

        DemolishCaptured();

        // A traitor nearby: a third of the time, make them pay.
        if (NeighbourTraitor() is { } traitor && Chance(3) && SendAttack(traitor))
            return;

        if (_emptyLandNearby)
        {
            if (AiTiles.Nearby(_game, _player).EmptyLand)
            {
                if (SendAttack(null))
                    return;
            }
            else
                _emptyLandNearby = false;
        }
        AttackRandomTarget();
    }

    private bool Chance(int n) => _rng.RangeInt(0, n - 1) == 0;

    /// <summary>A random non-allied traitor nearby, if any.</summary>
    private Player? NeighbourTraitor()
    {
        var traitors = Neighbours().FindAll(n => !_player.IsAlliedWith(n) && _game.IsTraitor(n));
        return traitors.Count == 0 ? null : traitors[_rng.RangeInt(0, traitors.Count - 1)];
    }

    /// <summary>Bots don't keep buildings: they tear down what they capture, one at a time.</summary>
    private void DemolishCaptured()
    {
        if (_game.Tick < _player.NextDemolishTick)
            return;
        foreach (var b in _game.BuildingsOf(_player))
            if (b.DemolishTick < 0)
            {
                _game.Submit(new DemolishIntent(_player.Id, b.Tile));
                return;
            }
    }

    private void AttackRandomTarget()
    {
        if (_player.Troops * 100 < _player.MaxTroops * _triggerPercent)
            return; // still saving up

        // Hit back at the biggest attack coming in.
        Attack? biggest = null;
        foreach (var a in _game.Attacks)
            if (a.TargetId == _player.Id && a.Attacker.IsAlive && (biggest is null || a.Troops > biggest.Troops))
                biggest = a;
        if (biggest is not null && SendAttack(biggest.Attacker))
            return;

        // Another traitor check (OpenFront asks twice).
        if (NeighbourTraitor() is { } traitor && Chance(3) && SendAttack(traitor))
            return;

        // Otherwise a random neighbour. People and nations are spared half the time.
        var neighbours = Neighbours();
        for (int i = neighbours.Count - 1; i > 0; i--) // shuffle
        {
            int j = _rng.RangeInt(0, i);
            (neighbours[i], neighbours[j]) = (neighbours[j], neighbours[i]);
        }
        foreach (var n in neighbours)
        {
            if (_player.IsAlliedWith(n))
                continue;
            if (!n.IsBot && _rng.RangeInt(0, 1) == 0)
                continue;
            if (SendAttack(n))
                return;
        }
    }

    /// <summary>
    /// Sends everything above the reserve against a player (or empty land, if null). Returns
    /// false if there's nothing to send or no way to get there.
    /// </summary>
    private bool SendAttack(Player? target)
    {
        if (target is null && !AiTiles.Nearby(_game, _player).BorderAnyEmptyLand)
            return BoatToEmptyLand();
        if (target is not null && _player.IsAlliedWith(target))
            return false;
        if (target is not null && !AiTiles.Nearby(_game, _player).Land.Contains(target.Id))
            return BoatAt(target); // just across a river

        // Against empty land, or a bot holding buildings, keep only the small expand reserve.
        bool expand = target is null || (target.IsBot && _game.BuildingsOf(target).Count > 0);
        long keep = _player.MaxTroops * (expand ? _expandPercent : _reservePercent) / 100;
        long troops = _player.Troops - keep;
        if (troops < 1)
            return false;
        _game.Submit(new AttackIntent(_player.Id, target?.Id ?? Game.Nobody, 0, troops));
        return true;
    }

    /// <summary>A player we don't border by land: a boat with a fifth of our troops at their nearest coast.</summary>
    private bool BoatAt(Player target)
    {
        if (!_game.CanSendBoat(_player) || _player.Troops / 5 < 1
            || _player.Coast.Count == 0 || target.Coast.Count == 0)
            return false;
        var ours = new List<int>(_player.Coast);
        var theirs = new List<int>(target.Coast);
        ours.Sort();
        theirs.Sort(); // HashSet order isn't guaranteed; keep every machine identical
        int ourStep = Math.Max(1, ours.Count / 50), theirStep = Math.Max(1, theirs.Count / 50);
        int landing = -1, best = int.MaxValue;
        for (int i = 0; i < theirs.Count; i += theirStep)
            for (int j = 0; j < ours.Count; j += ourStep)
            {
                int d = AiTiles.Manhattan(_game, theirs[i], ours[j]);
                if (d < best)
                    (landing, best) = (theirs[i], d);
            }
        if (landing < 0 || _game.PlanBoat(_player, landing, _game.Rules.A(300)) is null)
            return false;
        _game.Submit(new BoatIntent(_player.Id, landing, BoatTroopsPercent));
        return true;
    }

    /// <summary>Empty land just across a narrow strait: send a boat with a fifth of the troops.</summary>
    private bool BoatToEmptyLand()
    {
        if (!_game.CanSendBoat(_player) || _player.Troops / 5 < 1)
            return false;
        var map = _game.Map;
        int w = map.Width;
        var coast = new List<int>(_player.Coast);
        coast.Sort(); // HashSet order isn't guaranteed; keep every machine identical
        ReadOnlySpan<(int Dx, int Dy)> dirs = stackalloc (int, int)[] { (0, -1), (0, 1), (-1, 0), (1, 0) };
        for (int i = 0; i < coast.Count; i += CoastSampleStep)
        {
            int x = coast[i] % w, y = coast[i] / w;
            foreach (var (dx, dy) in dirs)
            {
                if (!map.InBounds(x + dx, y + dy) || !map.IsWater(map.Index(x + dx, y + dy)))
                    continue;
                int fx = x + dx * BoatReach, fy = y + dy * BoatReach;
                if (!map.InBounds(fx, fy))
                    continue;
                int far = map.Index(fx, fy);
                if (!map.IsLand(far) || _game.Owner[far] != Game.Nobody)
                    continue;
                _game.Submit(new BoatIntent(_player.Id, far, BoatTroopsPercent));
                return true;
            }
        }
        return false;
    }

    /// <summary>Players next to our land or just across a river, in id order.</summary>
    private List<Player> Neighbours() => new(AiTiles.Nearby(_game, _player).Players);
}

/// <summary>Made-up bot names, like OpenFront's: a people or dynasty plus a kind of state.</summary>
internal static class BotNames
{
    private static readonly string[] Prefixes =
    {
        "Akkadian", "Assyrian", "Babylonian", "Hittite", "Phoenician", "Minoan", "Etruscan", "Scythian",
        "Thracian", "Dacian", "Illyrian", "Roman", "Byzantine", "Persian", "Parthian", "Macedonian",
        "Carthaginian", "Ming", "Tang", "Han", "Mauryan", "Mughal", "Nubian", "Aksumite", "Songhai",
        "Malian", "Ashanti", "Zulu", "Berber", "Umayyad", "Abbasid", "Fatimid", "Seljuk", "Ottoman",
        "Frankish", "Visigothic", "Viking", "Norman", "Saxon", "Celtic", "Gaulish", "Carolingian",
        "Tudor", "Habsburg", "Romanov", "Bourbon", "Hanseatic", "Venetian", "Genoese", "Lombard",
        "Aztec", "Mayan", "Incan", "Olmec", "Khmer", "Mongol", "Tibetan", "Manchu", "Polynesian",
        "Hawaiian", "Maori", "Armenian", "Georgian", "Kurdish", "Circassian", "Tatar", "Cossack",
        "Spartan", "Athenian", "Moorish", "Basque", "Breton", "Frisian", "Livonian", "Teutonic",
    };

    private static readonly string[] Suffixes =
    {
        "Empire", "Dynasty", "Kingdom", "Sultanate", "Confederation", "Union", "Republic", "Caliphate",
        "Dominion", "Realm", "State", "Federation", "Territory", "Commonwealth", "League", "Duchy",
        "Province", "Protectorate", "Colony", "Free State", "Canton", "Nation", "Grand Duchy", "Council",
        "Order", "Horde", "Clan", "Brotherhood", "Tribe", "Principality", "Khanate", "March", "Emirate",
        "Shogunate", "Electorate", "Margraviate", "Satrapy", "Theocracy", "Junta", "Settlement",
    };

    public static string Random(Rng rng) =>
        $"{Prefixes[rng.RangeInt(0, Prefixes.Length - 1)]} {Suffixes[rng.RangeInt(0, Suffixes.Length - 1)]}";
}
