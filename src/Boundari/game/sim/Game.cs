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
public sealed partial class Game
{
    public const ushort Nobody = 0;

    public readonly GameMap Map;
    public readonly ulong Seed;

    /// <summary>Owner of each tile (player id), or <see cref="Nobody"/>.</summary>
    public readonly ushort[] Owner;

    /// <summary>How hard each tile's land cover is to cross, in tenths (10 = farmland). See <see cref="AttackRules.CoverTenths"/>.</summary>
    public readonly short[] CoverTenths;

    public int Tick { get; private set; }
    public GamePhase Phase { get; private set; } = GamePhase.Spawning;
    /// <summary>When the spawn phase ends (moved up to the moment <see cref="StartsWhenSpawned"/> picks a start).</summary>
    public int SpawnPhaseTicks { get; private set; }

    /// <summary>
    /// A player the match waits for (single player): the spawn phase has no countdown and
    /// lasts until they pick a start, then the match begins at once. Nobody = the usual
    /// timed spawn phase.
    /// </summary>
    public ushort StartsWhenSpawned { get; set; } = Nobody;

    /// <summary>Tiles whose owner (or fallout, or hatching) changed since the renderer last cleared this list.</summary>
    public readonly List<int> ChangedTiles = new();

    /// <summary>Time spent per part of a tick (bots, economy+boats, trade+warships+enclaves, attacks, enclaves alone), in Stopwatch ticks. For profiling only.</summary>
    public readonly long[] Profile = new long[5];

    /// <summary>Slowest single tick of each part of the world update, in Stopwatch ticks (see <see cref="StepParts"/>). For profiling only.</summary>
    public readonly long[] StepWorst = new long[9];
    public static readonly string[] StepParts = { "ports", "trade ships", "construction+rails", "trains", "warships", "nukes", "enclaves", "orders", "ai" };

    private readonly List<Player> _players = new() { null! }; // index = id; 0 is "nobody"
    private readonly List<Attack> _attacks = new();
    private readonly List<Boat> _boats = new();
    private readonly Dictionary<int, Building> _buildings = new(); // by tile
    private readonly List<TradeShip> _tradeShips = new();
    private readonly List<Warship> _warships = new();
    private readonly List<Shell> _shells = new();
    private readonly List<Building> _constructing = new();
    private readonly Dictionary<Building, Station> _stations = new();
    private readonly List<Railroad> _railroads = new();
    private readonly List<Train> _trains = new();
    private bool _clustersDirty;
    private int _nextWarshipId = 1;
    private int _nextUnitId = 1; // attacks and boats
    private readonly Dictionary<Building, PortTrade> _ports = new();
    private readonly Dictionary<(int, int), int[]?> _tradeRoutes = new();
    private Waterways.RouteJob? _routeJob;           // trade route being planned
    private (int From, int To) _routeJobPorts;
    private const int RoutePlanningBudget = 15_000;  // tile expansions per tick (~3 ms)

    // The AI's troop boats plan their sea route in the background, on the trade-route planner
    // (a long voyage can take tens of ms to plan, too much for one tick): boarding, first come
    // first served, ahead of trade routes. People's boats still leave at once.
    private readonly Queue<(Player Player, BoatPlan Plan, long Troops, ushort Target)> _boarding = new();
    private (Player Player, BoatPlan Plan, long Troops, ushort Target) _boardingNow;
    private Waterways.RouteJob? _boatJob;
    private const int BoatPlanningBudget = 30_000;   // tile expansions per tick (~6 ms)
    private Waterways? _waterways;
    private readonly bool[]? _coastal;
    private readonly List<Intent> _pending = new();
    private readonly List<IPlayerAi> _ais = new();
    private readonly List<Building> _demolishing = new();
    private readonly Rng _rng;
    private readonly long[] _troopsLastSecond = new long[ushort.MaxValue];

    /// <summary>How strong nations are this match.</summary>
    public readonly Difficulty Difficulty;

    /// <summary>OpenFront's tile-measured numbers, adjusted to this map's tile size.</summary>
    public readonly ScaledRules Rules;

