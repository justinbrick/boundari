using System;
using System.Collections.Generic;

namespace Boundari.Sim;

public sealed class Player
{
    /// <summary>Owner id on the map (never 0, which means "nobody").</summary>
    public readonly ushort Id;
    public readonly string Name;
    public readonly bool IsBot;

    /// <summary>Flag code (ISO country code, e.g. "pl"), or empty for none.</summary>
    public readonly string Flag;

    public long Troops { get; internal set; }
    public long Gold { get; internal set; }

    /// <summary>Total gold this player's trade ships have earned for them.</summary>
    public long TradeGold { get; internal set; }
    public int TileCount { get; internal set; }

    /// <summary>Owned tiles that touch land owned by someone else (or nobody).</summary>
    public readonly HashSet<int> Border = new();

    /// <summary>Owned tiles that touch water a boat can use (where ports and boats launch).</summary>
    public readonly HashSet<int> Coast = new();

    public bool HasSpawned { get; internal set; }

    internal int LastLandChange;
    internal int LastEnclaveCheck = -1;

    /// <summary>How many of each building type this player has built (for prices).</summary>
    internal readonly int[] Constructed = new int[Enum.GetValues<BuildingType>().Length];

    /// <summary>Starting tiles, so a spawn can be moved during the spawn phase.</summary>
    internal readonly List<int> SpawnArea = new();

    public bool IsAlive => !HasSpawned || TileCount > 0;

    /// <summary>Troops gained over the last second, for the HUD.</summary>
    public long TroopsPerSecond { get; internal set; }

    public long MaxTroops => Balance.MaxTroops(TileCount, CityLevels, IsBot);

    /// <summary>Levels of finished cities this player owns (each adds to the troop cap).</summary>
    public int CityLevels { get; internal set; }

    /// <summary>Tiles of this player's finished defense posts.</summary>
    internal readonly List<int> DefensePosts = new();

    /// <summary>Gold earned from trains (theirs or others' stopping at their stations).</summary>
    public long TrainGold { get; internal set; }

    internal Player(ushort id, string name, bool isBot, string flag)
    {
        Id = id;
        Name = name;
        IsBot = isBot;
        Flag = flag;
    }

    public override string ToString() => $"{Name} (#{Id})";
}
