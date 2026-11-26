namespace SoulConverter.Tests.Support;

/// <summary>Reads SN76489 logs for the tests, and holds the two formulas a conversion to it follows.</summary>
public static class Psg
{
    /// <summary>
    /// The PSG registers at the end of each 60 Hz frame, from silence: period and attenuation of
    /// tones 0, 1 and 2 (indices 0-5), then the noise control and the noise attenuation (6, 7).
    /// </summary>
    public static List<int[]> States(byte[] vgm)
    {
        int[] registers = [0, 15, 0, 15, 0, 15, 0, 15];
        var latched = 0;
        var states = new List<int[]>();
        foreach (var frame in TestVgm.Frames(vgm))
        {
            foreach (var value in frame.Select(write => write.Value))
            {
                latched = (value & 0x80) != 0 ? (value >> 4) & 7 : latched;
                var isTone = latched is 0 or 2 or 4;
                registers[latched] = ((value & 0x80) != 0, isTone) switch
                {
                    (true, true) => (registers[latched] & 0x3F0) | (value & 0x0F),
                    (false, true) => (registers[latched] & 0x00F) | ((value & 0x3F) << 4),
                    _ => value & 0x0F,
                };
            }

            states.Add([.. registers]);
        }

        return states;
    }

    /// <summary>The PSG channels (0-2 tones, 3 noise) a log writes to.</summary>
    public static SortedSet<int> ChannelsWritten(byte[] vgm) =>
        [.. TestVgm.Writes(vgm).Where(write => (write.Value & 0x80) != 0).Select(write => (write.Value >> 5) & 3)];

    /// <summary>The PSG period of a frequency: clock / (32 * period).</summary>
    public static int Period(double frequency) => (int)Math.Round(TestVgm.PsgClock / (32 * frequency));

    /// <summary>The attenuation of a linear volume 1-15, in the PSG's 2 dB steps.</summary>
    public static int Attenuation(int volume) => (int)Math.Round(20 * Math.Log10(15.0 / volume) / 2);
}
