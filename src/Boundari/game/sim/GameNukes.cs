using System;
using System.Collections.Generic;
using Boundari.Core;

namespace Boundari.Sim;

/// <summary>Bombs, silos and SAMs (see <see cref="NukeRules"/>).</summary>
public sealed partial class Game
{
    private readonly List<Nuke> _nukes = new();
    private readonly List<SamMissile> _samMissiles = new();
    private readonly List<Explosion> _explosions = new();
    private int _nextNukeId = 1;

    /// <summary>Land poisoned by a blast: nobody owns it, and it's slow and costly to take back (cleared when captured).</summary>
    public readonly bool[] Fallout;
    public int FalloutTiles { get; private set; }

    /// <summary>When each player was last hit by a nation's Tsar Bomba (nations wait a while before another).</summary>
    internal readonly Dictionary<ushort, int> NationMirvTargets = new();

    public IReadOnlyList<Nuke> Nukes => _nukes;

    /// <summary>Bombs launched this match, by type, and bombs SAMs have shot down.</summary>
    public readonly int[] NukesLaunched = new int[4];
    public int NukesIntercepted { get; private set; }
    public IReadOnlyList<SamMissile> SamMissiles => _samMissiles;

    /// <summary>Recent detonations (the last few seconds), for the renderer.</summary>
    public IReadOnlyList<Explosion> Explosions => _explosions;

    private const int ExplosionMemoryTicks = 5 * Balance.TicksPerSecond;

    /// <summary>True during the spawn phase and the first few seconds after it, when bombs can't be launched.</summary>
    public bool NukesLocked => Phase != GamePhase.Running || Tick < SpawnPhaseTicks + NukeRules.SpawnImmunityTicks;

    /// <summary>Has a missile ready (finished, and not all its levels reloading).</summary>
    public bool IsLoaded(Building launcher) => launcher.IsReady(Tick) && launcher.Reloads.Count < launcher.Level;

    /// <summary>The player's loaded silo nearest the target (counted along x + y), or null.</summary>
    public Building? ReadySilo(Player player, int target)
    {
        int w = Map.Width, tx = target % w, ty = target / w;
        Building? best = null;
        int bestDist = int.MaxValue;
        foreach (var b in _buildings.Values)
        {
            if (b.Owner != player.Id || b.Type != BuildingType.Silo || !IsLoaded(b))
                continue;
            int d = Math.Abs(b.Tile % w - tx) + Math.Abs(b.Tile / w - ty);
            if (d < bestDist || (d == bestDist && b.Tile < best!.Tile))
                (best, bestDist) = (b, d);
        }
        return best;
    }

    /// <summary>
    /// What a Mortar or Missile aimed here would do, for the aiming preview (OpenFront's
    /// trajectory preview): its path from the silo that would fire it, which stretches of it
    /// Flak can reach (near either end), the first point an enemy Flak's range covers there
    /// (-1 if none; allies' Flak counts if this strike would break the alliance), and which
    /// allies it would betray. Null if no silo is ready.
    /// </summary>
    public NukePreview? PreviewNuke(Player player, NukeType type, int tile, bool up = true)
    {
        if (type is not (NukeType.Moab or NukeType.FatMan) || tile < 0 || tile >= Map.TileCount || ReadySilo(player, tile) is not { } silo)
            return null;
        var (xs, ys, targetable) = NukePath(silo.Tile, tile, Rules.NukeSpeed(type), up);
        var betrayed = new SortedSet<ushort>();
        if (player.Allies.Count > 0)
            foreach (ushort id in HardHit(player, type, tile))
                if (player.Allies.Contains(id))
                    betrayed.Add(id);
        int intercept = -1;
        for (int i = 0; i < xs.Length && intercept < 0; i++)
        {
            if (!targetable[i])
                continue;
            int at = TileAtPoint(xs[i], ys[i]);
            foreach (var sam in _buildings.Values)
            {
                if (sam.Type != BuildingType.Sam || sam.Owner == player.Id || !sam.IsReady(Tick)
                    || (player.Allies.Contains(sam.Owner) && !betrayed.Contains(sam.Owner)))
                    continue;
                long range = Rules.SamRange(sam.Level);
                if (DistSq(sam.Tile, at) <= range * range)
                {
                    intercept = i;
                    break;
                }
            }
        }
        return new NukePreview(silo.Tile, xs, ys, targetable, intercept, betrayed.Count > 0);
    }

