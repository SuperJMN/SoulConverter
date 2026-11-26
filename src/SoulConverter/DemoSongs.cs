using System.Buffers.Binary;
using System.Text;

namespace SoulConverter;

/// <summary>
/// Synthetic NES 2A03 logs to try the converter on and to test it with. Nothing here is derived
/// from a published game, so the logs are safe to commit and to regenerate.
/// </summary>
public static class DemoSongs
{
    private const int CpuClockHz = 1789772;
    private const int FrameSamples = 735;
    private const int HeaderSize = 0x100;
    private const int Duty25 = 1, Duty50 = 2;
    private const int DmcBase = 0xC000;

    /// <summary>
    /// A short four-channel tune covering what the converter maps: duty changes, a hardware sweep,
    /// envelope decay, short and long noise, and bass notes below the Game Boy's range.
    /// </summary>
    public static byte[] FourChannel(bool loop = true)
    {
        double[] melody = [523.25, 587.33, 659.25, 698.46, 783.99, 659.25, 587.33, 523.25];
        double[] harmony = [261.63, 293.66, 329.63, 349.23, 392.00, 329.63, 293.66, 261.63];
        double[] bass = [65.41, 55.00, 32.70, 27.50]; // the last two sit below the Game Boy floor

        var frames = new List<List<(int, int)>> { new() { (0x15, 0x0F), (0x17, 0x40) } };
        for (var step = 0; step < 16; step++)
        {
            var writes = new List<(int, int)>();
            if (step % 2 == 0)
            {
                writes.AddRange(PulseNote(0, melody[step / 2], 12, step % 4));
                writes.AddRange(PulseNote(1, harmony[step / 2], 10, Duty25, decay: true));
            }

            if (step % 4 == 0)
            {
                writes.AddRange(TriangleNote(bass[step / 4]));
            }

            if (step % 2 == 0)
            {
                writes.AddRange(NoiseHit(step % 4 == 0 ? 8 : 12, 8, modeShort: step % 8 == 4));
            }

            frames.Add(writes);
            Rest(frames, 11);
        }

        // A hardware sweep on pulse 1, which another chip has to receive already resolved.
        var sweep = PulseNote(0, 440.0, 15, Duty50);
        sweep[1] = (0x01, 0x8A); // enabled, period 0, negate off, shift 2
        frames.Add(sweep);
        Rest(frames, 23);

        frames.Add([.. PulseOff(0), .. PulseOff(1), .. TriangleOff()]);
        Rest(frames, 11);
        return Build(frames, loop ? 1 : null, [], "SoulConverter demo");
    }

    /// <summary>
    /// A two-bar beat over a bass line, with the drums (kick, snare, hat) as DPCM samples. The
    /// tonal channels stay busy, so re-synthesising the drums on a Game Boy is a real decision.
    /// </summary>
    public static byte[] DrumKit(bool loop = true)
    {
        var (rom, layout) = Kit();
        string[] pattern = ["kick", "hat", "snare", "hat", "kick", "hat", "snare", "hat"];
        double[] bass = [82.41, 82.41, 110.0, 98.0];

        // The four tonal channels are enabled but the DMC is left off: with $4012/$4013 still
        // zero, setting bit 4 here would fire a one-byte sample no driver intends.
        var frames = new List<List<(int, int)>> { new() { (0x15, 0x0F), (0x17, 0x40) } };
        for (var step = 0; step < pattern.Length * 2; step++)
        {
            var (address, length) = layout[pattern[step % pattern.Length]];
            var writes = DpcmTrigger(address, length);
            if (step % 2 == 0)
            {
                writes.AddRange(TriangleNote(bass[step / 2 % bass.Length]));
                writes.AddRange(PulseNote(0, step % 4 == 0 ? 329.63 : 392.0, 10, Duty50));
                writes.AddRange(PulseNote(1, 246.94, 8, Duty25, decay: true));
            }

            frames.Add(writes);
            Rest(frames, 11);
        }

        frames.Add([.. PulseOff(0), .. PulseOff(1), .. TriangleOff(), (0x15, 0x0F)]);
        Rest(frames, 11);
        return Build(frames, loop ? 1 : null, rom, "SoulConverter DPCM demo");
    }

    /// <summary>
    /// A melody over a bass line the DMC plays by retuning one sample: the other way NES drivers
    /// use the sample channel. The DPCM is not decoration, it is the bass part.
    /// </summary>
    public static byte[] SampledBass(bool loop = true)
    {
        var length = DemoSamples.Bass.Length;

        // One sample, five rates: the scale that gives the instrument away.
        int[] line = [15, 14, 13, 15, 12, 14, 15, 13];
        double[] melody = [659.26, 587.33, 523.25, 587.33, 659.26, 783.99, 659.26, 523.25];

        var frames = new List<List<(int, int)>> { new() { (0x15, 0x0F), (0x17, 0x40) } };
        for (var step = 0; step < line.Length * 2; step++)
        {
            var writes = DpcmTrigger(DmcBase, length, line[step % line.Length]);
            writes.AddRange(PulseNote(0, melody[step % melody.Length], 11, Duty50));
            if (step % 4 == 0)
            {
                writes.AddRange(PulseNote(1, 329.63, 7, Duty25, decay: true));
            }

            if (step % 4 == 2)
            {
                writes.AddRange(TriangleNote(196.0));
            }
            else if (step % 4 == 3)
            {
                writes.AddRange(TriangleOff());
            }

            frames.Add(writes);
            Rest(frames, 15);
        }

        frames.Add([.. PulseOff(0), .. PulseOff(1), .. TriangleOff(), (0x15, 0x0F)]);
        Rest(frames, 15);
        return Build(frames, loop ? 1 : null, [(DmcBase, DemoSamples.Bass)], "SoulConverter sampled bass demo");
    }

