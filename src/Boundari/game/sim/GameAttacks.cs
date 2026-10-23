using System;
using Boundari.Core;
using Boundari.Map;

namespace Boundari.Sim;

/// <summary>OpenFront's attack numbers (EngineConfig.attackLogic and AttackExecution).</summary>
public static class AttackRules
{
    // Per terrain: "mag" (how bloody a tile is: attacker losses) and tile cost (how slow).
    public static (double Mag, double TileCost) Terrain(TerrainType t) => t switch
    {
        TerrainType.Highlands => (100, 20),
        TerrainType.Mountains => (120, 25),
        _ => (80, 16.5),
    };

    // Queue priority: tiles on higher ground wait longer (OpenFront's addNeighbors "mag").
    // Doubled so it stays a whole number: plains 2, highlands 3, mountains 4.
    public static int PriorityTerrainTimes2(TerrainType t) => t switch
    {
        TerrainType.Highlands => 3,
        TerrainType.Mountains => 4,
        _ => 2,
    };

    public const double LargeTerritoryMidpoint = 300_000;
    public const double LargeTerritorySteepness = 2.5;
    public const double LargeAttackerDepth = 0.7;
    public const double LargeDefenderDepth = 0.3;
    public const double LargeAttackerSpeedDepth = 0.73;
    public const double BotDefenderLossMult = 0.7;     // people and nations lose 30% less attacking tribes
    public const double TerraNulliusCostScale = 2000;
    public const double TerraNulliusMinCost = 5;
    public const double TerraNulliusMaxCost = 100;
    public const double AttackerLossBase = 0.463;
    public const double CheapestTroopRatio = 0.6;
    public const double AttackerLossPerDensity = 0.0039;
    public const double SpeedCostDivisor = 8.55;
    public const double DefensePostLossFactor = 5;     // attacking near a defense post: 5x the losses...
    public const double DefensePostSpeedFactor = 3;    // ...and 3x the time
    public const int RetreatMalusPercent = 25;
    public const int RetreatDelayTicks = 20;            // a called-off attack's troops take 2 s to come home         // a boat landing on its own coast loses a quarter

    // Land cover (our addition on top of OpenFront's elevation types): how much harder a
    // tile is to cross than farmland, in tenths. It scales the tile's time fully and its
    // troop cost by half as much, and pushes the tile back in the attack's queue, so fronts
    // race through open country and flow around forests, marsh, rock and snow.
    public static int CoverTenths(CoverType cover) => cover switch
    {
        CoverType.Meadow => 12,
        CoverType.Forest => 20,
        CoverType.Water => 25,  // small lakes and marsh
        CoverType.Rock => 25,
        CoverType.Snow => 40,
        _ => 10,                // farmland, or no data
    };
    public const int CoverTroopCostPercent = 50;

    /// <summary>Each tile's average land-cover difficulty, in tenths (10 = farmland).</summary>
    public static short[] TileCoverTenths(GameMap map)
    {
        var tenths = new short[map.TileCount];
        var cover = map.Cover;
        for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width; x++)
            {
                int tile = map.Index(x, y);
                if (!map.IsLand(tile))
                    continue;
                int value = 10;
                if (cover is not null)
                {
                    int sum = 0, n = 0, s = cover.Scale;
                    for (int cy = y * s; cy < (y + 1) * s; cy++)
                        for (int cx = x * s; cx < (x + 1) * s; cx++)
                        {
                            var c = cover.Cells[cover.Index(cx, cy)];
                            if (c == CoverType.None)
                                continue;
                            sum += CoverTenths(c);
                            n++;
                        }
                    if (n > 0)
                        value = (sum + n / 2) / n;
                }
                tenths[tile] = (short)value;
            }
        return tenths;
    }

    /// <summary>~1 for small territories, easing to 1 - depth for huge ones (halfway at the midpoint: 300k reference tiles).</summary>
    public static double LargeTerritoryBonus(int tiles, double depth, double midpoint = LargeTerritoryMidpoint)
    {
        if (tiles <= 0)
            return 1;
        double r = Math.Pow(tiles / midpoint, LargeTerritorySteepness); // = e^(2.5 (ln n - ln midpoint))
        return 1 - depth * (r / (1 + r));
    }
}

