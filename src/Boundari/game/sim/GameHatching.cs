using System;
using System.Collections.Generic;

namespace Boundari.Sim;

/// <summary>Hatched (occupied) land: Boundari's own rule, not OpenFront's.</summary>
public static class HatchRules
{
    /// <summary>How long land taken from another player stays hatched before it settles by itself.</summary>
    public const int SettleTicks = 30 * Balance.TicksPerSecond;

    // Hatched land barely holds: attacking it costs a fifth of the troops and takes a third
    // of the time (the mirror image of a defense post's 5x and 3x, so a post on fresh land
    // makes it hold like normal land).
    public const double LossFactor = 1 / 5.0;
    public const double SpeedFactor = 1 / 3.0;
}

// Hatched land: land taken from another player (tribes included) comes in hatched for a
// while. It's very weak (see HatchRules): anyone can take it cheaply and quickly until it
// settles by itself after HatchRules.SettleTicks. Building on it doesn't settle it. Win back
// a tile you lost while it's still hatched and it's yours again straight away (not hatched).
// Grabbing empty land is never hatched.
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
    /// The player's hatched tiles joined to this one (itself included). Empty if the tile
    /// isn't the player's hatched land.
    /// </summary>
    public List<int> HatchedPatch(Player player, int tile)
    {
        var patch = new List<int>();
        if (tile < 0 || tile >= Map.TileCount || !IsHatched(tile) || Owner[tile] != player.Id)
            return patch;
        var seen = new HashSet<int> { tile };
        patch.Add(tile);
        Span<int> nb = stackalloc int[4];
        for (int i = 0; i < patch.Count; i++)
        {
            int count = LandNeighbours(patch[i], nb);
            for (int k = 0; k < count; k++)
            {
                int n = nb[k];
                if (Owner[n] == player.Id && IsHatched(n) && seen.Add(n))
                    patch.Add(n);
            }
        }
        return patch;
    }
}
