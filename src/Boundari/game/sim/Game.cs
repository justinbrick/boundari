using System;
using System.Collections.Generic;
using System.Diagnostics;
using Boundari.Core;
using Boundari.Map;

namespace Boundari.Sim;

public enum GamePhase
{
    Spawning, // players pick where to start
    Running,
}

/// <summary>
/// The whole match: who owns which tile, players, attacks and bots. Advances one
/// fixed tick at a time and is fully deterministic (same seed + same intents = same
/// game), with no Godot types, so it can run headless in tests or on a server.
/// </summary>
public sealed class Game
{
    public const ushort Nobody = 0;

    public readonly GameMap Map;
    public readonly ulong Seed;

    /// <summary>Owner of each tile (player id), or <see cref="Nobody"/>.</summary>
    public readonly ushort[] Owner;

    /// <summary>How hard each tile is to take, in tenths (10 = plain farmland). See <see cref="Balance"/>.</summary>
    public readonly short[] CostTenths;

    public int Tick { get; private set; }
    public GamePhase Phase { get; private set; } = GamePhase.Spawning;
    public readonly int SpawnPhaseTicks;

    /// <summary>Tiles whose owner changed since the renderer last cleared this list.</summary>
    public readonly List<int> ChangedTiles = new();

    /// <summary>Time spent per part of a tick (bots, economy+boats, trade, attacks), in Stopwatch ticks. For profiling only.</summary>
    public readonly long[] Profile = new long[4];

    private readonly List<Player> _players = new() { null! }; // index = id; 0 is "nobody"
    private readonly List<Attack> _attacks = new();
    private readonly List<Boat> _boats = new();
    private readonly Dictionary<int, Building> _buildings = new(); // by tile
    private readonly List<TradeShip> _tradeShips = new();
    private readonly Dictionary<Building, PortTrade> _ports = new();
    private readonly Dictionary<(int, int), int[]?> _tradeRoutes = new();
    private int _routesPlannedThisTick;
    private const int MaxRoutesPlannedPerTick = 1;
    private Waterways? _waterways;
    private readonly bool[]? _coastal;
    private readonly List<Intent> _pending = new();
    private readonly List<SimpleBot> _bots = new();
    private readonly Rng _rng;
    private readonly long[] _troopsLastSecond = new long[ushort.MaxValue];

    public Game(GameMap map, ulong seed, int spawnPhaseTicks = Balance.SpawnPhaseTicks)
    {
        Map = map;
        Seed = seed;
        SpawnPhaseTicks = spawnPhaseTicks;
        Owner = new ushort[map.TileCount];
        CostTenths = Balance.TileCostTenths(map);
        _coastal = new bool[map.TileCount];
        Span<int> bodies = stackalloc int[4];
        for (int t = 0; t < map.TileCount; t++)
            _coastal[t] = map.IsLand(t) && Waterways.TouchingBodies(t, bodies) > 0;
        _rng = new Rng(seed);
    }

    public IReadOnlyList<Player> Players => _players.GetRange(1, _players.Count - 1);
    public IReadOnlyList<Attack> Attacks => _attacks;
    public IReadOnlyList<Boat> Boats => _boats;
    public IEnumerable<Building> Buildings => _buildings.Values;
    public IReadOnlyList<TradeShip> TradeShips => _tradeShips;

    /// <summary>Water bodies and sea routes for boats (worked out on first use; World.cs does it at match start).</summary>
    public Waterways Waterways => _waterways ??= new Waterways(Map);
    public Player? GetPlayer(ushort id) => id > 0 && id < _players.Count ? _players[id] : null;
    public int SecondsUntilStart => Math.Max(0, (SpawnPhaseTicks - Tick + Balance.TicksPerSecond - 1) / Balance.TicksPerSecond);

    public Player AddPlayer(string name, bool isBot = false, string flag = "")
    {
        if (_players.Count >= ushort.MaxValue - 1)
            throw new InvalidOperationException("Too many players.");
        var player = new Player((ushort)_players.Count, name, isBot, flag) { Troops = Balance.StartTroops };
        _players.Add(player);
        return player;
    }

