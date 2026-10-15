using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Boundari.Map;

/// <summary>
/// Reads and writes .bmap files: pre-built maps (e.g. real-world maps made by
/// tools/MapBuilder). Gzipped binary: header, then terrain, coast field and
/// elevation per tile (the two fields as 16-bit floats).
/// </summary>
public static class MapFile
{
    private const uint Magic = 0x50414D42; // "BMAP"
    private const int Version = 3; // 2: real-world location, 3: land cover

    public static void Write(GameMap map, Stream output)
    {
        using var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true);
        using var w = new BinaryWriter(gzip);
        w.Write(Magic);
        w.Write(Version);
        w.Write(map.Name);
        w.Write(map.Width);
        w.Write(map.Height);
        w.Write(map.Seed);
        w.Write(map.Geo is not null);
        if (map.Geo is { } geo)
        {
            w.Write(geo.LonMin);
            w.Write(geo.LonMax);
            w.Write(geo.LatMin);
            w.Write(geo.LatMax);
            w.Write(geo.StandardParallel);
        }
        w.Write(map.Cover is not null);
        if (map.Cover is { } cover)
        {
            w.Write(cover.Scale);
            w.Write(MemoryMarshal.AsBytes(cover.Cells.AsSpan()));
        }
        foreach (var t in map.Terrain)
            w.Write((byte)t);
        foreach (float v in map.CoastField)
            w.Write((Half)v);
        foreach (float v in map.Elevation)
            w.Write((Half)v);
    }

    public static GameMap Read(Stream input)
    {
        using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
        using var r = new BinaryReader(gzip);
        if (r.ReadUInt32() != Magic)
            throw new InvalidDataException("Not a Boundari map file.");
        int version = r.ReadInt32();
        if (version is < 1 or > Version)
            throw new InvalidDataException($"Unsupported map file version {version}.");

        string name = r.ReadString();
        int width = r.ReadInt32();
        int height = r.ReadInt32();
        ulong seed = r.ReadUInt64();
        var map = new GameMap(width, height, seed) { Name = name };
        if (version >= 2 && r.ReadBoolean())
            map.Geo = new MapGeo(r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
        if (version >= 3 && r.ReadBoolean())
        {
            var cover = new LandCover(r.ReadInt32(), width, height);
            byte[] cells = r.ReadBytes(cover.Cells.Length);
            if (cells.Length != cover.Cells.Length)
                throw new EndOfStreamException("Map file is truncated.");
            cells.CopyTo(MemoryMarshal.AsBytes(cover.Cells.AsSpan()));
            map.Cover = cover;
        }

        byte[] terrain = r.ReadBytes(map.TileCount);
        if (terrain.Length != map.TileCount)
            throw new EndOfStreamException("Map file is truncated.");
        for (int i = 0; i < map.TileCount; i++)
            map.Terrain[i] = (TerrainType)terrain[i];
        ReadHalves(r, map.CoastField);
        ReadHalves(r, map.Elevation);

        MapProcessing.LabelLandmasses(map);
        return map;
    }

    /// <summary>Reads 16-bit floats in one go (one at a time through the decompressor is very slow).</summary>
    private static void ReadHalves(BinaryReader r, float[] into)
    {
        byte[] bytes = r.ReadBytes(into.Length * 2);
        if (bytes.Length != into.Length * 2)
            throw new EndOfStreamException("Map file is truncated.");
        for (int i = 0; i < into.Length; i++)
            into[i] = (float)System.Buffers.Binary.BinaryPrimitives.ReadHalfLittleEndian(bytes.AsSpan(i * 2, 2));
    }
}
