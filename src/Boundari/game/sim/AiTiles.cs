using System;
using System.Collections.Generic;

namespace Boundari.Sim;

// What the AIs look at around a player, as OpenFront's nations and tribes see it.
internal static partial class AiTiles
{
    /// <summary>What lies around a player (OpenFront's nearby(): land neighbours, plus anyone just across a river).</summary>
    internal sealed class NearbyInfo
    {
        /// <summary>Players touching our land or just across a narrow strip of water, in id order.</summary>
        public readonly List<Player> Players = new();
        /// <summary>Ids of the players touching our land.</summary>
        public readonly HashSet<ushort> Land = new();
        /// <summary>Unclaimed land (not fallout) next to ours, or just across a narrow strip of water.</summary>
        public bool EmptyLand;
        /// <summary>Unclaimed land (not fallout) right next to ours.</summary>
        public bool BorderEmptyLand;
        /// <summary>Any unclaimed land right next to ours, fallout included.</summary>
        public bool BorderAnyEmptyLand;
        /// <summary>Fallout right next to ours.</summary>
        public bool BorderFallout;
    }

    // OpenFront looks 5 tiles across water from every 10th shore tile for players "across a river".
    private const int RiverSampleStep = 10;

    /// <summary>What lies around this player this tick (worked out once per tick).</summary>
    public static NearbyInfo Nearby(Game game, Player player)
    {
        if (player.AiNearby is { } known && player.AiNearbyTick == game.Tick)
            return known;
        var info = new NearbyInfo();
        var ids = new SortedSet<ushort>();
        Span<int> nb = stackalloc int[4];
        foreach (int t in player.Border)
        {
            int count = game.LandNeighbours(t, nb);
            for (int k = 0; k < count; k++)
            {
                int n = nb[k];
                ushort o = game.Owner[n];
                if (o == player.Id)
                    continue;
                if (o == Game.Nobody)
                {
                    info.BorderAnyEmptyLand = true;
                    if (game.Fallout[n])
                        info.BorderFallout = true;
                    else
                        info.BorderEmptyLand = info.EmptyLand = true;
                    continue;
                }
                info.Land.Add(o);
                ids.Add(o);
            }
        }

        // Across a river: from every 10th shore tile, the land 5 tiles out over the water.
        var map = game.Map;
        int w = map.Width, reach = game.Rules.D(5);
        var coast = new List<int>(player.Coast);
        coast.Sort(); // HashSet order isn't guaranteed; keep every machine identical
        ReadOnlySpan<(int Dx, int Dy)> dirs = stackalloc (int, int)[] { (0, -1), (0, 1), (-1, 0), (1, 0) };
        for (int i = 0; i < coast.Count; i += RiverSampleStep)
        {
            int x = coast[i] % w, y = coast[i] / w;
            foreach (var (dx, dy) in dirs)
            {
                if (!map.InBounds(x + dx, y + dy) || !map.IsWater(map.Index(x + dx, y + dy)))
                    continue;
                int fx = x + dx * reach, fy = y + dy * reach;
                if (!map.InBounds(fx, fy))
                    continue;
                int far = map.Index(fx, fy);
                if (!map.IsLand(far))
                    continue;
                ushort o = game.Owner[far];
                if (o == player.Id)
                    continue;
                if (o == Game.Nobody)
                {
                    if (!game.Fallout[far])
                        info.EmptyLand = true;
                }
                else
                    ids.Add(o);
            }
        }
        foreach (ushort id in ids)
            info.Players.Add(game.GetPlayer(id)!);
        player.AiNearby = info;
        player.AiNearbyTick = game.Tick;
        return info;
    }

    /// <summary>The box around a player's land (from its border and coast tiles), worked out once per tick. MaxX &lt; 0 if it has none.</summary>
    public static (int MinX, int MinY, int MaxX, int MaxY) Box(Game game, Player player)
    {
        if (player.AiBoxTick == game.Tick)
            return player.AiBox;
        int w = game.Map.Width, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
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
        player.AiBox = (minX, minY, maxX, maxY);
        player.AiBoxTick = game.Tick;
        return player.AiBox;
    }

