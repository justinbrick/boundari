using System;
using System.Collections.Generic;
using Boundari.Core;

namespace Boundari.Sim;

/// <summary>
/// A commander's dots (the AI side of the dots game, our own design): every couple of
/// seconds it keeps expanding while wilderness touches its land, buys dots up to its cap and
/// upgrades the weakest, sends dots to any stretch an enemy pushes harder than its dots there
/// hold, and attacks: it gathers its free dots on a weak spot (the least defended stretch of
/// a neighbouring commander, or a Locals neighbour once its dots together are strong enough)
/// and pushes in. The push stops when its dots tire or it stalls (the line digs in), and the
/// dots rest before the next one. A target that stalled a push is left alone for a while.
/// Only intents, like a human; its own seeded RNG, so it behaves the same on every machine.
/// </summary>
internal sealed class NationDots
{
    private enum Plan { Idle, Gathering, Pushing, Resting }

    private readonly Game _game;
    private readonly Player _me;
    private readonly Rng _rng;
    private readonly int _rate, _offset;

    private Plan _plan;
    private ushort _target;
    private int _spot, _targetTile; // our border tile the attack gathers on, and their tile in front of it
    private int[] _group = Array.Empty<int>();
    private long _since;
    private int _targetTiles, _stalls;
    private readonly Dictionary<ushort, long> _leaveAlone = new(); // target -> tick it may be tried again

    // This look's neighbours: for each, pairs of our border tile and their tile beside it.
    private readonly SortedDictionary<ushort, List<(int Mine, int Theirs)>> _neighbours = new();
    private int[] _border = Array.Empty<int>();

    /// <summary>
    /// A rough guess of the force a line of dots puts on each tile in front of it, per strength
    /// level, in thousandths (each tile gets most of the dot in front and some of its neighbours').
    /// </summary>
    private const int LineForcePerLevel = 1800;

    /// <summary>An attack is only tried where that guess beats the defence by this many percent.</summary>
    private const int MarginPercent = 15;

    /// <summary>Fewer free dots than this don't attack.</summary>
    private const int MinGroup = 3;

    /// <summary>Most ticks to wait for dots to gather, and to rest between attacks.</summary>
    private const int GatherTicks = 60 * Balance.TicksPerSecond, RestTicks = 45 * Balance.TicksPerSecond;

    /// <summary>Looks in a row with the target losing no land before the push is called off.</summary>
    private const int StallLooks = 4;

    /// <summary>How long a target that stalled a push is left alone.</summary>
    private const int LeaveAloneTicks = 90 * Balance.TicksPerSecond;

    public NationDots(Game game, Player me, Rng rng)
    {
        _game = game;
        _me = me;
        _rng = rng;
        _rate = rng.RangeInt(20, 30); // ticks between looks
        _offset = rng.RangeInt(0, _rate - 1);
    }

    /// <summary>True while it has a use for its Funds on dots (more of them, or upgrades): structures wait till not.</summary>
    public bool WantsFunds
    {
        get
        {
            if (_me.DotCount < _me.DotCap)
                return true;
            foreach (var d in _game.Dots)
                if (d.Owner == _me && d.Strength < DotRules.MaxStrength)
                    return true;
            return false;
        }
    }

    public void Think()
    {
        if (_game.Tick % _rate != _offset || _me.TileCount == 0)
            return;
        LookAround();
        if (_neighbours.ContainsKey(Game.Nobody) && !_game.IsExpanding(_me))
            _game.Submit(new ExpandIntent(_me.Id, Start: true));
        var dots = _game.DotsOf(_me);
        Spend(dots);
        bool hardPressed = Defend(dots);
        Attack(dots, hardPressed);
    }

    /// <summary>Who we border, and where (our border looked at in tile order).</summary>
    private void LookAround()
    {
        if (_border.Length != _me.Border.Count)
            _border = new int[_me.Border.Count];
        _me.Border.CopyTo(_border);
        Array.Sort(_border);
        foreach (var list in _neighbours.Values)
            list.Clear();
        var map = _game.Map;
        Span<int> nb = stackalloc int[4];
        foreach (int t in _border)
        {
            int count = _game.LandNeighbours(t, nb);
            for (int k = 0; k < count; k++)
            {
                ushort o = _game.Owner[nb[k]];
                if (o == _me.Id || !map.IsLand(nb[k]))
                    continue;
                if (!_neighbours.TryGetValue(o, out var pairs))
                    _neighbours[o] = pairs = new List<(int, int)>();
                pairs.Add((t, nb[k]));
                break;
            }
        }
        var gone = new List<ushort>();
        foreach (var (o, list) in _neighbours)
            if (list.Count == 0)
                gone.Add(o);
        foreach (ushort o in gone)
            _neighbours.Remove(o);
    }

