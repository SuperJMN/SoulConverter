using System.Buffers.Binary;

namespace SoulConverter;

/// <summary>Writes mono 16-bit WAV files, for listening to a render.</summary>
public static class WavFile
{
    /// <summary>
    /// Encodes <paramref name="samples"/> as a mono 16-bit PCM WAV, scaled so its peak sits just
    /// under full scale.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<double> samples, int sampleRate = 44100)
    {
        var peak = 0.0;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        var scale = peak > 0 ? 0.95 / peak : 1.0;
        var dataSize = samples.Length * 2;
        var wav = new byte[44 + dataSize];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), 36 + dataSize);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), dataSize);
        for (var index = 0; index < samples.Length; index++)
        {
            var value = (short)(Math.Clamp(samples[index] * scale, -1.0, 1.0) * 32767.0);
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + (index * 2)), value);
        }

        return wav;
    }
}
