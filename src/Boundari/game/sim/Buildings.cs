namespace Boundari.Sim;

public enum BuildingType : byte
{
    Port,
    City,
    Factory,
    Defense,
    Silo,
    Sam,
    Warship, // not a building: placed on water near your port, see Warships.cs
}

/// <summary>A structure on the map. Belongs to whoever owns its tile, so it's captured with the land.</summary>
public sealed class Building
{
    public readonly BuildingType Type;
    public readonly int Tile;
    public ushort Owner { get; internal set; }

    /// <summary>True once the building is gone (abandoned, or a defense post that was captured).</summary>
    public bool Removed { get; internal set; }

    /// <summary>Stacked levels: build the same thing on top to raise it (see <see cref="Balance.Stackable"/>).</summary>
    public int Level { get; internal set; } = 1;

    /// <summary>Tick when construction finishes; until then it does nothing.</summary>
    public readonly int ReadyTick;
    public readonly int StartTick;

    public bool IsReady(int tick) => tick >= ReadyTick;

    /// <summary>Tick it gets torn down, if its owner ordered that (-1 = not).</summary>
    public int DemolishTick { get; internal set; } = -1;

    internal Building(BuildingType type, int tile, ushort owner, int startTick, int readyTick)
    {
        Type = type;
        Tile = tile;
        Owner = owner;
        StartTick = startTick;
        ReadyTick = readyTick;
    }
}

/// <summary>Place a building. Rules are checked in <see cref="Game.CanBuild"/>.</summary>
public sealed record BuildIntent(ushort PlayerId, BuildingType Type, int Tile) : Intent(PlayerId);

/// <summary>The three bombs (not launchable yet; prices only). MOAB = OpenFront's atom bomb,
/// Fat Man = hydrogen bomb, Tsar Bomba = MIRV.</summary>
public enum NukeType : byte
{
    Moab,
    FatMan,
    TsarBomba,
}
