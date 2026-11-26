using System.Buffers.Binary;
using System.IO.Compression;
using SoulConverter.Analysis;

namespace SoulConverter;

/// <summary>Draws the spectrograms of two renders one over the other, to compare them by eye.</summary>
public static class SpectrogramImage
{
    private const int Height = 160;

    /// <summary>Encodes a PNG with the spectrogram of <paramref name="top"/> over that of <paramref name="bottom"/>.</summary>
    public static byte[] EncodePng(ReadOnlySpan<double> top, ReadOnlySpan<double> bottom, int width = 900)
    {
        var rows = new List<byte[]>();
        rows.AddRange(Image(top, width));
        for (var row = 0; row < 4; row++)
        {
            var separator = new byte[width * 3];
            Array.Fill(separator, (byte)60);
            rows.Add(separator);
        }

        rows.AddRange(Image(bottom, width));
        return Png(rows, width);
    }

    private static IEnumerable<byte[]> Image(ReadOnlySpan<double> samples, int width)
    {
        var spectrum = Metrics.Spectrogram(samples);
        var image = Enumerable.Range(0, Height).Select(_ => new byte[width * 3]).ToArray();
        var frames = spectrum[0].Length;
        if (frames < 2)
        {
            return image;
        }

        var rows = Dsp.Linspace(0, spectrum.Length - 1, Height);
        var columns = Dsp.Linspace(0, frames - 1, width);
        for (var y = 0; y < Height; y++)
        {
            // Low frequencies at the bottom.
            var source = spectrum[(int)rows[Height - 1 - y]];
            for (var x = 0; x < width; x++)
            {
                var value = Math.Clamp(((20.0 * Math.Log10(source[(int)columns[x]] + 1e-9)) + 80.0) / 80.0, 0.0, 1.0);
                image[y][x * 3] = (byte)(Math.Pow(value, 1.4) * 255);
                image[y][(x * 3) + 1] = (byte)(value * 255);
                image[y][(x * 3) + 2] = (byte)(Math.Sqrt(value) * 255);
            }
        }

        return image;
    }

    private static byte[] Png(List<byte[]> rows, int width)
    {
        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            foreach (var row in rows)
            {
                zlib.WriteByte(0);
                zlib.Write(row);
            }
        }

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), rows.Count);
        header[8] = 8; // bit depth
        header[9] = 2; // RGB

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR"u8, header);
        Chunk(png, "IDAT"u8, raw.ToArray());
        Chunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> payload)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, payload.Length);
        png.Write(number);
        png.Write(tag);
        png.Write(payload);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(payload, Crc32(tag, 0xFFFFFFFF)) ^ 0xFFFFFFFF);
        png.Write(number);
    }

    private static uint Crc32(ReadOnlySpan<byte> data, uint crc)
    {
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return crc;
    }
}
