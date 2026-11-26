namespace SoulConverter.Analysis;

/// <summary>Phase-continuous oscillators shared by the reference synthesisers.</summary>
internal static class Oscillators
{
    public const int SampleRate = 44100;

    // The 32-step 4-bit triangle of the NES triangle channel.
    public static readonly double[] TriangleSequence = [.. Enumerable.Range(0, 16).Select(step => 15.0 - step), .. Enumerable.Range(0, 16).Select(step => (double)step)];

    private static readonly double[] DutyRatios = [0.125, 0.25, 0.5, 0.75];

    /// <summary>Renders a duty-cycled square in the 0..1 range into <paramref name="output"/>; returns the new phase.</summary>
    public static double SquareBlock(Span<double> output, double phase, double frequency, int duty)
    {
        var increment = frequency / SampleRate;
        var ratio = DutyRatios[duty & 0x03];
        for (var index = 0; index < output.Length; index++)
        {
            var position = phase + (increment * index);
            output[index] = position - Math.Floor(position) < ratio ? 1.0 : 0.0;
        }

        var next = phase + (increment * output.Length);
        return next - Math.Floor(next);
    }

    /// <summary>Renders a wavetable, values as given, into <paramref name="output"/>; returns the new phase.</summary>
    public static double WaveBlock(Span<double> output, double phase, double frequency, ReadOnlySpan<double> table)
    {
        var increment = frequency / SampleRate;
        for (var index = 0; index < output.Length; index++)
        {
            var position = phase + (increment * index);
            output[index] = table[(int)Math.Floor((position - Math.Floor(position)) * table.Length)];
        }

        var next = phase + (increment * output.Length);
        return next - Math.Floor(next);
    }

    /// <summary>
    /// One full period of an LFSR's output. The register is <paramref name="bits"/> wide, feedback
    /// is bit 0 XOR bit <paramref name="tap"/>, and the audible output is the inverted bit 0, which
    /// is how both the NES and the Game Boy noise channels work.
    /// </summary>
    public static double[] LfsrSequence(int bits, int tap)
    {
        var start = (1 << bits) - 1;
        var register = start;
        var values = new List<double>();
        do
        {
            values.Add((register & 1) == 0 ? 1.0 : 0.0);
            var feedback = (register & 1) ^ ((register >> tap) & 1);
            register = (register >> 1) | (feedback << (bits - 1));
        }
        while (register != start);

        return [.. values];
    }

    /// <summary>Renders an LFSR clocked at <paramref name="rateHz"/> into <paramref name="output"/>; returns the new position.</summary>
    public static double NoiseBlock(Span<double> output, double position, double rateHz, double[] sequence)
    {
        var step = rateHz / SampleRate;
        for (var index = 0; index < output.Length; index++)
        {
            output[index] = sequence[(int)((long)Math.Floor(position + (step * index)) % sequence.Length)];
        }

        return (position + (step * output.Length)) % sequence.Length;
    }
}