    /// <summary>Whether the player could launch this bomb at that tile right now.</summary>
    public bool CanLaunch(Player player, NukeType type, int tile)
    {
        if (NukesLocked || !player.IsAlive || tile < 0 || tile >= Map.TileCount || type == NukeType.Warhead)
            return false;
        if (type == NukeType.TsarBomba && Owner[tile] == Nobody)
            return false; // a Tsar Bomba is aimed at someone's land
        return player.CanAfford(NukeCost(type)) && ReadySilo(player, tile) is not null;
    }

    private void LaunchNuke(Player player, NukeType type, int tile, bool up = true)
    {
        if (!CanLaunch(player, type, tile))
            return;
        var silo = ReadySilo(player, tile)!;
        player.Spend(NukeCost(type));
        NukesLaunched[(int)type]++;
        if (type == NukeType.TsarBomba)
            TsarBombasLaunched++;

        // A silo sends one bomb per tick; later ones queue up behind.
        int wait = 0, lastDeparture = 0;
        foreach (int launched in silo.Reloads)
            lastDeparture = Math.Max(launched + 1, lastDeparture + 1);
        if (lastDeparture > Tick)
            wait = lastDeparture - Tick;
        silo.Reloads.Enqueue(Tick);
        if (GetPlayer(Owner[tile]) is { } aimedAt && aimedAt != player)
            Report(GameEventKind.NukeLaunched, player, aimedAt, (int)type);

        if (type == NukeType.TsarBomba && GetPlayer(Owner[tile]) is { } victim)
            OnTsarBombaLaunched(player, victim);
        else
            OnBombLaunched(player, type, tile);

        if (type == NukeType.TsarBomba)
            LaunchTsarBomba(player, silo.Tile, tile, wait);
        else
            _nukes.Add(MakeNuke(player, type, silo.Tile, tile, Rules.NukeSpeed(type), wait, Nobody, up));
    }

    private Nuke MakeNuke(Player owner, NukeType type, int from, int to, double speed, int wait, ushort targetPlayer, bool up = true)
    {
        var (xs, ys, targetable) = NukePath(from, to, speed, up);
        return new Nuke(_nextNukeId++, owner, type, from, to, xs, ys, targetable, targetPlayer) { WaitTicks = wait };
    }

    /// <summary>A bomb's flight points from silo to target, and which of them Flak can reach (near either end).</summary>
    private (float[] X, float[] Y, bool[] Targetable) NukePath(int from, int to, double speed, bool up = true)
    {
        int w = Map.Width;
        var (xs, ys) = NukeRules.Arc(from % w, from / w, to % w, to / w, Map.Height, speed, Rules.NukeArcMinHeight, up: up);
        var targetable = new bool[xs.Length];
        long r2 = (long)Rules.NukeTargetableRange * Rules.NukeTargetableRange;
        for (int i = 0; i < xs.Length; i++)
        {
            int t = TileAtPoint(xs[i], ys[i]);
            targetable[i] = DistSq(t, to) < r2 || DistSq(t, from) < r2;
        }
        return (xs, ys, targetable);
    }

    /// <summary>
    /// A Tsar Bomba climbs to a point well "above" its target (halfway across, 450 tiles
    /// up the map) in about 14 ticks, a bit longer for long flights, then splits.
    /// </summary>
    private void LaunchTsarBomba(Player player, int from, int target, int wait)
    {
        int w = Map.Width, tx = target % w, ty = target / w;
        int split = Map.Index((tx + from % w) / 2, Math.Max(0, ty - Rules.MirvClimb) + Rules.MirvClimbFloor);
        int sx = split % w, sy = split / w, fx = from % w, fy = from / w;

        // OpenFront's flight time: ~14 ticks, plus a little for long climbs (in hundredths of a tick).
        double ideal = NukeRules.ArcLength(fx, fy, sx, sy, Rules.NukeArcMinHeight) / Rules.NukeSpeed(NukeType.TsarBomba);
        int idealTicks = (int)(ideal * 100), baseTicks = NukeRules.MirvFlightTicks * 100, ticks = baseTicks;
        if (idealTicks > baseTicks)
            ticks = baseTicks + (int)Math.Sqrt(idealTicks - baseTicks) * 14 + (idealTicks - baseTicks) * 10 / 100;
        else if (idealTicks < baseTicks)
            ticks = baseTicks - (int)Math.Sqrt(baseTicks - idealTicks) * 10;
        ticks = Math.Max(100, ticks);
        double length = NukeRules.Arc(fx, fy, sx, sy, Map.Height, 1, Rules.NukeArcMinHeight).X.Length; // ~1 tile per point
        var mirv = MakeNuke(player, NukeType.TsarBomba, from, split, Math.Max(1, length * 100 / ticks), wait, Owner[target]);
        _nukes.Add(mirv);
        _mirvAims[mirv] = target;
    }