    /// <summary>Adds bots and places them around the map, spread apart.</summary>
    public void AddBots(int count)
    {
        var spawns = new List<int>();
        var countries = new List<(string Code, string Name)>(Countries.All);
        for (int b = 0; b < count; b++)
        {
            // Each bot gets a different country's name and flag (numbered if we run out).
            int pick = _rng.RangeInt(0, countries.Count - 1);
            var (code, name) = countries[pick];
            countries.RemoveAt(pick);
            if (countries.Count == 0)
                countries.AddRange(Countries.All);
            var bot = AddPlayer(b < Countries.All.Length ? name : $"{name} {b / Countries.All.Length + 1}", isBot: true, flag: code);
            _bots.Add(new SimpleBot(this, bot, new Rng(_rng.Fork())));
            int tile = FindBotSpawn(spawns);
            if (tile >= 0)
            {
                spawns.Add(tile);
                Spawn(bot, tile);
            }
        }
    }

    public void Submit(Intent intent) => _pending.Add(intent);

    /// <summary>Advances the game by one tick.</summary>
    public void Step()
    {
        foreach (var intent in _pending)
            Apply(intent);
        _pending.Clear();

        if (Phase == GamePhase.Spawning)
        {
            if (++Tick >= SpawnPhaseTicks)
                Phase = GamePhase.Running;
            return;
        }

        long t0 = Stopwatch.GetTimestamp();
        foreach (var bot in _bots)
            bot.Think();
        long t1 = Stopwatch.GetTimestamp();
        GrowEconomies();
        SailBoats();
        long t2 = Stopwatch.GetTimestamp();
        RunPorts();
        SailTradeShips();
        long t3 = Stopwatch.GetTimestamp();
        AdvanceAttacks();
        long t4 = Stopwatch.GetTimestamp();
        Profile[0] += t1 - t0;
        Profile[1] += t2 - t1;
        Profile[2] += t3 - t2;
        Profile[3] += t4 - t3;
        Tick++;
    }

    // ------------------------------------------------------------------
    //  Intents
    // ------------------------------------------------------------------

    private void Apply(Intent intent)
    {
        var player = GetPlayer(intent.PlayerId);
        if (player is null || !player.IsAlive)
            return;
        switch (intent)
        {
            case SpawnIntent spawn when Phase == GamePhase.Spawning:
                Spawn(player, spawn.Tile);
                break;
            case AttackIntent attack when Phase == GamePhase.Running:
                StartAttack(player, attack.TargetId, attack.RatioPercent);
                break;
            case BoatIntent boat when Phase == GamePhase.Running:
                LaunchBoat(player, boat.Tile, boat.RatioPercent);
                break;
            case BuildIntent build when Phase == GamePhase.Running && CanBuild(player, build.Type, build.Tile):
                player.Gold -= Balance.BuildingCost(build.Type);
                var building = new Building(build.Type, build.Tile, player.Id);
                _buildings[build.Tile] = building;
                if (build.Type == BuildingType.Port)
                    _ports[building] = new PortTrade(_rng.Fork(), Tick % TradeRules.PortCheckTicks);
                break;
        }
    }

    private void Spawn(Player player, int tile)
    {
        if (tile < 0 || tile >= Map.TileCount || !Map.IsLand(tile) || Owner[tile] != Nobody)
            return;

        // Moving your spawn during the spawn phase: give back the old land first.
        foreach (int t in player.SpawnArea)
            if (Owner[t] == player.Id)
                SetOwner(t, Nobody);
        player.SpawnArea.Clear();

        // Spread out over connected land only, so a spawn never jumps across a river.
        int cx = tile % Map.Width, cy = tile / Map.Width, r = Balance.SpawnRadius;
        var frontier = new Queue<int>();
        var seen = new HashSet<int> { tile };
        frontier.Enqueue(tile);
        Span<int> nb = stackalloc int[4];
        while (frontier.Count > 0)
        {
            int t = frontier.Dequeue();
            if (Owner[t] != Nobody)
                continue;
            SetOwner(t, player.Id);
            player.SpawnArea.Add(t);
            int count = LandNeighbours(t, nb);
            for (int k = 0; k < count; k++)
            {
                int n = nb[k], dx = n % Map.Width - cx, dy = n / Map.Width - cy;
                if (dx * dx + dy * dy <= r * r && seen.Add(n))
                    frontier.Enqueue(n);
            }
        }
        player.HasSpawned = true;
        player.Troops = Balance.StartTroops;
    }

