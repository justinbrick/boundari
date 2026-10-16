using System;
using System.Collections.Generic;
using Boundari.Core;

namespace Boundari.Sim;

/// <summary>
/// What a nation builds and where, following OpenFront's nation rules:
///   - Cities come first and set the pace: ports and factories are built in proportion to
///     them (fewer factories when the nation has a coast, since ports pay better there).
///   - Each placement samples spots across the territory and picks the best one: high
///     ground, away from the border, apart from its own buildings, and (often) where it
///     links up with nearby rail networks.
///   - Once land gets crowded with buildings, it stacks levels onto existing ones instead.
///   - When a big attack is coming in, it drops a defense post behind the front and holds
///     off on everything else.
///   - It saves up for SAMs and bombs by treating buildings as dearer the more it owns.
/// </summary>
internal sealed class NationStructures
{
    private readonly Game _game;
    private readonly Player _player;
    private readonly Rng _rng;
    private readonly Difficulty _difficulty;

    private int _placements;
    private bool _builtCrowdedMapFirst;
    private int _postSaveUpStart = -1;

    // With bombs turned off (as OpenFront allows), nations skip SAMs and silos and only save up for a SAM.
    private static readonly bool BombsWork = true;

    // How many of each structure per city (OpenFront).
    private const double PortsPerCity = 0.75;
    private const double FactoriesPerCity = 0.75;
    private const double SiloRatio = 0.2, FirstSiloRatio = 0.4;
    private const int MaxSilos = 3;

    // Spacing used to score spots: OpenFront uses the atom bomb's outer radius (30 tiles).
    private readonly int BorderSpacing;   // 30 tiles on the reference map (see ScaledRules)
    private readonly int StructureSpacing;

    private const int SampleTiles = 25;
    private readonly double UpgradeDensity; // buildings per tile above which it stacks instead (1/1500 on the reference map)
    private readonly int MinPortWater;      // ignore ponds smaller than this for ports (3000 tiles on the reference map)
    private readonly double HighNationDensity;
    private const int PostSaveUpPhaseTicks = 15 * Balance.TicksPerSecond;

    // Defense posts (OpenFront).
    private const double UnderAttackRatio = 0.35;   // incoming troops / ours before it reacts
    private const double DefensePostsPerRatio = 0.4;
    private const int DefensePostSampleAttempts = 150;

    public NationStructures(Game game, Player player, Rng rng)
    {
        _game = game;
        _player = player;
        _rng = rng;
        _difficulty = game.Difficulty;
        BorderSpacing = game.Rules.D(30);
        StructureSpacing = BorderSpacing * 2;
        UpgradeDensity = 1.0 / 1500 / game.Rules.Area;
        MinPortWater = game.Rules.A(3000);
        HighNationDensity = 1.0 / 7500 / game.Rules.Area;
    }

    // ---- Difficulty tables (OpenFront) ----
    private double SamRatio => _difficulty switch { Difficulty.Easy => 0.15, Difficulty.Medium => 0.2, Difficulty.Hard => 0.25, _ => 0.3 };
    private int CitiesBeforeSaving => _difficulty switch { Difficulty.Easy => 0, Difficulty.Medium => 3, Difficulty.Hard => 4, _ => 5 };
    private double IncomeSavingWeight => _difficulty switch { Difficulty.Easy => 1, Difficulty.Medium => 0.8, Difficulty.Hard => 0.6, _ => 0.4 };
    private double CoastalFactoryRatio => _difficulty switch { Difficulty.Easy => 0.33, Difficulty.Medium => 0.45, Difficulty.Hard => 0.55, _ => 0.6 };
    private int MaxUpgradesAtOnce => _difficulty switch { Difficulty.Easy => 3, Difficulty.Medium => 10, _ => int.MaxValue };
    private int RandomUpgradePercent => _difficulty switch { Difficulty.Easy => 70, Difficulty.Medium => 40, Difficulty.Hard => 25, _ => 10 };
    private int ConnectivityPercent => _difficulty switch { Difficulty.Easy => 0, Difficulty.Medium => 60, Difficulty.Hard => 75, _ => 100 };

