using System;

namespace Boundari.Map;

public enum TerrainType : byte
{
    Ocean,
    Lake,
    Plains,
    Highlands,
    Mountains,
}

/// <summary>
/// The game map: a grid of tiles used by the simulation (territory, expansion,
/// bots), plus continuous fields the renderer uses to draw smooth coastlines.
/// Pure data, no Godot types, so it can run on a server later.
/// </summary>
public sealed class GameMap
{
    public const int NoLandmass = -1;

    public readonly int Width;
    public readonly int Height;
    public readonly ulong Seed;

    /// <summary>Display name, e.g. "World". Empty for random maps.</summary>
    public string Name { get; set; } = "";

    public readonly TerrainType[] Terrain;

    /// <summary>Signed field: above 0 is land, below 0 is water. The coastline is where it crosses 0.</summary>
    public readonly float[] CoastField;

    /// <summary>Land height in [0, 1]. Drives terrain type and shading.</summary>
    public readonly float[] Elevation;

    /// <summary>Which island/continent each land tile belongs to, or <see cref="NoLandmass"/>.</summary>
    public readonly int[] LandmassId;

    /// <summary>Tile count of each landmass, indexed by landmass id.</summary>
    public int[] LandmassSizes { get; internal set; } = Array.Empty<int>();

    public int LandTileCount { get; internal set; }

    public GameMap(int width, int height, ulong seed)
    {
        Width = width;
        Height = height;
        Seed = seed;
        int n = width * height;
        Terrain = new TerrainType[n];
        CoastField = new float[n];
        Elevation = new float[n];
        LandmassId = new int[n];
    }

    public int TileCount => Width * Height;

    public int Index(int x, int y) => y * Width + x;

    public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    public bool IsLand(int index) => Terrain[index] >= TerrainType.Plains;

    public bool IsWater(int index) => Terrain[index] <= TerrainType.Lake;

    /// <summary>Landmasses at least this big count as continents.</summary>
    public int CountLandmasses(int minTiles)
    {
        int count = 0;
        foreach (int size in LandmassSizes)
            if (size >= minTiles)
                count++;
        return count;
    }
}