    private int FindBotSpawn(List<int> taken)
    {
        // Best of many random tries: the spot furthest from existing spawns. On crowded
        // maps that may be closer than MinSpawnSpacing, which beats not spawning at all.
        long wanted = (long)Balance.MinSpawnSpacing * Balance.MinSpawnSpacing;
        int best = -1;
        long bestDist = -1;
        for (int tries = 0; tries < 80 && bestDist < wanted; tries++)
        {
            int t = _rng.RangeInt(0, Map.TileCount - 1);
            if (!Map.IsLand(t) || Owner[t] != Nobody || Map.LandmassSizes[Map.LandmassId[t]] < 1100)
                continue;
            long d = long.MaxValue;
            foreach (int s in taken)
            {
                long dx = t % Map.Width - s % Map.Width, dy = t / Map.Width - s / Map.Width;
                d = Math.Min(d, dx * dx + dy * dy);
            }
            if (d > bestDist)
            {
                bestDist = d;
                best = t;
            }
        }
        return best;
    }

    // ------------------------------------------------------------------
    //  Economy
    // ------------------------------------------------------------------

    private void GrowEconomies()
    {
        bool newSecond = Tick % Balance.TicksPerSecond == 0;
        for (int id = 1; id < _players.Count; id++)
        {
            var p = _players[id];
            if (!p.HasSpawned || p.TileCount == 0)
                continue;
            p.Troops = Math.Max(0, p.Troops + Balance.TroopGrowth(p.Troops, p.TileCount));
            p.Gold += Balance.GoldPerTick(p);
            if (newSecond)
            {
                p.TroopsPerSecond = p.Troops - _troopsLastSecond[id];
                _troopsLastSecond[id] = p.Troops;
            }
        }
    }

    // ------------------------------------------------------------------
    //  Attacks
    // ------------------------------------------------------------------

    // ------------------------------------------------------------------
    //  Buildings
    // ------------------------------------------------------------------

    public Building? BuildingAt(int tile) => _buildings.GetValueOrDefault(tile);

    public int CountBuildings(Player player, BuildingType type)
    {
        int n = 0;
        foreach (var b in _buildings.Values)
            if (b.Owner == player.Id && b.Type == type)
                n++;
        return n;
    }

    /// <summary>Whether the player may place this building here right now (and can afford it).</summary>
    public bool CanBuild(Player player, BuildingType type, int tile)
    {
        if (Phase != GamePhase.Running || tile < 0 || tile >= Map.TileCount || Owner[tile] != player.Id)
            return false;
        if (player.Gold < Balance.BuildingCost(type))
            return false;
        if (type == BuildingType.Port && !IsCoastal(tile))
            return false;
        int spacing = Balance.MinBuildingSpacing, x = tile % Map.Width, y = tile / Map.Width;
        foreach (var b in _buildings.Values)
        {
            int dx = b.Tile % Map.Width - x, dy = b.Tile / Map.Width - y;
            if (dx * dx + dy * dy < spacing * spacing)
                return false;
        }
        return true;
    }

    /// <summary>Land tile touching sea-connected water (or a lake): where ports and boats work.</summary>
    public bool IsCoastal(int tile) => tile >= 0 && tile < Map.TileCount && _coastal![tile];

    // ------------------------------------------------------------------
    //  Boats
    // ------------------------------------------------------------------

    /// <summary>
    /// True if the player can attack the owner of this tile over land: some of the
    /// player's border on the same landmass touches that owner. Otherwise it takes a boat.
    /// </summary>
    public bool CanReachByLand(Player player, int tile)
    {
        if (tile < 0 || !Map.IsLand(tile))
            return false;
        ushort target = Owner[tile];
        int landmass = Map.LandmassId[tile];
        Span<int> nb = stackalloc int[4];
        foreach (int t in player.Border)
        {
            if (Map.LandmassId[t] != landmass)
                continue;
            int count = LandNeighbours(t, nb);
            for (int k = 0; k < count; k++)
                if (Owner[nb[k]] == target)
                    return true;
        }
        return false;
    }

    public int BoatsAtSea(Player player)
    {
        int n = 0;
        foreach (var b in _boats)
            if (b.Owner == player)
                n++;
        return n;
    }