    public Game(GameMap map, ulong seed, int spawnPhaseTicks = Balance.SpawnPhaseTicks, Difficulty difficulty = Difficulty.Medium)
    {
        Map = map;
        Seed = seed;
        Rules = new ScaledRules(map.TileScale);
        Difficulty = difficulty;
        Fallout = new bool[map.TileCount];
        SpawnPhaseTicks = spawnPhaseTicks;
        Owner = new ushort[map.TileCount];
        HatchedUntil = new int[map.TileCount];
        _hatchedFrom = new ushort[map.TileCount];
        CoverTenths = AttackRules.TileCoverTenths(map);
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
    public IReadOnlyList<Warship> Warships => _warships;
    public IReadOnlyList<Shell> Shells => _shells;
    public IReadOnlyList<Railroad> Railroads => _railroads;
    public IReadOnlyList<Train> Trains => _trains;

    /// <summary>Tsar Bombas launched so far (each one makes the next dearer).</summary>
    public int TsarBombasLaunched { get; internal set; }

    /// <summary>Water bodies and sea routes for boats (worked out on first use; World.cs does it at match start).</summary>
    public Waterways Waterways => _waterways ??= new Waterways(Map, Rules.Linear);
    public Player? GetPlayer(ushort id) => id > 0 && id < _players.Count ? _players[id] : null;
    public int SecondsUntilStart => Math.Max(0, (SpawnPhaseTicks - Tick + Balance.TicksPerSecond - 1) / Balance.TicksPerSecond);

    public Player AddPlayer(string name, PlayerKind kind = PlayerKind.Human, string flag = "")
    {
        if (_players.Count >= ushort.MaxValue - 1)
            throw new InvalidOperationException("Too many players.");
        var player = new Player((ushort)_players.Count, name, kind, Difficulty, flag) { Troops = Balance.StartTroopsFor(kind, Difficulty), AreaScale = Rules.Area };
        _players.Add(player);
        return player;
    }

    private readonly List<int> _aiSpawns = new();

    /// <summary>Adds nations (the proper AI opponents, named after countries) and places them, spread apart.</summary>
    public void AddNations(int count)
    {
        var countries = new List<(string Code, string Name)>(Countries.All);
        for (int b = 0; b < count; b++)
        {
            // Each nation gets a different country's name and flag (numbered if we run out).
            int pick = _rng.RangeInt(0, countries.Count - 1);
            var (code, name) = countries[pick];
            countries.RemoveAt(pick);
            if (countries.Count == 0)
                countries.AddRange(Countries.All);
            var nation = AddPlayer(b < Countries.All.Length ? name : $"{name} {b / Countries.All.Length + 1}", PlayerKind.Nation, code);
            _ais.Add(new NationAi(this, nation, new Rng(_rng.Fork())));
            SpawnAi(nation);
        }
    }

    /// <summary>
    /// Adds a map's own nations, each starting near its real-world home (like OpenFront's
    /// map nations): a random free spot within 25 tiles, or failing that the nearest free land.
    /// </summary>
    public void AddNations(IReadOnlyList<NationSpec> nations)
    {
        var geo = Map.Geo;
        foreach (var spec in nations)
        {
            var nation = AddPlayer(spec.Name, PlayerKind.Nation, spec.Flag);
            _ais.Add(new NationAi(this, nation, new Rng(_rng.Fork())));
            if (geo is null)
            {
                SpawnAi(nation);
                continue;
            }
            int x = Math.Clamp((int)geo.TileX(spec.Lon, Map.Width), 0, Map.Width - 1);
            int y = Math.Clamp((int)geo.TileY(spec.Lat, Map.Height), 0, Map.Height - 1);
            int tile = NationSpawnTile(x, y);
            if (tile < 0)
            {
                SpawnAi(nation);
                continue;
            }
            _aiSpawns.Add(tile);
            Spawn(nation, tile);
        }
    }

    private int NationSpawnTile(int x, int y)
    {
        int Delta = Rules.NationSpawnSpread;
        for (int tries = 0; tries < 50; tries++)
        {
            int tx = _rng.RangeInt(x - Delta, x + Delta), ty = _rng.RangeInt(y - Delta, y + Delta);
            if (!Map.InBounds(tx, ty))
                continue;
            int t = Map.Index(tx, ty);
            if (!Map.IsLand(t) || Owner[t] != Nobody)
                continue;
            if (Map.Terrain[t] == TerrainType.Mountains && _rng.RangeInt(0, 1) == 0)
                continue; // prefer not to start on a mountain
            return t;
        }
        // Nothing free close by (or the home is off the map): nearest free land.
        int start = Map.Index(x, y), w = Map.Width;
        var seen = new HashSet<int> { start };
        var queue = new Queue<int>();
        queue.Enqueue(start);
        while (queue.Count > 0 && seen.Count < 400_000)
        {
            int t = queue.Dequeue();
            if (Map.IsLand(t) && Owner[t] == Nobody)
                return t;
            int cx = t % w, cy = t / w;
            if (cx > 0 && seen.Add(t - 1)) queue.Enqueue(t - 1);
            if (cx < w - 1 && seen.Add(t + 1)) queue.Enqueue(t + 1);
            if (cy > 0 && seen.Add(t - w)) queue.Enqueue(t - w);
            if (cy < Map.Height - 1 && seen.Add(t + w)) queue.Enqueue(t + w);
        }
        return -1;
    }

    /// <summary>Adds bots: OpenFront's weak filler players with made-up names and no flag.</summary>
    public void AddBots(int count)
    {
        var names = new Rng(_rng.Fork());
        for (int b = 0; b < count; b++)
        {
            var bot = AddPlayer(BotNames.Random(names), PlayerKind.Bot);
            _ais.Add(new BotAi(this, bot, new Rng(_rng.Fork())));
            SpawnAi(bot);
        }
    }

    private void SpawnAi(Player player)
    {
        int tile = FindBotSpawn(_aiSpawns);
        if (tile < 0)
            return;
        _aiSpawns.Add(tile);
        Spawn(player, tile);
    }

    public void Submit(Intent intent) => _pending.Add(intent);

    /// <summary>Advances the game by one tick.</summary>
    public void Step()
    {
        long applyStart = Stopwatch.GetTimestamp();
        foreach (var intent in _pending)
            Apply(intent);
        _pending.Clear();
        StepWorst[7] = Math.Max(StepWorst[7], Stopwatch.GetTimestamp() - applyStart);

        if (Phase == GamePhase.Spawning)
        {
            if (StartsWhenSpawned != Nobody)
            {
                // Waiting (time stands still) until that player has spawned.
                if (GetPlayer(StartsWhenSpawned) is { HasSpawned: true })
                {
                    SpawnPhaseTicks = Tick;
                    Phase = GamePhase.Running;
                }
                return;
            }
            if (++Tick >= SpawnPhaseTicks)
                Phase = GamePhase.Running;
            return;
        }

        long t0 = Stopwatch.GetTimestamp();
        foreach (var ai in _ais)
            ai.Think();
        StepWorst[8] = Math.Max(StepWorst[8], Stopwatch.GetTimestamp() - t0);
        RunAlliances();
        SendReplies();
        long t1 = Stopwatch.GetTimestamp();
        SettleHatchedLand();
        GrowEconomies();
        PlanBoardingBoats();
        SailBoats();
        long t2 = Stopwatch.GetTimestamp();
        long q = t2;
        void Lap(int part)
        {
            long now = Stopwatch.GetTimestamp();
            StepWorst[part] = Math.Max(StepWorst[part], now - q);
            q = now;
        }
        RunPorts(); Lap(0);
        SailTradeShips(); Lap(1);
        FinishConstruction(); LayRails(); Lap(2);
        RunTrains(); Lap(3);
        RunWarships(); FlyShells(); Lap(4);
        RunNukes(); Lap(5);
        long te = Stopwatch.GetTimestamp();
        RemoveClusters();
        q = te;
        Lap(6);
        Profile[4] += Stopwatch.GetTimestamp() - te;
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
            case SpawnIntent late when player.LateSpawn && !player.HasSpawned:
                Spawn(player, late.Tile, takeOwned: true);
                break;
            case AttackIntent attack when Phase == GamePhase.Running:
                StartAttack(player, attack.TargetId, attack.Troops > 0
                    ? Math.Min(attack.Troops, player.Troops)
                    : player.Troops * Math.Clamp(attack.RatioPercent, 1, 100) / 100);
                break;
            case DirectedAttackIntent directed when Phase == GamePhase.Running:
                StartDirectedAttack(player, directed);
                break;
            case NukeIntent nuke:
                LaunchNuke(player, nuke.Type, nuke.Tile, nuke.DirectionUp);
                break;
            case AllianceIntent ally when Phase == GamePhase.Running && GetPlayer(ally.TargetId) is { } other:
                RequestAlliance(player, other);
                break;
            case BreakAllianceIntent split when GetPlayer(split.TargetId) is { } former:
                BreakAlliance(player, former);
                break;
            case EmbargoIntent embargo when GetPlayer(embargo.TargetId) is { } trader:
                SetEmbargo(player, trader, embargo.Start);
                break;
            case TargetIntent mark when Phase == GamePhase.Running && GetPlayer(mark.TargetId) is { } marked:
                MarkTarget(player, marked);
                break;
            case EmojiIntent emoji when Phase == GamePhase.Running:
                SendEmoji(player, emoji.RecipientId, emoji.Emoji);
                break;
            case QuickChatIntent chat when Phase == GamePhase.Running && GetPlayer(chat.RecipientId) is { } listener:
                SendQuickChat(player, listener, chat.Phrase);
                break;
            case RetreatIntent retreat:
                OrderRetreat(player, retreat.AttackId);
                break;
            case BoatRetreatIntent recall when _boats.Find(b => b.Id == recall.BoatId && b.Owner == player) is { } called:
                called.Retreating = true;
                break;
            case RejectAllianceIntent no when GetPlayer(no.RequesterId) is { } asker && PendingRequest(asker, player) is { } request:
                RejectAlliance(request);
                break;
            case DemolishIntent demolish when Phase == GamePhase.Running:
                StartDemolition(player, demolish.Tile);
                break;
            case BoatIntent boat when Phase == GamePhase.Running:
                LaunchBoat(player, boat.Tile, boat.RatioPercent, boat.Troops);
                break;
            case MoveWarshipIntent move when _warships.Find(w => w.Id == move.WarshipId && w.Owner == player) is { } ship
                && move.Tile >= 0 && move.Tile < Map.TileCount && Waterways.Body[move.Tile] == Waterways.Body[ship.Tile]:
                ship.PatrolTile = move.Tile;
                ship.Path = null; // heads for the new patrol area at once
                break;
            case BuildIntent { Type: BuildingType.Warship } ship when Phase == GamePhase.Running && CanBuild(player, ship.Type, ship.Tile):
                player.Spend(BuildingCost(player, BuildingType.Warship));
                player.Constructed[(int)BuildingType.Warship]++;
                var (_, launch) = WarshipLaunch(player, ship.Tile);
                _warships.Add(new Warship(_nextWarshipId++, player, launch, ship.Tile, _rng.Fork()));
                break;
            case BuildIntent build when Phase == GamePhase.Running && CanBuild(player, build.Type, build.Tile):
                player.Spend(BuildingCost(player, build.Type));
                player.Constructed[(int)build.Type]++;
                if (UpgradeTarget(player, build.Type, build.Tile) is { } existing)
                {
                    existing.Level++; // stacking: one more level, straight away (like OpenFront's upgrades)
                    if (existing.Type == BuildingType.City && existing.IsReady(Tick))
                        player.CityLevels++;
                    break;
                }
                int buildTicks = player.InstantBuild ? 0 : Balance.ConstructionTicks(build.Type);
                var building = new Building(build.Type, build.Tile, player.Id, Tick, Tick + buildTicks);
                _buildings[build.Tile] = building;
                _constructing.Add(building);
                if (build.Type == BuildingType.Port)
                    _ports[building] = new PortTrade(_rng.Fork(), Tick % TradeRules.PortCheckTicks);
                break;
        }
    }

    /// <summary>Dev option: lets a player who hasn't spawned yet drop in after the spawn phase (see <see cref="Player.LateSpawn"/>).</summary>
    public void AllowLateSpawn(Player player) => player.LateSpawn = true;