    /// <summary>One building decision. Returns true if it ordered something.</summary>
    public bool Handle()
    {
        // Defense posts sit outside the normal pacing, and never come first.
        if (_placements > 0)
        {
            if (TryBuildDefensePost())
                return true;
            if (DefensePostNeeded())
                return false; // a big attack is coming: keep the gold for a post
        }
        if (InPostSaveUpBlockedPhase())
            return false;
        bool built = DoHandle();
        if (built)
            _placements++;
        return built;
    }

    private bool DoHandle()
    {
        int cities = _game.CountLevels(_player, BuildingType.City);
        bool coastal = HasPortWater();

        // On crowded maps the first building is a port (or a factory inland) for early income.
        if (!_builtCrowdedMapFirst && IsHighNationDensity())
        {
            if (MaybeSpawn(coastal ? BuildingType.Port : BuildingType.Factory))
            {
                _builtCrowdedMapFirst = true;
                return true;
            }
        }

        foreach (var type in new[] { BuildingType.Port, BuildingType.Factory, BuildingType.Sam, BuildingType.Silo })
        {
            if (type == BuildingType.Port && !coastal)
                continue;
            if (type is BuildingType.Sam or BuildingType.Silo && !BombsWork)
                continue;
            if (LevelsDue(type, cities, coastal) > 0 && MaybeSpawn(type))
                return true;
        }
        return MaybeSpawn(BuildingType.City);
    }

    /// <summary>Levels of this type the ratio to cities still asks for.</summary>
    private int LevelsDue(BuildingType type, int cities, bool coastal)
    {
        double ratio = type switch
        {
            BuildingType.Port => PortsPerCity,
            BuildingType.Factory => FactoriesPerCity * (coastal ? CoastalFactoryRatio : 1),
            BuildingType.Sam => SamRatio,
            BuildingType.Silo => SiloRatio,
            _ => 0,
        };
        int owned = _game.CountLevels(_player, type);
        if (type == BuildingType.Silo)
        {
            if (owned == 0)
                ratio = FirstSiloRatio; // the first one comes early
            return Math.Min((int)(cities * ratio), MaxSilos) - owned;
        }
        return (int)(cities * ratio) - owned;
    }

    private bool MaybeSpawn(BuildingType type)
    {
        if (_player.Gold < PerceivedCost(type))
            return false;

        var mine = _game.BuildingsOf(_player, type);
        if (Balance.Stackable(type) && StructureDensity() > UpgradeDensity)
        {
            if (MaybeUpgrade(mine))
                return true;
            if (mine.Count > 0)
                return false; // crowded, and nothing to stack onto yet: wait
            // none of this type yet: build the first one anyway (probably a small island)
        }

        int tile = SpawnTile(type);
        if (tile < 0) // no room: stack onto one instead
            return Balance.Stackable(type) && _difficulty != Difficulty.Easy && MaybeUpgrade(mine);
        _game.Submit(new BuildIntent(_player.Id, type, tile));
        return true;
    }

    /// <summary>
    /// What a building "feels" like it costs: dearer for each one already owned while the
    /// nation saves up (OpenFront's trick to make nations keep gold for SAMs and bombs).
    /// </summary>
    private long PerceivedCost(BuildingType type)
    {
        long real = _game.BuildingCost(_player, type);
        long target = SaveUpTarget();
        if (target == 0 || _player.Gold >= target)
            return real;
        if (_game.CountLevels(_player, BuildingType.City) < CitiesBeforeSaving)
            return real; // like people, nations don't save before their first few cities

        double increase = type switch
        {
            BuildingType.City or BuildingType.Port or BuildingType.Factory or BuildingType.Silo => 1,
            BuildingType.Sam => 0.3,
            _ => 0.1,
        };
        if (type is BuildingType.City or BuildingType.Port or BuildingType.Factory)
            increase *= IncomeSavingWeight; // these pay for themselves
        return (long)Math.Ceiling(real * (1 + increase * _game.CountLevels(_player, type)));
    }

