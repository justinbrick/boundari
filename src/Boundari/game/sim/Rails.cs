using System;
using System.Collections.Generic;
using Boundari.Core;

namespace Boundari.Sim;

/// <summary>
/// A train station: every factory is one, and so is any city or port within reach of a
/// factory. Stations joined by railroads form a network that trains run on.
/// </summary>
public sealed class Station
{
    public readonly Building Building;
    public readonly List<Railroad> Rails = new();
    internal int Cluster = -1;          // connected network this station belongs to
    internal int LastTrainTick = -1000; // factories: cooldown between trains
    internal Rng? Rng;                  // factories: their own random stream for spawning

    internal Station(Building building) => Building = building;

    public bool IsTradeStop => Building.Type is BuildingType.City or BuildingType.Port;
    public bool Active => !Building.Removed;

    public IEnumerable<Station> Neighbours()
    {
        foreach (var r in Rails)
            yield return r.Other(this);
    }
}

/// <summary>A railroad between two stations, laid over land.</summary>
public sealed class Railroad
{
    public readonly Station A, B;
    public readonly int[] Tiles; // from A to B

    internal Railroad(Station a, Station b, int[] tiles)
    {
        A = a;
        B = b;
        Tiles = tiles;
    }

    public Station Other(Station s) => s == A ? B : A;
}

/// <summary>A train: runs from a factory to a trade stop, earning gold at every city or port it stops at.</summary>
public sealed class Train
{
    public readonly Player Owner;
    public readonly Station Source, Destination;
    public readonly int[] Tiles;               // the whole route, tile by tile
    internal readonly Station[] Stops;         // stations along the route
    internal readonly int[] StopTileIndex;     // where along Tiles each stop is
    internal int NextStop = 1;
    public int TradeStopsVisited { get; internal set; }

    /// <summary>Distance along <see cref="Tiles"/>, in thousandths of a tile.</summary>
    public long Progress { get; internal set; }

    internal Train(Player owner, Station[] stops, int[] tiles, int[] stopTileIndex)
    {
        Owner = owner;
        Stops = stops;
        Source = stops[0];
        Destination = stops[^1];
        Tiles = tiles;
        StopTileIndex = stopTileIndex;
    }
}

/// <summary>
/// Rail and train rules, modelled on OpenFront (Config.trainStationMaxRange,
/// trainSpawnRate, trainGold, TrainStationExecution, RailNetworkImpl).
/// </summary>
public static class RailRules
{
    public const int StationMaxRange = 110;   // cities/ports within this of a factory become stations
    public const int StationMinRange = 15;    // never lay rail between stations this close
    public const int MaxRailLength = 230;     // tiles; routes run in straight lines with right-angle turns
    public const int MaxBridgeTiles = 12;     // rails may bridge water this wide (rivers, narrow straits)
    public const int StepCost = 10;           // route cost of one tile of land...
    public const int BridgeStepCost = 30;     // ...of one tile of bridge (so land is preferred)
    public const int TurnCost = 60;           // ...and of each 90-degree turn (so routes stay straight)
    public const int AlreadyConnectedHops = 4; // skip a new rail if already this close on the network
    public const int TrainCooldownTicks = 10;
    public const long TrainSpeedPerTick = 2000; // thousandths of a tile: 2 tiles per tick
    public const int UnitsPerTrain = 7;        // engine, tail and 5 cars (for the spawn throttle)

    /// <summary>Gold for a stop: 10K at your own city/port, 25K at someone else's (both of you get it).
    /// The first 10 stops pay in full, then 5K less each, down to 5K.</summary>
    public static long TrainGold(bool ownStation, int tradeStopsVisited)
    {
        long baseGold = ownStation ? 10_000 : 25_000;
        int penalised = Math.Max(0, tradeStopsVisited - 9);
        return Math.Max(5_000, baseGold - penalised * 5_000L);
    }

    /// <summary>Fewer new trains once lots are running (with a small early boost).</summary>
    public static double Saturation(int trainUnits)
    {
        double boost = 1 + 0.5 * Math.Exp(-trainUnits / 30.0);
        double damping = 1 - Sigmoid(trainUnits, Math.Log(2) / 100, 560);
        double plateau = 0.25 * (1 - Sigmoid(trainUnits, Math.Log(2) / 150, 900));
        return boost * Math.Max(damping, plateau);
    }

    /// <summary>The N in a factory level's 1-in-N chance per tick of sending a train.</summary>
    public static int SpawnOdds(int ownerFactories, int trainUnits)
    {
        double saturation = Saturation(trainUnits);
        if (saturation <= 1e-9)
            return int.MaxValue;
        return Math.Max(1, (int)Math.Floor((ownerFactories + 10) * 15 / saturation));
    }

    private static double Sigmoid(double x, double k, double mid) => 1 / (1 + Math.Exp(-k * (x - mid)));
}