    /// <summary>Buys dots up to the cap (where the attack gathers, or anywhere on the border), else upgrades the weakest.</summary>
    private void Spend(List<Dot> dots)
    {
        int wanted = _game.DotsAffordable(_me, _me.DotCap - _me.DotCount);
        if (wanted > 0)
        {
            int at = _plan != Plan.Idle && _me.Border.Contains(_spot) ? _spot : _border.Length > 0 ? _border[_rng.RangeInt(0, _border.Length - 1)] : -1;
            if (at >= 0)
                _game.Submit(new BuyDotsIntent(_me.Id, wanted, at));
            return;
        }
        if (_me.DotCount < _me.DotCap)
            return; // saving up for dots
        Dot? weakest = null;
        foreach (var d in dots) // the weakest, those in the attack first
            if (d.Strength < DotRules.MaxStrength && (weakest is null || d.Strength < weakest.Strength
                || (d.Strength == weakest.Strength && InGroup(d) && !InGroup(weakest))))
                weakest = d;
        if (weakest is not null && _me.CanAfford(Game.DotUpgradeCost(weakest)))
            _game.Submit(new UpgradeDotIntent(_me.Id, weakest.Id));
    }

    private bool InGroup(Dot d) => Array.IndexOf(_group, d.Id) >= 0;

    /// <summary>
    /// Sends dots to every enemy push on our land that is stronger than our dots there. True
    /// if one couldn't be matched (then no new attack starts, and a running one is called off).
    /// </summary>
    private bool Defend(List<Dot> dots)
    {
        int w = _game.Map.Width, near = _game.Rules.DotLink * 2;
        bool short_ = false;
        foreach (var push in _game.Pushes)
        {
            if (push.TargetId != _me.Id || push.Dots.Count == 0)
                continue;
            long attack = 0, sx = 0, sy = 0;
            foreach (var d in push.Dots)
            {
                attack += Game.DotForce(d);
                sx += d.Tile % w;
                sy += d.Tile / w;
            }
            int cx = (int)(sx / push.Dots.Count), cy = (int)(sy / push.Dots.Count);
            long Dist(int tile) => Math.Max(Math.Abs(tile % w - cx), Math.Abs(tile / w - cy));

            long defence = 0;
            var spare = new List<Dot>();
            foreach (var d in dots)
            {
                if (Dist(d.Destination) <= near + push.Dots.Count * _game.Rules.DotSpacing / 2)
                    defence += Game.DotForce(d);
                else if (!(_plan == Plan.Pushing && InGroup(d)))
                    spare.Add(d);
            }
            if (defence >= attack)
                continue;
            spare.Sort((a, b) => Dist(a.Tile) != Dist(b.Tile) ? Dist(a.Tile).CompareTo(Dist(b.Tile)) : a.Id.CompareTo(b.Id));
            var sent = new List<int>();
            foreach (var d in spare)
            {
                if (defence >= attack)
                    break;
                sent.Add(d.Id);
                defence += Game.DotForce(d);
            }
            int at = _game.BorderTileNear(_me, cy * w + cx);
            if (sent.Count > 0 && at >= 0)
                _game.Submit(new MoveDotsIntent(_me.Id, sent.ToArray(), at));
            if (defence < attack)
                short_ = true;
        }
        return short_;
    }

