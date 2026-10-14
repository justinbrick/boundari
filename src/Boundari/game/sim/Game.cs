using System;
using System.Collections.Generic;
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

    private readonly List<Player> _players = new() { null! }; // index = id; 0 is "nobody"
    private readonly List<Attack> _attacks = new();
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
        _rng = new Rng(seed);
    }

    public IReadOnlyList<Player> Players => _players.GetRange(1, _players.Count - 1);
    public IReadOnlyList<Attack> Attacks => _attacks;
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

        foreach (var bot in _bots)
            bot.Think();
        GrowEconomies();
        AdvanceAttacks();
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
            p.Gold += Balance.GoldPerTick(p.TileCount);
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

        if (old != Nobody)
        {
            var oldPlayer = _players[old];
            oldPlayer.TileCount--;
            oldPlayer.Border.Remove(tile);
        }
        if (newOwner != Nobody)
            _players[newOwner].TileCount++;

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
