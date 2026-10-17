using System;
using System.Collections.Generic;
using System.Linq;
using Boundari.Core;

namespace Boundari.Sim;

/// <summary>
/// A nation's warships, following OpenFront's NationWarshipBehavior:
///   - Now and then (1 in 50 attack cycles) builds a first warship near one of its harbors.
///   - Hits back with a warship where someone sank its troop boat, captured its trade ship,
///     or is sailing a boat at its coast (Medium 15% of the time, Hard 50%, Impossible 80%;
///     Easy never), or sends one it already has.
///   - Hard and Impossible clear warships blocking their boats with a squad, and the three
///     richest nations answer anyone filling the sea with over 10 warships.
/// </summary>
internal sealed class NationWarships
{
    private const int MaxWarships = 10;

    private readonly Game _game;
    private readonly Player _player;
    private readonly Rng _rng;
    private readonly NationEmojis _emojis;
    private Difficulty D => _game.Difficulty;

    // Our boats and trade ships at sea, and boats coming at our coast (and those we've dealt with).
    private readonly List<Boat> _ourBoats = new();
    private readonly List<TradeShip> _ourTradeShips = new();
    private readonly List<Boat> _incoming = new();
    private readonly HashSet<Boat> _dealtWith = new();

    public NationWarships(Game game, Player player, Rng rng, NationEmojis emojis)
    {
        _game = game;
        _player = player;
        _rng = rng;
        _emojis = emojis;
    }

    private long WarshipCost => _game.BuildingCost(_player, BuildingType.Warship);
    private List<Warship> Ours => _game.Warships.Where(w => w.Owner == _player && w.Alive).ToList();
    private List<Building> Ports => _game.BuildingsOf(_player, BuildingType.Port).Where(p => p.IsReady(_game.Tick)).ToList();

    public void MaybeSpawnWarship()
    {
        if (!Chance(50))
            return;
        var ports = Ports;
        if (ports.Count == 0 || Ours.Count > 0 || _player.Gold <= WarshipCost)
            return;
        var port = ports[_rng.RangeInt(0, ports.Count - 1)];
        if (SpawnTile(port.Tile, _game.Rules.D(250)) is { } tile && _game.CanBuild(_player, BuildingType.Warship, tile))
            _game.Submit(new BuildIntent(_player.Id, BuildingType.Warship, tile));
    }

    /// <summary>A random water tile within this many tiles of a spot (50 tries).</summary>
    private int? SpawnTile(int around, int radius)
    {
        var map = _game.Map;
        int x = around % map.Width, y = around / map.Width;
        for (int i = 0; i < 50; i++)
        {
            int rx = _rng.RangeInt(x - radius, x + radius - 1), ry = _rng.RangeInt(y - radius, y + radius - 1);
            if (!map.InBounds(rx, ry))
                continue;
            int tile = map.Index(rx, ry);
            if (map.IsWater(tile))
                return tile;
        }
        return null;
    }

    private bool Chance(int n) => _rng.RangeInt(0, n - 1) == 0;

    // ------------------------------------------------------------------
    //  Hitting back (every tick)
    // ------------------------------------------------------------------

    public void TrackShipsAndRetaliate()
    {
        TrackBoats();
        TrackTradeShips();
        TrackIncomingBoats();
    }

    /// <summary>A boat of ours was sunk: send a warship there.</summary>
    private void TrackBoats()
    {
        foreach (var b in _game.Boats)
            if (b.Owner == _player && !_ourBoats.Contains(b))
                _ourBoats.Add(b);
        for (int i = 0; i < _ourBoats.Count; i++)
        {
            var boat = _ourBoats[i];
            if (_game.Boats.Contains(boat) && !boat.Sunk)
                continue;
            _ourBoats.RemoveAt(i--);
            if (boat.Sunk && boat.SunkBy is { } sinker)
                MaybeRetaliate(boat.Tile, sinker, trade: false);
        }
    }

