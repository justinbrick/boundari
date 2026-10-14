using System;
using System.IO;
using System.IO.Compression;

namespace Boundari.MapBuilder;

/// <summary>Minimal PNG decoder: 8-bit RGB / RGBA, non-interlaced. Enough for elevation tiles.</summary>
public static class Png
{
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