    private void Spawn(Player player, int tile, bool takeOwned = false)
    {
        if (tile < 0 || tile >= Map.TileCount || !Map.IsLand(tile) || (Owner[tile] != Nobody && !takeOwned) || Fallout[tile])
            return;

        // Moving your spawn during the spawn phase: give back the old land first.
        foreach (int t in player.SpawnArea)
            if (Owner[t] == player.Id)
                SetOwner(t, Nobody);
        player.SpawnArea.Clear();

        // Spread out over connected land only, so a spawn never jumps across a river.
        int cx = tile % Map.Width, cy = tile / Map.Width, r = Rules.SpawnRadius;
        var frontier = new Queue<int>();
        var seen = new HashSet<int> { tile };
        frontier.Enqueue(tile);
        Span<int> nb = stackalloc int[4];
        while (frontier.Count > 0)
        {
            int t = frontier.Dequeue();
            if (Owner[t] != Nobody && !takeOwned)
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
        player.Troops = Balance.StartTroopsFor(player.Kind, Difficulty);
    }

    private int FindBotSpawn(List<int> taken)
    {
        // Best of many random tries: the spot furthest from existing spawns. On crowded
        // maps that may be closer than MinSpawnSpacing, which beats not spawning at all.
        long wanted = (long)Rules.MinSpawnSpacing * Rules.MinSpawnSpacing;
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
            p.Troops = Math.Max(0, p.Troops + Balance.TroopGrowth(p.Troops, p.MaxTroops, p.Kind, Difficulty));
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

    /// <summary>Price of the player's next building of this type (rises with each one, like OpenFront).</summary>
    public long BuildingCost(Player player, BuildingType type)
    {
        // Like OpenFront: every level you own counts (a building still under construction
        // counts once), but never more than you built yourself.
        int Count(BuildingType t) => Math.Min(t == BuildingType.Warship ? CountWarships(player) : CountLevels(player, t), player.Constructed[(int)t]);
        int owned = type is BuildingType.Port or BuildingType.Factory
            ? Count(BuildingType.Port) + Count(BuildingType.Factory)
            : Count(type);
        return Balance.BuildingCost(type, owned);
    }

    /// <summary>Total levels of this building type the player owns (under construction counts once).</summary>
    public int CountLevels(Player player, BuildingType type)
    {
        int n = 0;
        foreach (var b in _buildings.Values)
            if (b.Owner == player.Id && b.Type == type)
                n += b.IsReady(Tick) ? b.Level : 1;
        return n;
    }

    /// <summary>
    /// The player's own finished building of this type that a new one placed here would
    /// stack onto (the nearest within building spacing), or null.
    /// </summary>
    public Building? UpgradeTarget(Player player, BuildingType type, int tile)
    {
        if (!Balance.Stackable(type) || tile < 0 || tile >= Map.TileCount)
            return null;
        int spacing = Rules.MinBuildingSpacing, x = tile % Map.Width, y = tile / Map.Width;
        Building? best = null;
        long bestDist = long.MaxValue;
        foreach (var b in _buildings.Values)
        {
            if (b.Owner != player.Id || b.Type != type || !b.IsReady(Tick))
                continue;
            long dx = b.Tile % Map.Width - x, dy = b.Tile / Map.Width - y, d = dx * dx + dy * dy;
            if (d < (long)spacing * spacing && d < bestDist)
                (best, bestDist) = (b, d);
        }
        return best;
    }

    /// <summary>The player's buildings of one type (any, if null), in tile order.</summary>
    public List<Building> BuildingsOf(Player player, BuildingType? type = null)
    {
        var list = new List<Building>();
        foreach (var b in _buildings.Values)
            if (b.Owner == player.Id && (type is null || b.Type == type))
                list.Add(b);
        list.Sort((a, b) => a.Tile.CompareTo(b.Tile));
        return list;
    }

    /// <summary>Which rail network a building's station is on: -1 if it's a lone station, null if it isn't one.</summary>
    internal int? StationCluster(Building b) => _stations.TryGetValue(b, out var s) ? s.Cluster : null;

    /// <summary>Whether a brand-new building (not a stack onto one of ours) may go here.</summary>
    public bool CanBuildNew(Player player, BuildingType type, int tile) =>
        CanBuild(player, type, tile) && UpgradeTarget(player, type, tile) is null;

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
        if (type == BuildingType.Warship)
            return Phase == GamePhase.Running && player.IsAlive && player.CanAfford(BuildingCost(player, type))
                && WarshipLaunch(player, tile).Port is not null;
        if (Phase != GamePhase.Running || tile < 0 || tile >= Map.TileCount)
            return false;
        if (!player.CanAfford(BuildingCost(player, type)))
            return false;
        if (UpgradeTarget(player, type, tile) is not null)
            return true; // stacking onto one of ours
        if (Owner[tile] != player.Id)
            return false;
        if (type == BuildingType.Port && !IsCoastal(tile))
            return false;
        int spacing = Rules.MinBuildingSpacing, x = tile % Map.Width, y = tile / Map.Width;
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

    /// <summary>The player's troop boats at sea (and still boarding).</summary>
    public int BoatsAtSea(Player player)
    {
        int n = 0;
        foreach (var b in _boats)
            if (b.Owner == player)
                n++;
        foreach (var b in _boarding)
            if (b.Player == player)
                n++;
        if (_boatJob is not null && _boardingNow.Player == player)
            n++;
        return n;
    }

    /// <summary>
    /// Sends troops by sea: picks the shore nearest the clicked tile that the player can
    /// reach by water, the player's own shore closest to it, and sails between them.
    /// </summary>
    private void LaunchBoat(Player player, int tile, int ratioPercent, long exactTroops = 0)
    {
        if (BoatsAtSea(player) >= Balance.MaxBoatsPerPlayer)
            return;
        long troops = exactTroops > 0
            ? Math.Min(exactTroops, player.Troops)
            : player.Troops * Math.Clamp(ratioPercent, 1, 100) / 100;
        if (troops <= 0)
            return;
        if (PlanBoat(player, tile) is not { } plan)
            return;
        if (player.Kind != PlayerKind.Human)
        {
            player.Troops -= troops; // aboard, waiting for the route
            _boarding.Enqueue((player, plan, troops, Owner[tile]));
            return;
        }
        int[]? path = Waterways.FindPath(plan.LaunchWater, plan.LandingWater);
        if (path is null)
            return;
        player.Troops -= troops;
        SetSail(player, plan, troops, Owner[tile], path);
    }

    private void SetSail(Player player, BoatPlan plan, long troops, ushort target, int[] path)
    {
        player.AttacksSent++;
        _boats.Add(new Boat(player, troops, plan.Landing, plan.Source, path) { Id = _nextUnitId++ });
        if (GetPlayer(target) is { } heading)
            Report(GameEventKind.BoatLaunched, player, heading);
    }

    /// <summary>Plans the routes of boarding AI boats on the background planner, and sends them off once planned.</summary>
    private void PlanBoardingBoats()
    {
        int budget = BoatPlanningBudget;
        while (budget > 0)
        {
            if (_boatJob is null)
            {
                if (_boarding.Count == 0)
                    return;
                var next = _boarding.Peek();
                if (Waterways.StartBackground(next.Plan.LaunchWater, next.Plan.LandingWater) is not { } job)
                    return; // a trade route is being planned: wait for it
                _boarding.Dequeue();
                _boardingNow = next;
                _boatJob = job;
                if (!job.Done)
                    Waterways.StepBackground(budget); // the rough route over the coarse grid
                budget /= 2;
                continue;
            }
            if (!_boatJob.Done)
            {
                Waterways.StepBackground(budget);
                budget = 0;
            }
            if (!_boatJob.Done)
                return;
            var (player, plan, troops, target) = _boardingNow;
            var path = _boatJob.Result;
            _boatJob = null;
            _boardingNow = default;
            if (!player.IsAlive)
                continue;
            if (path is null)
                player.Troops += troops; // no way there after all: back ashore
            else
                SetSail(player, plan, troops, target, path);
        }
    }

    private Player? _boatPlanFor;
    private int _boatPlanTick = -1, _boatPlanCoast;
    private readonly List<int> _boatPlanShores = new();
    private readonly HashSet<int> _boatPlanBodies = new();

    /// <summary>A boat the player could send at this tile: our shore it leaves from, the shore it lands on, and the water tiles beside each.</summary>
    public readonly record struct BoatPlan(int Source, int Landing, int LaunchWater, int LandingWater);

    /// <summary>
    /// Where a boat sent at this tile would set off and land (OpenFront's canBuildTransportShip),
    /// without planning the route at sea. Null if there's no way to get there by boat.
    /// </summary>
    public BoatPlan? PlanBoat(Player player, int tile, int maxSearch = int.MaxValue)
    {
        if (tile < 0 || tile >= Map.TileCount || !Map.IsLand(tile) || Owner[tile] == player.Id)
            return null;
        if (player.Allies.Contains(Owner[tile]))
            return null; // no landings on allies

        // Which water bodies does the player have a shore on? (Kept for the rest of the tick:
        // the AI plans many boats at once.)
        var ways = Waterways;
        Span<int> bodies = stackalloc int[4];
        if (_boatPlanFor != player || _boatPlanTick != Tick || _boatPlanCoast != player.Coast.Count)
        {
            _boatPlanFor = player;
            _boatPlanTick = Tick;
            _boatPlanCoast = player.Coast.Count;
            _boatPlanShores.Clear();
            _boatPlanShores.AddRange(player.Coast);
            _boatPlanShores.Sort(); // HashSet order isn't guaranteed; keep every machine identical
            _boatPlanBodies.Clear();
            foreach (int t in _boatPlanShores)
            {
                int n = ways.TouchingBodies(t, bodies);
                for (int k = 0; k < n; k++)
                    _boatPlanBodies.Add(bodies[k]);
            }
        }
        var myShores = _boatPlanShores;
        var myBodies = _boatPlanBodies;
        if (myShores.Count == 0)
            return null; // no water access

        // Landing: the clicked owner's own coast nearest the click (like OpenFront), searched
        // only through that owner's land (or unclaimed land) and not too far from the click.
        // If they have no coast near there on our waters, no boat goes at all.
        ushort targetOwner = Owner[tile];
        int cx = tile % Map.Width, cy = tile / Map.Width;
        long reach2 = (long)Rules.BoatLandingSearchRadius * Rules.BoatLandingSearchRadius;
        int landing = -1, landingBody = Waterways.NoBody;
        var queue = new Queue<int>();
        var seen = new HashSet<int> { tile };
        queue.Enqueue(tile);
        Span<int> nb = stackalloc int[4];
        int searchLimit = maxSearch == int.MaxValue ? Rules.BoatLandingSearchTiles : Math.Min(maxSearch + 1, Rules.BoatLandingSearchTiles);
        while (queue.Count > 0 && seen.Count < searchLimit)
        {
            int t = queue.Dequeue();
            int touching = ways.TouchingBodies(t, bodies);
            for (int k = 0; k < touching && landing < 0; k++)
                if (myBodies.Contains(bodies[k]))
                {
                    landing = t;
                    landingBody = bodies[k];
                }
            if (landing >= 0)
                break;
            int count = LandNeighbours(t, nb);
            for (int k = 0; k < count; k++)
            {
                int n = nb[k];
                long dx = n % Map.Width - cx, dy = n / Map.Width - cy;
                if (Owner[n] == targetOwner && dx * dx + dy * dy <= reach2 && seen.Add(n))
                    queue.Enqueue(n);
            }
        }
        if (landing < 0)
            return null;

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
            return null;
        return new BoatPlan(source, landing, ways.ShoreWater(source, landingBody), ways.ShoreWater(landing, landingBody));
    }

    private void SailBoats()
    {
        for (int i = 0; i < _boats.Count; i++)
        {
            var boat = _boats[i];
            if (!boat.Owner.IsAlive || boat.Sunk)
            {
                _boats.RemoveAt(i--); // nobody left to land for
                continue;
            }
            if (boat.Retreating)
            {
                // Sails back the way it came, then comes ashore where it set off: on our own
                // coast that costs a quarter of the troops (see Land); if the coast has been
                // taken meanwhile, it lands there as an attack, like OpenFront's.
                boat.Progress = Math.Max(0, boat.Progress - Rules.BoatSpeedPerTick);
                if (boat.Progress > 0)
                    continue;
                _boats.RemoveAt(i--);
                boat.LandingTile = boat.SourceTile;
                Land(boat);
                continue;
            }
            boat.Progress += Rules.BoatSpeedPerTick;
            if (!boat.Arrived)
                continue;
            _boats.RemoveAt(i--);
            Land(boat);
        }
    }

    // ------------------------------------------------------------------
    //  Trade
    // ------------------------------------------------------------------

    /// <summary>Each port rolls now and then to send a trade ship to another player's port.</summary>
    private void RunPorts()
    {
        PlanTradeRoutes();
        foreach (var (port, trade) in _ports)
        {
            if (!port.IsReady(Tick) || (Tick + trade.CheckOffset) % TradeRules.PortCheckTicks != 0)
                continue;
            // One roll per level (like OpenFront), with a pity timer between failed rolls.
            bool send = false;
            for (int level = 0; level < port.Level && !send; level++)
            {
                int odds = TradeRules.SpawnOdds(trade.Rejections, _tradeShips.Count);
                send = odds != int.MaxValue && trade.Rng.RangeInt(0, odds - 1) == 0;
                trade.Rejections = send ? 0 : trade.Rejections + 1;
            }
            if (!send)
                continue;

            var destination = PickTradePartner(port, trade.Rng);
            if (destination is null)
                continue;
            if (!_tradeRoutes.TryGetValue((port.Tile, destination.Tile), out int[]? route))
            {
                RequestTradeRoute(port, destination); // planned over the next few ticks
                trade.Rejections++;                   // so try again on the next roll
                continue;
            }
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
            if (dest.Owner == port.Owner || !_players[dest.Owner].IsAlive || !dest.IsReady(Tick) || !CanTrade(port.Owner, dest.Owner))
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
            // Higher-level ports are likelier destinations: one entry per level.
            for (int level = 0; level < candidates[i].Port.Level; level++)
            {
                weighted.Add(candidates[i].Port);
                if (i < bonus && candidates[i].Distance >= Rules.ShortRangeDebuffTiles)
                    weighted.Add(candidates[i].Port);
            }
        }
        return weighted[rng.RangeInt(0, weighted.Count - 1)];
    }

    /// <summary>
    /// Starts planning the sea route between two ports in the background, unless the
    /// planner is busy (the port will simply ask again later). Routes are remembered,
    /// since the same pairs trade again and again.
    /// </summary>
    private void RequestTradeRoute(Building from, Building to)
    {
        if (_routeJob is not null)
            return;
        Span<int> bodies = stackalloc int[4];
        int n = Waterways.TouchingBodies(from.Tile, bodies);
        for (int i = 0; i < n; i++)
        {
            int a = Waterways.ShoreWater(from.Tile, bodies[i]), b = Waterways.ShoreWater(to.Tile, bodies[i]);
            if (a < 0 || b < 0)
                continue;
            _routeJob = Waterways.StartBackground(a, b);
            _routeJobPorts = (from.Tile, to.Tile);
            return;
        }
        _tradeRoutes[(from.Tile, to.Tile)] = null; // no shared water: these two never trade
    }

    private void PlanTradeRoutes()
    {
        if (_routeJob is null)
            return;
        Waterways.StepBackground(RoutePlanningBudget);
        if (!_routeJob.Done)
            return;
        var route = _routeJob.Result;
        _tradeRoutes[_routeJobPorts] = route;
        // The same water works the other way round.
        int[]? back = route is null ? null : (int[])route.Clone();
        if (back is not null)
            Array.Reverse(back);
        _tradeRoutes.TryAdd((_routeJobPorts.To, _routeJobPorts.From), back);
        _routeJob = null;
    }

    private void SailTradeShips()
    {
        for (int i = 0; i < _tradeShips.Count; i++)
        {
            var ship = _tradeShips[i];
            // Cancelled if the destination is gone or changed hands (a captured ship needs
            // the captor's port; a normal one must not end up trading with itself).
            bool lost = ship.Captured ? ship.To.Owner != ship.Owner.Id : ship.To.Owner == ship.Owner.Id;
            bool embargoed = !ship.Captured && !CanTrade(ship.Owner.Id, ship.To.Owner); // OpenFront: an embargo cancels it
            if (ship.To.Removed || lost || embargoed || !ship.Owner.IsAlive)
            {
                _tradeShips.RemoveAt(i--);
                continue;
            }
            ship.Progress += Rules.TradeShipSpeedPerTick;
            if (!ship.Arrived)
                continue;
            _tradeShips.RemoveAt(i--);

            long gold = TradeRules.Gold((int)Math.Round((ship.EarlierTiles + ship.Path.Length) / Rules.Linear)); // in reference tiles
            if (ship.Captured)
            {
                ship.Owner.Gold += gold; // the captor keeps it all
                ReportGold(ship.Owner, gold, ship.To.Tile);
                continue;
            }
            // Both ends profit: whoever owns each port when the ship docks.
            if (!ship.From.Removed)
            {
                _players[ship.From.Owner].Gold += gold;
                ReportGold(_players[ship.From.Owner], gold, ship.From.Tile);
            }
            _players[ship.To.Owner].Gold += gold;
            ReportGold(_players[ship.To.Owner], gold, ship.To.Tile);
            ship.Owner.TradeGold += gold;
        }
    }

    // ------------------------------------------------------------------
    //  Buildings: construction, capture, effects
    // ------------------------------------------------------------------

    /// <summary>Price of a bomb (OpenFront's prices; Tsar Bombas get dearer with each one launched).</summary>
    public long NukeCost(NukeType type) => type switch
    {
        NukeType.Moab => Balance.MoabCost,
        NukeType.FatMan => Balance.FatManCost,
        NukeType.TsarBomba => Balance.TsarBombaBaseCost + TsarBombasLaunched * Balance.TsarBombaCostPerLaunch,
        _ => 0,
    };

    /// <summary>Dev cheats for testing: endless gold and/or buildings that finish instantly.</summary>
    public void SetCheats(Player player, bool infiniteGold, bool instantBuild)
    {
        player.InfiniteGold = infiniteGold;
        player.InstantBuild = instantBuild;
    }

    /// <summary>True if this tile is within range of one of the owner's finished defense posts.</summary>
    public bool DefendedByPost(int tile, ushort owner)
    {
        if (owner == Nobody)
            return false;
        var posts = _players[owner].DefensePosts;
        if (posts.Count == 0)
            return false;
        int x = tile % Map.Width, y = tile / Map.Width, r = Rules.DefensePostRange;
        foreach (int post in posts)
        {
            int dx = post % Map.Width - x, dy = post / Map.Width - y;
            if (dx * dx + dy * dy <= r * r)
                return true;
        }
        return false;
    }

    private void StartDemolition(Player player, int tile)
    {
        if (BuildingAt(tile) is not { } b || b.Owner != player.Id || b.DemolishTick >= 0 || Tick < player.NextDemolishTick)
            return;
        b.DemolishTick = Tick + Balance.DemolishTicks;
        player.NextDemolishTick = Tick + Balance.DemolishCooldownTicks;
        _demolishing.Add(b);
    }

    /// <summary>Buildings start working once built, and come down when their demolition is due.</summary>
    private void FinishConstruction()
    {
        for (int i = 0; i < _demolishing.Count; i++)
        {
            var b = _demolishing[i];
            if (b.Removed || b.DemolishTick < 0) // gone already, or captured (which cancels it)
            {
                _demolishing.RemoveAt(i--);
                continue;
            }
            if (Tick < b.DemolishTick)
                continue;
            _demolishing.RemoveAt(i--);
            RemoveBuilding(b);
        }

        for (int i = 0; i < _constructing.Count; i++)
        {
            var b = _constructing[i];
            if (b.Removed)
            {
                _constructing.RemoveAt(i--);
                continue;
            }
            if (!b.IsReady(Tick))
                continue;
            _constructing.RemoveAt(i--);
            Activate(b, _players[b.Owner]);
            if (b.Type == BuildingType.Factory)
                OnFactoryBuilt(b);
            else if (b.Type is BuildingType.City or BuildingType.Port && FactoryNear(b.Tile))
                AddStation(b);
        }
    }

    /// <summary>Applies a finished building's lasting effect for its owner.</summary>
    private void Activate(Building b, Player owner)
    {
        if (b.Type == BuildingType.City)
            owner.CityLevels += b.Level;
        else if (b.Type == BuildingType.Defense)
            owner.DefensePosts.Add(b.Tile);
    }

    private void Deactivate(Building b, Player owner)
    {
        if (b.Type == BuildingType.City)
            owner.CityLevels -= b.Level;
        else if (b.Type == BuildingType.Defense)
            owner.DefensePosts.Remove(b.Tile);
    }

    private void TransferBuilding(Building b, ushort newOwner)
    {
        b.DemolishTick = -1; // the new owner didn't order it
        if (b.IsReady(Tick))
        {
            Deactivate(b, _players[b.Owner]);
            Activate(b, _players[newOwner]);
        }
        b.Owner = newOwner;
    }

    private void RemoveBuilding(Building b)
    {
        if (b.IsReady(Tick))
            Deactivate(b, _players[b.Owner]);
        _buildings.Remove(b.Tile);
        _ports.Remove(b);
        b.Removed = true;
        RemoveStation(b);
    }

    // ------------------------------------------------------------------
    //  Railroads and trains (factories)
    // ------------------------------------------------------------------

    private bool FactoryNear(int tile)
    {
        int x = tile % Map.Width, y = tile / Map.Width, r = Rules.StationMaxRange;
        foreach (var s in _stations.Keys)
        {
            if (s.Type != BuildingType.Factory)
                continue;
            int dx = s.Tile % Map.Width - x, dy = s.Tile / Map.Width - y;
            if (dx * dx + dy * dy <= r * r)
                return true;
        }
        return false;
    }

    /// <summary>A new factory: it becomes a station, and so does every city, port or factory around it.</summary>
    private void OnFactoryBuilt(Building factory)
    {
        AddStation(factory);
        int x = factory.Tile % Map.Width, y = factory.Tile / Map.Width, r = Rules.StationMaxRange;
        var nearby = new List<(Building B, int D)>();
        foreach (var b in _buildings.Values)
        {
            if (b == factory || _stations.ContainsKey(b) || !b.IsReady(Tick)
                || b.Type is not (BuildingType.City or BuildingType.Port or BuildingType.Factory))
                continue;
            int dx = b.Tile % Map.Width - x, dy = b.Tile / Map.Width - y, d = dx * dx + dy * dy;
            if (d <= r * r)
                nearby.Add((b, d));
        }
        nearby.Sort((a, b) => a.D != b.D ? a.D.CompareTo(b.D) : a.B.Tile.CompareTo(b.B.Tile));
        foreach (var (b, _) in nearby)
            AddStation(b);
    }

    /// <summary>
    /// Makes a station and lays rail to nearby stations (closest first), skipping ones that
    /// are too close or already within a few stops on the network.
    /// </summary>
    private void AddStation(Building b)
    {
        if (_stations.ContainsKey(b))
            return;
        var station = new Station(b);
        if (b.Type == BuildingType.Factory)
            station.Rng = new Rng(_rng.Fork());
        _stations[b] = station;
        _clustersDirty = true;

        int x = b.Tile % Map.Width, y = b.Tile / Map.Width;
        int max = Rules.StationMaxRange, min = Rules.StationMinRange;
        var nearby = new List<(Station S, int D)>();
        foreach (var other in _stations.Values)
        {
            if (other == station)
                continue;
            int dx = other.Building.Tile % Map.Width - x, dy = other.Building.Tile / Map.Width - y, d = dx * dx + dy * dy;
            if (d <= max * max && d > min * min)
                nearby.Add((other, d));
        }
        nearby.Sort((a, c) => a.D != c.D ? a.D.CompareTo(c.D) : a.S.Building.Tile.CompareTo(c.S.Building.Tile));
        foreach (var (other, _) in nearby)
            _railQueue.Enqueue((station, other)); // laid over the next few ticks, closest first
    }

    // Rail links waiting to be laid. Finding a route takes a while, so a new factory's
    // links are spread over a few ticks instead of all landing in one.
    private readonly Queue<(Station From, Station To)> _railQueue = new();
    private const int RailsPerTick = 3;

    private void LayRails()
    {
        for (int laid = 0; laid < RailsPerTick && _railQueue.Count > 0;)
        {
            var (station, other) = _railQueue.Dequeue();
            if (!_stations.ContainsKey(station.Building) || !_stations.ContainsKey(other.Building))
                continue; // one end is gone
            if (Hops(station, other, RailRules.AlreadyConnectedHops) >= 0)
                continue; // already well connected that way
            laid++;
            int[]? path = RailPath(station.Building.Tile, other.Building.Tile);
            if (path is null)
                continue;
            var rail = new Railroad(station, other, path);
            station.Rails.Add(rail);
            other.Rails.Add(rail);
            _railroads.Add(rail);
            _clustersDirty = true;
        }
    }

    /// <summary>
    /// The roads a structure placed on this tile would lay (OpenFront's ghost rails, for
    /// the placement preview): a Depot links to the closest stations and to cities, ports
    /// and depots around it; a Barracks or Harbor only joins when a Depot is in range.
    /// At most 5 roads, closest first, skipping stations a road already shown would reach
    /// within a few stops. Display only: changes nothing.
    /// </summary>
    public List<int[]> PreviewRails(BuildingType type, int tile)
    {
        var paths = new List<int[]>();
        if (type is not (BuildingType.City or BuildingType.Port or BuildingType.Factory))
            return paths;
        bool factory = type == BuildingType.Factory;
        if (!factory && !FactoryNear(tile))
            return paths;
        int x = tile % Map.Width, y = tile / Map.Width, max = Rules.StationMaxRange, min = Rules.StationMinRange;
        var nearby = new List<(Building B, int D)>();
        foreach (var b in _buildings.Values)
        {
            if (b.Tile == tile || !b.IsReady(Tick) || b.Type is not (BuildingType.City or BuildingType.Port or BuildingType.Factory))
                continue;
            int dx = b.Tile % Map.Width - x, dy = b.Tile / Map.Width - y, d = dx * dx + dy * dy;
            if (d <= max * max && d > min * min)
                nearby.Add((b, d));
        }
        nearby.Sort((a, c) => a.D != c.D ? a.D.CompareTo(c.D) : a.B.Tile.CompareTo(c.B.Tile));
        var linked = new List<Station>();
        foreach (var (b, _) in nearby)
        {
            if (paths.Count >= 5)
                break;
            _stations.TryGetValue(b, out var station);
            if (station is null && !factory)
                continue; // only a new Depot turns plain buildings into stations
            if (station is not null && linked.Exists(s => s == station || Hops(s, station, RailRules.AlreadyConnectedHops) >= 0))
                continue;
            if (RailPath(tile, b.Tile) is { } path)
            {
                paths.Add(path);
                if (station is not null)
                    linked.Add(station);
            }
        }
        return paths;
    }

    private void RemoveStation(Building b)
    {
        if (!_stations.Remove(b, out var station))
            return;
        foreach (var rail in station.Rails)
        {
            rail.Other(station).Rails.Remove(rail);
            _railroads.Remove(rail);
        }
        station.Rails.Clear();
        _clustersDirty = true;
    }

    /// <summary>Stops between two stations on the network (fewest), up to a limit; -1 if further or not linked.</summary>
    private static int Hops(Station from, Station to, int limit)
    {
        var frontier = new List<Station> { from };
        var seen = new HashSet<Station> { from };
        for (int hops = 1; hops <= limit && frontier.Count > 0; hops++)
        {
            var next = new List<Station>();
            foreach (var s in frontier)
                foreach (var n in s.Neighbours())
                {
                    if (n == to)
                        return hops;
                    if (seen.Add(n))
                        next.Add(n);
                }
            frontier = next;
        }
        return -1;
    }

    // Rail search memory: a window of tiles around the start (a rail can't reach further),
    // with one search state per tile and heading (up, down, left, right), reused between
    // searches. A state's entries count only if its mark is the current search.
    private int RailWindow => 2 * Rules.MaxRailLength + 3;
    private int[]? _railCostStore, _railFromStore, _railMarkStore;
    private short[]? _railStepsStore;
    private byte[]? _railWaterStore;
    private int RailCells => RailWindow * RailWindow * 4;
    private int[] _railCost => _railCostStore ??= new int[RailCells];
    private int[] _railFrom => _railFromStore ??= new int[RailCells];
    private int[] _railMark => _railMarkStore ??= new int[RailCells];
    private short[] _railSteps => _railStepsStore ??= new short[RailCells];
    private byte[] _railWater => _railWaterStore ??= new byte[RailCells];
    private readonly PriorityQueue<int, int> _railOpen = new();
    private int _railSearch;
    private static readonly int[] RailDx = { 0, 0, -1, 1 }, RailDy = { -1, 1, 0, 0 };

    /// <summary>
    /// A rail route between two tiles: straight runs joined by right-angle turns (no
    /// staircases), over anyone's land, bridging rivers and narrow water up to
    /// <see cref="RailRules.MaxBridgeTiles"/> wide in a straight line. At most
    /// <see cref="RailRules.MaxRailLength"/> tiles; null if there's no such route.
    /// </summary>
    private int[]? RailPath(int from, int to)
    {
        int w = Map.Width, h = Map.Height, tx = to % w, ty = to / w, fx = from % w, fy = from / w;
        if (Math.Abs(tx - fx) + Math.Abs(ty - fy) + 1 > Rules.MaxRailLength)
            return null;
        int half = RailWindow / 2, ox = fx - half, oy = fy - half;
        int State(int x, int y, int dir) => ((y - oy) * RailWindow + (x - ox)) * 4 + dir;
        int Heuristic(int x, int y)
        {
            int dx = Math.Abs(x - tx), dy = Math.Abs(y - ty);
            return RailRules.StepCost * (dx + dy) + (dx != 0 && dy != 0 ? Rules.RailTurnCost : 0);
        }

        int search = ++_railSearch;
        var open = _railOpen;
        open.Clear();
        for (int dir = 0; dir < 4; dir++) // first step out of the station, any way
            Visit(fx, fy, -1, dir, 0, 0, 0);

        while (open.TryDequeue(out int state, out int priority))
        {
            int local = state / 4, dir = state % 4;
            int x = local % RailWindow + ox, y = local / RailWindow + oy;
            int g = _railCost[state];
            if (priority > g + Heuristic(x, y))
                continue; // stale entry: reached more cheaply since
            if (x == tx && y == ty)
            {
                var path = new List<int> { Map.Index(fx, fy) };
                var back = new List<int>();
                for (int s = state; s >= 0; s = _railFrom[s])
                {
                    int l = s / 4;
                    back.Add(Map.Index(l % RailWindow + ox, l / RailWindow + oy));
                }
                back.Reverse();
                path.AddRange(back);
                return path.ToArray();
            }
            bool onWater = Map.IsWater(Map.Index(x, y));
            for (int nd = 0; nd < 4; nd++)
            {
                if ((dir ^ nd) == 1 && dir / 2 == nd / 2)
                    continue; // no U-turns
                if (onWater && nd != dir)
                    continue; // bridges run straight
                int cost = g + (nd != dir ? Rules.RailTurnCost : 0);
                Visit(x, y, state, nd, cost, _railSteps[state], _railWater[state]);
            }
        }
        return null;

        void Visit(int x, int y, int fromState, int dir, int cost, int steps, int water)
        {
            int nx = x + RailDx[dir], ny = y + RailDy[dir];
            if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                return;
            if (nx - ox < 0 || ny - oy < 0 || nx - ox >= RailWindow || ny - oy >= RailWindow)
                return;
            int n = Map.Index(nx, ny);
            bool water2 = Map.IsWater(n);
            int run = water2 ? water + 1 : 0;
            if (run > Rules.MaxBridgeTiles || (water2 && nx == tx && ny == ty))
                return;
            int nsteps = steps + 1;
            if (nsteps + Math.Abs(nx - tx) + Math.Abs(ny - ty) + 1 > Rules.MaxRailLength)
                return; // can't reach the far station within the length limit from here
            int ng = cost + (water2 ? RailRules.BridgeStepCost : RailRules.StepCost);
            int s = State(nx, ny, dir);
            if (_railMark[s] == search && _railCost[s] <= ng)
                return;
            _railMark[s] = search;
            _railCost[s] = ng;
            _railFrom[s] = fromState;
            _railSteps[s] = (short)nsteps;
            _railWater[s] = (byte)run;
            open.Enqueue(s, ng + Heuristic(nx, ny));
        }
    }

    private void UpdateClusters()
    {
        if (!_clustersDirty)
            return;
        _clustersDirty = false;
        foreach (var s in _stations.Values)
            s.Cluster = -1;
        int next = 0;
        var stack = new Stack<Station>();
        foreach (var start in StationsInOrder())
        {
            if (start.Cluster >= 0)
                continue;
            start.Cluster = next;
            stack.Push(start);
            while (stack.Count > 0)
                foreach (var n in stack.Pop().Neighbours())
                    if (n.Cluster < 0)
                    {
                        n.Cluster = next;
                        stack.Push(n);
                    }
            next++;
        }
    }

    /// <summary>Stations in a fixed order (by tile), so every machine makes the same choices.</summary>
    private List<Station> StationsInOrder()
    {
        var list = new List<Station>(_stations.Values);
        list.Sort((a, b) => a.Building.Tile.CompareTo(b.Building.Tile));
        return list;
    }

    /// <summary>Factories send out trains; trains run their routes and pay out at each city or port.</summary>
    private void RunTrains()
    {
        UpdateClusters();
        if (_stations.Count > 0)
        {
            var ordered = StationsInOrder();
            int trainUnits = _trains.Count * RailRules.UnitsPerTrain;
            foreach (var factory in ordered)
            {
                if (factory.Building.Type != BuildingType.Factory || factory.Rng is null
                    || Tick < factory.LastTrainTick + RailRules.TrainCooldownTicks)
                    continue;
                var owner = _players[factory.Building.Owner];
                int odds = RailRules.SpawnOdds(CountBuildings(owner, BuildingType.Factory), trainUnits);
                bool go = false;
                for (int i = 0; i < factory.Building.Level && !go; i++)
                    go = odds != int.MaxValue && factory.Rng.RangeInt(0, odds - 1) == 0;
                if (!go)
                    continue;

                // A random trade stop (city or port) on the same network.
                Station? destination = null;
                int seen = 0;
                foreach (var s in ordered)
                    if (s != factory && s.Cluster == factory.Cluster && s.IsTradeStop && factory.Rng.RangeInt(0, seen++) == 0)
                        destination = s;
                if (destination is null)
                    continue;
                var train = MakeTrain(owner, factory, destination);
                if (train is null)
                    continue;
                _trains.Add(train);
                factory.LastTrainTick = Tick;
                trainUnits += RailRules.UnitsPerTrain;
            }
        }

        for (int i = 0; i < _trains.Count; i++)
        {
            var train = _trains[i];
            if (!train.Source.Active || !train.Destination.Active || !train.Owner.IsAlive)
            {
                _trains.RemoveAt(i--);
                continue;
            }
            train.Progress += Rules.TrainSpeedPerTick;
            int at = (int)Math.Min(train.Progress / 1000, train.Tiles.Length - 1);
            bool stopped = false;
            while (train.NextStop < train.Stops.Length && at >= train.StopTileIndex[train.NextStop])
            {
                var stop = train.Stops[train.NextStop++];
                if (stop.Building.Owner != train.Owner.Id && !CanTrade(train.Owner.Id, stop.Building.Owner))
                {
                    stopped = true; // OpenFront: a train goes no further than an embargoed player's station
                    break;
                }
                if (stop.Active && stop.IsTradeStop)
                {
                    var stationOwner = _players[stop.Building.Owner];
                    long gold = RailRules.TrainGold(stationOwner == train.Owner, train.TradeStopsVisited);
                    if (stationOwner != train.Owner) // both sides share in someone else's stop
                    {
                        stationOwner.Gold += gold;
                        stationOwner.TrainGold += gold;
                        ReportGold(stationOwner, gold, stop.Building.Tile);
                    }
                    train.Owner.Gold += gold;
                    train.Owner.TrainGold += gold;
                    ReportGold(train.Owner, gold, stop.Building.Tile);
                    train.TradeStopsVisited++;
                }
            }
            if (stopped || train.NextStop >= train.Stops.Length)
                _trains.RemoveAt(i--);
        }
    }

    /// <summary>Shortest route over the rail network (by track length) from a factory to a stop.</summary>
    private Train? MakeTrain(Player owner, Station from, Station to)
    {
        var dist = new Dictionary<Station, int> { [from] = 0 };
        var prev = new Dictionary<Station, (Station S, Railroad R)>();
        var open = new PriorityQueue<Station, int>();
        open.Enqueue(from, 0);
        while (open.TryDequeue(out var s, out int d))
        {
            if (d > dist[s])
                continue;
            if (s == to)
                break;
            foreach (var rail in s.Rails)
            {
                var n = rail.Other(s);
                int nd = d + rail.Tiles.Length;
                if (dist.TryGetValue(n, out int old) && old <= nd)
                    continue;
                dist[n] = nd;
                prev[n] = (s, rail);
                open.Enqueue(n, nd);
            }
        }
        if (!prev.ContainsKey(to))
            return null;

        var stops = new List<Station> { to };
        var rails = new List<Railroad>();
        for (var s = to; s != from; )
        {
            var (p, r) = prev[s];
            rails.Add(r);
            stops.Add(p);
            s = p;
        }
        stops.Reverse();
        rails.Reverse();

        var tiles = new List<int>();
        var stopIndex = new int[stops.Count];
        for (int i = 0; i < rails.Count; i++)
        {
            var rail = rails[i];
            bool forward = rail.A == stops[i];
            var seg = rail.Tiles;
            int startAt = tiles.Count == 0 ? 0 : 1; // don't repeat the shared station tile
            for (int k = startAt; k < seg.Length; k++)
                tiles.Add(forward ? seg[k] : seg[seg.Length - 1 - k]);
            stopIndex[i + 1] = tiles.Count - 1;
        }
        return new Train(owner, stops.ToArray(), tiles.ToArray(), stopIndex);
    }

    // ------------------------------------------------------------------
    //  Warships
    // ------------------------------------------------------------------

    public int CountWarships(Player player)
    {
        int n = 0;
        foreach (var w in _warships)
            if (w.Owner == player)
                n++;
        return n;
    }

    /// <summary>The player's port a warship placed at this water tile would sail from, and its launch tile.</summary>
    public (Building? Port, int Launch) WarshipLaunch(Player player, int tile)
    {
        if (tile < 0 || tile >= Map.TileCount || !Map.IsWater(tile))
            return (null, -1);
        int body = Waterways.Body[tile];
        int x = tile % Map.Width, y = tile / Map.Width;
        Building? best = null;
        long bestDist = (long)Rules.WarshipMaxDistanceFromPort * Rules.WarshipMaxDistanceFromPort + 1;
        foreach (var port in _ports.Keys)
        {
            if (port.Owner != player.Id || !port.IsReady(Tick) || Waterways.ShoreWater(port.Tile, body) < 0)
                continue;
            long dx = port.Tile % Map.Width - x, dy = port.Tile / Map.Width - y, d = dx * dx + dy * dy;
            if (d < bestDist || (d == bestDist && best is not null && port.Tile < best.Tile))
            {
                bestDist = d;
                best = port;
            }
        }
        return best is null ? (null, -1) : (best, Waterways.ShoreWater(best.Tile, body));
    }

    private void RunWarships()
    {
        for (int i = 0; i < _warships.Count; i++)
        {
            var ship = _warships[i];
            if (!ship.Alive || !ship.Owner.IsAlive)
            {
                _warships.RemoveAt(i--);
                continue;
            }
            ship.PreviousTile = ship.Tile;
            if (NearOwnPort(ship))
                ship.Health = Math.Min(WarshipRules.MaxHealth, ship.Health + WarshipRules.HealPerTick);

            if (Tick >= ship.NextRetargetTick || !TargetValid(ship))
            {
                ship.Target = FindTarget(ship);
                ship.NextRetargetTick = Tick + WarshipRules.RetargetTicks;
            }

            switch (ship.Target)
            {
                case Boat boat:
                    Fire(ship, boat, boat.Tile, rateLimited: false);
                    Patrol(ship);
                    break;
                case Warship enemy:
                    Fire(ship, enemy, enemy.Tile, rateLimited: true);
                    Patrol(ship);
                    break;
                case TradeShip trade:
                    Hunt(ship, trade);
                    break;
                default:
                    Patrol(ship);
                    break;
            }
        }
    }

    private bool NearOwnPort(Warship ship)
    {
        int x = ship.Tile % Map.Width, y = ship.Tile / Map.Width;
        long range = (long)Rules.WarshipHealRange * Rules.WarshipHealRange;
        foreach (var port in _ports.Keys)
        {
            if (port.Owner != ship.Owner.Id)
                continue;
            long dx = port.Tile % Map.Width - x, dy = port.Tile / Map.Width - y;
            if (dx * dx + dy * dy <= range)
                return true;
        }
        return false;
    }

    private bool TargetValid(Warship ship) => ship.Target switch
    {
        Boat b => !b.Sunk && _boats.Contains(b) && !ship.Owner.IsAlliedWith(b.Owner),
        Warship w => w.Alive && _warships.Contains(w) && !ship.Owner.IsAlliedWith(w.Owner),
        TradeShip t => !t.Captured && t.Owner != ship.Owner && _tradeShips.Contains(t) && !ship.Owner.IsAlliedWith(t.Owner),
        _ => true,
    };

    /// <summary>Nearest enemy in range: troop boats first, then warships, then trade ships near its patrol area.</summary>
    private object? FindTarget(Warship ship)
    {
        int x = ship.Tile % Map.Width, y = ship.Tile / Map.Width;
        long range = (long)Rules.WarshipTargetRange * Rules.WarshipTargetRange;
        long Dist(int tile)
        {
            long dx = tile % Map.Width - x, dy = tile / Map.Width - y;
            return dx * dx + dy * dy;
        }

        object? best = null;
        long bestDist = long.MaxValue;
        foreach (var b in _boats)
            if (b.Owner != ship.Owner && !ship.Owner.IsAlliedWith(b.Owner) && !b.Sunk && Dist(b.Tile) <= range && Dist(b.Tile) < bestDist && !AlreadyShot(b))
                (best, bestDist) = (b, Dist(b.Tile));
        if (best is not null)
            return best;
        foreach (var w in _warships)
            if (w.Owner != ship.Owner && !ship.Owner.IsAlliedWith(w.Owner) && w.Alive && Dist(w.Tile) <= range && Dist(w.Tile) < bestDist)
                (best, bestDist) = (w, Dist(w.Tile));
        if (best is not null)
            return best;

        int px = ship.PatrolTile % Map.Width, py = ship.PatrolTile / Map.Width;
        long patrol = (long)Rules.WarshipPatrolRange * Rules.WarshipPatrolRange;
        foreach (var t in _tradeShips)
        {
            // Leave alone ships that are trading with us, or already taken.
            if (t.Owner == ship.Owner || ship.Owner.IsAlliedWith(t.Owner) || t.Captured || t.To.Owner == ship.Owner.Id || Dist(t.Tile) > range)
                continue;
            long pdx = t.Tile % Map.Width - px, pdy = t.Tile / Map.Width - py;
            if (pdx * pdx + pdy * pdy <= patrol && Dist(t.Tile) < bestDist)
                (best, bestDist) = (t, Dist(t.Tile));
        }
        return best;
    }

    private bool AlreadyShot(Boat boat)
    {
        foreach (var s in _shells)
            if (s.Target == boat)
                return true;
        return false;
    }

    private void Fire(Warship ship, object target, int targetTile, bool rateLimited)
    {
        if (rateLimited && Tick - ship.LastShotTick < WarshipRules.ShotCooldownTicks)
            return;
        if (target is Boat boat && AlreadyShot(boat))
            return;
        ship.LastShotTick = Tick;
        int dx = targetTile % Map.Width - ship.Tile % Map.Width, dy = targetTile / Map.Width - ship.Tile / Map.Width;
        int distance = (int)Math.Sqrt((double)dx * dx + (double)dy * dy);
        int flight = Math.Max(1, (distance + Rules.WarshipShellTilesPerTick - 1) / Rules.WarshipShellTilesPerTick);
        int damage = ship.Rng.RangeInt(WarshipRules.MinShellDamage, WarshipRules.MaxShellDamage);
        _shells.Add(new Shell(ship, target, ship.Tile, Tick, Tick + flight, damage));
    }

    private void FlyShells()
    {
        for (int i = 0; i < _shells.Count; i++)
        {
            var shell = _shells[i];
            if (Tick < shell.HitTick)
                continue;
            _shells.RemoveAt(i--);
            switch (shell.Target)
            {
                case Boat boat when !boat.Sunk && _boats.Contains(boat):
                    boat.Sunk = true; // one hit sinks a troop boat; its troops are lost
                    boat.SunkBy = shell.Shooter.Owner;
                    break;
                case Warship enemy when enemy.Alive:
                    enemy.Health -= shell.Damage;
                    break;
            }
        }
    }

    /// <summary>Chase a trade ship; once alongside, capture it and sail it to our nearest port.</summary>
    private void Hunt(Warship ship, TradeShip trade)
    {
        for (int step = 0; step < Rules.WarshipChaseTilesPerTick; step++)
        {
            int dx = Math.Abs(trade.Tile % Map.Width - ship.Tile % Map.Width), dy = Math.Abs(trade.Tile / Map.Width - ship.Tile / Map.Width);
            if (dx + dy <= Rules.WarshipCaptureRange)
            {
                Capture(ship, trade);
                return;
            }
            int next = StepToward(ship.Tile, trade.Tile);
            if (next < 0)
            {
                ship.Target = null; // can't get there by sea
                return;
            }
            ship.Tile = next;
        }
        ship.Path = null; // resume patrol from wherever the chase ended
    }

    private void Capture(Warship ship, TradeShip trade)
    {
        ship.Target = null;
        int here = trade.Tile, body = Waterways.Body[here];
        Building? home = null;
        long bestDist = long.MaxValue;
        foreach (var port in _ports.Keys)
        {
            if (port.Owner != ship.Owner.Id || Waterways.ShoreWater(port.Tile, body) < 0)
                continue;
            long dx = port.Tile % Map.Width - here % Map.Width, dy = port.Tile / Map.Width - here / Map.Width;
            if (dx * dx + dy * dy < bestDist)
                (home, bestDist) = (port, dx * dx + dy * dy);
        }
        int[]? route = home is null ? null : Waterways.FindPath(here, Waterways.ShoreWater(home.Tile, body));
        if (home is null || route is null)
        {
            _tradeShips.Remove(trade); // nowhere to take it: it's scuttled
            return;
        }
        trade.EarlierTiles += (int)(trade.Progress / 1000);
        trade.Owner = ship.Owner;
        trade.To = home;
        trade.Path = route;
        trade.Progress = 0;
        trade.Captured = true;
    }

    /// <summary>The 8-neighbour water tile that gets closest to the goal (no corner cutting), or -1.</summary>
    private int StepToward(int from, int goal)
    {
        int w = Map.Width, h = Map.Height, x = from % w, y = from / w, gx = goal % w, gy = goal / w;
        int best = -1;
        long bestDist = (long)(x - gx) * (x - gx) + (long)(y - gy) * (y - gy);
        var body = Waterways.Body;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= w || ny >= h)
                    continue;
                int n = ny * w + nx;
                if (body[n] == Waterways.NoBody || (dx != 0 && dy != 0 && (body[y * w + nx] == Waterways.NoBody || body[ny * w + x] == Waterways.NoBody)))
                    continue;
                long d = (long)(nx - gx) * (nx - gx) + (long)(ny - gy) * (ny - gy);
                if (d < bestDist)
                    (best, bestDist) = (n, d);
            }
        return best;
    }

