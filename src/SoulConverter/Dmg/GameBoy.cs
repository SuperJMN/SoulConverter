namespace SoulConverter.Dmg;

/// <summary>
/// Game Boy DMG APU facts: register indices, frequency maths and the noise grid. Register indices
/// are VGM <c>0xB3</c> register numbers, i.e. <c>address - 0xFF10</c>.
/// </summary>
internal static class GameBoy
{
    public const int NR10 = 0x00, NR11 = 0x01, NR12 = 0x02, NR13 = 0x03, NR14 = 0x04;
    public const int NR21 = 0x06, NR22 = 0x07, NR23 = 0x08, NR24 = 0x09;
    public const int NR30 = 0x0A, NR31 = 0x0B, NR32 = 0x0C, NR33 = 0x0D, NR34 = 0x0E;
    public const int NR41 = 0x10, NR42 = 0x11, NR43 = 0x12, NR44 = 0x13;
    public const int NR50 = 0x14, NR51 = 0x15, NR52 = 0x16;
    public const int WaveRamBase = 0x20;
    public const int WaveRamSize = 16;

    public const double SquareFreqNumerator = 131072.0;
    public const double WaveFreqNumerator = 65536.0;
    public const double SquareMinHz = SquareFreqNumerator / 2048.0; // 64 Hz
    public const double WaveMinHz = WaveFreqNumerator / 2048.0;     // 32 Hz

    private const int NoiseMaxShift = 13;

    /// <summary>A 32-step 4-bit triangle, matching the NES triangle channel's own sequence.</summary>
    public static readonly int[] TriangleWave = [.. Enumerable.Range(0, 16).Select(step => 15 - step), .. Enumerable.Range(0, 16)];

    /// <summary>A 32-step 50% square, for a source whose third tone is a square.</summary>
    public static readonly int[] SquareWave = [.. Enumerable.Repeat(15, 16), .. Enumerable.Repeat(0, 16)];

    private static readonly (double Frequency, int Shift, int Divisor)[] NoiseTable = BuildNoiseTable();

    /// <summary>Packs 32 nibbles into the 16 bytes of wave RAM.</summary>
    public static byte[] PackWave(IReadOnlyList<int> nibbles)
    {
        if (nibbles.Count != 32)
        {
            throw new ArgumentException("Wave RAM needs exactly 32 nibbles.", nameof(nibbles));
        }

        if (nibbles.Any(value => value is < 0 or > 15))
        {
            throw new ArgumentException("Wave RAM nibbles must be in 0..15.", nameof(nibbles));
        }

        var packed = new byte[WaveRamSize];
        for (var index = 0; index < WaveRamSize; index++)
        {
            packed[index] = (byte)((nibbles[index * 2] << 4) | nibbles[(index * 2) + 1]);
        }

        return packed;
    }

    public static (int Period, int OctavesUp) SquarePeriod(double frequencyHz, bool allowTranspose = true) =>
        Period(frequencyHz, SquareFreqNumerator, SquareMinHz, allowTranspose);

    public static (int Period, int OctavesUp) WavePeriod(double frequencyHz, bool allowTranspose = true) =>
        Period(frequencyHz, WaveFreqNumerator, WaveMinHz, allowTranspose);

    public static double NoiseFrequency(int shift, int divisorCode)
    {
        var divisor = divisorCode == 0 ? 0.5 : divisorCode;
        return 262144.0 / (divisor * Math.Pow(2, shift));
    }

    /// <summary>
    /// The <c>(shift, divisor)</c> whose rate is closest to <paramref name="targetHz"/>, measured in
    /// octaves so the error is even across the range, and that error in cents.
    /// </summary>
    public static (int Shift, int Divisor, double ErrorCents) NearestNoise(double targetHz)
    {
        if (targetHz <= 0)
        {
            return (0, 0, 0.0);
        }

        var bestError = double.PositiveInfinity;
        var best = NoiseTable[0];
        foreach (var entry in NoiseTable)
        {
            var error = Math.Abs(Math.Log2(entry.Frequency / targetHz));
            if (error < bestError)
            {
                bestError = error;
                best = entry;
            }
        }

        return (best.Shift, best.Divisor, 1200.0 * Math.Log2(best.Frequency / targetHz));
    }

    // The Game Boy bottoms out at 64 Hz (square) and 32 Hz (wave). Notes under that floor rise by
    // whole octaves so they stay in tune, and the shift is returned so it can be reported.
    private static (int Period, int OctavesUp) Period(double frequencyHz, double numerator, double minimumHz, bool allowTranspose)
    {
        if (frequencyHz <= 0)
        {
            return (0, 0);
        }

        var octaves = 0;
        var frequency = frequencyHz;
        if (allowTranspose)
        {
            while (frequency < minimumHz)
            {
                frequency *= 2.0;
                octaves++;
            }
        }

        var period = Math.Round(2048.0 - (numerator / frequency));
        return ((int)Math.Clamp(period, 0, 2047), octaves);
    }

    private static (double Frequency, int Shift, int Divisor)[] BuildNoiseTable()
    {
        var entries = new List<(double Frequency, int Shift, int Divisor)>();
        for (var shift = 0; shift <= NoiseMaxShift; shift++)
        {
            for (var divisor = 0; divisor < 8; divisor++)
            {
                entries.Add((NoiseFrequency(shift, divisor), shift, divisor));
            }
        }

        // Ordered as tuples, so equal rates keep the lower shift first.
        return [.. entries.OrderBy(entry => entry.Frequency).ThenBy(entry => entry.Shift).ThenBy(entry => entry.Divisor)];
    }
}
