using System.Buffers.Binary;
using System.IO.Compression;
using SoulConverter.Tests.Support;

namespace SoulConverter.Tests;

/// <summary>Which logs are read, which are refused and how, and what happens with a log of the target chip.</summary>
public sealed class ReadingTests
{
    public static TheoryData<SoundChip> Chips() => [SoundChip.Nes2A03, SoundChip.GameBoyDmg, SoundChip.Sn76489];

    [Theory]
    [MemberData(nameof(Chips))]
    public void The_chip_of_a_log_is_found_from_its_header(SoundChip chip)
    {
        Assert.Equal(chip, VgmConverter.DetectChip(ScoreEncoder.Encode(chip, Score.Tune)));
    }

    [Theory]
    [MemberData(nameof(Chips))]
    public void A_log_that_already_drives_the_target_is_returned_as_it_is(SoundChip chip)
    {
        var source = ScoreEncoder.Encode(chip, Score.Tune);

        var result = VgmConverter.Convert(source, chip);

        Assert.Equal(source, result.Vgm);
        Assert.Equal(chip, result.Source);
        Assert.Equal(chip, result.Target);
        Assert.False(result.IsSilent);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void A_gzipped_vgz_converts_like_its_vgm()
    {
        var plain = DemoSongs.FourChannel();
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal))
        {
            gzip.Write(plain);
        }

