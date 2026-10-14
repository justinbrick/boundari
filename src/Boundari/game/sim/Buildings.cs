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

    /// <summary>True once the building is gone (its land was abandoned).</summary>
    public bool Removed { get; internal set; }

    internal Building(BuildingType type, int tile, ushort owner)
    {
        Type = type;
        Tile = tile;
        Owner = owner;
    }
}

/// <summary>Place a building. Rules are checked in <see cref="Game.CanBuild"/>.</summary>
public sealed record BuildIntent(ushort PlayerId, BuildingType Type, int Tile) : Intent(PlayerId);