    /// <summary>Gold the nation wants to keep: a Tsar Bomba and a Fat Man if one would get through, else five Fat Men.</summary>
    private long SaveUpTarget()
    {
        if (!BombsWork)
            return _game.BuildingCost(_player, BuildingType.Sam);
        if (NationNukes.IsTsarBombaWorthSavingFor(_game, _player, _rng))
            return _game.NukeCost(NukeType.TsarBomba) + _game.NukeCost(NukeType.FatMan);
        return _game.NukeCost(NukeType.FatMan) * 5;
    }

    /// <summary>
    /// After first reaching its savings target, a nation builds in 15-second bursts with
    /// 15-second pauses, leaving gold for bombs (unless it has twice the target).
    /// </summary>
    private bool InPostSaveUpBlockedPhase()
    {
        long target = SaveUpTarget();
        if (_postSaveUpStart < 0)
        {
            if (_player.Gold < target)
                return false;
            _postSaveUpStart = _game.Tick;
        }
        if (_player.Gold >= target * 2)
            return false;
        return (_game.Tick - _postSaveUpStart) % (PostSaveUpPhaseTicks * 2) >= PostSaveUpPhaseTicks;
    }

    private double StructureDensity()
    {
        int n = 0;
        foreach (var b in _game.BuildingsOf(_player))
            if (b.Type != BuildingType.Defense)
                n++;
        return _player.TileCount > 0 ? (double)n / _player.TileCount : 0;
    }

    private bool IsHighNationDensity()
    {
        int nations = 0;
        foreach (var p in _game.Players)
            if (p.IsNation)
                nations++;
        return _game.Map.LandTileCount > 0 && (double)nations / _game.Map.LandTileCount > HighNationDensity;
    }

    // ------------------------------------------------------------------
    //  Stacking
    // ------------------------------------------------------------------

    private bool MaybeUpgrade(List<Building> structures)
    {
        var target = BestToUpgrade(structures);
        if (target is null)
            return false;
        int levels = UpgradeAmount(target.Type);
        for (int i = 0; i < levels; i++)
            _game.Submit(new BuildIntent(_player.Id, target.Type, target.Tile));
        return true;
    }

    /// <summary>One level, or as many as the gold beyond the savings target pays for (and the ratios ask for).</summary>
    private int UpgradeAmount(BuildingType type)
    {
        int due = type == BuildingType.City ? int.MaxValue
            : LevelsDue(type, _game.CountLevels(_player, BuildingType.City), HasPortWater());
        int max = Math.Min(MaxUpgradesAtOnce, due);
        long spare = _player.Gold - SaveUpTarget();
        int owned = OwnedForPrice(type), amount = 0;
        while (amount < max)
        {
            long cost = Balance.BuildingCost(type, owned + amount);
            if (cost > spare)
                break;
            spare -= cost;
            amount++;
        }
        return Math.Max(1, amount);
    }

    // The count prices are based on (ports and factories share one, like Game.BuildingCost).
    private int OwnedForPrice(BuildingType type)
    {
        int Count(BuildingType t) => Math.Min(_game.CountLevels(_player, t), _player.Constructed[(int)t]);
        return type is BuildingType.Port or BuildingType.Factory
            ? Count(BuildingType.Port) + Count(BuildingType.Factory)
            : Count(type);
    }