// Attacks, the way OpenFront runs them (AttackExecution + EngineConfig.attackLogic).
public sealed partial class Game
{
    /// <summary>
    /// A land attack: takes the troops, cancels against any attack the target has coming
    /// at us, absorbs our earlier attacks on the same target, and queues every target tile
    /// along our border. If there's nothing to take, the troops come straight back. A
    /// directed attack (with an <paramref name="arrow"/>) stays on its own: it neither
    /// absorbs nor is absorbed by other attacks.
    /// </summary>
    private void StartAttack(Player attacker, ushort targetId, long troops, AttackArrow? arrow = null)
    {
        if (targetId == attacker.Id || (targetId != Nobody && GetPlayer(targetId) is not { IsAlive: true }))
            return;
        if (troops <= 0 || attacker.Allies.Contains(targetId))
            return; // (allies can't attack each other)
        if (targetId != Nobody)
            attacker.AttacksSent++;

        attacker.Troops -= troops;
        var attack = new Attack(attacker, targetId, troops, -1, new Rng(_rng.Fork()), arrow) { Id = _nextUnitId++ };
        RefreshToConquer(attack);
        if (CancelAgainstIncoming(attack))
            return;
        bool reinforcing = false;
        for (int i = _attacks.Count - 1; i >= 0 && arrow is null; i--)
        {
            var other = _attacks[i];
            if (other.Attacker == attacker && other.TargetId == targetId && other.Arrow is null)
            {
                attack.TroopsExact += other.TroopsExact;
                _attacks.RemoveAt(i);
                reinforcing = true;
            }
        }
        if (attack.ToConquer.Count == 0)
        {
            Retreat(attack); // doesn't border the target: nothing to attack
            return;
        }
        if (targetId != Nobody)
        {
            OnAttacked(attacker, _players[targetId]);
            if (!reinforcing)
                Report(GameEventKind.AttackStarted, attacker, _players[targetId]);
        }
        _attacks.Add(attack);
    }

    /// <summary>
    /// A boat's troops come ashore: the landing tile is taken for free and an attack spreads
    /// from it (not merged with other attacks). Landing on our own coast costs a quarter of
    /// the troops, like a called-off attack; landing on an ally's just brings them home.
    /// </summary>
    private void Land(Boat boat)
    {
        var player = boat.Owner;
        int tile = boat.LandingTile;
        ushort target = Owner[tile];
        if (target == player.Id)
        {
            player.Troops += boat.Troops * (100 - AttackRules.RetreatMalusPercent) / 100;
            return;
        }
        if (player.Allies.Contains(target))
        {
            player.Troops += boat.Troops;
            return;
        }
        SetOwner(tile, player.Id);
        var attack = new Attack(player, target, boat.Troops, tile, new Rng(_rng.Fork())) { Id = _nextUnitId++ };
        AddNeighbours(attack, tile);
        if (CancelAgainstIncoming(attack))
            return;
        if (attack.ToConquer.Count == 0)
        {
            Retreat(attack);
            return;
        }
        if (GetPlayer(target) is { } defender)
            OnAttacked(player, defender);
        _attacks.Add(attack);
    }

    /// <summary>Two attacks between the same pair, in opposite directions, cancel out. True if this one was used up.</summary>
    private bool CancelAgainstIncoming(Attack attack)
    {
        for (int i = _attacks.Count - 1; i >= 0; i--)
        {
            var incoming = _attacks[i];
            if (incoming.Attacker.Id != attack.TargetId || incoming.TargetId != attack.Attacker.Id)
                continue;
            if (incoming.TroopsExact > attack.TroopsExact)
            {
                incoming.TroopsExact -= attack.TroopsExact;
                return true;
            }
            attack.TroopsExact -= incoming.TroopsExact;
            _attacks.RemoveAt(i);
        }
        return false;
    }

    /// <summary>Ends an attack, sending what's left home.</summary>
    private void Retreat(Attack attack, int malusPercent = 0)
    {
        attack.Attacker.Troops += (long)(attack.TroopsExact * (100 - malusPercent) / 100);
        _attacks.Remove(attack);
    }

    /// <summary>A player calls off one of their attacks: it stops taking land now; see <see cref="AdvanceAttacks"/>.</summary>
    private void OrderRetreat(Player player, int attackId)
    {
        var attack = _attacks.Find(a => a.Id == attackId && a.Attacker == player);
        if (attack is not null && !attack.Retreating)
            attack.RetreatTick = Tick;
    }

    /// <summary>Queues every target tile next to the attacker's border (a fresh start, as OpenFront's refreshToConquer).</summary>
    private void RefreshToConquer(Attack attack)
    {
        attack.ToConquer.Clear();
        attack.Border.Clear();
        var border = new int[attack.Attacker.Border.Count];
        attack.Attacker.Border.CopyTo(border);
        Array.Sort(border); // HashSet order isn't guaranteed; keep every machine identical
        foreach (int tile in border)
            AddNeighbours(attack, tile);
    }

