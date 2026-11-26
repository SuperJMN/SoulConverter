using System.Buffers.Binary;

namespace SoulConverter.Tests.Support;

/// <summary>One register write of a log, at a VGM sample position.</summary>
public readonly record struct Write(int Sample, int Register, int Value);

/// <summary>
/// Builds and reads VGM logs for the tests, on its own: nothing here goes through the library, so
/// a test compares what the library wrote with an independent reading of it.
/// </summary>
public static class TestVgm
{
    public const int FrameSamples = 735;
    public const double NesClock = 1789773;
    public const double PsgClock = 3579545;

    /// <summary>A 2A03 log: one 60 Hz frame per entry, registers as $40xx addresses.</summary>
    public static byte[] Nes(params (int Address, int Value)[][] frames) =>
        Build(0x84, 1789773, frames.Select(frame => frame.SelectMany(write => new byte[] { 0xB4, (byte)(write.Address - 0x4000), (byte)write.Value })), null);

    /// <summary>A DMG log: one 60 Hz frame per entry, registers as $FFxx addresses.</summary>
    public static byte[] Dmg(params (int Address, int Value)[][] frames) => DmgLooping(null, frames);

    public static byte[] DmgLooping(int? loopFrame, params (int Address, int Value)[][] frames) =>
        Build(0x80, 4194304, frames.Select(frame => frame.SelectMany(write => new byte[] { 0xB3, (byte)(write.Address - 0xFF10), (byte)write.Value })), loopFrame);

    /// <summary>An SN76489 log: one 60 Hz frame of PSG bytes per entry.</summary>
    public static byte[] Sn(params int[][] frames) => SnLooping(null, frames);

    public static byte[] SnLooping(int? loopFrame, params int[][] frames) =>
        Build(0x0C, 3579545, frames.Select(frame => frame.SelectMany(value => new byte[] { 0x50, (byte)value })), loopFrame);

    public static byte[] NesLooping(int? loopFrame, params (int Address, int Value)[][] frames) =>
        Build(0x84, 1789773, frames.Select(frame => frame.SelectMany(write => new byte[] { 0xB4, (byte)(write.Address - 0x4000), (byte)write.Value })), loopFrame);

    /// <summary>A log whose header declares a clock at <paramref name="clockOffset"/> and whose body is <paramref name="commands"/>.</summary>
    public static byte[] Raw(int clockOffset, params byte[] commands)
    {
        var header = Header(1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(clockOffset), 1_000_000);
        return [.. header, .. commands];
    }

    /// <summary>Every chip write of a log, in order, whatever the chip.</summary>
    public static List<Write> Writes(byte[] vgm)
    {
        var writes = new List<Write>();
        Walk(vgm, (sample, command, register, value) =>
        {
            if (command is 0xB3 or 0xB4 or 0x50)
            {
                writes.Add(new Write(sample, register, value));
            }
        });
        return writes;
    }

    /// <summary>The command bytes a log uses.</summary>
    public static SortedSet<int> Commands(byte[] vgm)
    {
        var commands = new SortedSet<int>();
        Walk(vgm, (_, command, _, _) => commands.Add(command));
        return commands;
    }

    public static int TotalSamples(byte[] vgm) => (int)BinaryPrimitives.ReadUInt32LittleEndian(vgm.AsSpan(0x18));

    /// <summary>The sample the log loops back to, or null without a loop.</summary>
    public static int? LoopSample(byte[] vgm) =>
        BinaryPrimitives.ReadUInt32LittleEndian(vgm.AsSpan(0x1C)) == 0
            ? null
            : TotalSamples(vgm) - (int)BinaryPrimitives.ReadUInt32LittleEndian(vgm.AsSpan(0x20));

    public static uint Clock(byte[] vgm, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(vgm.AsSpan(offset));

    /// <summary>The writes of a log grouped by 60 Hz frame, up to its last frame.</summary>
    public static List<List<Write>> Frames(byte[] vgm)
    {
        var count = Math.Max(1, (int)Math.Ceiling(TotalSamples(vgm) / (double)FrameSamples));
        var frames = Enumerable.Range(0, count).Select(_ => new List<Write>()).ToList();
        foreach (var write in Writes(vgm))
        {
            frames[Math.Min(count - 1, write.Sample / FrameSamples)].Add(write);
        }

        return frames;
    }

    private static void Walk(byte[] vgm, Action<int, int, int, int> visit)
    {
        var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(vgm.AsSpan(0x34)) + 0x34;
        var sample = 0;
        while (offset < vgm.Length)
        {
            var command = vgm[offset];
            switch (command)
            {
                case 0x66:
                    visit(sample, command, 0, 0);
                    return;
                case 0xB3 or 0xB4:
                    visit(sample, command, vgm[offset + 1], vgm[offset + 2]);
                    offset += 3;
                    break;
                case 0x50:
                    visit(sample, command, 0, vgm[offset + 1]);
                    offset += 2;
                    break;
                case 0x61:
                    visit(sample, command, 0, 0);
                    sample += BinaryPrimitives.ReadUInt16LittleEndian(vgm.AsSpan(offset + 1));
                    offset += 3;
                    break;
                case 0x62:
                    visit(sample, command, 0, 0);
                    sample += FrameSamples;
                    offset++;
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected VGM command 0x{command:X2} at 0x{offset:X}.");
            }
        }
    }

    private static byte[] Build(int clockOffset, uint clock, IEnumerable<IEnumerable<byte>> frames, int? loopFrame)
    {
        var commands = new List<byte>();
        var count = 0;
        int? loopOffset = null;
        foreach (var frame in frames)
        {
            if (loopFrame == count)
            {
                loopOffset = commands.Count;
            }

            commands.AddRange(frame);
            commands.Add(0x62);
            count++;
        }

        commands.Add(0x66);
        var header = Header(count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(clockOffset), clock);
        if (loopOffset is { } offset && loopFrame is { } loop)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x1C), (uint)(header.Length + offset - 0x1C));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x20), (uint)((count - loop) * FrameSamples));
        }

        return [.. header, .. commands];
    }

    private static byte[] Header(int frames)
    {
        var header = new byte[0x100];
        "Vgm "u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x08), 0x161);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x18), (uint)(frames * FrameSamples));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x34), 0x100 - 0x34);
        return header;
    }
}