    /// <summary>Which building to raise: sometimes any, else the best SAM-covered one (with a little variety).</summary>
    private Building? BestToUpgrade(List<Building> structures)
    {
        var ready = structures.FindAll(b => b.IsReady(_game.Tick) && _game.CanBuild(_player, b.Type, b.Tile));
        if (ready.Count == 0)
            return null;
        if (_rng.RangeInt(0, 99) < RandomUpgradePercent)
            return ready[_rng.RangeInt(0, ready.Count - 1)];

        var sams = _game.BuildingsOf(_player, BuildingType.Sam);
        var scored = new List<(Building B, double Score)>(ready.Count);
        foreach (var b in ready)
        {
            double score = 0;
            foreach (var sam in sams)
            {
                double r = _game.Rules.SamRange(sam.Level);
                if (DistSq(b.Tile, sam.Tile) <= r * r)
                    score += 10 + (sam.Level - 1) * 7.5;
            }
            scored.Add((b, score + _rng.RangeInt(0, 4)));
        }
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (scored.Count >= 2 && _rng.RangeInt(0, 1) == 0)
            return scored[scored.Count >= 3 ? _rng.RangeInt(1, 2) : 1].B;
        return scored[0].B;
    }

    // ------------------------------------------------------------------
    //  Where to build
    // ------------------------------------------------------------------

    /// <summary>The best of a couple of dozen sampled spots that can take a new building, or -1.</summary>
    private int SpawnTile(BuildingType type)
    {
        var tiles = type == BuildingType.Port ? CoastalSample() : TerritorySample();
        if (tiles.Count == 0)
            return -1;
        var value = ValueFunction(type);
        int best = -1;
        double bestValue = 0;
        foreach (int t in tiles)
        {
            double v = value(t);
            if (v <= bestValue && best >= 0)
                continue;
            if (!_game.CanBuildNew(_player, type, t))
                continue;
            best = t;
            bestValue = v;
        }
        return best;
    }

    private Func<int, double> ValueFunction(BuildingType type) => type switch
    {
        BuildingType.Port => PortValue(),
        BuildingType.City => InteriorValue(BuildingType.City, BuildingType.Factory, StructureSpacing),
        BuildingType.Factory => InteriorValue(BuildingType.Factory, BuildingType.City, _game.Rules.StationMaxRange),
        BuildingType.Silo => InteriorValue(BuildingType.Silo, null, StructureSpacing),
        BuildingType.Sam => SamValue(),
        _ => _ => 0,
    };

    /// <summary>Ports: as far as possible from our other ports.</summary>
    private Func<int, double> PortValue()
    {
        var others = TilesOf(BuildingType.Port);
        return t => Nearest(others, t);
    }

    /// <summary>
    /// Cities, factories and silos: high ground, away from the border, apart from others of
    /// the same kind (and from the other of city/factory), plus rail connections when used.
    /// </summary>
    private Func<int, double> InteriorValue(BuildingType type, BuildingType? apartFrom, int sameSpacing)
    {
        var same = TilesOf(type);
        var other = apartFrom is { } a ? TilesOf(a) : new List<int>();
        bool connect = type != BuildingType.Silo && _rng.RangeInt(0, 99) < ConnectivityPercent;
        var stations = connect ? ReachableStations() : null;
        return t =>
        {
            double w = Height(t) + Math.Min(BorderDistance(t, BorderSpacing), BorderSpacing);
            double d = Nearest(same, t, exclude: t);
            if (!double.IsPositiveInfinity(d))
                w += Math.Min(d, sameSpacing);
            double d2 = Nearest(other, t);
            if (!double.IsPositiveInfinity(d2))
                w += Math.Min(d2, StructureSpacing);
            if (stations is not null)
                w += Connectivity(t, stations) * StructureSpacing;
            return w;
        };
    }