    /// <summary>
    /// Sends troops by sea: picks the shore nearest the clicked tile that the player can
    /// reach by water, the player's own shore closest to it, and sails between them.
    /// </summary>
    private void LaunchBoat(Player player, int tile, int ratioPercent)
    {
        if (tile < 0 || tile >= Map.TileCount || !Map.IsLand(tile) || Owner[tile] == player.Id)
            return;
        if (BoatsAtSea(player) >= Balance.MaxBoatsPerPlayer)
            return;
        long troops = player.Troops * Math.Clamp(ratioPercent, 1, 100) / 100;
        if (troops <= 0)
            return;

        // Which water bodies does the player have a shore on?
        var ways = Waterways;
        var myBodies = new HashSet<int>();
        var myShores = new List<int>(player.Coast);
        myShores.Sort(); // HashSet order isn't guaranteed; keep every machine identical
        Span<int> bodies = stackalloc int[4];
        foreach (int t in myShores)
        {
            int n = ways.TouchingBodies(t, bodies);
            for (int k = 0; k < n; k++)
                myBodies.Add(bodies[k]);
        }
        if (myShores.Count == 0)
            return; // no water access

        // Landing: nearest land tile to the click (on its landmass) on one of those waters.
        int landing = -1, landingBody = Waterways.NoBody;
        var queue = new Queue<int>();
        var seen = new HashSet<int> { tile };
        queue.Enqueue(tile);
        Span<int> nb = stackalloc int[4];
        while (queue.Count > 0 && seen.Count < Balance.BoatLandingSearchTiles)
        {
            int t = queue.Dequeue();
            if (Owner[t] != player.Id)
            {
                int n = ways.TouchingBodies(t, bodies);
                for (int k = 0; k < n && landing < 0; k++)
                    if (myBodies.Contains(bodies[k]))
                    {
                        landing = t;
                        landingBody = bodies[k];
                    }
                if (landing >= 0)
                    break;
            }
            int count = LandNeighbours(t, nb);
            for (int k = 0; k < count; k++)
                if (seen.Add(nb[k]))
                    queue.Enqueue(nb[k]);
        }
        if (landing < 0)
            return;

        // Launch from the player's shore on that water closest to the landing.
        int lx = landing % Map.Width, ly = landing / Map.Width;
        int source = -1;
        long best = long.MaxValue;
        foreach (int t in myShores)
        {
            if (ways.ShoreWater(t, landingBody) < 0)
                continue;
            long dx = t % Map.Width - lx, dy = t / Map.Width - ly, d = dx * dx + dy * dy;
            if (d < best)
            {
                best = d;
                source = t;
            }
        }
        if (source < 0)
            return;

        int[]? path = ways.FindPath(ways.ShoreWater(source, landingBody), ways.ShoreWater(landing, landingBody));
        if (path is null)
            return;
        player.Troops -= troops;
        _boats.Add(new Boat(player, troops, landing, path));
    }

    private void SailBoats()
    {
        for (int i = 0; i < _boats.Count; i++)
        {
            var boat = _boats[i];
            if (!boat.Owner.IsAlive)
            {
                _boats.RemoveAt(i--); // nobody left to land for
                continue;
            }
            boat.Progress += Balance.BoatSpeedPerTick;
            if (!boat.Arrived)
                continue;
            _boats.RemoveAt(i--);
            Land(boat);
        }
    }

    /// <summary>The boat's troops come ashore and attack whoever owns the landing tile.</summary>
    private void Land(Boat boat)
    {
        var player = boat.Owner;
        int tile = boat.LandingTile;
        ushort target = Owner[tile];
        if (target == player.Id)
        {
            player.Troops += boat.Troops; // already ours: troops just come home
            return;
        }
        var defender = GetPlayer(target);
        long cost = TileCost(tile, defender);
        if (boat.Troops <= cost)
            return; // too few to even take the beach
        if (defender is not null)
            defender.Troops = Math.Max(0, defender.Troops - DefenderLoss(defender));
        SetOwner(tile, player.Id);

        var attack = _attacks.Find(a => a.Attacker == player && a.TargetId == target);
        if (attack is null)
        {
            attack = new Attack(player, target, 0);
            _attacks.Add(attack);
        }
        attack.Troops += boat.Troops - cost;
        attack.TroopsSent += boat.Troops - cost;
        Span<int> nb = stackalloc int[4];
        int count = LandNeighbours(tile, nb);
        for (int k = 0; k < count; k++)
            Enqueue(attack, nb[k], attack.Clock);
    }

