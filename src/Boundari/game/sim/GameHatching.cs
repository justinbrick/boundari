using System;
using System.Collections.Generic;

namespace Boundari.Sim;

/// <summary>Hatched (occupied) land: Boundari's own rule, not OpenFront's.</summary>
public static class HatchRules
{
    /// <summary>How long land taken from another player stays hatched before it settles by itself.</summary>
    public const int SettleTicks = 30 * Balance.TicksPerSecond;
}

// Hatched land: land taken from another player (tribes included) comes in hatched for a
// while. It adds no room to the troop cap until it settles, which it does by itself after
// HatchRules.SettleTicks, or at once when its owner builds a structure on it (that secures
// the whole connected hatched patch). Win back a tile you lost while it's still hatched and
// it's yours again straight away. Grabbing empty land is never hatched.
public sealed partial class Game
{
    /// <summary>Match option: play with hatched land (on by default; off = classic OpenFront).</summary>
    public bool HatchedLand { get; set; } = true;

    /// <summary>Tick each hatched tile settles at (turns into normal land); 0 = not hatched.</summary>
    public readonly int[] HatchedUntil;

    /// <summary>Who each hatched tile was taken from (they get it back unhatched if they retake it).</summary>
    private readonly ushort[] _hatchedFrom;

    // Hatched tiles in the order they settle (every tile waits the same time, so first in,
    // first out). An entry whose time no longer matches the tile's is stale: skipped.
    private readonly Queue<(int Until, int Tile)> _settling = new();

    public bool IsHatched(int tile) => HatchedUntil[tile] != 0;

    /// <summary>Hatches or unhatches a tile changing hands (called by SetOwner, before the counts change).</summary>
    private void HatchOnCapture(int tile, ushort old, ushort newOwner)
    {
        bool wasHatched = HatchedUntil[tile] != 0;
        bool retaken = wasHatched && _hatchedFrom[tile] == newOwner;
        if (wasHatched)
        {
            _players[old].HatchedTiles--;
            HatchedUntil[tile] = 0;
            _hatchedFrom[tile] = Nobody;
        }
        if (!HatchedLand || old == Nobody || newOwner == Nobody || retaken)
            return;
        int until = Tick + HatchRules.SettleTicks;
        HatchedUntil[tile] = until;
        _hatchedFrom[tile] = old;
        _players[newOwner].HatchedTiles++;
        _settling.Enqueue((until, tile));
    }

    /// <summary>Turns a hatched tile into normal land.</summary>
    private void Settle(int tile)
    {
        HatchedUntil[tile] = 0;
        _hatchedFrom[tile] = Nobody;
        _players[Owner[tile]].HatchedTiles--;
        ChangedTiles.Add(tile);
    }

    /// <summary>Hatched land whose time is up turns normal.</summary>
    private void SettleHatchedLand()
    {
        while (_settling.Count > 0 && _settling.Peek().Until <= Tick)
        {
            var (until, tile) = _settling.Dequeue();
            if (HatchedUntil[tile] == until)
                Settle(tile);
        }
    }

    /// <summary>
    /// A structure built on hatched land secures it: the whole hatched patch of the owner's
    /// land joined to that tile turns normal.
    /// </summary>
    private void SecureHatchedPatch(Player player, int tile)
    {
        if (!IsHatched(tile) || Owner[tile] != player.Id)
            return;
        var queue = new Queue<int>();
        queue.Enqueue(tile);
        Settle(tile);
        Span<int> nb = stackalloc int[4];
        while (queue.Count > 0)
        {
            int count = LandNeighbours(queue.Dequeue(), nb);
            for (int k = 0; k < count; k++)
            {
                int n = nb[k];
                if (Owner[n] != player.Id || !IsHatched(n))
                    continue;
                Settle(n);
                queue.Enqueue(n);
            }
        }
    }
}
