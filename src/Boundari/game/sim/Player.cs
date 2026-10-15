using System;
using System.Collections.Generic;

namespace Boundari.Sim;

/// <summary>
/// Who is playing, as in OpenFront: people; nations (proper AI opponents, built for a
/// fight); and bots (OpenFront's "tribes": weak filler that grabs land and gets eaten).
/// </summary>
public enum PlayerKind : byte
{
    Human,
    Nation,
    Bot,
}

/// <summary>How strong nations are (OpenFront's difficulty setting). Bots are always weak.</summary>
public enum Difficulty : byte
{
    Easy,
    Medium,
    Hard,
    Impossible,
}

public sealed class Player
{
    /// <summary>Owner id on the map (never 0, which means "nobody").</summary>
    public readonly ushort Id;
    public readonly string Name;
    public readonly PlayerKind Kind;
    public bool IsBot => Kind == PlayerKind.Bot;
    public bool IsNation => Kind == PlayerKind.Nation;

    /// <summary>The match's difficulty (only changes nations' numbers).</summary>
    public readonly Difficulty Difficulty;

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
    internal int LastClusterCalc = int.MinValue; // when its walled-in pockets were last checked (GameClusters.cs)

    /// <summary>How many of each building type this player has built (for prices).</summary>
    internal readonly int[] Constructed = new int[Enum.GetValues<BuildingType>().Length];

    /// <summary>Starting tiles, so a spawn can be moved during the spawn phase.</summary>
    internal readonly List<int> SpawnArea = new();

    public bool IsAlive => !HasSpawned || TileCount > 0;

    /// <summary>Troops gained over the last second, for the HUD.</summary>
    public long TroopsPerSecond { get; internal set; }

    public long MaxTroops => Balance.MaxTroops(TileCount, CityLevels, Kind, Difficulty);

    /// <summary>Same, without what cities add.</summary>
    public long MaxTroopsWithoutCities => Balance.MaxTroops(TileCount, 0, Kind, Difficulty);

    /// <summary>Attacks and boats this player has sent (someone who never fought gives up no gold when conquered).</summary>
    internal int AttacksSent;

    /// <summary>Dev option: may still spawn after the spawn phase, anywhere on land (taking it from whoever owns it).</summary>
    public bool LateSpawn { get; internal set; }

    /// <summary>Dev cheats: never runs out of gold / buildings finish instantly.</summary>
    public bool InfiniteGold { get; internal set; }
    public bool InstantBuild { get; internal set; }

    public bool CanAfford(long cost) => InfiniteGold || Gold >= cost;

    internal void Spend(long cost)
    {
        if (!InfiniteGold)
            Gold -= cost;
    }

    /// <summary>Earliest tick this player may order another demolition.</summary>
    internal int NextDemolishTick;

    /// <summary>Levels of finished cities this player owns (each adds to the troop cap).</summary>
    public int CityLevels { get; internal set; }

    /// <summary>Tiles of this player's finished defense posts.</summary>
    internal readonly List<int> DefensePosts = new();

    /// <summary>Gold earned from trains (theirs or others' stopping at their stations).</summary>
    public long TrainGold { get; internal set; }

    /// <summary>Ids of the players this one is allied with (see GameAlliances.cs).</summary>
    internal readonly HashSet<ushort> Allies = new();

    /// <summary>Allied with that player right now.</summary>
    public bool IsAlliedWith(Player other) => Allies.Contains(other.Id);

    /// <summary>Number of alliances this player has.</summary>
    public int AllianceCount => Allies.Count;

    /// <summary>Until this tick the player is a traitor (broke an alliance): easier to attack.</summary>
    public int TraitorUntilTick { get; internal set; } = -1;

    /// <summary>How this player feels about each other player (by id), in hundredths: -10000..10000. Null until it matters.</summary>
    internal int[]? Relations;

    internal Player(ushort id, string name, PlayerKind kind, Difficulty difficulty, string flag)
    {
        Id = id;
        Name = name;
        Kind = kind;
        Difficulty = difficulty;
        Flag = flag;
    }

    public override string ToString() => $"{Name} (#{Id})";
}
