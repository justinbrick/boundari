using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Boundari.Map;

namespace Boundari.MapBuilder;

/// <summary>
/// Adds real land cover to a map from ESA WorldCover. The raw grid is fetched by
/// tools/landcover/worldcover.py (run automatically the first time) and cached in tools/data.
/// </summary>
public static class LandCoverBuilder
{
    private const byte Unassigned = 255;

    // Water patches closer than this to the coastline (in tiles) are WorldCover's own
    // version of the sea or coast. Our coastline already draws that, so drop them.
    private const float CoastFringeTiles = 1.5f;

    // Smallest lake worth showing: ~150 cells is about 60 km² on the Europe map. Thin
    // features (river slivers) need some cells fully surrounded by water to stay.
    private const int MinLakeCells = 150;
    private const int MinLakeInteriorCells = 40;

    public static LandCover Build(GameMap map, MapSpec spec, string root)
    {
        var geo = map.Geo ?? throw new InvalidOperationException("Land cover needs a real-world map.");
        var cover = new LandCover(spec.LandCoverScale, map.Width, map.Height);
        byte[] raw = LoadWorldCover(spec, geo, cover.Width, cover.Height, root);

        // 1. WorldCover classes to ours. Towns, no-data and coastal water are left to fill in.
        var cells = new byte[cover.Cells.Length];
        float fringe = CoastFringeTiles * 0.004f; // coast field units per tile, see Program.FieldPerTile
        for (int y = 0; y < cover.Height; y++)
        {
            for (int x = 0; x < cover.Width; x++)
            {
                int i = cover.Index(x, y);
                float field = CoastFieldAt(map, (x + 0.5f) / cover.Scale, (y + 0.5f) / cover.Scale);
                if (field <= 0)
                {
                    cells[i] = (byte)CoverType.None;
                    continue;
                }
                var type = FromWorldCover(raw[i]);
                if (type == CoverType.Water && field < fringe)
                    type = CoverType.None;
                cells[i] = type == CoverType.None ? Unassigned : (byte)type;
            }
        }

        // 2. Drop tiny ponds and thin river slivers (real rivers are cut into the map itself).
        DropSmallWater(cells, cover.Width, cover.Height);

        // 3. Fill the gaps from the nearest real cover (spreads outwards one cell at a time).
        FillFromNeighbours(cells, cover.Width, cover.Height);
        for (int i = 0; i < cells.Length; i++)
            cover.Cells[i] = cells[i] == Unassigned ? CoverType.Farmland : (CoverType)cells[i];
        return cover;
    }

    private static CoverType FromWorldCover(byte code) => code switch
    {
        10 or 95 => CoverType.Forest,           // tree cover, mangroves
        40 => CoverType.Farmland,               // cropland
        20 or 30 or 90 or 100 => CoverType.Meadow, // shrubs, grass, wetland, moss/lichen
        60 => CoverType.Rock,                   // bare / sparse
        70 => CoverType.Snow,                   // snow and ice
        80 => CoverType.Water,
        _ => CoverType.None,                    // 0 no data, 50 built-up (no town colouring)
    };

    private static byte[] LoadWorldCover(MapSpec spec, MapGeo geo, int width, int height, string root)
    {
        string path = Path.Combine(root, "tools", "data", "worldcover", $"{spec.Id}_{width}x{height}.bin");
        if (!File.Exists(path))
            RunFetchScript(geo, width, height, path, root);
        byte[] raw = File.ReadAllBytes(path);
        if (raw.Length != width * height)
            throw new InvalidDataException($"{path} has the wrong size; delete it to fetch again.");
        return raw;
    }