    /// <summary>SAMs: high ground, inland, apart from other SAMs, and near what needs covering.</summary>
    private Func<int, double> SamValue()
    {
        var others = TilesOf(BuildingType.Sam);
        var protect = new List<(int Tile, int Weight)>();
        bool byLevel = _difficulty >= Difficulty.Hard;
        foreach (var b in _game.BuildingsOf(_player))
            if (b.Type is BuildingType.City or BuildingType.Factory or BuildingType.Silo or BuildingType.Port)
                protect.Add((b.Tile, byLevel ? b.Level : 1));
        double range = _game.Rules.SamRange(1);
        return t =>
        {
            double w = Height(t) + Math.Min(BorderDistance(t, BorderSpacing), BorderSpacing);
            double d = Nearest(others, t, exclude: t);
            if (!double.IsPositiveInfinity(d))
                w += Math.Min(d, StructureSpacing);
            if (_difficulty != Difficulty.Easy)
                foreach (var (tile, weight) in protect)
                    if (DistSq(t, tile) <= range * range)
                        w += StructureSpacing * weight;
            return w;
        };
    }

    // Rail stops a new city or factory here could join, weighted by what trains would pay
    // there (our own stops 10K, a neighbour's 25K, out of an ally's 35K in OpenFront).
    private List<(int Tile, int Cluster, double Weight)> ReachableStations()
    {
        var list = new List<(int, int, double)>();
        void Add(Player owner, double weight)
        {
            foreach (var b in _game.BuildingsOf(owner))
                if (b.Type is BuildingType.City or BuildingType.Port or BuildingType.Factory && _game.StationCluster(b) is { } cluster)
                    list.Add((b.Tile, cluster, weight));
        }
        Add(_player, RailRules.TrainGold(true, 0) / 35_000.0);
        foreach (var n in Neighbours())
            if (!n.IsBot)
                Add(n, RailRules.TrainGold(false, 0) / 35_000.0);
        return list;
    }

    /// <summary>Sum over rail networks within station range of the best stop weight on each.</summary>
    private double Connectivity(int tile, List<(int Tile, int Cluster, double Weight)> stations)
    {
        long min = (long)_game.Rules.StationMinRange * _game.Rules.StationMinRange;
        long max = (long)_game.Rules.StationMaxRange * _game.Rules.StationMaxRange;
        var best = new Dictionary<int, double>();
        double lone = 0;
        foreach (var (t, cluster, weight) in stations)
        {
            long d = DistSq(tile, t);
            if (d < min || d > max)
                continue;
            if (cluster < 0)
                lone += weight;
            else if (!best.TryGetValue(cluster, out double w) || weight > w)
                best[cluster] = weight;
        }
        foreach (double w in best.Values)
            lone += w;
        return lone;
    }

