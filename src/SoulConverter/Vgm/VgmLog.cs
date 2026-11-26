using System.Buffers.Binary;
using System.IO.Compression;

namespace SoulConverter.Vgm;

/// <summary>One register write at an absolute VGM sample position (44100 Hz).</summary>
internal readonly record struct RegisterWrite(int Sample, int Register, int Value);

/// <summary>What the reader kept aside while parsing, for the report.</summary>
internal sealed class DroppedStats
{
    public int DpcmBlockBytes;
    public int DpcmBlockCount;
    public int DpcmRegisterWrites;
    public int PcmStreamCommands;
}

/// <summary>
/// The sample memory the 2A03's DMC channel reads from. Sparse on purpose: a rip only carries the
/// pages the song plays, and the DMC address space ($C000-$FFFF) is mostly program ROM.
/// </summary>
internal sealed class DpcmRom
{
    private readonly Dictionary<int, byte> data = [];

    public int BlockCount { get; set; }

    public int Count => data.Count;

    public void Load(int start, ReadOnlySpan<byte> payload)
    {
        for (var index = 0; index < payload.Length; index++)
        {
            data[(start + index) & 0xFFFF] = payload[index];
        }
    }

    /// <summary>Reads one byte, wrapping as the DMC does. Missing pages read as 0.</summary>
    public byte Read(int address) => data.GetValueOrDefault(address & 0xFFFF);
}

/// <summary>A parsed VGM log of one of the supported chips: its writes in time order and its header facts.</summary>
internal sealed class VgmLog
{
    public const int SampleRate = 44100;

    /// <summary>The longest log read, one hour: a longer one is a corrupt header or a hostile file.</summary>
    public const int MaxSamples = SampleRate * 60 * 60;

    /// <summary>The largest log read, compressed or not.</summary>
    public const int MaxBytes = 64 * 1024 * 1024;

    public required SoundChip Chip { get; init; }

    public required int ClockHz { get; init; }

    public required int Version { get; init; }

    public int TotalSamples { get; set; }

    public int? LoopSample { get; set; }

    public byte[]? Gd3 { get; init; }

    public List<RegisterWrite> Writes { get; } = [];

    public DroppedStats Dropped { get; } = new();

    public DpcmRom DpcmRom { get; } = new();

    public double DurationSeconds => TotalSamples / (double)SampleRate;

    public string VersionText => $"{Version >> 8:x}.{Version & 0xFF:x2}";
}

/// <summary>
/// Parses VGM and gzipped VGZ logs of the NES 2A03, the Game Boy DMG and the SN76489. A log that
/// declares any other chip, an expansion chip, or more than one of the three, is refused by name.
/// </summary>
internal static class VgmLogReader
{
    private const int MinHeaderSize = 0x40;
    private const int SnClockOffset = 0x0C;
    private const int DmgClockOffset = 0x80;
    private const int NesClockOffset = 0x84;
    private const int DmcBaseAddress = 0xC000;

    /// <summary>The register number the Game Gear stereo byte (command 0x4F) is logged under.</summary>
    public const int SnStereoRegister = 1;

    // Header offset -> chip name, for every other chip clock field defined up to VGM 1.71.
    private static readonly (int Offset, string Chip)[] OtherChipClocks =
    [
        (0x10, "YM2413"), (0x2C, "YM2612"), (0x30, "YM2151"), (0x38, "Sega PCM"), (0x40, "RF5C68"),
        (0x44, "YM2203"), (0x48, "YM2608"), (0x4C, "YM2610/YM2610B"), (0x50, "YM3812"), (0x54, "YM3526"),
        (0x58, "Y8950"), (0x5C, "YMF262"), (0x60, "YMF278B"), (0x64, "YMF271"), (0x68, "YMZ280B"),
        (0x6C, "RF5C164"), (0x70, "PWM"), (0x74, "AY8910"), (0x88, "MultiPCM"), (0x8C, "uPD7759"),
        (0x90, "OKIM6258"), (0x98, "OKIM6295"), (0x9C, "K051649/SCC1"), (0xA0, "K054539"), (0xA4, "HuC6280"),
        (0xA8, "C140"), (0xAC, "K053260"), (0xB0, "Pokey"), (0xB4, "QSound"), (0xB8, "SCSP"),
        (0xC0, "WonderSwan"), (0xC4, "VSU"), (0xC8, "SAA1099"), (0xCC, "ES5503"), (0xD0, "ES5505/ES5506"),
        (0xD8, "X1-010"), (0xDC, "C352"), (0xE0, "GA20"),
    ];

    // VGM routes NES expansion audio through command 0xB4 with registers >= 0x20.
    private static readonly (int Low, int High, string Chip)[] NesExpansionRegisters =
    [
        (0x20, 0x3F, "FDS"), (0x40, 0x5F, "Namco 163"), (0x60, 0x7F, "MMC5"),
        (0x80, 0x9F, "Sunsoft 5B"), (0xA0, 0xBF, "VRC6"), (0xC0, 0xDF, "VRC7"),
    ];