    private readonly Dictionary<Nuke, int> _mirvAims = new();

    // ------------------------------------------------------------------
    //  Each tick
    // ------------------------------------------------------------------

    private void RunNukes()
    {
        ReloadLaunchers();
        if (_nukes.Count > 0)
            RunSams();
        FlySamMissiles();
        FlyNukes();
        while (_explosions.Count > 0 && Tick - _explosions[0].Tick > ExplosionMemoryTicks)
            _explosions.RemoveAt(0);
    }

    /// <summary>Silos and SAMs get a missile back 9 seconds after firing it.</summary>
    private void ReloadLaunchers()
    {
        foreach (var b in _buildings.Values)
        {
            if (b.Reloads.Count == 0)
                continue;
            int cooldown = b.Type == BuildingType.Sam ? Balance.SamCooldownTicks : Balance.SiloCooldownTicks;
            while (b.Reloads.Count > 0 && Tick - b.Reloads.Peek() >= cooldown)
                b.Reloads.Dequeue();
        }
    }

    private void FlyNukes()
    {
        for (int i = 0; i < _nukes.Count; i++)
        {
            var n = _nukes[i];
            if (n.Gone)
            {
                _nukes.RemoveAt(i--);
                continue;
            }
            if (n.WaitTicks > 0)
            {
                n.WaitTicks--;
                continue;
            }
            n.Index++;
            if (n.Type == NukeType.TsarBomba)
            {
                int remaining = n.PathX.Length - n.Index;
                if (remaining <= 10 && !n.Split)
                    SplitTsarBomba(n, Math.Max(0, remaining));
                if (n.Index >= n.PathX.Length - 1)
                {
                    n.Gone = true;
                    _mirvAims.Remove(n);
                }
                continue;
            }
            if (n.Index >= n.PathX.Length - 1)
            {
                n.Index = n.PathX.Length - 1;
                n.Gone = true;
                if (!n.TargetedBySam) // an interceptor about to hit it stops the blast
                    Detonate(n);
            }
        }
        for (int i = 0; i < _nukes.Count; i++)
            if (_nukes[i].Gone)
                _nukes.RemoveAt(i--);
    }

    /// <summary>
    /// The Tsar Bomba splits: warheads aimed at random spots of the target's land within
    /// 1500 tiles of the aim point, spread at least 55 tiles apart, the farthest first.
    /// </summary>
    private void SplitTsarBomba(Nuke mirv, int remaining)
    {
        mirv.Split = true;
        int aim = _mirvAims.GetValueOrDefault(mirv, mirv.To);
        int w = Map.Width, bx = aim % w, by = aim / w;
        ushort target = mirv.TargetPlayer;
        var rng = new Rng(_rng.Fork());
        var targets = new List<int> { aim };
        long range2 = (long)Rules.MirvRange * Rules.MirvRange;
        for (int attempt = 0; attempt < 1500 && targets.Count < NukeRules.MirvWarheads; attempt++)
            for (int tries = 0; tries < 100; tries++)
            {
                int x = bx + rng.RangeInt(-Rules.MirvRange, Rules.MirvRange);
                int y = by + rng.RangeInt(-Rules.MirvRange, Rules.MirvRange);
                if (!Map.InBounds(x, y))
                    continue;
                int t = Map.Index(x, y);
                if (!Map.IsLand(t) || Owner[t] != target || (long)(x - bx) * (x - bx) + (long)(y - by) * (y - by) > range2)
                    continue;
                bool overlaps = false;
                foreach (int taken in targets)
                    if (Math.Abs(taken % w - x) + Math.Abs(taken / w - y) < Rules.MirvSpread)
                    {
                        overlaps = true;
                        break;
                    }
                if (overlaps)
                    continue;
                targets.Add(t);
                break;
            }
        if (Owner[aim] != target)
            targets.Remove(aim);
        targets.Sort((a, b) => (Math.Abs(b % w - bx) + Math.Abs(b / w - by)).CompareTo(Math.Abs(a % w - bx) + Math.Abs(a / w - by)));

        for (int i = 0; i < targets.Count; i++)
        {
            int extraSpeed = i < 70 ? 0 : i < 140 ? 1 : i < 210 ? 2 : i < 280 ? 3 : 4;
            _nukes.Add(MakeNuke(mirv.Owner, NukeType.Warhead, mirv.To, targets[i],
                Rules.NukeSpeed(NukeType.Warhead) + extraSpeed, remaining + rng.RangeInt(0, 14), target));
        }
    }

