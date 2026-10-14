using System;
using System.Collections.Concurrent;
using System.IO;

namespace Boundari.MapBuilder;

/// <summary>
/// Real-world elevation from "Terrarium" terrain tiles (AWS Open Data, Mapzen
/// Terrain Tiles). Tiles are 256x256 Web Mercator PNGs named "{x}_{y}.png", where
/// metres = R * 256 + G + B / 256 - 32768. Only the tiles a map needs have to be
/// downloaded; they're loaded on first use and missing ones read as sea level.
/// </summary>
public sealed class ElevationSource
{
    private const double MaxMercatorLat = 85.05112878;
    private const int TileSize = 256;

    private readonly string _folder;
    private readonly int _size;  // full mosaic size in pixels
    private readonly ConcurrentDictionary<(int, int), float[]?> _cache = new();

    private int _missingTiles;

    public int MissingTiles => _missingTiles;

    public ElevationSource(string folder, int zoom)
    {
        _folder = folder;
        _size = (1 << zoom) * TileSize;
    }

    /// <summary>Elevation in metres at a point (bilinear). Negative values are sea depth.</summary>
    public float Sample(double lon, double lat)
    {
        lat = Math.Clamp(lat, -MaxMercatorLat, MaxMercatorLat);
        double latRad = lat * Math.PI / 180.0;
        double fx = (lon + 180.0) / 360.0 * _size - 0.5;
        double fy = (1.0 - Math.Log(Math.Tan(Math.PI / 4 + latRad / 2)) / Math.PI) / 2.0 * _size - 0.5;

        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        double tx = fx - x0, ty = fy - y0;
        double top = Pixel(x0, y0) * (1 - tx) + Pixel(x0 + 1, y0) * tx;
        double bottom = Pixel(x0, y0 + 1) * (1 - tx) + Pixel(x0 + 1, y0 + 1) * tx;
        return (float)(top * (1 - ty) + bottom * ty);
    }

    private float Pixel(int x, int y)
    {
        x = ((x % _size) + _size) % _size; // wraps around the date line
        y = Math.Clamp(y, 0, _size - 1);
        float[]? tile = _cache.GetOrAdd((x / TileSize, y / TileSize), Load);
        return tile is null ? 0 : tile[(y % TileSize) * TileSize + x % TileSize];
    }

    private float[]? Load((int X, int Y) key)
    {
        string path = Path.Combine(_folder, $"{key.X}_{key.Y}.png");
        if (!File.Exists(path))
        {
            System.Threading.Interlocked.Increment(ref _missingTiles);
            return null;
        }
        var (w, h, ch, px) = Png.Decode(File.ReadAllBytes(path));
        if (w != TileSize || h != TileSize)
            throw new InvalidDataException($"{path} is {w}x{h}, expected {TileSize}x{TileSize}.");
        var metres = new float[TileSize * TileSize];
        for (int i = 0; i < metres.Length; i++)
            metres[i] = px[i * ch] * 256f + px[i * ch + 1] + px[i * ch + 2] / 256f - 32768f;
        return metres;
    }
}
