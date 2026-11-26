using System.Buffers.Binary;
using System.Text.Json;

namespace SoulConverter.Tests;

/// <summary>The optional renders and measurements, the demo songs and the sample audit.</summary>
public sealed class AnalysisTests
{
    public static TheoryData<SoundChip, SoundChip> Directions() => new()
    {
        { SoundChip.Nes2A03, SoundChip.GameBoyDmg },
        { SoundChip.Nes2A03, SoundChip.Sn76489 },
        { SoundChip.GameBoyDmg, SoundChip.Nes2A03 },
        { SoundChip.GameBoyDmg, SoundChip.Sn76489 },
        { SoundChip.Sn76489, SoundChip.Nes2A03 },
        { SoundChip.Sn76489, SoundChip.GameBoyDmg },
    };

    [Fact]
    public void A_conversion_does_not_render_unless_asked()
    {
        var result = VgmConverter.Convert(DemoSongs.FourChannel(), SoundChip.GameBoyDmg);

        Assert.Null(result.SourceAudio);
        Assert.Null(result.TargetAudio);
        Assert.Empty(result.Report["metrics"]!.AsObject());
    }

    [Theory]
    [MemberData(nameof(Directions))]
    public void Analysing_renders_both_logs_and_they_rise_and_fall_together(SoundChip source, SoundChip target)
    {
        var log = Demo(source);

        var result = VgmConverter.Convert(log, target, new ConversionOptions { Analyze = true });

        Assert.Equal(result.SourceAudio!.Length, result.TargetAudio!.Length);
        Assert.True(result.TargetAudio.Max() > 0.01, "the target render makes sound");
        Assert.True(result.Report["metrics"]!["envelope_correlation"]!.GetValue<double>() > 0.7, result.Summary);
        Assert.True(result.Report["metrics"]!["spectral_correlation"]!.GetValue<double>() > 0.9, result.Summary);
    }

    [Fact]
    public void The_reference_render_plays_the_samples_the_nes_played()
    {
        // With the DPCM dropped, the Game Boy render misses the drums the reference has; putting
        // them back brings the two closer.
        var off = VgmConverter.Convert(DemoSongs.DrumKit(), SoundChip.GameBoyDmg, new ConversionOptions { Analyze = true });
        var full = VgmConverter.Convert(DemoSongs.DrumKit(), SoundChip.GameBoyDmg, new ConversionOptions { Analyze = true, Dpcm = DpcmStrategy.Full });

        Assert.True(
            Envelope(full) > Envelope(off),
            $"envelope correlation {Envelope(off):0.000} without the drums, {Envelope(full):0.000} with them");

        static double Envelope(ConversionResult result) => result.Report["metrics"]!["envelope_correlation"]!.GetValue<double>();
    }

    [Fact]
    public void The_report_serialises_as_json_and_names_the_source_and_the_settings()
    {
        var result = VgmConverter.Convert(
            DemoSongs.FourChannel(),
            SoundChip.GameBoyDmg,
            new ConversionOptions { SourceName = "demo.vgm", VolumeMode = VolumeMode.Envelope, TickHz = 120, Analyze = true });

        using var report = JsonDocument.Parse(result.Report.ToJsonString());
        Assert.Equal("demo.vgm", report.RootElement.GetProperty("source").GetProperty("name").GetString());
        Assert.Equal("NES 2A03", report.RootElement.GetProperty("source").GetProperty("system").GetString());
        Assert.Equal("Game Boy DMG", report.RootElement.GetProperty("output").GetProperty("system").GetString());
        Assert.Equal("envelope", report.RootElement.GetProperty("settings").GetProperty("volume_mode").GetString());
        Assert.Equal(120, report.RootElement.GetProperty("settings").GetProperty("tick_hz").GetDouble());
    }

    [Fact]
    public void The_summary_mentions_every_channel()
    {
        var summary = VgmConverter.Convert(DemoSongs.FourChannel(), SoundChip.GameBoyDmg).Summary;

        Assert.All(new[] { "pulse1", "pulse2", "triangle", "noise" }, channel => Assert.Contains(channel, summary, StringComparison.Ordinal));
    }

    [Fact]
    public void The_demo_songs_are_the_same_bytes_every_time_and_are_2a03_logs()
    {
        Assert.Equal(DemoSongs.FourChannel(), DemoSongs.FourChannel());
        Assert.Equal(DemoSongs.DrumKit(), DemoSongs.DrumKit());
        Assert.Equal(DemoSongs.SampledBass(), DemoSongs.SampledBass());
        Assert.All(
            new[] { DemoSongs.FourChannel(), DemoSongs.DrumKit(), DemoSongs.SampledBass(), DemoSongs.FourChannel(loop: false) },
            song => Assert.Equal(SoundChip.Nes2A03, VgmConverter.DetectChip(song)));
    }

    [Fact]
    public void The_sample_audit_lists_each_distinct_sample_decoded_with_its_role()
    {
        var samples = VgmConverter.AnalyzeDpcm(DemoSongs.DrumKit());

        Assert.Equal([DpcmRole.Kick, DpcmRole.Hat, DpcmRole.Snare], samples.Select(sample => sample.Role));
        Assert.All(samples, sample => Assert.Equal(sample.Length * 8, sample.Waveform.Count));
        Assert.All(samples, sample => Assert.InRange(sample.Waveform.Max(Math.Abs), 0.1, 1.0));
    }

    [Fact]
    public void The_same_sample_at_another_rate_is_a_different_sound_in_the_audit()
    {
        var samples = VgmConverter.AnalyzeDpcm(DemoSongs.SampledBass());

        Assert.Equal([12, 13, 14, 15], samples.Select(sample => sample.RateIndex).Order());
        Assert.All(samples, sample => Assert.Equal(0xC000, sample.Address));
    }

    [Fact]
    public void A_log_without_a_sample_channel_has_no_samples_to_audit()
    {
        Assert.Empty(VgmConverter.AnalyzeDpcm(VgmConverter.Convert(DemoSongs.DrumKit(), SoundChip.Sn76489).Vgm));
    }

    [Fact]
    public void A_wav_is_mono_16_bit_at_the_given_rate_and_scaled_under_full_scale()
    {
        var wav = WavFile.Encode([0.0, 0.25, -0.5, 0.125], sampleRate: 22050);

        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));
        Assert.Equal(22050, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
        Assert.Equal(8, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
        Assert.Equal((short)(-0.95 * 32767), BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(48)));
    }

    [Fact]
    public void A_spectrogram_is_a_png_of_the_asked_width_with_one_picture_over_the_other()
    {
        var result = VgmConverter.Convert(DemoSongs.FourChannel(), SoundChip.GameBoyDmg, new ConversionOptions { Analyze = true });

        var png = SpectrogramImage.EncodePng(result.SourceAudio, result.TargetAudio, width: 320);

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        Assert.Equal(320, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(324, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
    }

    // The demo tune as a log of each chip: the 2A03 one is generated, the others are converted from
    // it, which is fine here because these tests only compare a render with its own conversion.
    private static byte[] Demo(SoundChip chip) => VgmConverter.Convert(DemoSongs.FourChannel(), chip).Vgm;
}
