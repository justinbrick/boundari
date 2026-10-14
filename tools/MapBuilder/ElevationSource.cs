using System;
using System.IO;

namespace Boundari.MapBuilder;

/// <summary>
/// Real-world elevation from "Terrarium" terrain tiles (AWS Open Data, Mapzen
/// Terrain Tiles). Tiles are Web Mercator PNGs where
/// metres = R * 256 + G + B / 256 - 32768.
/// </summary>
public sealed class ElevationSource
{
    private const double MaxMercatorLat = 85.05112878;

    private readonly int _size; // full mosaic size in pixels
    private readonly float[] _metres;

    /// <param name="folder">Folder holding "{x}_{y}.png" tiles for one zoom level.</param>
    public ElevationSource(string folder, int zoom)
    {
        int tiles = 1 << zoom;
        _size = tiles * 256;
        _metres = new float[_size * _size];
        for (int ty = 0; ty < tiles; ty++)
            for (int tx = 0; tx < tiles; tx++)
            {
                var (w, h, ch, px) = Png.Decode(File.ReadAllBytes(Path.Combine(folder, $"{tx}_{ty}.png")));
                if (w != 256 || h != 256)
                    throw new InvalidDataException($"Tile {tx}_{ty} is {w}x{h}, expected 256x256.");
                for (int y = 0; y < 256; y++)
                    for (int x = 0; x < 256; x++)
                    {
                        int p = (y * 256 + x) * ch;
                        float metres = px[p] * 256f + px[p + 1] + px[p + 2] / 256f - 32768f;
                        _metres[(ty * 256 + y) * _size + tx * 256 + x] = metres;
                    }
            }
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
        float At(int x, int y)
        {
            x = ((x % _size) + _size) % _size; // wraps around the date line
            y = Math.Clamp(y, 0, _size - 1);
            return _metres[y * _size + x];
        }
        double top = At(x0, y0) * (1 - tx) + At(x0 + 1, y0) * tx;
        double bottom = At(x0, y0 + 1) * (1 - tx) + At(x0 + 1, y0 + 1) * tx;
        return (float)(top * (1 - ty) + bottom * ty);
    }
}