    /// <summary>A trade ship of ours was captured: send a warship after it.</summary>
    private void TrackTradeShips()
    {
        foreach (var s in _game.TradeShips)
            if (s.Owner == _player && !_ourTradeShips.Contains(s))
                _ourTradeShips.Add(s);
        for (int i = 0; i < _ourTradeShips.Count; i++)
        {
            var ship = _ourTradeShips[i];
            if (!_game.TradeShips.Contains(ship))
            {
                _ourTradeShips.RemoveAt(i--);
                continue;
            }
            if (ship.Owner != _player)
            {
                _ourTradeShips.RemoveAt(i--);
                MaybeRetaliate(ship.Tile, ship.Owner, trade: true);
            }
        }
    }

    /// <summary>Someone is boating at our coast: if no warship of ours guards that shore, maybe send one to meet it.</summary>
    private void TrackIncomingBoats()
    {
        foreach (var b in _game.Boats)
            if (b.Owner != _player && !b.Retreating && !b.Sunk && _game.Owner[b.LandingTile] == _player.Id && !_incoming.Contains(b))
                _incoming.Add(b);
        int near = _game.Rules.D(90);
        for (int i = 0; i < _incoming.Count; i++)
        {
            var boat = _incoming[i];
            if (!_game.Boats.Contains(boat) || boat.Sunk || boat.Retreating)
            {
                _incoming.RemoveAt(i--);
                _dealtWith.Remove(boat);
                continue;
            }
            if (_dealtWith.Contains(boat))
                continue;
            int target = boat.LandingTile;
            if (AiTiles.Manhattan(_game, boat.Tile, target) < _game.Rules.D(20))
            {
                _dealtWith.Add(boat); // too close to do anything about
                continue;
            }
            if (_player.IsAlliedWith(boat.Owner))
                continue;
            if (Ours.Any(w => AiTiles.DistSq(_game, w.Tile, target) <= (long)near * near || AiTiles.Manhattan(_game, target, w.PatrolTile) < near))
            {
                _dealtWith.Add(boat);
                continue;
            }
            if (SpawnTile(target, _game.Rules.D(30)) is not { } water)
                continue;
            MaybeRetaliate(water, boat.Owner, trade: false);
            _dealtWith.Add(boat);
            break;
        }
    }

    private void MaybeRetaliate(int tile, Player enemy, bool trade)
    {
        if (enemy == _player || WarshipsCovering(tile).Count > 0)
            return; // one of ours already patrols there
        if (Ours.Count >= MaxWarships)
        {
            MaybeMoveWarship(tile);
            return;
        }
        int odds = D switch { Difficulty.Medium => 15, Difficulty.Hard => 50, Difficulty.Impossible => 80, _ => 0 };
        if (_rng.RangeInt(0, 99) >= odds)
            return;
        if (!_game.CanBuild(_player, BuildingType.Warship, tile))
        {
            MaybeMoveWarship(tile);
            return;
        }
        _game.Submit(new BuildIntent(_player.Id, BuildingType.Warship, tile));
        _emojis.MaybeSend(enemy, NationMoods.WarshipRetaliation);
        _game.UpdateRelation(_player, enemy, trade ? -750 : -1500);
    }

    /// <summary>Sends the nearest of our warships that isn't already on its way somewhere.</summary>
    private void MaybeMoveWarship(int tile)
    {
        if (!_game.Map.IsWater(tile))
            return;
        int travelling = _game.Rules.D(130);
        var ship = Ours
            .Where(w => AiTiles.Manhattan(_game, w.Tile, w.PatrolTile) < travelling)
            .OrderBy(w => AiTiles.Manhattan(_game, w.Tile, tile))
            .FirstOrDefault();
        if (ship is not null)
            _game.Submit(new MoveWarshipIntent(_player.Id, ship.Id, tile));
    }

