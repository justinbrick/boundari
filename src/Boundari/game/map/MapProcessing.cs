using System;
using System.Collections.Generic;

namespace Boundari.Map;

/// <summary>
/// Clean-up and classification steps shared by every map source (random
/// generator, real-world map builder). They all work from <see cref="GameMap.CoastField"/>:
/// a tile is land when its value is above 0.
/// </summary>
public static class MapProcessing
{
    // Keeps flipped values clearly on the new side of 0.
    private const float SignNudge = 0.001f;

    public static void RemoveSmallLandmasses(GameMap map, int minTiles)
    {
        var visited = new bool[map.TileCount];
        var region = new List<int>();
        var queue = new int[map.TileCount];
        for (int i = 0; i < map.TileCount; i++)
        {
            if (visited[i] || map.CoastField[i] <= 0)
                continue;
            FloodFill(map, i, visited, queue, region, land: true);
            // Flip the field's sign (rather than flattening it) so the sea floor stays smooth.
            if (region.Count < minTiles)
                foreach (int t in region)
                    map.CoastField[t] = -map.CoastField[t] - SignNudge;
        }
    }
    /// <summary>Water touching the map edge is ocean; enclosed water is a lake (or filled in if tiny).</summary>
    public static void ClassifyWater(GameMap map, int minLakeTiles)
    {
        var visited = new bool[map.TileCount];
        var region = new List<int>();
        var queue = new int[map.TileCount];
        int w = map.Width, h = map.Height;
        for (int i = 0; i < map.TileCount; i++)
        {
            if (map.CoastField[i] > 0)
            {
                map.Terrain[i] = TerrainType.Plains; // refined in AssignTerrain
                continue;
            }
            if (visited[i])
                continue;

            FloodFill(map, i, visited, queue, region, land: false);
            bool touchesEdge = false;
            foreach (int t in region)
            {
                int x = t % w, y = t / w;
                if (x == 0 || y == 0 || x == w - 1 || y == h - 1)
                {
                    touchesEdge = true;
                    break;
                }
            }

            if (touchesEdge)
                foreach (int t in region)
                    map.Terrain[t] = TerrainType.Ocean;
            else if (region.Count >= minLakeTiles)
                foreach (int t in region)
                    map.Terrain[t] = TerrainType.Lake;
            else
                foreach (int t in region)
                {
                    map.CoastField[t] = -map.CoastField[t] + SignNudge;
                    map.Terrain[t] = TerrainType.Plains;
                }
        }
    }

    /// <summary>Splits land into plains / highlands / mountains by elevation (0..1).</summary>
    public static void AssignTerrain(GameMap map, float plainsEnd, float highlandsEnd)
    {
        for (int i = 0; i < map.TileCount; i++)
        {
            if (map.Terrain[i] <= TerrainType.Lake)
                continue;
            float e = map.Elevation[i];
            map.Terrain[i] = e < plainsEnd ? TerrainType.Plains
                : e < highlandsEnd ? TerrainType.Highlands
                : TerrainType.Mountains;
        }
    }

    /// <summary>Numbers each island/continent and counts land. Run last, after terrain is final.</summary>
    public static void LabelLandmasses(GameMap map)
    {
        Array.Fill(map.LandmassId, GameMap.NoLandmass);
        var visited = new bool[map.TileCount];
        var region = new List<int>();
        var queue = new int[map.TileCount];
        var sizes = new List<int>();
        for (int i = 0; i < map.TileCount; i++)
        {
            if (visited[i] || !map.IsLand(i))
                continue;
            FloodFill(map, i, visited, queue, region, land: true, playable: true);
            int id = sizes.Count;
            foreach (int t in region)
                map.LandmassId[t] = id;
            sizes.Add(region.Count);
        }
        map.LandmassSizes = sizes.ToArray();
        int landTiles = 0;
        foreach (int size in sizes)
            landTiles += size;
        map.LandTileCount = landTiles;
    }

    /// <summary>4-connected flood fill over land (CoastField &gt; 0) or water tiles; <paramref name="playable"/>: not into the area outside the play area.</summary>
    private static void FloodFill(GameMap map, int start, bool[] visited, int[] queue, List<int> region, bool land, bool playable = false)
    {
        region.Clear();
        int w = map.Width, h = map.Height;
        int head = 0, tail = 0;
        queue[tail++] = start;
        visited[start] = true;
        while (head < tail)
        {
            int t = queue[head++];
            region.Add(t);
            int x = t % w, y = t / w;
            if (x > 0) Visit(t - 1);
            if (x < w - 1) Visit(t + 1);
            if (y > 0) Visit(t - w);
            if (y < h - 1) Visit(t + w);
        }

        void Visit(int n)
        {
            if (visited[n] || (map.CoastField[n] > 0) != land || (playable && map.IsOutside(n)))
                return;
            visited[n] = true;
            queue[tail++] = n;
        }
    }
}