    private static void RunFetchScript(MapGeo geo, int width, int height, string outPath, string root)
    {
        string python = Path.Combine(root, "tools", ".venv", "Scripts", "python.exe");
        if (!File.Exists(python))
            throw new FileNotFoundException("Python tools are not set up. See tools/MapBuilder/README.md.", python);

        string F(double v) => v.ToString(CultureInfo.InvariantCulture);
        var psi = new ProcessStartInfo(python)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
        };
        foreach (string arg in new[]
        {
            "-I", Path.Combine(root, "tools", "landcover", "worldcover.py"),
            "--bbox", F(geo.LonMin), F(geo.LatMin), F(geo.LonMax), F(geo.LatMax),
            "--size", width.ToString(CultureInfo.InvariantCulture), height.ToString(CultureInfo.InvariantCulture),
            "--out", outPath,
        })
            psi.ArgumentList.Add(arg);

        Console.WriteLine("  fetching land cover from ESA WorldCover...");
        using var process = Process.Start(psi)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"worldcover.py failed with exit code {process.ExitCode}.");
    }

    /// <summary>Bilinear coast field at a position in tile units.</summary>
    private static float CoastFieldAt(GameMap map, float tx, float ty)
    {
        float fx = Math.Clamp(tx - 0.5f, 0, map.Width - 1), fy = Math.Clamp(ty - 0.5f, 0, map.Height - 1);
        int x0 = (int)fx, y0 = (int)fy;
        int x1 = Math.Min(x0 + 1, map.Width - 1), y1 = Math.Min(y0 + 1, map.Height - 1);
        float u = fx - x0, v = fy - y0;
        float top = map.CoastField[map.Index(x0, y0)] * (1 - u) + map.CoastField[map.Index(x1, y0)] * u;
        float bottom = map.CoastField[map.Index(x0, y1)] * (1 - u) + map.CoastField[map.Index(x1, y1)] * u;
        return top * (1 - v) + bottom * v;
    }

    /// <summary>
    /// Marks water patches for filling unless they're a proper lake: big enough, and
    /// wide enough to have some cells completely surrounded by water.
    /// </summary>
    private static void DropSmallWater(byte[] cells, int w, int h)
    {
        const byte WaterCell = (byte)CoverType.Water;
        var visited = new bool[cells.Length];
        var queue = new int[cells.Length];
        for (int start = 0; start < cells.Length; start++)
        {
            if (visited[start] || cells[start] != WaterCell)
                continue;

            int head = 0, tail = 0, interior = 0;
            queue[tail++] = start;
            visited[start] = true;
            while (head < tail)
            {
                int i = queue[head++];
                int x = i % w, y = i / w;
                int waterNeighbours = 0;
                if (x > 0) Visit(i - 1);
                if (x < w - 1) Visit(i + 1);
                if (y > 0) Visit(i - w);
                if (y < h - 1) Visit(i + w);
                if (waterNeighbours == 4)
                    interior++;

                void Visit(int n)
                {
                    if (cells[n] != WaterCell)
                        return;
                    waterNeighbours++;
                    if (visited[n])
                        return;
                    visited[n] = true;
                    queue[tail++] = n;
                }
            }

            if (tail < MinLakeCells || interior < MinLakeInteriorCells)
                for (int k = 0; k < tail; k++)
                    cells[queue[k]] = Unassigned;
        }
    }

    /// <summary>Breadth-first fill: each unassigned land cell takes the class of the nearest assigned one.</summary>
    private static void FillFromNeighbours(byte[] cells, int w, int h)
    {
        var queue = new int[cells.Length];
        int head = 0, tail = 0;
        for (int i = 0; i < cells.Length; i++)
            if (cells[i] != Unassigned && cells[i] != (byte)CoverType.None)
                queue[tail++] = i;

        while (head < tail)
        {
            int i = queue[head++];
            int x = i % w, y = i / w;
            if (x > 0) Spread(i - 1);
            if (x < w - 1) Spread(i + 1);
            if (y > 0) Spread(i - w);
            if (y < h - 1) Spread(i + w);

            void Spread(int n)
            {
                if (cells[n] != Unassigned)
                    return;
                cells[n] = cells[i] == (byte)CoverType.Water ? (byte)CoverType.Meadow : cells[i];
                queue[tail++] = n;
            }
        }
    }
}