    /// <summary>Returns the VGM bytes of <paramref name="raw"/>, decompressing a gzipped VGZ.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> raw)
    {
        if (raw.Length > VgmLog.MaxBytes)
        {
            throw TooLarge();
        }

        if (raw.Length < 2 || raw[0] != 0x1F || raw[1] != 0x8B)
        {
            return raw.ToArray();
        }

        try
        {
            using var input = new MemoryStream(raw.ToArray());
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            return ReadAtMost(gzip);
        }
        catch (InvalidDataException error)
        {
            throw new VgmFormatException($"the gzipped VGZ payload is corrupt: {error.Message}");
        }
    }

    /// <summary>Reads <paramref name="stream"/> to the end, refusing more than <see cref="VgmLog.MaxBytes"/>.</summary>
    public static byte[] ReadAtMost(Stream stream)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            if (output.Length + read > VgmLog.MaxBytes)
            {
                throw TooLarge();
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    public static SoundChip DetectChip(byte[] data, string name) => Header(data, name).Chip;

    public static VgmLog Read(byte[] data, string name)
    {
        var (chip, clock, dataOffset) = Header(data, name);
        var loopOffset = U32(data, 0x1C);

        var log = new VgmLog
        {
            Chip = chip,
            ClockHz = clock,
            Version = (int)U32(data, 0x08),
            Gd3 = ReadGd3(data),
        };

        // The length and the loop come from the commands, not from the header's totals: a header
        // can claim hours over a few bytes of data.
        DecodeCommands(data, name, dataOffset, loopOffset == 0 ? -1 : 0x1C + loopOffset, log);
        return log;
    }

    private static (SoundChip Chip, int Clock, int DataOffset) Header(byte[] data, string name)
    {
        if (data.Length < MinHeaderSize || !data.AsSpan(0, 4).SequenceEqual("Vgm "u8))
        {
            throw new VgmFormatException($"{name}: does not start with the 'Vgm ' signature");
        }

        // Per the VGM specification, header fields that overlap the start of the command data are
        // zero, so older and shorter headers do not read garbage.
        var headerLimit = DataOffset(data, name);
        var declared = new List<(SoundChip Chip, int Clock)>();
        foreach (var (offset, chip) in new[] { (NesClockOffset, SoundChip.Nes2A03), (DmgClockOffset, SoundChip.GameBoyDmg), (SnClockOffset, SoundChip.Sn76489) })
        {
            var clock = (int)(HeaderU32(data, offset, headerLimit) & 0x3FFFFFFF);
            if (clock != 0)
            {
                declared.Add((chip, clock));
            }
        }

        foreach (var (offset, chip) in OtherChipClocks)
        {
            if ((HeaderU32(data, offset, headerLimit) & 0x3FFFFFFF) != 0)
            {
                throw new UnsupportedChipException(
                    $"{name}: declares a {chip} clock at header offset 0x{offset:X2}; only the NES 2A03, " +
                    "the Game Boy DMG and the SN76489 are supported");
            }
        }

        return declared.Count switch
        {
            0 => throw new UnsupportedChipException(
                $"{name}: declares no NES APU (2A03), Game Boy DMG or SN76489 clock; SoulConverter converts between those three chips"),
            1 => (declared[0].Chip, declared[0].Clock, headerLimit),
            _ => throw new UnsupportedChipException(
                $"{name}: declares more than one sound chip ({string.Join(", ", declared.Select(entry => entry.Chip.DisplayName()))}); " +
                "a log must drive exactly one"),
        };
    }

    private static void DecodeCommands(byte[] data, string name, int position, long loopPosition, VgmLog log)
    {
        var samples = 0;
        while (position < data.Length)
        {
            var start = position;
            if (start == loopPosition)
            {
                log.LoopSample = samples;
            }

            var command = data[position++];
            switch (command)
            {
                case 0x66:
                    log.TotalSamples = samples;
                    if (log.LoopSample >= samples)
                    {
                        log.LoopSample = null;
                    }

                    return;
                case 0x61:
                    Require(data, position, 2, start, name);
                    samples = Wait(samples, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position)), name);
                    position += 2;
                    break;
                case 0x62:
                    samples = Wait(samples, 735, name);
                    break;
                case 0x63:
                    samples = Wait(samples, 882, name);
                    break;
                case >= 0x70 and <= 0x7F:
                    samples = Wait(samples, (command & 0x0F) + 1, name);
                    break;
                case 0xB4 when log.Chip == SoundChip.Nes2A03:
                    Require(data, position, 2, start, name);
                    RecordNesWrite(log, samples, data[position], data[position + 1], start, name);
                    position += 2;
                    break;
                case 0xB3 when log.Chip == SoundChip.GameBoyDmg:
                    Require(data, position, 2, start, name);
                    var register = data[position];
                    if (!(register <= 0x16 || register is >= 0x20 and <= 0x2F))
                    {
                        throw new UnsupportedChipException(
                            $"{name}: register 0x{register:X2} at 0x{start:X} is outside the Game Boy DMG register ranges");
                    }

                    log.Writes.Add(new RegisterWrite(samples, register, data[position + 1]));
                    position += 2;
                    break;
                case 0x50 when log.Chip == SoundChip.Sn76489:
                    Require(data, position, 1, start, name);
                    log.Writes.Add(new RegisterWrite(samples, 0, data[position++]));
                    break;
                case 0x4F when log.Chip == SoundChip.Sn76489:
                    Require(data, position, 1, start, name);
                    log.Writes.Add(new RegisterWrite(samples, SnStereoRegister, data[position++]));
                    break;
                case 0x67 when log.Chip == SoundChip.Nes2A03:
                    position = ReadDataBlock(data, position, start, name, log);
                    break;
                case 0xE0 when log.Chip == SoundChip.Nes2A03:
                    Require(data, position, 4, start, name);
                    position += 4;
                    log.Dropped.PcmStreamCommands++;
                    break;
                default:
                    throw new UnsupportedChipException(
                        $"{name}: unsupported VGM command 0x{command:X2} at 0x{start:X} for {log.Chip.DisplayName()} import");
            }
        }

