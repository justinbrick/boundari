using System;

namespace Boundari.Map;

public enum TerrainType : byte
{
    Ocean,
    Lake,
    Plains,
    Highlands,
    Mountains,

    /// <summary>
    /// Outside the play area (land or sea past a regional map's natural edge): drawn greyed
    /// out like the world around it, and neither land nor water to the game, so nothing can
    /// claim, cross or sail it. Its look (land or sea) still comes from the coast field.
    /// </summary>
    Outside,
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

    /// <summary>
    /// How many of this map's tiles span one tile of the map OpenFront's numbers were tuned
    /// on (our Europe at 3000 tiles wide): 1 for that, bigger for finer maps. See Sim.ScaledRules.
    /// </summary>
    public double TileScale { get; set; } = 1;

    /// <summary>
    /// The tile scale for a built-in map, from its name and size (1 for anything else). Europe's
    /// is measured in tiles per degree of longitude against the original 3000 tiles over 70°, so
    /// it stays right however far the map reaches.
    /// </summary>
    public static double TileScaleFor(string name, int width, MapGeo? geo = null) => name switch
    {
        "Europe" => geo is null ? width / 3000.0 : width / (geo.LonMax - geo.LonMin) / (3000.0 / 70),
        "World" => width / 2400.0,
        _ => 1,
    };

    /// <summary>Real-world location, or null for random maps.</summary>
    public MapGeo? Geo { get; set; }

    /// <summary>Real land cover (forests, fields...), or null if the map has none.</summary>
    public LandCover? Cover { get; set; }

    /// <summary>
    /// For a map with a play area: two bytes per tile, how far inside the play area the tile
    /// is if it's land, then the same if it's water (see <see cref="PlayFieldTiles"/>); or null.
    /// The renderer draws the greyed-out edge from it smoothly; <see cref="TerrainType.Outside"/>
    /// tiles are where it's below 0.
    /// </summary>
    public byte[]? PlayField { get; set; }

    /// <summary>A <see cref="PlayField"/> land byte is 128 + this many steps per tile of distance (so it spans ±8 tiles).</summary>
    public const int PlayFieldSteps = 16;

    /// <summary>A <see cref="PlayField"/> water byte is 128 + this many steps per tile (±42 tiles: the sea fades out past the edge).</summary>
    public const int PlayFieldWaterSteps = 3;

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

    public bool IsLand(int index) => (uint)(Terrain[index] - TerrainType.Plains) <= TerrainType.Mountains - TerrainType.Plains;

    /// <summary>Outside the play area: neither land nor water to the game (see <see cref="TerrainType.Outside"/>).</summary>
    public bool IsOutside(int index) => Terrain[index] == TerrainType.Outside;

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
