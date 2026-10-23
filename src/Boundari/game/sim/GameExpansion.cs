using System;
using System.Collections.Generic;
using Boundari.Core;
using Boundari.Map;

namespace Boundari.Sim;

/// <summary>
/// Expanding into the wilderness (the user's design): the whole border facing unclaimed
/// land grows outwards, tile by tile from OpenFront's randomised queue (so the land swells
/// roundly, as an OpenFront attack on empty land does), with no troops spent. Its pace
/// comes from how many dots the player has: OpenFront's top expansion speed at
/// <see cref="DotRules.ExpandFullSpeedDots"/>, slower with fewer. It runs until stopped or
/// no wilderness touches the player's land. Dots don't push into the wilderness; they
/// just keep to the border as it grows.
/// </summary>
public sealed partial class Game
{
    private readonly List<Attack> _expansions = new(); // one per expanding player, in start order

    /// <summary>True if the player is expanding into the wilderness.</summary>
    public bool IsExpanding(Player player) => _expansions.Exists(e => e.Attacker == player);

    private void StartExpansion(Player player)
    {
        if (!player.IsAlive || IsExpanding(player))
            return;
        var expansion = new Attack(player, Nobody, 0, -1, new Rng(_rng.Fork())) { Id = _nextUnitId++ };
        RefreshToConquer(expansion);
        if (expansion.ToConquer.Count > 0)
            _expansions.Add(expansion);
    }

    private void StopExpansion(Player player) => _expansions.RemoveAll(e => e.Attacker == player);

    /// <summary>
    /// Share of a tick one wilderness tile takes: OpenFront's cost for empty land with the
    /// player's dots in place of troops (fewer dots, slower), spread over the front.
    /// </summary>
    private double ExpansionTileCost(Player player, int tile, int borderSize)
    {
        double tileCost = AttackRules.Terrain(Map.Terrain[tile]).TileCost * CoverTenths[tile] / 10.0;
        double plains = AttackRules.Terrain(TerrainType.Plains).TileCost;
        double cost = AttackRules.TerraNulliusMinCost * tileCost / plains * DotRules.ExpandFullSpeedDots / Math.Max(1, player.DotCount);
        cost = Math.Clamp(cost, AttackRules.TerraNulliusMinCost * tileCost / plains, AttackRules.TerraNulliusMaxCost);
        return cost / (borderSize * 2.0) / Rules.Linear;
    }

    /// <summary>Every expansion takes wilderness tiles off its queue until the tick's budget is spent.</summary>
    private void RunExpansions()
    {
        for (int i = 0; i < _expansions.Count; i++)
        {
            var e = _expansions[i];
            var player = e.Attacker;
            if (!player.IsAlive)
            {
                _expansions.RemoveAt(i--);
                continue;
            }
            int borderSize = Math.Max(1, e.Border.Count + e.Rng.RangeInt(0, 4));
            double budget = 1;
            bool refreshed = false;
            while (budget > 0)
            {
                if (!e.ToConquer.TryDequeue(out int tile, out _))
                {
                    // Out of tiles: look along the whole border once (land won elsewhere may
                    // touch new wilderness); nothing there means the wilderness is all taken.
                    if (!refreshed)
                    {
                        refreshed = true;
                        RefreshToConquer(e);
                        if (e.ToConquer.Count > 0)
                            continue;
                    }
                    _expansions.RemoveAt(i--);
                    break;
                }
                e.Border.Remove(tile);
                if (Owner[tile] != Nobody || !Map.IsLand(tile) || !Touches(tile, player.Id))
                    continue; // stale: taken by someone, or no longer next to us
                AddNeighbours(e, tile);
                budget -= ExpansionTileCost(player, tile, borderSize);
                SetOwner(tile, player.Id);
            }
        }
    }
}