        throw new VgmFormatException($"{name}: ended before the 0x66 end-of-data command");
    }

    private static int Wait(int samples, int count, string name) =>
        samples + count <= VgmLog.MaxSamples
            ? samples + count
            : throw new VgmFormatException($"{name}: lasts more than {VgmLog.MaxSamples / VgmLog.SampleRate / 60} minutes, the longest log SoulConverter reads");

    private static VgmFormatException TooLarge() =>
        new($"the log is larger than {VgmLog.MaxBytes / (1024 * 1024)} MB, the largest SoulConverter reads");

    private static void RecordNesWrite(VgmLog log, int samples, int register, int value, int offset, string name)
    {
        if (register > 0x1F)
        {
            foreach (var (low, high, chip) in NesExpansionRegisters)
            {
                if (register >= low && register <= high)
                {
                    throw new UnsupportedChipException(
                        $"{name}: register 0x{register:X2} at 0x{offset:X} targets {chip} expansion audio, which is not supported");
                }
            }

            throw new UnsupportedChipException($"{name}: unknown NES register 0x{register:X2} at 0x{offset:X}");
        }

        if (register is >= 0x10 and <= 0x13)
        {
            log.Dropped.DpcmRegisterWrites++;
        }

        log.Writes.Add(new RegisterWrite(samples, register, value));
    }

    private static int ReadDataBlock(byte[] data, int position, int start, string name, VgmLog log)
    {
        Require(data, position, 6, start, name);
        if (data[position] != 0x66)
        {
            throw new VgmFormatException($"{name}: malformed data block at 0x{start:X}: missing 0x66 marker");
        }

        var blockType = data[position + 1];
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 2));
        position += 6;
        if (size > (uint)(data.Length - position))
        {
            throw new VgmFormatException($"{name}: data block at 0x{start:X} extends past the end of the file");
        }

        var body = data.AsSpan(position, (int)size);

        // 0x07 = NES DPCM ROM image, 0xC2 = NES APU RAM write. Both carry sample data.
        if (blockType == 0xC2 && body.Length >= 2)
        {
            log.DpcmRom.Load(BinaryPrimitives.ReadUInt16LittleEndian(body), body[2..]);
            Count(log, (int)size);
        }
        else if (blockType == 0x07 && body.Length >= 8)
        {
            // Some rips give an absolute DMC address, others an offset into the image.
            var address = (long)BinaryPrimitives.ReadUInt32LittleEndian(body[4..]);
            log.DpcmRom.Load((int)((address >= DmcBaseAddress ? address : DmcBaseAddress + address) & 0xFFFF), body[8..]);
            Count(log, (int)size);
        }

        return position + (int)size;

        static void Count(VgmLog log, int size)
        {
            log.DpcmRom.BlockCount++;
            log.Dropped.DpcmBlockBytes += size;
            log.Dropped.DpcmBlockCount++;
        }
    }

    private static int DataOffset(byte[] data, string name)
    {
        var relative = U32(data, 0x34);
        var offset = relative == 0 ? MinHeaderSize : 0x34 + relative;
        if (offset < MinHeaderSize || offset >= data.Length)
        {
            throw new VgmFormatException($"{name}: invalid VGM data offset 0x{offset:X}");
        }

        return (int)offset;
    }

    private static byte[]? ReadGd3(byte[] data)
    {
        var relative = U32(data, 0x14);
        if (relative == 0)
        {
            return null;
        }

        var offset = 0x14 + relative;
        if (offset + 12 > data.Length || !data.AsSpan((int)offset, 4).SequenceEqual("Gd3 "u8))
        {
            return null;
        }

        var length = U32(data, (int)offset + 8);
        var end = offset + 12 + length;
        return end > data.Length ? null : data[((int)offset + 12)..(int)end];
    }

    private static long HeaderU32(byte[] data, int offset, int headerLimit) => offset + 4 > headerLimit ? 0 : U32(data, offset);

    private static long U32(byte[] data, int offset) =>
        offset + 4 > data.Length ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));

    private static void Require(byte[] data, int position, int count, int offset, string name)
    {
        if (position + count > data.Length)
        {
            throw new VgmFormatException($"{name}: command at 0x{offset:X} extends past the end of the file");
        }
    }
}
