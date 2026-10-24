using System;
using System.IO;
using System.IO.Compression;

namespace Boundari.MapBuilder;

/// <summary>Minimal PNG decoder (8-bit RGB / RGBA, non-interlaced: enough for elevation tiles) and greyscale encoder.</summary>
public static class Png
{
    /// <summary>
    /// An 8-bit greyscale PNG of these pixels (one byte each, row by row). Each row gets
    /// whichever filter shrinks it most (the usual heuristic), then it's deflated.
    /// </summary>
    public static byte[] EncodeGrey(int width, int height, byte[] pixels)
    {
        const int Bpp = 1;
        int stride = width * Bpp;
        var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var row = new byte[stride + 1];
            var best = new byte[stride + 1];
            for (int y = 0; y < height; y++)
            {
                long bestCost = long.MaxValue;
                for (byte filter = 0; filter <= 4; filter++)
                {
                    row[0] = filter;
                    long cost = 0;
                    for (int i = 0; i < stride; i++)
                    {
                        int x = pixels[y * stride + i];
                        int a = i >= Bpp ? pixels[y * stride + i - Bpp] : 0;
                        int b = y > 0 ? pixels[(y - 1) * stride + i] : 0;
                        int c = i >= Bpp && y > 0 ? pixels[(y - 1) * stride + i - Bpp] : 0;
                        int predicted = filter switch
                        {
                            1 => a,
                            2 => b,
                            3 => (a + b) / 2,
                            4 => Paeth(a, b, c),
                            _ => 0,
                        };
                        byte v = (byte)(x - predicted);
                        row[i + 1] = v;
                        cost += v < 128 ? v : 256 - v;
                    }
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        Array.Copy(row, best, row.Length);
                    }
                }
                z.Write(best, 0, best.Length);
            }
        }

        var file = new MemoryStream();
        file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        WriteInt32BE(header, 0, width);
        WriteInt32BE(header, 4, height);
        header[8] = 8;  // bit depth
        header[9] = 0;  // greyscale
        WriteChunk(file, "IHDR", header);
        WriteChunk(file, "IDAT", raw.ToArray());
        WriteChunk(file, "IEND", Array.Empty<byte>());
        return file.ToArray();
    }

    private static void WriteChunk(Stream file, string type, byte[] data)
    {
        var length = new byte[4];
        WriteInt32BE(length, 0, data.Length);
        file.Write(length);
        var body = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(body, 0);
        data.CopyTo(body, 4);
        file.Write(body);
        var crc = new byte[4];
        WriteInt32BE(crc, 0, (int)Crc32(body));
        file.Write(crc);
    }

    private static uint[]? _crcTable;

    private static uint Crc32(byte[] data)
    {
        if (_crcTable is null)
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            _crcTable = table;
        }
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
            crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static void WriteInt32BE(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    public static (int Width, int Height, int Channels, byte[] Pixels) Decode(byte[] file)
    {
        ReadOnlySpan<byte> signature = stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        if (!file.AsSpan(0, 8).SequenceEqual(signature))
            throw new InvalidDataException("Not a PNG file.");

        int width = 0, height = 0, channels = 0;
        using var idat = new MemoryStream();
        int pos = 8;
        while (pos < file.Length)
        {
            int length = ReadInt32BE(file, pos);
            string type = System.Text.Encoding.ASCII.GetString(file, pos + 4, 4);
            int data = pos + 8;
            if (type == "IHDR")
            {
                width = ReadInt32BE(file, data);
                height = ReadInt32BE(file, data + 4);
                byte bitDepth = file[data + 8], colorType = file[data + 9], interlace = file[data + 12];
                channels = colorType switch { 2 => 3, 6 => 4, _ => 0 };
                if (bitDepth != 8 || channels == 0 || interlace != 0)
                    throw new NotSupportedException($"PNG format not supported (depth {bitDepth}, colour type {colorType}, interlace {interlace}).");
            }
            else if (type == "IDAT")
                idat.Write(file, data, length);
            else if (type == "IEND")
                break;
            pos = data + length + 4; // skip CRC
        }

        int stride = width * channels;
        var raw = new byte[(stride + 1) * height];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
            z.ReadExactly(raw);

        var pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            byte filter = raw[y * (stride + 1)];
            var src = raw.AsSpan(y * (stride + 1) + 1, stride);
            var dst = pixels.AsSpan(y * stride, stride);
            var prev = y > 0 ? pixels.AsSpan((y - 1) * stride, stride) : Span<byte>.Empty;
            for (int x = 0; x < stride; x++)
            {
                int a = x >= channels ? dst[x - channels] : 0;
                int b = y > 0 ? prev[x] : 0;
                int c = x >= channels && y > 0 ? prev[x - channels] : 0;
                int predictor = filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException($"Bad PNG filter {filter}."),
                };
                dst[x] = (byte)(src[x] + predictor);
            }
        }
        return (width, height, channels, pixels);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static int ReadInt32BE(byte[] b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
}