        Assert.Equal(
            VgmConverter.Convert(plain, SoundChip.GameBoyDmg).Vgm,
            VgmConverter.Convert(buffer.ToArray(), SoundChip.GameBoyDmg).Vgm);
    }

    [Fact]
    public void A_stream_converts_like_its_bytes()
    {
        var bytes = DemoSongs.FourChannel();

        Assert.Equal(
            VgmConverter.Convert(bytes, SoundChip.Sn76489).Vgm,
            VgmConverter.Convert(new MemoryStream(bytes), SoundChip.Sn76489).Vgm);
    }

    [Fact]
    public void A_file_that_is_not_a_vgm_is_refused()
    {
        var error = Assert.Throws<VgmFormatException>(() => VgmConverter.Convert("this is not a VGM file at all, only sixty-four bytes of plain text"u8.ToArray(), SoundChip.GameBoyDmg));

        Assert.Contains("'Vgm ' signature", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_corrupt_vgz_is_refused_as_a_format_error()
    {
        byte[] corrupt = [0x1F, 0x8B, 0x08, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x03, 0x12, 0x34, 0x56];

        Assert.Throws<VgmFormatException>(() => VgmConverter.Convert(corrupt, SoundChip.GameBoyDmg));
    }

    [Fact]
    public void A_log_that_ends_before_its_end_command_is_refused()
    {
        var truncated = TestVgm.Nes([(0x4015, 0x01)])[..^1];

        var error = Assert.Throws<VgmFormatException>(() => VgmConverter.Convert(truncated, SoundChip.GameBoyDmg));

        Assert.Contains("ended before the 0x66", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_expansion_chip_is_refused_by_name()
    {
        // Register $A0 through the NES write command is VRC6.
        var error = Assert.Throws<UnsupportedChipException>(() => VgmConverter.Convert(TestVgm.Raw(0x84, 0xB4, 0xA0, 0x00, 0x66), SoundChip.GameBoyDmg));

        Assert.Contains("VRC6", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Another_chip_is_refused_by_name()
    {
        // Header offset 0x2C is the YM2612 clock.
        var error = Assert.Throws<UnsupportedChipException>(() => VgmConverter.Convert(TestVgm.Raw(0x2C, 0x66), SoundChip.GameBoyDmg));

        Assert.Contains("YM2612", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_log_that_declares_no_supported_chip_is_refused()
    {
        // Offset 0xF0 is past every chip clock field.
        var error = Assert.Throws<UnsupportedChipException>(() => VgmConverter.Convert(TestVgm.Raw(0xF0, 0x66), SoundChip.GameBoyDmg));

        Assert.Contains("declares no NES APU (2A03), Game Boy DMG or SN76489 clock", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_log_that_declares_two_supported_chips_is_refused()
    {
        var both = TestVgm.Nes([(0x4015, 0x01)]);
        BitConverter.TryWriteBytes(both.AsSpan(0x80), 4194304u);

        var error = Assert.Throws<UnsupportedChipException>(() => VgmConverter.Convert(both, SoundChip.Sn76489));

        Assert.Contains("more than one sound chip", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_command_of_another_chip_is_refused()
    {
        // 0xB3 is a Game Boy write, in a log that declares a 2A03.
        var error = Assert.Throws<UnsupportedChipException>(() => VgmConverter.Convert(TestVgm.Raw(0x84, 0xB3, 0x00, 0x00, 0x66), SoundChip.Sn76489));

        Assert.Contains("0xB3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_error_names_the_log_when_the_caller_names_it()
    {
        var error = Assert.Throws<VgmFormatException>(() => VgmConverter.Convert(
            new byte[64],
            SoundChip.GameBoyDmg,
            new ConversionOptions { SourceName = "theme.vgm" }));

        Assert.StartsWith("theme.vgm: ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-60.0)]
    [InlineData(double.NaN)]
    [InlineData(0.5)]
    [InlineData(1000.5)]
    [InlineData(1e9)]
    [InlineData(double.PositiveInfinity)]
    public void The_tick_rate_must_be_within_its_range(double tickHz)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VgmConverter.Convert(DemoSongs.FourChannel(), SoundChip.GameBoyDmg, new ConversionOptions { TickHz = tickHz }));
    }

    [Theory]
    [InlineData(ConversionOptions.MinTickHz)]
    [InlineData(ConversionOptions.MaxTickHz)]
    public void The_ends_of_the_tick_rate_range_convert(double tickHz)
    {
        var source = TestVgm.Nes([(0x4015, 0x01), (0x4000, 0xBF), (0x4002, 0xFD), (0x4003, 0x08)], [], []);

        var result = VgmConverter.Convert(source, SoundChip.GameBoyDmg, new ConversionOptions { TickHz = tickHz });

        Assert.Equal(3 * TestVgm.FrameSamples, TestVgm.TotalSamples(result.Vgm));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x7FFFFFFFu)]
    [InlineData(0xFFFFFFFFu)]
    public void The_length_comes_from_the_commands_and_not_from_the_header(uint declared)
    {
        var source = TestVgm.Nes([(0x4015, 0x01), (0x4000, 0xBF), (0x4002, 0xFD), (0x4003, 0x08)], [], []);
        BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(0x18), declared);

        var result = VgmConverter.Convert(source, SoundChip.GameBoyDmg, new ConversionOptions { Analyze = true });

        Assert.Equal(3 * TestVgm.FrameSamples, result.Report["source"]!["total_samples"]!.GetValue<int>());
        Assert.Equal(3 * TestVgm.FrameSamples, TestVgm.TotalSamples(result.Vgm));
    }

    [Fact]
    public void The_loop_comes_from_the_loop_offset_and_not_from_the_header_totals()
    {
        var source = TestVgm.NesLooping(1, [(0x4015, 0x01), (0x4000, 0xBF), (0x4002, 0xFD), (0x4003, 0x08)], [], []);
        BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(0x18), 0x7FFFFFFFu);
        BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(0x20), 0x12345u);

        var result = VgmConverter.Convert(source, SoundChip.GameBoyDmg);

        Assert.Equal(TestVgm.FrameSamples, result.Report["source"]!["loop_sample"]!.GetValue<int>());
    }

    [Fact]
    public void A_log_that_waits_longer_than_an_hour_is_refused()
    {
        const int OneHour = 44100 * 60 * 60;
        var waits = (OneHour / 0xFFFF) + 1;
        byte[] commands = [.. Enumerable.Repeat<byte[]>([0x61, 0xFF, 0xFF], waits).SelectMany(wait => wait), 0x66];

        var error = Assert.Throws<VgmFormatException>(() => VgmConverter.Convert(TestVgm.Raw(0x84, commands), SoundChip.GameBoyDmg));

        Assert.Contains("lasts more than 60 minutes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_vgz_that_inflates_past_the_size_limit_is_refused()
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest))
        {
            var zeros = new byte[1024 * 1024];
            for (var megabyte = 0; megabyte <= 64; megabyte++)
            {
                gzip.Write(zeros);
            }
        }

        var error = Assert.Throws<VgmFormatException>(() => VgmConverter.Convert(buffer.ToArray(), SoundChip.GameBoyDmg));

        Assert.Contains("larger than 64 MB", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_log_with_a_short_old_header_is_read()
    {
        // VGM 1.10: a 64-byte header, no data offset, the SN76489 clock at 0x0C.
        byte[] old =
        [
            .. "Vgm "u8, 0, 0, 0, 0, 0x10, 0x01, 0, 0, .. BitConverter.GetBytes(3579545u),
            .. new byte[8], .. BitConverter.GetBytes(735u), .. new byte[36],
            0x50, 0x8E, 0x50, 0x0F, 0x50, 0x90, 0x62, 0x66,
        ];

        var result = VgmConverter.Convert(old, SoundChip.Nes2A03);

        Assert.Equal(SoundChip.Sn76489, result.Source);
        Assert.False(result.IsSilent);
    }
}