    // ------------------------------------------------------------------
    //  SAMs
    // ------------------------------------------------------------------

    /// <summary>
    /// Each loaded SAM looks for enemy bombs it can reach: a point on the bomb's path inside
    /// its range where its interceptor gets there in time. It fires when the bomb is due there
    /// within a tick of its missile, so it always hits (as in OpenFront).
    /// </summary>
    private void RunSams()
    {
        int w = Map.Width;
        long detect2 = (long)Rules.MaxSamRange * 4 * Rules.MaxSamRange * 4;
        long max2 = (long)Rules.MaxSamRange * Rules.MaxSamRange;
        var targets = new List<(Nuke Nuke, int Tile, long Score)>();
        foreach (var sam in _buildings.Values)
        {
            if (sam.Type != BuildingType.Sam || !IsLoaded(sam))
                continue;
            long range2 = (long)Rules.SamRange(sam.Level) * Rules.SamRange(sam.Level);
            targets.Clear();
            foreach (var n in _nukes)
            {
                if (n.Gone || n.TargetedBySam || n.Type == NukeType.TsarBomba || n.Owner.Id == sam.Owner || n.Owner.Allies.Contains(sam.Owner))
                    continue;
                int cur = Math.Max(0, n.Index);
                if (DistSq(sam.Tile, TileAtPoint(n.PathX[cur], n.PathY[cur])) > detect2)
                    continue;
                int last = n.PathX.Length - 1, increasing = 0;
                long lastDist = -1;
                for (int i = cur; i <= last; i++)
                {
                    int at = Math.Max(0, Math.Min(i, last - 1)); // where they'd meet (just short of the target)
                    int point = TileAtPoint(n.PathX[at], n.PathY[at]);
                    long d2 = DistSq(sam.Tile, TileAtPoint(n.PathX[i], n.PathY[i]));
                    increasing = lastDist >= 0 && d2 > lastDist ? increasing + 1 : 0;
                    lastDist = d2;
                    int nukeTicks = i - n.Index + n.WaitTicks;
                    int samTicks = (Math.Abs(point % w - sam.Tile % w) + Math.Abs(point / w - sam.Tile / w) + Rules.SamMissileSpeed - 1) / Rules.SamMissileSpeed;
                    if (n.Targetable[i] && d2 <= range2 && nukeTicks >= samTicks)
                    {
                        if (nukeTicks - samTicks <= 1) // fire now; otherwise wait for a later tick
                        {
                            long distToTarget = Math.Abs(n.To % w - sam.Tile % w) + Math.Abs(n.To / w - sam.Tile / w);
                            long score = (n.Type == NukeType.FatMan ? 70_001 : 0)
                                + Math.Max(0, 200_000 - distToTarget * 1000)
                                + Math.Max(0, 10_000 - Math.Max(1, n.PathX.Length - cur) * 100);
                            targets.Add((n, point, score));
                        }
                        break;
                    }
                    if (increasing > 3 && d2 > max2)
                        break; // flying away
                }
            }
            if (targets.Count == 0)
                continue;
            targets.Sort((a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score) : a.Nuke.Id.CompareTo(b.Nuke.Id));
            foreach (var (nuke, point, _) in targets)
            {
                if (!IsLoaded(sam))
                    break;
                sam.Reloads.Enqueue(Tick);
                nuke.TargetedBySam = true;
                int flight = (Math.Abs(point % w - sam.Tile % w) + Math.Abs(point / w - sam.Tile / w) + Rules.SamMissileSpeed - 1) / Rules.SamMissileSpeed;
                _samMissiles.Add(new SamMissile(_players[sam.Owner], sam.Tile, point, nuke, flight));
            }
        }
    }

    private void FlySamMissiles()
    {
        for (int i = 0; i < _samMissiles.Count; i++)
        {
            var m = _samMissiles[i];
            m.Age++;
            if (m.Age < m.FlightTicks)
                continue;
            _samMissiles.RemoveAt(i--);
            m.Target.Gone = true; // shot down
            NukesIntercepted++;
            _explosions.Add(new Explosion(m.To, m.Target.Type, Tick, Intercepted: true));
        }
    }

    // ------------------------------------------------------------------
    //  Detonation
    // ------------------------------------------------------------------

    private int[]? _blastMark;
    private int _blastStamp;