    /// <summary>Wander between random spots of water around the patrol point.</summary>
    private void Patrol(Warship ship)
    {
        if (ship.Path is null || ship.PathIndex >= ship.Path.Length - 1)
        {
            ship.Path = null;
            int px = ship.PatrolTile % Map.Width, py = ship.PatrolTile / Map.Width, body = Waterways.Body[ship.Tile];
            for (int tries = 0; tries < 12 && ship.Path is null; tries++)
            {
                int x = px + ship.Rng.RangeInt(-Rules.WarshipPatrolRange, Rules.WarshipPatrolRange);
                int y = py + ship.Rng.RangeInt(-Rules.WarshipPatrolRange, Rules.WarshipPatrolRange);
                if (x < 0 || y < 0 || x >= Map.Width || y >= Map.Height)
                    continue;
                int goal = Map.Index(x, y);
                if (Waterways.Body[goal] == body && goal != ship.Tile)
                    ship.Path = Waterways.FindPath(ship.Tile, goal);
            }
            ship.PathIndex = 0;
            if (ship.Path is null)
                return;
        }
        for (int step = 0; step < Rules.WarshipTilesPerTick && ship.PathIndex < ship.Path.Length - 1; step++)
            ship.Tile = ship.Path[++ship.PathIndex];
    }