    // ------------------------------------------------------------------
    //  Trade
    // ------------------------------------------------------------------

    /// <summary>Each port rolls now and then to send a trade ship to another player's port.</summary>
    private void RunPorts()
    {
        _routesPlannedThisTick = 0;
        foreach (var (port, trade) in _ports)
        {
            if ((Tick + trade.CheckOffset) % TradeRules.PortCheckTicks != 0)
                continue;
            int odds = TradeRules.SpawnOdds(trade.Rejections, _tradeShips.Count);
            if (odds == int.MaxValue || trade.Rng.RangeInt(0, odds - 1) != 0)
            {
                trade.Rejections++;
                continue;
            }
            trade.Rejections = 0;

            var destination = PickTradePartner(port, trade.Rng);
            if (destination is null)
                continue;
            if (!_tradeRoutes.ContainsKey((port.Tile, destination.Tile)) && _routesPlannedThisTick >= MaxRoutesPlannedPerTick)
            {
                trade.Rejections++; // busy planning other routes: try again on the next roll
                continue;
            }
            int[]? route = TradeRoute(port, destination);
            if (route is not null && route.Length > 1)
                _tradeShips.Add(new TradeShip(_players[port.Owner], port, destination, route));
        }
    }

    /// <summary>
    /// A random port of another player on the same water. Every candidate counts once; the
    /// nearest few (but not too-near) count twice, so trade favours sensible routes.
    /// </summary>
    private Building? PickTradePartner(Building port, Rng rng)
    {
        Span<int> bodies = stackalloc int[4];
        int n = Waterways.TouchingBodies(port.Tile, bodies);
        int px = port.Tile % Map.Width, py = port.Tile / Map.Width;
        var candidates = new List<(Building Port, int Distance)>();
        Span<int> other = stackalloc int[4];
        foreach (var dest in _ports.Keys)
        {
            if (dest.Owner == port.Owner || !_players[dest.Owner].IsAlive)
                continue;
            int m = Waterways.TouchingBodies(dest.Tile, other);
            bool sameWater = false;
            for (int i = 0; i < n && !sameWater; i++)
                for (int j = 0; j < m && !sameWater; j++)
                    sameWater = bodies[i] == other[j];
            if (!sameWater)
                continue;
            int d = Math.Abs(dest.Tile % Map.Width - px) + Math.Abs(dest.Tile / Map.Width - py);
            candidates.Add((dest, d));
        }
        if (candidates.Count == 0)
            return null;
        candidates.Sort((a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : a.Port.Tile.CompareTo(b.Port.Tile));

        var weighted = new List<Building>();
        int bonus = TradeRules.ProximityBonusPorts(candidates.Count);
        for (int i = 0; i < candidates.Count; i++)
        {
            weighted.Add(candidates[i].Port);
            if (i < bonus && candidates[i].Distance >= TradeRules.ShortRangeDebuffTiles)
                weighted.Add(candidates[i].Port);
        }
        return weighted[rng.RangeInt(0, weighted.Count - 1)];
    }

    /// <summary>Sea route between two ports (remembered, since the same pairs trade again and again).</summary>
    private int[]? TradeRoute(Building from, Building to)
    {
        if (_tradeRoutes.TryGetValue((from.Tile, to.Tile), out var cached))
            return cached;
        int[]? route = null;
        Span<int> bodies = stackalloc int[4];
        int n = Waterways.TouchingBodies(from.Tile, bodies);
        for (int i = 0; i < n && route is null; i++)
        {
            int a = Waterways.ShoreWater(from.Tile, bodies[i]), b = Waterways.ShoreWater(to.Tile, bodies[i]);
            if (a >= 0 && b >= 0)
                route = Waterways.FindPath(a, b);
        }
        _tradeRoutes[(from.Tile, to.Tile)] = route;
        _routesPlannedThisTick++;
        return route;
    }

    private void SailTradeShips()
    {
        for (int i = 0; i < _tradeShips.Count; i++)
        {
            var ship = _tradeShips[i];
            // Cancelled if the destination is gone, or now belongs to the ship's own side.
            if (ship.To.Removed || ship.To.Owner == ship.Owner.Id || !ship.Owner.IsAlive)
            {
                _tradeShips.RemoveAt(i--);
                continue;
            }
            ship.Progress += Balance.TradeShipSpeedPerTick;
            if (!ship.Arrived)
                continue;
            _tradeShips.RemoveAt(i--);

            // Both ends profit: whoever owns each port when the ship docks.
            long gold = TradeRules.Gold(ship.Path.Length);
            if (!ship.From.Removed)
                _players[ship.From.Owner].Gold += gold;
            _players[ship.To.Owner].Gold += gold;
            ship.Owner.TradeGold += gold;
        }
    }

    // ------------------------------------------------------------------
    //  Attacks (continued)
    // ------------------------------------------------------------------

    private void StartAttack(Player attacker, ushort targetId, int ratioPercent)
    {
        if (targetId == attacker.Id || (targetId != Nobody && GetPlayer(targetId) is not { IsAlive: true }))
            return;
        long troops = attacker.Troops * Math.Clamp(ratioPercent, 1, 100) / 100;
        if (troops <= 0)
            return;

        // Reinforce an attack already heading for the same target.
        var attack = _attacks.Find(a => a.Attacker == attacker && a.TargetId == targetId);
        bool isNew = attack is null;
        attack ??= new Attack(attacker, targetId, 0);

        Span<int> nb = stackalloc int[4];
        foreach (int t in attacker.Border)
        {
            int count = LandNeighbours(t, nb);
            for (int k = 0; k < count; k++)
                Enqueue(attack, nb[k], attack.Clock);
        }
        if (attack.Front.Count == 0)
            return; // doesn't border the target: nothing to attack (boats come later)

        attacker.Troops -= troops;
        attack.Troops += troops;
        attack.TroopsSent += troops;
        if (isNew)
            _attacks.Add(attack);
    }

    private void AdvanceAttacks()
    {
        Span<int> nb = stackalloc int[4];
        for (int a = 0; a < _attacks.Count; a++)
        {
            var attack = _attacks[a];
            attack.Clock += Balance.WaveSpeedPerTick * SpeedPermille(attack) / 1000;
            int budget = Balance.MaxTilesPerTick;
            var defender = GetPlayer(attack.TargetId);

            // Take every front tile the wave has reached by now.
            while (budget > 0 && attack.Front.TryPeek(out int tile, out long arrival) && arrival <= attack.Clock)
            {
                if (Owner[tile] != attack.TargetId || !Touches(tile, attack.Attacker.Id))
                {
                    attack.Front.Dequeue();
                    continue;
                }

                long cost = TileCost(tile, defender);
                if (attack.Troops < cost)
                    break;
                attack.Front.Dequeue();

                attack.Troops -= cost;
                if (defender is not null)
                    defender.Troops = Math.Max(0, defender.Troops - DefenderLoss(defender));
                SetOwner(tile, attack.Attacker.Id);
                int count = LandNeighbours(tile, nb);
                for (int k = 0; k < count; k++)
                    Enqueue(attack, nb[k], arrival);
                budget--;
            }

            // Drop stale entries so the checks below look at a real front tile.
            while (attack.Front.TryPeek(out int top, out _) && (Owner[top] != attack.TargetId || !Touches(top, attack.Attacker.Id)))
                attack.Front.Dequeue();
            bool outOfTroops = attack.Front.Count > 0 && attack.Troops < TileCost(attack.Front.Peek(), defender);
            if (attack.Front.Count == 0 || outOfTroops || !attack.Attacker.IsAlive)
            {
                attack.Attacker.Troops += attack.Troops; // survivors come home
                _attacks.RemoveAt(a--);
            }
        }
    }

    /// <summary>
    /// Puts a tile on the attack's front, arriving <c>from</c> (the time its attacking
    /// neighbour fell) plus the time to cross it: terrain, randomness, and a discount
    /// when several of the attacker's tiles already surround it.
    /// </summary>
    private void Enqueue(Attack attack, int tile, long from)
    {
        if (Owner[tile] != attack.TargetId)
            return;
        Span<int> nb = stackalloc int[4];
        int own = 0, count = LandNeighbours(tile, nb);
        for (int k = 0; k < count; k++)
            if (Owner[nb[k]] == attack.Attacker.Id)
                own++;
        long travel = Balance.TravelUnit * CostTenths[tile] / 10
            * _rng.RangeInt(Balance.TravelRandomMinPercent, Balance.TravelRandomMaxPercent) / 100
            * Balance.SurroundedTravelPercent[own] / 100;
        attack.Front.Enqueue(tile, Math.Max(from, attack.Clock - Balance.WaveSpeedPerTick) + travel);
    }

    /// <summary>Full speed until the attack runs low on troops, then easing down to a crawl.</summary>
    private static long SpeedPermille(Attack attack)
    {
        long threshold = Math.Max(1, attack.TroopsSent * Balance.SlowdownBelowPercent / 100);
        return Math.Clamp(attack.Troops * 1000 / threshold, Balance.MinSpeedPermille, 1000);
    }

    private long TileCost(int tile, Player? defender)
    {
        // Terrain counts fully towards crossing time, but only partly towards troop cost.
        long terrain = 10 + (CostTenths[tile] - 10) * Balance.TerrainTroopCostPercent / 100;
        long cost = Balance.NeutralTileCost * terrain / 10;
        if (defender is not null)
            cost += Density(defender) * Balance.DefenderDensityCostTenths * terrain / 100;
        return Math.Max(cost, 1);
    }

    private static long DefenderLoss(Player defender) => Density(defender) * Balance.DefenderLossTenths / 10;

    private static long Density(Player p) => p.Troops / Math.Max(1, p.TileCount);

    // ------------------------------------------------------------------
    //  Territory
    // ------------------------------------------------------------------

    private void SetOwner(int tile, ushort newOwner)
    {
        ushort old = Owner[tile];
        if (old == newOwner)
            return;
        Owner[tile] = newOwner;
        ChangedTiles.Add(tile);

        bool coastal = _coastal![tile];
        if (old != Nobody)
        {
            var oldPlayer = _players[old];
            oldPlayer.TileCount--;
            oldPlayer.Border.Remove(tile);
            if (coastal)
                oldPlayer.Coast.Remove(tile);
        }
        if (newOwner != Nobody)
        {
            _players[newOwner].TileCount++;
            if (coastal)
                _players[newOwner].Coast.Add(tile);
        }

        // Buildings change hands with their land (and vanish if the land is abandoned).
        if (_buildings.TryGetValue(tile, out var building))
        {
            if (newOwner == Nobody)
            {
                _buildings.Remove(tile);
                _ports.Remove(building);
                building.Removed = true;
            }
            else
                building.Owner = newOwner;
        }

        UpdateBorder(tile);
        Span<int> nb = stackalloc int[4];
        int count = LandNeighbours(tile, nb);
        for (int k = 0; k < count; k++)
            UpdateBorder(nb[k]);
    }

    private void UpdateBorder(int tile)
    {
        ushort owner = Owner[tile];
        if (owner == Nobody)
            return;
        Span<int> nb = stackalloc int[4];
        bool border = false;
        int count = LandNeighbours(tile, nb);
        for (int k = 0; k < count; k++)
            if (Owner[nb[k]] != owner)
                border = true;
        if (border)
            _players[owner].Border.Add(tile);
        else
            _players[owner].Border.Remove(tile);
    }

    /// <summary>True if any land neighbour of the tile belongs to this player.</summary>
    public bool Touches(int tile, ushort playerId)
    {
        Span<int> nb = stackalloc int[4];
        int count = LandNeighbours(tile, nb);
        for (int k = 0; k < count; k++)
            if (Owner[nb[k]] == playerId)
                return true;
        return false;
    }

    /// <summary>Writes the tile's land neighbours (up to 4) into <paramref name="result"/>; returns how many.</summary>
    public int LandNeighbours(int tile, Span<int> result)
    {
        int w = Map.Width, x = tile % w, y = tile / w, n = 0;
        if (x > 0 && Map.IsLand(tile - 1)) result[n++] = tile - 1;
        if (x < w - 1 && Map.IsLand(tile + 1)) result[n++] = tile + 1;
        if (y > 0 && Map.IsLand(tile - w)) result[n++] = tile - w;
        if (y < Map.Height - 1 && Map.IsLand(tile + w)) result[n++] = tile + w;
        return n;
    }
}