    // A three-piece sampled drum kit and where each piece lives. Addresses are 64-byte aligned
    // because $4012 can only express that.
    private static (List<(int Address, byte[] Payload)> Rom, Dictionary<string, (int Address, int Length)> Layout) Kit()
    {
        var rom = new List<(int, byte[])>();
        var layout = new Dictionary<string, (int, int)>();
        var address = DmcBase;
        foreach (var (name, payload) in new[] { ("kick", DemoSamples.Kick), ("snare", DemoSamples.Snare), ("hat", DemoSamples.Hat) })
        {
            rom.Add((address, payload));
            layout[name] = (address, payload.Length);
            address += (payload.Length + 63) / 64 * 64;
        }

        return (rom, layout);
    }

    private static void Rest(List<List<(int, int)>> frames, int count)
    {
        for (var index = 0; index < count; index++)
        {
            frames.Add([]);
        }
    }

    private static int PulsePeriod(double frequencyHz) => Math.Clamp((int)Math.Round(CpuClockHz / (16.0 * frequencyHz)) - 1, 8, 0x7FF);

    private static int TrianglePeriod(double frequencyHz) => Math.Clamp((int)Math.Round(CpuClockHz / (32.0 * frequencyHz)) - 1, 2, 0x7FF);

    // The writes that start a sustained note on pulse 1 (0) or 2 (1).
    private static List<(int, int)> PulseNote(int channel, double frequencyHz, int volume, int duty, bool decay = false)
    {
        var register = channel == 0 ? 0x00 : 0x04;
        var period = PulsePeriod(frequencyHz);
        return
        [
            (register, (duty << 6) | 0x20 | (decay ? 0x00 : 0x10) | (volume & 0x0F)),
            (register + 1, 0x00), // sweep off
            (register + 2, period & 0xFF),
            (register + 3, (0x1F << 3) | (period >> 8)),
        ];
    }

    private static List<(int, int)> PulseOff(int channel) => [(channel == 0 ? 0x00 : 0x04, 0x30)];

    private static List<(int, int)> TriangleNote(double frequencyHz)
    {
        var period = TrianglePeriod(frequencyHz);
        return [(0x08, 0xFF), (0x0A, period & 0xFF), (0x0B, (0x1F << 3) | (period >> 8))];
    }

    private static List<(int, int)> TriangleOff() => [(0x08, 0x00)];

    private static List<(int, int)> NoiseHit(int periodIndex, int volume, bool modeShort) =>
        [(0x0C, volume & 0x0F), (0x0E, (modeShort ? 0x80 : 0x00) | (periodIndex & 0x0F)), (0x0F, 0x1F << 3)];

    // The writes that start a DPCM sample, as a driver emits them.
    private static List<(int, int)> DpcmTrigger(int address, int lengthBytes, int rateIndex = 15) =>
        [(0x10, rateIndex & 0x0F), (0x12, ((address - DmcBase) / 64) & 0xFF), (0x13, Math.Max(0, (lengthBytes - 1) / 16) & 0xFF), (0x15, 0x1F)];

    // Encodes per-frame writes as a NES VGM 1.61 file, with the DPCM ROM as 0xC2 data blocks ahead
    // of the first frame, which is where a real log puts them.
    private static byte[] Build(List<List<(int Register, int Value)>> frames, int? loopFrame, List<(int Address, byte[] Payload)> rom, string title)
    {
        var data = new List<byte>();
        foreach (var (address, payload) in rom)
        {
            data.AddRange([0x67, 0x66, 0xC2]);
            data.AddRange(BitConverter.GetBytes((uint)(payload.Length + 2)));
            data.AddRange(BitConverter.GetBytes((ushort)address));
            data.AddRange(payload);
        }

        int? loopOffset = null;
        for (var index = 0; index < frames.Count; index++)
        {
            if (loopFrame == index)
            {
                loopOffset = data.Count;
            }

            foreach (var (register, value) in frames[index])
            {
                data.AddRange([0xB4, (byte)register, (byte)value]);
            }

            data.AddRange([0x61, FrameSamples & 0xFF, FrameSamples >> 8]);
        }

        data.Add(0x66);

        var gd3Body = new List<byte>();
        foreach (var field in new[] { title, string.Empty, "SoulConverter", string.Empty, "NES/Famicom", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty })
        {
            gd3Body.AddRange(Encoding.Unicode.GetBytes(field));
            gd3Body.AddRange([0, 0]);
        }

        var totalSamples = frames.Count * FrameSamples;
        var gd3Position = HeaderSize + data.Count;
        var header = new byte[HeaderSize];
        "Vgm "u8.CopyTo(header);
        Put(header, 0x04, gd3Position + 12 + gd3Body.Count - 0x04);
        Put(header, 0x08, 0x00000161);
        Put(header, 0x14, gd3Position - 0x14);
        Put(header, 0x18, totalSamples);
        if (loopOffset is { } offset && loopFrame is { } frame)
        {
            Put(header, 0x1C, HeaderSize + offset - 0x1C);
            Put(header, 0x20, totalSamples - (frame * FrameSamples));
        }

        Put(header, 0x24, 60);
        Put(header, 0x34, HeaderSize - 0x34);
        Put(header, 0x84, CpuClockHz);

        var gd3Header = new byte[12];
        "Gd3 "u8.CopyTo(gd3Header);
        Put(gd3Header, 4, 0x100);
        Put(gd3Header, 8, gd3Body.Count);
        return [.. header, .. data, .. gd3Header, .. gd3Body];
    }

    private static void Put(byte[] target, int offset, int value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset), unchecked((uint)value));
}