    // ------------------------------------------------------------------
    //  Conquest
    // ------------------------------------------------------------------

    /// <summary>The conqueror takes the loser's gold (half from a person, none from one who never fought).</summary>
    private void TakeGold(Player winner, Player loser)
    {
        long gold = loser.Kind == PlayerKind.Human && loser.AttacksSent == 0 ? 0 : loser.Gold;
        if (gold > 0)
        {
            long gained = loser.Kind == PlayerKind.Human ? gold / 2 : gold;
            winner.Gold += gained;
            loser.Gold -= gold;
            ReportGold(winner, gained, -1, loser);
        }
    }

    /// <summary>
    /// A player beaten down to a few scraps is finished off, as in OpenFront: the attacker
    /// takes their gold (half from a person, none from one who never fought) and every
    /// remaining tile next to the attacker; tiles out of reach go to whoever else borders them.
    /// </summary>
    private void Conquer(Player winner, Player loser)
    {
        TakeGold(winner, loser);

        // The loser's land: flood out from its edges (every piece has some).
        var tiles = new List<int>();
        var seen = new HashSet<int>();
        var queue = new Queue<int>();
        foreach (int t in loser.Border)
            if (seen.Add(t)) queue.Enqueue(t);
        foreach (int t in loser.Coast)
            if (seen.Add(t)) queue.Enqueue(t);
        Span<int> nb = stackalloc int[4];
        while (queue.Count > 0)
        {
            int t = queue.Dequeue();
            tiles.Add(t);
            int count = LandNeighbours(t, nb);
            for (int k = 0; k < count; k++)
                if (Owner[nb[k]] == loser.Id && seen.Add(nb[k]))
                    queue.Enqueue(nb[k]);
        }
        tiles.Sort(); // HashSet order isn't guaranteed; keep every machine identical

        for (int pass = 0; pass < 100; pass++)
        {
            bool progressed = false;
            foreach (int t in tiles)
            {
                if (Owner[t] != loser.Id)
                    continue;
                ushort taker = Nobody;
                int count = LandNeighbours(t, nb);
                for (int k = 0; k < count && taker != winner.Id; k++)
                {
                    ushort o = Owner[nb[k]];
                    if (o == winner.Id || (taker == Nobody && o != Nobody && o != loser.Id))
                        taker = o;
                }
                if (taker == Nobody)
                    continue;
                SetOwner(t, taker);
                progressed = true;
            }
            if (!progressed)
                break;
        }
        if (loser.TileCount == 0)
            Report(GameEventKind.Conquered, winner, loser);
    }

    // ------------------------------------------------------------------
    //  Territory
    // ------------------------------------------------------------------

    private void SetOwner(int tile, ushort newOwner)
    {
        ushort old = Owner[tile];
        if (old == newOwner)
            return;
        HatchOnCapture(tile, old, newOwner);
        Owner[tile] = newOwner;
        ChangedTiles.Add(tile);
        if (old != Nobody)
            _players[old].LastLandChange = Tick;
        if (newOwner != Nobody)
            _players[newOwner].LastLandChange = Tick;

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
            if (Fallout[tile]) // retaking poisoned land cleans it up
            {
                Fallout[tile] = false;
                FalloutTiles--;
            }
            _players[newOwner].TileCount++;
            if (coastal)
                _players[newOwner].Coast.Add(tile);
        }

        // Buildings change hands with their land. Defense posts are destroyed instead (as in
        // OpenFront), and anything on abandoned land vanishes.
        if (_buildings.TryGetValue(tile, out var building))
        {
            if (newOwner == Nobody || building.Type == BuildingType.Defense)
                RemoveBuilding(building);
            else
                TransferBuilding(building, newOwner);
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