    /// <summary>
    /// The blast: every tile inside the inner radius and about half of the outer ring (spreading
    /// out from the centre) is wiped clean and left as fallout. Owners lose troops for each tile
    /// lost, and every building, ship and train within the outer radius is destroyed.
    /// </summary>
    private void Detonate(Nuke n)
    {
        var (inner, outer) = Rules.Blast(n.Type);
        long inner2 = (long)inner * inner, outer2 = (long)outer * outer;
        var rng = new Rng(_rng.Fork());
        _blastMark ??= new int[Map.TileCount];
        int stamp = ++_blastStamp;
        var lost = new SortedDictionary<ushort, int>();
        var queue = new Queue<int>();
        queue.Enqueue(n.To);
        _blastMark[n.To] = stamp;
        int w = Map.Width;
        while (queue.Count > 0)
        {
            int t = queue.Dequeue();
            if (Map.IsLand(t))
            {
                ushort owner = Owner[t];
                if (owner != Nobody)
                {
                    SetOwner(t, Nobody);
                    lost[owner] = lost.GetValueOrDefault(owner) + 1;
                }
                if (!Fallout[t])
                {
                    Fallout[t] = true;
                    FalloutTiles++;
                    if (owner == Nobody)
                        ChangedTiles.Add(t);
                }
            }
            int x = t % w, y = t / w;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + (k == 0 ? -1 : k == 1 ? 1 : 0), ny = y + (k == 2 ? -1 : k == 3 ? 1 : 0);
                if (!Map.InBounds(nx, ny))
                    continue;
                int nt = Map.Index(nx, ny);
                if (_blastMark[nt] == stamp)
                    continue;
                long d2 = DistSq(nt, n.To);
                if (d2 > outer2 || (d2 > inner2 && rng.RangeInt(0, 1) == 0))
                    continue; // outside, or spared this time (another neighbour may still pull it in)
                _blastMark[nt] = stamp;
                queue.Enqueue(nt);
            }
        }

        // Troops die with the land: armies at home, attacking, and at sea.
        foreach (var (id, count) in lost)
        {
            var p = _players[id];
            int before = p.TileCount + count;
            long max = p.MaxTroops;
            double troops = p.Troops;
            var attacks = _attacks.FindAll(a => a.Attacker == p);
            var attackTroops = attacks.ConvertAll(a => (double)a.Troops);
            var boats = _boats.FindAll(b => b.Owner == p && !b.Sunk);
            var boatTroops = boats.ConvertAll(b => (double)b.Troops);
            for (int i = 0; i < count; i++)
            {
                int left = before - i;
                troops -= NukeRules.Deaths(n.Type, troops, left, max, Rules.Area);
                for (int a = 0; a < attackTroops.Count; a++)
                    attackTroops[a] -= NukeRules.Deaths(n.Type, attackTroops[a], left, max, Rules.Area);
                for (int b = 0; b < boatTroops.Count; b++)
                    boatTroops[b] -= NukeRules.Deaths(n.Type, boatTroops[b], left, max, Rules.Area);
            }
            p.Troops = Math.Max(0, (long)troops);
            for (int a = 0; a < attacks.Count; a++)
                attacks[a].Troops = Math.Max(0, (long)attackTroops[a]);
            for (int b = 0; b < boats.Count; b++)
                boats[b].Troops = Math.Max(0, (long)boatTroops[b]);
        }

        // Everything within the outer radius goes.
        var doomed = new List<Building>();
        foreach (var b in _buildings.Values)
            if (DistSq(b.Tile, n.To) < outer2)
                doomed.Add(b);
        foreach (var b in doomed)
            RemoveBuilding(b);
        _warships.RemoveAll(s => DistSq(s.Tile, n.To) < outer2);
        _tradeShips.RemoveAll(s => DistSq(s.Tile, n.To) < outer2);
        foreach (var b in _boats)
            if (DistSq(b.Tile, n.To) < outer2)
            {
                b.Sunk = true;
                b.SunkBy = n.Owner;
            }
        _trains.RemoveAll(tr => DistSq(tr.Tiles[(int)Math.Min(tr.Progress / 1000, tr.Tiles.Length - 1)], n.To) < outer2);

        _explosions.Add(new Explosion(n.To, n.Type, Tick));
    }

    private int TileAtPoint(float x, float y) =>
        Map.Index(Math.Clamp((int)x, 0, Map.Width - 1), Math.Clamp((int)y, 0, Map.Height - 1));

    private long DistSq(int a, int b)
    {
        int w = Map.Width;
        long dx = a % w - b % w, dy = a / w - b / w;
        return dx * dx + dy * dy;
    }
}
