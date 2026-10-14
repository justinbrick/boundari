namespace Boundari.Map;

/// <summary>What covers the ground. Drawn as flat colours; towns are deliberately left out.</summary>
public enum CoverType : byte
{
    None,     // sea, or no data
    Farmland,
    Meadow,   // grassland, shrubs, wetland, tundra
    Forest,
    Rock,     // bare ground
    Snow,     // glaciers and permanent snow
    Water,    // small lakes and rivers inside the land
}

/// <summary>
/// Land cover on a grid finer than the game tiles (<see cref="Scale"/> cells per tile
/// side), so small forests and fields keep their real, intricate shapes.
/// </summary>
public sealed class LandCover
{
    public readonly int Scale;
    public readonly int Width;
    public readonly int Height;
    public readonly CoverType[] Cells;

    public LandCover(int scale, int mapWidth, int mapHeight)
    {
        Scale = scale;
        Width = mapWidth * scale;
        Height = mapHeight * scale;
        Cells = new CoverType[Width * Height];
    }

    public int Index(int x, int y) => y * Width + x;
}