    /// <summary>Our warships on the same water whose patrol area takes in this tile.</summary>
    private List<Warship> WarshipsCovering(int tile)
    {
        int body = _game.Waterways.Body[tile];
        int range = _game.Rules.WarshipPatrolRange;
        return Ours.Where(w => _game.Waterways.Body[w.Tile] == body && AiTiles.Manhattan(_game, w.PatrolTile, tile) <= range).ToList();
    }

    private int HostileWarshipsNear(int tile)
    {
        long range = _game.Rules.WarshipTargetRange;
        return _game.Warships.Count(w => w.Alive && w.Owner != _player && !_player.IsAlliedWith(w.Owner)
            && AiTiles.DistSq(_game, w.Tile, tile) <= range * range);
    }

    // ------------------------------------------------------------------
    //  Clearing the way (Hard and Impossible)
    // ------------------------------------------------------------------

    /// <summary>
    /// Sends a squad at the fleet blocking our boats, all at once or not at all (ships sent in
    /// one by one get sunk one by one): Hard as many ships as they have there, Impossible one more.
    /// </summary>
    public void ClearSeaLane(Warship blocker)
    {
        int fleet = HostileWarshipsNear(blocker.Tile);
        int wanted = D switch { Difficulty.Hard => fleet, Difficulty.Impossible => fleet + 1, _ => 0 };
        if (wanted == 0)
            return;
        int target = blocker.Tile;
        var covering = WarshipsCovering(target);
        int body = _game.Waterways.Body[target];
        // Ships with enemies around their patrol area are busy: pulling them away would thrash.
        var spare = Ours
            .Where(w => !covering.Contains(w) && _game.Waterways.Body[w.Tile] == body && HostileWarshipsNear(w.PatrolTile) == 0)
            .OrderBy(w => AiTiles.Manhattan(_game, w.Tile, target))
            .ToList();
        var recruits = spare.Take(Math.Max(0, wanted - covering.Count)).ToList();
        int builds = wanted - covering.Count - recruits.Count;
        if (builds > 0)
        {
            if (Ours.Count + builds > MaxWarships || !_player.CanAfford(WarshipCost * builds)
                || !_game.CanBuild(_player, BuildingType.Warship, target))
                return;
        }
        foreach (var w in recruits)
            _game.Submit(new MoveWarshipIntent(_player.Id, w.Id, target));
        for (int i = 0; i < builds; i++)
            _game.Submit(new BuildIntent(_player.Id, BuildingType.Warship, target));
    }

    /// <summary>
    /// Hard and up: if we're one of the three richest nations and an enemy has over 10
    /// warships (trying to own the sea), build a warship on top of one of theirs.
    /// </summary>
    public void CounterWarshipInfestation()
    {
        if (D < Difficulty.Hard || _game.Warships.Count <= MaxWarships || WarshipCost > _player.Gold
            || Ports.Count == 0 || Ours.Count >= MaxWarships)
            return;
        var richest = _game.Players.Where(p => p.Kind != PlayerKind.Human && p.IsAlive)
            .OrderByDescending(p => p.Gold).Take(3);
        if (!richest.Contains(_player))
            return;
        foreach (var enemy in _game.Players)
        {
            if (enemy == _player || _player.IsAlliedWith(enemy))
                continue;
            var fleet = _game.Warships.Where(w => w.Owner == enemy && w.Alive).ToList();
            if (fleet.Count <= MaxWarships)
                continue;
            var ship = fleet[_rng.RangeInt(0, fleet.Count - 1)];
            if (!_game.CanBuild(_player, BuildingType.Warship, ship.Tile))
            {
                MaybeMoveWarship(ship.Tile);
                return;
            }
            _game.Submit(new BuildIntent(_player.Id, BuildingType.Warship, ship.Tile));
            _emojis.Send(Game.Nobody, NationMoods.WarshipRetaliation);
            return;
        }
    }
}
