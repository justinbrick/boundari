using System;
using System.Collections.Generic;
using System.Linq;

namespace Boundari.Sim;

/// <summary>
/// OpenFront's tribe annexation (Hard and Impossible nations): a tribe walled in by one
/// player is handed over whole the next time its land changes (see GameClusters), so the
/// nation takes tribes in an order that walls others in, then pokes those with a tiny
/// attack to collect them for next to nothing.
/// </summary>
internal static class BotAnnexation
{
    /// <summary>What a tribe's land touches, seen from the player planning to take it.</summary>
    private sealed class Surroundings
    {
        public required Player Bot;
        public bool BordersUs;
        /// <summary>Touches water, the map edge, unclaimed land or anyone but us and tribes we may attack.</summary>
        public bool Open;
        /// <summary>The other tribes it borders.</summary>
        public readonly SortedSet<ushort> Bots = new();
    }

    /// <summary>Tribes that border only us and that we aren't attacking yet: any attack gets them annexed.</summary>
    public static List<Player> WalledIn(Game game, Player player, List<Player> bots)
    {
        var attacking = AttackedBy(game, player);
        var noBots = new HashSet<ushort>();
        var result = new List<Player>();
        foreach (var bot in bots)
            if (!attacking.Contains(bot.Id) && Scan(game, player, bot, true, noBots) is { BordersUs: true })
                result.Add(bot);
        return result;
    }

    /// <summary>
    /// Tribes to attack, best first: a checkerboard of tribes across our front and the row
    /// behind it is left to be annexed for free, and the tribes walling them in go first.
    /// </summary>
    public static List<Player> PlanAttacks(Game game, Player player, List<Player> bots)
    {
        var region = new Dictionary<ushort, Surroundings>();
        foreach (var bot in bots)
            region[bot.Id] = Scan(game, player, bot, false, null)!;
        var front = bots.Where(b => region[b.Id].BordersUs).ToList();
        // Open tribes behind the front are never kept, so only the closed ones are needed.
        var behind = new HashSet<ushort>();
        foreach (var bot in front)
            foreach (ushort other in region[bot.Id].Bots.ToList())
            {
                if (region.ContainsKey(other) || !behind.Add(other))
                    continue;
                if (Scan(game, player, game.GetPlayer(other)!, true, null) is { } s)
                    region[other] = s;
            }
        var attacking = AttackedBy(game, player);

        // Largest first, tribes that don't border each other; each neighbour earns a share of the land it walls in.
        var candidates = region.Values
            .Where(s => !s.Open && s.Bots.Count > 0 && !attacking.Contains(s.Bot.Id) && game.BuildingsOf(s.Bot).Count == 0)
            .OrderByDescending(s => s.Bot.TileCount)
            .ToList();
        var kept = new HashSet<ushort>();
        var nextToKept = new HashSet<ushort>();
        var walledIn = new Dictionary<ushort, double>();
        foreach (var s in candidates)
        {
            if (nextToKept.Contains(s.Bot.Id))
                continue;
            kept.Add(s.Bot.Id);
            double share = (double)s.Bot.TileCount / s.Bots.Count;
            foreach (ushort other in s.Bots)
            {
                nextToKept.Add(other);
                walledIn[other] = walledIn.GetValueOrDefault(other) + share;
            }
        }

        // Most land per tribe troop, counting the land it walls in.
        double Value(Player bot) => (bot.TileCount + walledIn.GetValueOrDefault(bot.Id)) / Math.Max(1, bot.Troops);
        var targets = bots
            .Where(bot =>
            {
                var s = region[bot.Id];
                bool walled = s.BordersUs && !s.Open && s.Bots.Count == 0;
                return !kept.Contains(bot.Id) && !walled;
            })
            .OrderByDescending(Value)
            .ToList();
        // Behind water or the map edge, kept tribes can be all we border: then they're the way on.
        if (!targets.Any(bot => region[bot.Id].BordersUs))
            targets.AddRange(front.Where(bot => kept.Contains(bot.Id)).OrderByDescending(Value));
        return targets;
    }

    private static HashSet<ushort> AttackedBy(Game game, Player player)
    {
        var targets = new HashSet<ushort>();
        foreach (var a in game.Attacks)
            if (a.Attacker == player && a.TargetId != Game.Nobody)
                targets.Add(a.TargetId);
        return targets;
    }

    /// <summary>
    /// Looks all around a tribe's land, like the walled-in check does. Gives up (null) once it
    /// turns out open if <paramref name="closedOnly"/>, or once it borders a tribe outside
    /// <paramref name="within"/>.
    /// </summary>
    private static Surroundings? Scan(Game game, Player player, Player bot, bool closedOnly, HashSet<ushort>? within)
    {
        var map = game.Map;
        int w = map.Width, h = map.Height;
        var s = new Surroundings { Bot = bot };
        Span<int> nb = stackalloc int[4];
        bool Check(int tile, Span<int> nb)
        {
            int x = tile % w, y = tile / w;
            if (x == 0 || y == 0 || x == w - 1 || y == h - 1)
            {
                s.Open = true;
                return !closedOnly;
            }
            int n = 0;
            if (x > 0) nb[n++] = tile - 1;
            if (x < w - 1) nb[n++] = tile + 1;
            if (y > 0) nb[n++] = tile - w;
            if (y < h - 1) nb[n++] = tile + w;
            for (int k = 0; k < n; k++)
            {
                ushort o = game.Owner[nb[k]];
                if (o == bot.Id)
                    continue;
                if (o == player.Id)
                {
                    s.BordersUs = true;
                    continue;
                }
                // Water and unclaimed land count as open, like anyone but tribes we may attack.
                var other = o == Game.Nobody || map.IsWater(nb[k]) ? null : game.GetPlayer(o);
                if (other is null || !other.IsBot || player.IsAlliedWith(other))
                {
                    s.Open = true;
                    if (closedOnly)
                        return false;
                }
                else if (within is not null && !within.Contains(o))
                    return false;
                else
                    s.Bots.Add(o);
            }
            return true;
        }
        foreach (int t in bot.Border)
            if (!Check(t, nb))
                return null;
        foreach (int t in bot.Coast)
            if (!Check(t, nb))
                return null;
        return s;
    }
}