    /// <summary>
    /// Queues the target's tiles around a tile. Priority (lower goes first) is the current
    /// tick plus a random 10..16, scaled down for tiles we already half-surround and up for
    /// high ground and rough land cover: that randomness is what lets fast attacks race ahead
    /// in branching veins. Worked in 40ths of a tick so it's a whole number.
    /// </summary>
    private void AddNeighbours(Attack attack, int tile)
    {
        Span<int> nb = stackalloc int[4];
        Span<int> nb2 = stackalloc int[4];
        int count = LandNeighbours(tile, nb);
        for (int k = 0; k < count; k++)
        {
            int n = nb[k];
            if (Owner[n] != attack.TargetId)
                continue;
            attack.Border.Add(n);
            int mine = 0, inner = LandNeighbours(n, nb2);
            for (int j = 0; j < inner; j++)
                if (Owner[nb2[j]] == attack.Attacker.Id)
                    mine++;
            // 40 x (1 - mine/2 + mag x cover/2), OpenFront's factor with land cover on its terrain term.
            long factor40 = 40 - 20 * mine + AttackRules.PriorityTerrainTimes2(Map.Terrain[n]) * CoverTenths[n];
            long delay = (attack.Rng.RangeInt(0, 6) + 10) * factor40;
            if (attack.Arrow is { } arrow)
                delay = arrow.Lean(delay, tile, n, Map.Width); // a directed attack leans its way
            long priority = delay + 40L * Tick;
            attack.ToConquer.Enqueue(n, priority);
        }
    }

    /// <summary>
    /// Every tick, each attack takes tiles off its queue until its tick budget (1) is
    /// spent. Each tile costs a fraction of the budget (see <see cref="AttackLogic"/>) and
    /// troops from the attack, and the defender loses its average troops per tile. An
    /// attack ends when its troops run out (lost) or its queue empties (survivors go home).
    /// </summary>
    private void AdvanceAttacks()
    {
        for (int a = 0; a < _attacks.Count; a++)
        {
            var attack = _attacks[a];
            var attacker = attack.Attacker;
            var defender = GetPlayer(attack.TargetId);
            if (!attacker.IsAlive)
            {
                _attacks.RemoveAt(a--);
                continue;
            }
            if (attack.Retreating)
            {
                // Called off: holds still, then the troops come home (a quarter lost if it was against a player).
                if (Tick >= attack.RetreatTick + AttackRules.RetreatDelayTicks)
                {
                    Retreat(attack, attack.TargetId != Nobody ? AttackRules.RetreatMalusPercent : 0);
                    a--;
                }
                continue;
            }
            if (attacker.Allies.Contains(attack.TargetId))
            {
                Retreat(attack); // allied after the attack began: pull back
                a--;
                continue;
            }

            // A directed attack's front counts no wider than its arrow (thin ones), so a thin
            // push takes fewer tiles than a broad one, all going forward.
            int front = attack.Arrow is { } arrow ? Math.Min(attack.Border.Count, arrow.FrontCap) : attack.Border.Count;
            int borderSize = Math.Max(1, front + attack.Rng.RangeInt(0, 4));
            double budget = 1;
            while (budget > 0)
            {
                if (attack.TroopsExact < 1)
                {
                    _attacks.RemoveAt(a--); // out of troops: the attack dies out
                    break;
                }
                if (!attack.ToConquer.TryDequeue(out int tile, out _))
                {
                    Retreat(attack); // nothing left to take
                    a--;
                    break;
                }
                attack.Border.Remove(tile);
                if (Owner[tile] != attack.TargetId || !Touches(tile, attacker.Id))
                    continue; // stale: taken by someone, or no longer next to us
                AddNeighbours(attack, tile);
                var (attackerLoss, defenderLoss, tickFraction) = AttackLogic(attack, tile, defender, attack.TroopsExact, borderSize);
                budget -= tickFraction;
                attack.TroopsExact = Math.Max(0, attack.TroopsExact - attackerLoss);
                if (defender is not null)
                    defender.Troops = Math.Max(0, defender.Troops - (long)defenderLoss);
                SetOwner(tile, attacker.Id);
                if (defender is not null && defender.TileCount > 0 && defender.TileCount < Rules.ConquerBelowTiles)
                    Conquer(attacker, defender);
            }
        }
    }