    /// <summary>Random tiles we own, sampled inside our bounding box.</summary>
    private List<int> TerritorySample()
    {
        var map = _game.Map;
        int w = map.Width, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        void Grow(int t)
        {
            int x = t % w, y = t / w;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        foreach (int t in _player.Border)
            Grow(t);
        foreach (int t in _player.Coast)
            Grow(t);
        var tiles = new List<int>(SampleTiles);
        if (maxX < 0)
            return tiles;
        for (int i = 0; i < SampleTiles; i++)
            for (int tries = 0; tries < 100; tries++)
            {
                int t = map.Index(_rng.RangeInt(minX, maxX), _rng.RangeInt(minY, maxY));
                if (_game.Owner[t] == _player.Id)
                {
                    tiles.Add(t);
                    break;
                }
            }
        return tiles;
    }

    /// <summary>A sample of our coast on water big enough to be worth a port.</summary>
    private List<int> CoastalSample()
    {
        var shore = new List<int>();
        foreach (int t in _player.Coast)
            if (OnPortWater(t))
                shore.Add(t);
        shore.Sort(); // HashSet order isn't guaranteed; keep every machine identical
        if (shore.Count <= SampleTiles)
            return shore;
        var picked = new List<int>(SampleTiles);
        for (int i = 0; i < SampleTiles; i++)
        {
            int j = _rng.RangeInt(i, shore.Count - 1);
            (shore[i], shore[j]) = (shore[j], shore[i]);
            picked.Add(shore[i]);
        }
        return picked;
    }

    private bool HasPortWater()
    {
        foreach (int t in _player.Coast)
            if (OnPortWater(t))
                return true;
        return false;
    }

    private bool OnPortWater(int tile)
    {
        var ways = _game.Waterways;
        Span<int> bodies = stackalloc int[4];
        int n = ways.TouchingBodies(tile, bodies);
        for (int k = 0; k < n; k++)
            if (_difficulty == Difficulty.Easy || ways.BodySize[bodies[k]] >= MinPortWater)
                return true;
        return false;
    }

    // ------------------------------------------------------------------
    //  Defense posts
    // ------------------------------------------------------------------

    private (long Incoming, HashSet<ushort> Attackers) IncomingAttacks()
    {
        long incoming = 0;
        var attackers = new HashSet<ushort>();
        foreach (var a in _game.Attacks)
            if (a.TargetId == _player.Id)
            {
                incoming += a.Troops;
                attackers.Add(a.Attacker.Id);
            }
        return (incoming, attackers);
    }

    private bool DefensePostNeeded()
    {
        if (_difficulty == Difficulty.Easy || _player.Troops <= 0)
            return false;
        var (incoming, attackers) = IncomingAttacks();
        return attackers.Count > 0 && (double)incoming / _player.Troops >= UnderAttackRatio;
    }

    /// <summary>
    /// A defense post behind the front of a big incoming attack, far enough back to be
    /// finished before the attack gets there. Medium: half the time, one post per front.
    /// </summary>
    private bool TryBuildDefensePost()
    {
        if (_difficulty == Difficulty.Easy)
            return false;
        if (_difficulty == Difficulty.Medium && _rng.RangeInt(0, 1) == 0)
            return false;
        if (_player.Troops <= 0)
            return false;
        var (incoming, attackers) = IncomingAttacks();
        if (attackers.Count == 0)
            return false;
        double ratio = (double)incoming / _player.Troops;
        if (ratio < UnderAttackRatio)
            return false;

        int allowed = _difficulty == Difficulty.Medium ? 1 : (int)Math.Ceiling(ratio / DefensePostsPerRatio);
        double range = _game.Rules.DefensePostRange;
        double scaled = _difficulty == Difficulty.Medium ? 0 : Math.Min(ratio, 2);
        double minDist = range * (2.0 / 3 + scaled / 3);
        double maxDist = minDist + range / 2;

        var front = FrontFacing(attackers);
        if (PostsNearFront(front, maxDist, allowed) >= allowed)
            return false;
        if (_player.Gold < _game.BuildingCost(_player, BuildingType.Defense))
            return false;
        int tile = DefensePostTile(front, minDist, maxDist);
        if (tile < 0)
            return false;
        _game.Submit(new BuildIntent(_player.Id, BuildingType.Defense, tile));
        return true;
    }

    /// <summary>Our border tiles touching land of any of these players.</summary>
    private List<int> FrontFacing(HashSet<ushort> players)
    {
        var front = new List<int>();
        Span<int> nb = stackalloc int[4];
        foreach (int t in _player.Border)
        {
            int count = _game.LandNeighbours(t, nb);
            for (int k = 0; k < count; k++)
                if (players.Contains(_game.Owner[nb[k]]))
                {
                    front.Add(t);
                    break;
                }
        }
        front.Sort(); // HashSet order isn't guaranteed; keep every machine identical
        return front;
    }

    private int PostsNearFront(List<int> front, double range, int cap)
    {
        double r2 = range * range;
        int count = 0;
        foreach (var post in _game.BuildingsOf(_player, BuildingType.Defense))
            foreach (int f in front)
                if (DistSq(post.Tile, f) <= r2)
                {
                    if (++count >= cap)
                        return count;
                    break;
                }
        return count;
    }

    private int DefensePostTile(List<int> front, double minDist, double maxDist)
    {
        if (front.Count == 0)
            return -1;
        var posts = TilesOf(BuildingType.Defense);
        double spread = _game.Rules.DefensePostRange * 1.5, spread2 = spread * spread;
        bool NearPost(int t) => posts.Exists(p => DistSq(t, p) < spread2);

        var anchors = front.FindAll(f => !NearPost(f));
        if (anchors.Count == 0)
            return -1;
        var map = _game.Map;
        int w = map.Width, radius = (int)Math.Ceiling(maxDist);
        double min2 = minDist * minDist, max2 = maxDist * maxDist;
        for (int attempt = 0; attempt < DefensePostSampleAttempts; attempt++)
        {
            int anchor = anchors[_rng.RangeInt(0, anchors.Count - 1)];
            int x = _rng.RangeInt(anchor % w - radius, anchor % w + radius);
            int y = _rng.RangeInt(anchor / w - radius, anchor / w + radius);
            if (!map.InBounds(x, y))
                continue;
            int t = map.Index(x, y);
            if (_game.Owner[t] != _player.Id || NearPost(t))
                continue;
            double nearest = double.MaxValue;
            foreach (int f in front)
            {
                nearest = Math.Min(nearest, DistSq(t, f));
                if (nearest < min2)
                    break;
            }
            if (nearest < min2 || nearest > max2)
                continue;
            if (_game.CanBuildNew(_player, BuildingType.Defense, t))
                return t;
        }
        return -1;
    }

    // ------------------------------------------------------------------
    //  Geometry helpers
    // ------------------------------------------------------------------

    // Land height on OpenFront's 0-30 scale, so it weighs the same against distances.
    private double Height(int tile) => (int)(_game.Map.Elevation[tile] * 30);

    private List<int> TilesOf(BuildingType type) => _game.BuildingsOf(_player, type).ConvertAll(b => b.Tile);

    private long DistSq(int a, int b)
    {
        int w = _game.Map.Width;
        long dx = a % w - b % w, dy = a / w - b / w;
        return dx * dx + dy * dy;
    }

    private double Nearest(List<int> tiles, int tile, int exclude = -1)
    {
        long best = long.MaxValue;
        foreach (int t in tiles)
            if (t != exclude)
                best = Math.Min(best, DistSq(t, tile));
        return best == long.MaxValue ? double.PositiveInfinity : Math.Sqrt(best);
    }

    /// <summary>Distance to the nearest tile we don't own (land or water), searched out to <paramref name="cap"/>.</summary>
    private double BorderDistance(int tile, int cap)
    {
        var map = _game.Map;
        int w = map.Width, cx = tile % w, cy = tile / w;
        long best = long.MaxValue;
        for (int r = 1; r <= cap; r++)
        {
            if ((long)r * r > best) // nothing in further rings can be closer
                break;
            for (int dx = -r; dx <= r; dx++)
            {
                int step = Math.Abs(dx) == r ? 1 : 2 * r; // whole edge columns, else just top and bottom
                for (int dy = -r; dy <= r; dy += step)
                {
                    int x = cx + dx, y = cy + dy;
                    long d = (long)dx * dx + (long)dy * dy;
                    if (d >= best)
                        continue;
                    if (!map.InBounds(x, y) || _game.Owner[map.Index(x, y)] != _player.Id)
                        best = d;
                }
            }
        }
        return best == long.MaxValue ? cap : Math.Sqrt(best);
    }

    private List<Player> Neighbours()
    {
        var ids = new SortedSet<ushort>();
        Span<int> nb = stackalloc int[4];
        foreach (int t in _player.Border)
        {
            int count = _game.LandNeighbours(t, nb);
            for (int k = 0; k < count; k++)
            {
                ushort o = _game.Owner[nb[k]];
                if (o != Game.Nobody && o != _player.Id)
                    ids.Add(o);
            }
        }
        var list = new List<Player>(ids.Count);
        foreach (ushort id in ids)
            list.Add(_game.GetPlayer(id)!);
        return list;
    }
}
