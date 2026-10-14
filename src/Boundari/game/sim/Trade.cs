using System;
using System.Collections.Generic;
using Boundari.Core;

namespace Boundari.Sim;

/// <summary>
/// A trade ship sailing between two players' ports. When it arrives, both port owners
/// are paid (more for longer routes). If either port changes hands or is lost on the
/// way, the trip is cancelled.
/// </summary>
public sealed class TradeShip
{
    public Player Owner { get; internal set; }
    public readonly Building From;
    public Building To { get; internal set; }
    public int[] Path { get; internal set; }

    /// <summary>Taken by an enemy warship: now sailing to the captor's port.</summary>
    public bool Captured { get; internal set; }

    /// <summary>Tiles sailed on earlier legs (before a capture re-routed it).</summary>
    internal int EarlierTiles;

    public int Tile => Path[(int)System.Math.Min(Progress / 1000, Path.Length - 1)];

    /// <summary>Distance travelled along <see cref="Path"/>, in thousandths of a step.</summary>
    public long Progress { get; internal set; }

    internal TradeShip(Player owner, Building from, Building to, int[] path)
    {
        Owner = owner;
        From = from;
        To = to;
        Path = path;
    }

    public bool Arrived => Progress >= (Path.Length - 1) * 1000L;
}

/// <summary>
/// Trade rules, modelled on OpenFront's (src/core/configuration: tradeShipGold,
/// tradeShipSpawnRate, PortExecution). Kept in one place for balancing.
/// </summary>
public static class TradeRules
{
    // A port rolls for a new trade ship every this many ticks.
    public const int PortCheckTicks = 10;

    // Trades shorter than this (Manhattan tiles) pay very little and get no proximity bonus.
    public const int ShortRangeDebuffTiles = 300;

    // Base 1-in-N chance per roll; a pity timer shortens it after each failed roll.
    public const int BaseSpawnOdds = 100;

    // Safety cap on ships at sea, far beyond normal games.
    public const int MaxTradeShips = 800;

    /// <summary>
    /// Gold paid to EACH port owner for a trip of this many tiles: an S-curve that
    /// heavily punishes short routes, plus a linear part so long routes keep paying.
    /// </summary>
    public static long Gold(int tilesTravelled)
    {
        double gold = 75_000 / (1 + Math.Exp(-0.03 * (tilesTravelled - ShortRangeDebuffTiles))) + 50.0 * tilesTravelled;
        return (long)Math.Floor(gold);
    }

    /// <summary>
    /// Fleet-size throttle: a small boost while few ships are at sea, then damping past
    /// ~330 ships down to a 0.25 plateau, so more ports always help but with diminishing returns.
    /// </summary>
    public static double Saturation(int shipsAtSea)
    {
        double boost = 1 + 0.45 * Math.Exp(-shipsAtSea / 120.0);
        double damping = 1 - 1 / (1 + Math.Exp(-(Math.Log(2) / 50) * (shipsAtSea - 330)));
        double plateau = shipsAtSea >= MaxTradeShips ? 0 : 0.25;
        return boost * Math.Max(damping, plateau);
    }

    /// <summary>The N in this roll's 1-in-N chance of sending a ship.</summary>
    public static int SpawnOdds(int rejections, int shipsAtSea)
    {
        if (shipsAtSea >= MaxTradeShips)
            return int.MaxValue; // hard cap: no new ships
        double saturation = Saturation(shipsAtSea);
        if (saturation <= 0)
            return int.MaxValue;
        return Math.Max(1, (int)Math.Floor(BaseSpawnOdds / (rejections + 1.0) / saturation));
    }

    /// <summary>How many of the nearest destination ports get double weight.</summary>
    public static int ProximityBonusPorts(int totalPorts) => Math.Clamp(totalPorts / 3, Math.Min(4, totalPorts), totalPorts);
}

/// <summary>Per-port trade state (pity timer and its own random stream).</summary>
internal sealed class PortTrade
{
    public readonly Rng Rng;
    public readonly int CheckOffset;
    public int Rejections;

    public PortTrade(ulong seed, int checkOffset)
    {
        Rng = new Rng(seed);
        CheckOffset = checkOffset;
    }
}