    /// <summary>
    /// OpenFront's attackLogic for one tile: troops the attacker loses, troops the defender
    /// loses, and the share of the tick it takes. Against empty land it's cheap and its
    /// pace depends on how many troops the attack has; against a player both cost and pace
    /// are driven by the defender-to-attack troop ratio, the defender's troops per tile,
    /// and how big both territories are. Spread over the front: longer fronts go faster.
    /// </summary>
    private (double AttackerLoss, double DefenderLoss, double TickFraction) AttackLogic(Attack attack, int tile, Player? defender, double attackTroops, int borderSize)
    {
        var (mag, tileCost) = AttackRules.Terrain(Map.Terrain[tile]);
        double cover = CoverTenths[tile] / 10.0;
        tileCost *= cover;
        mag *= 1 + (cover - 1) * AttackRules.CoverTroopCostPercent / 100;
        if (defender is not null && DefendedByPost(tile, defender.Id))
        {
            mag *= AttackRules.DefensePostLossFactor;
            tileCost *= AttackRules.DefensePostSpeedFactor;
        }
        if (defender is not null && IsHatched(tile))
        {
            mag *= HatchRules.LossFactor;
            tileCost *= HatchRules.SpeedFactor;
        }
        if (Fallout[tile])
        {
            // Fallout: 5x dearer and slower, easing to 3x as more of the map is poisoned.
            double fallout = 5 - 2.0 * FalloutTiles / Math.Max(1, Map.LandTileCount);
            mag *= fallout;
            tileCost *= fallout;
        }

        // Finer tiles (see ScaledRules): each tile is a smaller share of the land, so it costs
        // that much fewer troops (base costs over Area) and takes that much less time (the
        // front is Linear times longer already, so the rest comes off each tile's time).
        var attacker = attack.Attacker;
        double area = Rules.Area, linear = Rules.Linear;
        if (defender is null)
        {
            double loss = mag / (attacker.IsBot ? 10 : 5) / area;
            double cost = Math.Clamp(AttackRules.TerraNulliusCostScale * tileCost / attackTroops,
                AttackRules.TerraNulliusMinCost, AttackRules.TerraNulliusMaxCost);
            if (attack.Arrow is { } flat)
                return (loss * flat.Loss, 0, cost / (borderSize * 2.0) / linear / flat.Speed);
            return (loss, 0, cost / (borderSize * 2.0) / linear);
        }

        double attackerLoss = AttackerTileLoss(attacker, defender, mag, attackTroops, out double defenderLoss, out double ratio);
        double largeDefender = AttackRules.LargeTerritoryBonus(defender.TileCount, AttackRules.LargeDefenderDepth, Rules.LargeTerritoryMidpoint);
        double traitorCost = IsTraitor(defender) ? AllianceRules.TraitorTravelPercent / 100.0 : 1;
        double speedCost = Math.Clamp(ratio, 0.82, 7.5) * Math.Clamp(ratio / 20, 1, 50) / AttackRules.SpeedCostDivisor;
        double largeAttackerSpeed = AttackRules.LargeTerritoryBonus(attacker.TileCount, AttackRules.LargeAttackerSpeedDepth, Rules.LargeTerritoryMidpoint);
        double tickFraction = speedCost * tileCost * largeAttackerSpeed * largeDefender * traitorCost / borderSize / linear;
        if (attack.Arrow is { } arrow) // directed: thin arrows go faster and pay more, flat ones the other way round
            return (attackerLoss * arrow.Loss, defenderLoss, tickFraction / arrow.Speed);
        return (attackerLoss, defenderLoss, tickFraction);
    }

    /// <summary>Troops an attack of this size loses per tile of the defender's land with this terrain cost (OpenFront's attackerTroopLoss).</summary>
    private double AttackerTileLoss(Player attacker, Player defender, double mag, double attackTroops, out double defenderLoss, out double ratio)
    {
        if (!attacker.IsBot && defender.IsBot)
            mag *= AttackRules.BotDefenderLossMult;
        double largeAttacker = AttackRules.LargeTerritoryBonus(attacker.TileCount, AttackRules.LargeAttackerDepth, Rules.LargeTerritoryMidpoint);
        double largeDefender = AttackRules.LargeTerritoryBonus(defender.TileCount, AttackRules.LargeDefenderDepth, Rules.LargeTerritoryMidpoint);
        double traitorLoss = IsTraitor(defender) ? AllianceRules.TraitorTroopLossPercent / 100.0 : 1;
        defenderLoss = (double)defender.Troops / Math.Max(1, defender.TileCount);
        ratio = defender.Troops / attackTroops;
        return mag * traitorLoss * Math.Clamp(ratio, AttackRules.CheapestTroopRatio, 2)
            * (AttackRules.AttackerLossBase * largeAttacker * largeDefender / Rules.Area + AttackRules.AttackerLossPerDensity * defenderLoss);
    }

    /// <summary>
    /// For the AI: troops an attack of this size would lose taking one tile of the defender's
    /// land on this terrain (with this land cover), no defense post or fallout (OpenFront's
    /// estimate for sizing attacks on bots).
    /// </summary>
    internal double EstimateTileLoss(Player attacker, Player defender, TerrainType terrain, int coverTenths, double attackTroops)
    {
        var (mag, _) = AttackRules.Terrain(terrain);
        mag *= 1 + (coverTenths / 10.0 - 1) * AttackRules.CoverTroopCostPercent / 100;
        return AttackerTileLoss(attacker, defender, mag, attackTroops, out _, out _);
    }
}