    /// <summary>Fewest tiles between two players' boxes (0 if either is unknown or they overlap).</summary>
    public static int BoxGap(Game game, Player a, Player b)
    {
        var p = Box(game, a);
        var q = Box(game, b);
        if (p.MaxX < 0 || q.MaxX < 0)
            return 0;
        int dx = Math.Max(0, Math.Max(p.MinX - q.MaxX, q.MinX - p.MaxX));
        int dy = Math.Max(0, Math.Max(p.MinY - q.MaxY, q.MinY - p.MaxY));
        return dx + dy;
    }

    /// <summary>The tile at the middle of a player's box.</summary>
    public static int BoxCenter(Game game, Player player)
    {
        var b = Box(game, player);
        if (b.MaxX < 0)
            return 0;
        return game.Map.Index((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2);
    }

    public static int Manhattan(Game game, int a, int b)
    {
        int w = game.Map.Width;
        return Math.Abs(a % w - b % w) + Math.Abs(a / w - b / w);
    }

    /// <summary>Troops in all attacks against this player right now.</summary>
    public static long IncomingTroops(Game game, Player player)
    {
        long sum = 0;
        foreach (var a in game.Attacks)
            if (a.TargetId == player.Id)
                sum += a.Troops;
        return sum;
    }

    /// <summary>Whether anyone is attacking this player right now.</summary>
    public static bool UnderAttack(Game game, Player player)
    {
        foreach (var a in game.Attacks)
            if (a.TargetId == player.Id)
                return true;
        return false;
    }

    /// <summary>
    /// OpenFront's findJuiciestTarget: the candidate with the most structures worth taking,
    /// the emptiest troop cap and the most land (each scaled 0..1 over the candidates).
    /// </summary>
    public static Player? Juiciest(Game game, IReadOnlyList<Player> candidates)
    {
        if (candidates.Count == 0)
            return null;
        var structures = new double[candidates.Count];
        var gaps = new double[candidates.Count];
        var tiles = new double[candidates.Count];
        for (int i = 0; i < candidates.Count; i++)
        {
            var p = candidates[i];
            int levels = 0;
            foreach (var b in game.BuildingsOf(p))
                if (b.Type != BuildingType.Defense && b.Type != BuildingType.Silo)
                    levels += b.Level;
            long max = p.MaxTroops;
            structures[i] = levels;
            gaps[i] = max > 0 ? 1 - (double)p.Troops / max : 0;
            tiles[i] = p.TileCount;
        }
        static double Normalize(double v, double[] all)
        {
            double min = double.MaxValue, max = double.MinValue;
            foreach (double x in all)
            {
                min = Math.Min(min, x);
                max = Math.Max(max, x);
            }
            return max > min ? (v - min) / (max - min) : 0;
        }
        Player? best = null;
        double bestScore = double.NegativeInfinity;
        for (int i = 0; i < candidates.Count; i++)
        {
            double score = Normalize(structures[i], structures) + Normalize(gaps[i], gaps) + Normalize(tiles[i], tiles);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidates[i];
            }
        }
        return best;
    }

    /// <summary>
    /// OpenFront's runaway leader: the non-tribe player with the most land, if it has this
    /// many times the runner-up's (Medium 3x, Hard 2x, Impossible 1.5x; Easy never notices).
    /// </summary>
    public static Player? RunawayLeader(Game game)
    {
        double factor = game.Difficulty switch { Difficulty.Easy => 0, Difficulty.Medium => 3, Difficulty.Hard => 2, _ => 1.5 };
        if (factor == 0)
            return null;
        Player? leader = null, runnerUp = null;
        foreach (var p in game.Players)
        {
            if (p.IsBot || !p.IsAlive)
                continue;
            if (leader is null || p.TileCount > leader.TileCount)
                (runnerUp, leader) = (leader, p);
            else if (runnerUp is null || p.TileCount > runnerUp.TileCount)
                runnerUp = p;
        }
        return leader is not null && runnerUp is not null && leader.TileCount >= runnerUp.TileCount * factor ? leader : null;
    }
}
