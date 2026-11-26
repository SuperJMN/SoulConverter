using System.Buffers.Binary;
using System.Text;

namespace SoulConverter.Vgm;

/// <summary>
/// Builds a VGM 1.61 log for one chip out of timed register writes. The output keeps to the
/// commands every importer reads: the chip's write command, <c>0x61</c>/<c>0x62</c> waits and
/// <c>0x66</c>. The GD3 tag sits after <c>0x66</c>, where no consumer reads it as a command.
/// </summary>
internal sealed class VgmWriter(SoundChip chip)
{
    public const int NesClockHz = 1789773;
    public const int DmgClockHz = 4194304;
    public const int SnClockHz = 3579545;

    private const int HeaderSize = 0x100;
    private const int MaxWait = 0xFFFF;
    private const int FrameWait60Hz = 735;
    private const int Gd3FieldCount = 11;

    private readonly List<byte> data = [];
    private int sample;
    private int? loopDataOffset;
    private int? loopSample;

    public int Sample => sample;

    public int RegisterWrites { get; private set; }

    public void WaitUntil(int target)
    {
        if (target < sample)
        {
            throw new InvalidOperationException($"A write goes back in time ({target} < {sample}).");
        }

        var samples = target - sample;
        while (samples > 0)
        {
            if (samples == FrameWait60Hz)
            {
                data.Add(0x62);
                break;
            }

            var chunk = Math.Min(samples, MaxWait);
            data.Add(0x61);
            data.Add((byte)(chunk & 0xFF));
            data.Add((byte)(chunk >> 8));
            samples -= chunk;
        }

        sample = target;
    }

    /// <summary>Marks the current position as the loop point.</summary>
    public void MarkLoop()
    {
        loopDataOffset = data.Count;
        loopSample = sample;
    }

    public bool HasLoop => loopDataOffset is not null;

    public void Write(int register, int value)
    {
        switch (chip)
        {
            case SoundChip.Nes2A03:
                if (register is < 0 or > 0x1F)
                {
                    throw new InvalidOperationException($"Register 0x{register:X2} is outside the base NES 2A03 range.");
                }

                data.Add(0xB4);
                data.Add((byte)register);
                break;
            case SoundChip.GameBoyDmg:
                if (!(register is >= 0 and <= 0x16 or >= 0x20 and <= 0x2F))
                {
                    throw new InvalidOperationException(
                        $"Register 0x{register:X2} is outside the importable DMG range (0x00-0x16, 0x20-0x2F).");
                }

                data.Add(0xB3);
                data.Add((byte)register);
                break;
            default:
                data.Add(0x50);
                break;
        }

        data.Add((byte)value);
        RegisterWrites++;
    }

    public byte[] Finish(int totalSamples, byte[]? gd3Source, string note)
    {
        totalSamples = Math.Max(totalSamples, sample);
        WaitUntil(totalSamples);
        data.Add(0x66);

        var gd3 = BuildGd3(gd3Source, note);
        var gd3Position = HeaderSize + data.Count;
        var header = new byte[HeaderSize];
        "Vgm "u8.CopyTo(header);
        Put(header, 0x04, gd3Position + gd3.Length - 0x04);
        Put(header, 0x08, 0x00000161);
        Put(header, 0x14, gd3Position - 0x14);
        Put(header, 0x18, totalSamples);
        if (loopDataOffset is { } offset && loopSample is { } loop)
        {
            Put(header, 0x1C, HeaderSize + offset - 0x1C);
            Put(header, 0x20, totalSamples - loop);
        }

        Put(header, 0x24, 60);
        Put(header, 0x34, HeaderSize - 0x34);
        switch (chip)
        {
            case SoundChip.Nes2A03:
                Put(header, 0x84, NesClockHz);
                break;
            case SoundChip.GameBoyDmg:
                Put(header, 0x80, DmgClockHz);
                break;
            default:
                Put(header, 0x0C, SnClockHz);
                header[0x28] = 0x09; // the Sega PSG's feedback pattern
                header[0x2A] = 16;   // and its 16-bit shift register
                break;
        }

        return [.. header, .. data, .. gd3];
    }

    private byte[] BuildGd3(byte[]? source, string note)
    {
        var fields = new string[Gd3FieldCount];
        Array.Fill(fields, string.Empty);
        if (source is { Length: > 0 })
        {
            var parts = Encoding.Unicode.GetString(source).Split('\0');
            for (var index = 0; index < Math.Min(Gd3FieldCount, parts.Length); index++)
            {
                fields[index] = parts[index];
            }
        }

        fields[4] = chip switch
        {
            SoundChip.Nes2A03 => "NES",
            SoundChip.GameBoyDmg => "Game Boy",
            _ => "Sega Master System",
        };
        fields[5] = string.Empty;
        fields[10] = $"{fields[10]}\n{note}".Trim();

        var body = new List<byte>();
        foreach (var field in fields)
        {
            body.AddRange(Encoding.Unicode.GetBytes(field));
            body.Add(0);
            body.Add(0);
        }

        var tag = new byte[12 + body.Count];
        "Gd3 "u8.CopyTo(tag);
        Put(tag, 4, 0x100);
        Put(tag, 8, body.Count);
        body.CopyTo(tag, 12);
        return tag;
    }

    private static void Put(byte[] target, int offset, int value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset), unchecked((uint)value));
}