    private void Attack(List<Dot> dots, bool hardPressed)
    {
        switch (_plan)
        {
            case Plan.Pushing:
                Push? mine = null;
                foreach (var p in _game.Pushes)
                    if (p.Attacker == _me && p.TargetId == _target)
                        mine = p;
                if (mine is null || _game.GetPlayer(_target) is not { } them)
                {
                    _plan = Plan.Idle; // over: they're gone, became an ally, or the dots left
                    break;
                }
                long stamina = 0;
                foreach (var d in mine.Dots)
                    stamina += d.Stamina;
                bool tired = stamina / mine.Dots.Count <= DotRules.MinStamina + (DotRules.MaxStamina - DotRules.MinStamina) / 5;
                _stalls = them.TileCount < _targetTiles ? 0 : _stalls + 1;
                _targetTiles = them.TileCount;
                if (tired || _stalls >= StallLooks || hardPressed)
                {
                    if (_stalls >= StallLooks)
                        _leaveAlone[_target] = _game.Tick + LeaveAloneTicks;
                    _game.Submit(new StopPushIntent(_me.Id, mine.Dots[0].Id));
                    _plan = Plan.Resting;
                    _since = _game.Tick;
                }
                break;

            case Plan.Resting:
                long rest = 0;
                foreach (var d in dots)
                    rest += d.Stamina;
                if (dots.Count == 0 || rest / dots.Count >= DotRules.MaxStamina * 9 / 10 || _game.Tick - _since > RestTicks)
                    _plan = Plan.Idle;
                break;

            case Plan.Gathering:
                bool arrived = true;
                foreach (var d in dots)
                    if (InGroup(d) && d.IsWalking)
                        arrived = false;
                if (!arrived && _game.Tick - _since < GatherTicks)
                    break;
                if (_game.Owner[_targetTile] == _target && _game.DotToPush(_me, _targetTile) is { } first)
                {
                    _game.Submit(new PushIntent(_me.Id, first.Id, _targetTile));
                    _plan = Plan.Pushing;
                    _since = _game.Tick;
                    _stalls = 0;
                    _targetTiles = _game.GetPlayer(_target)?.TileCount ?? 0;
                }
                else
                    _plan = Plan.Idle; // the spot changed hands, or no dot faces it
                break;

            case Plan.Idle:
                if (!hardPressed)
                    ChooseTarget(dots);
                break;
        }
    }

    /// <summary>Picks a weak spot and sends the free dots there (the plan becomes Gathering), if any is worth it.</summary>
    private void ChooseTarget(List<Dot> dots)
    {
        int w = _game.Map.Width;
        var free = new List<Dot>();
        foreach (var d in dots)
            if (!UnderAttackNear(d))
                free.Add(d);
        if (free.Count < MinGroup)
            return;
        long strength = 0, sx = 0, sy = 0;
        foreach (var d in free)
        {
            strength += d.Strength;
            sx += d.Tile % w;
            sy += d.Tile / w;
        }
        long cx = sx / free.Count, cy = sy / free.Count;
        long force = strength * LineForcePerLevel / free.Count; // a line of these dots, per tile

        // Of each neighbour, its weakest spot; of those, the best: a gap in a commander's
        // line first, then whatever we can break nearest our dots.
        int bestMine = -1, bestTheirs = -1;
        ushort bestTarget = Game.Nobody;
        long bestScore = long.MinValue;
        foreach (var (owner, pairs) in _neighbours)
        {
            if (owner == Game.Nobody || _game.GetPlayer(owner) is not { IsAlive: true } them || _me.IsAlliedWith(them))
                continue;
            if (_leaveAlone.TryGetValue(owner, out long until) && _game.Tick < until)
                continue;
            int step = Math.Max(1, pairs.Count / 64); // a sample along the border
            for (int i = _rng.RangeInt(0, step - 1); i < pairs.Count; i += step)
            {
                var (mine, theirs) = pairs[i];
                long defence = _game.DefenceAt(theirs);
                if (force * 100 < defence * (100 + MarginPercent))
                    continue;
                long dx = mine % w - cx, dy = mine / w - cy;
                long score = (force - defence) * 4 - (long)Math.Sqrt(dx * dx + dy * dy) * 10 + _rng.RangeInt(0, 200);
                if (!them.IsBot && defence <= DotRules.UndefendedForce * 2)
                    score += 20_000; // a real gap in a rival's line
                if (score > bestScore)
                    (bestScore, bestMine, bestTheirs, bestTarget) = (score, mine, theirs, owner);
            }
        }
        if (bestMine < 0)
            return;
        _target = bestTarget;
        _spot = bestMine;
        _targetTile = bestTheirs;
        _group = free.ConvertAll(d => d.Id).ToArray();
        _game.Submit(new MoveDotsIntent(_me.Id, _group, _spot));
        _plan = Plan.Gathering;
        _since = _game.Tick;
    }

    /// <summary>True if an enemy push is close to this dot (it stays to hold the line there).</summary>
    private bool UnderAttackNear(Dot dot)
    {
        int w = _game.Map.Width, near = _game.Rules.DotLink * 2;
        foreach (var push in _game.Pushes)
            if (push.TargetId == _me.Id)
                foreach (var d in push.Dots)
                    if (Math.Max(Math.Abs(d.Tile % w - dot.Destination % w), Math.Abs(d.Tile / w - dot.Destination / w)) <= near)
                        return true;
        return false;
    }
}
