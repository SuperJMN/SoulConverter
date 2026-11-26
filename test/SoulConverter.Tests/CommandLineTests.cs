using System.IO.Compression;
using System.Text.Json;
using SoulConverter.Cli;
using SoulConverter.Tests.Support;

namespace SoulConverter.Tests;

/// <summary>The <c>soulconverter</c> command does what the README documents, over the library.</summary>
public sealed class CommandLineTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "soulconverter-tests", Guid.NewGuid().ToString("N"));
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();

    public CommandLineTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public void Demo_writes_a_log_that_converts_straight_away()
    {
        var demo = At("made", "demo.vgm");

        Assert.Equal(0, Run("demo", "-o", demo));
        Assert.Equal(0, Run("convert", demo, "--quiet"));

        Assert.Equal(DemoSongs.FourChannel(), File.ReadAllBytes(demo));
        Assert.Equal(SoundChip.GameBoyDmg, VgmConverter.DetectChip(File.ReadAllBytes(At("made", "demo.gb.vgm"))));
    }

    [Theory]
    [InlineData("drum-kit")]
    [InlineData("sampled-bass")]
    public void Demo_writes_the_songs_with_samples_too(string song)
    {
        Assert.Equal(0, Run("demo", "-o", At("demo.vgm"), "--song", song));

        Assert.NotEmpty(VgmConverter.AnalyzeDpcm(File.ReadAllBytes(At("demo.vgm"))));
    }

    [Fact]
    public void A_file_converts_to_the_log_the_library_gives()
    {
        var source = Source("tune.vgm", DemoSongs.FourChannel());

        Assert.Equal(0, Run("convert", source, "-o", At("out", "tune.gb.vgm"), "--quiet"));

        Assert.Equal(VgmConverter.Convert(DemoSongs.FourChannel(), SoundChip.GameBoyDmg).Vgm, File.ReadAllBytes(At("out", "tune.gb.vgm")));
    }

    [Fact]
    public void Without_a_target_a_game_boy_log_goes_to_the_nes()
    {
        var source = Source("tune.gb.vgm", ScoreEncoder.Encode(SoundChip.GameBoyDmg, Score.Tune));

        Assert.Equal(0, Run("convert", source, "--quiet"));

        Assert.Equal(SoundChip.Nes2A03, VgmConverter.DetectChip(File.ReadAllBytes(At("tune.gb.nes.vgm"))));
    }

    [Theory]
    [InlineData("sms", SoundChip.Sn76489)]
    [InlineData("gb", SoundChip.GameBoyDmg)]
    [InlineData("nes", SoundChip.Nes2A03)]
    public void The_target_chip_can_be_named(string name, SoundChip chip)
    {
        var source = Source("tune.vgm", DemoSongs.FourChannel());

        Assert.Equal(0, Run("convert", source, "--to", name, "--quiet"));

        Assert.Equal(chip, VgmConverter.DetectChip(File.ReadAllBytes(At($"tune.{name}.vgm"))));
    }

    [Fact]
    public void A_master_system_log_needs_its_target_named()
    {
        var source = Source("tune.sms.vgm", ScoreEncoder.Encode(SoundChip.Sn76489, Score.Tune));

        Assert.Equal(1, Run("convert", source, "--quiet"));

        Assert.Contains("--to nes or --to gb", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_vgz_source_is_read_and_a_vgz_destination_is_really_gzipped()
    {
        var source = Source("tune.vgz", Gzip(DemoSongs.FourChannel()));

        Assert.Equal(0, Run("convert", source, "-o", At("tune.gb.vgz"), "--quiet"));

        Assert.Equal(VgmConverter.Convert(DemoSongs.FourChannel(), SoundChip.GameBoyDmg).Vgm, Gunzip(File.ReadAllBytes(At("tune.gb.vgz"))));
    }

    [Fact]
    public void The_summary_is_printed_unless_silenced()
    {
        var source = Source("tune.vgm", DemoSongs.FourChannel());

        Run("convert", source);
        var loud = output.ToString();
        output.GetStringBuilder().Clear();
        Run("convert", source, "--quiet");

        Assert.Contains("envelope correlation", loud, StringComparison.Ordinal);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void Warnings_do_not_turn_into_a_failure()
    {
        var source = Source("drums.vgm", DemoSongs.DrumKit());

        Assert.Equal(0, Run("convert", source));

        Assert.Contains("DPCM dropped", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Report_writes_the_conversion_report_as_json_with_the_options_given()
    {
        var source = Source("tune.vgm", DemoSongs.FourChannel());

        Assert.Equal(0, Run("convert", source, "--report", At("reports"), "--volume-mode", "envelope", "--tick-hz", "120", "--no-transpose", "--quiet"));

        using var report = JsonDocument.Parse(File.ReadAllText(At("reports", "tune.json")));
        var settings = report.RootElement.GetProperty("settings");
        Assert.Equal("envelope", settings.GetProperty("volume_mode").GetString());
        Assert.Equal(120, settings.GetProperty("tick_hz").GetDouble());
        Assert.False(settings.GetProperty("transpose_out_of_range").GetBoolean());
        Assert.Equal(At("tune.gb.vgm"), report.RootElement.GetProperty("output").GetProperty("path").GetString());
        Assert.True(report.RootElement.GetProperty("metrics").GetProperty("spectral_correlation").GetDouble() > 0.9);
    }

    [Fact]
    public void Wav_and_spectrogram_write_both_renders_and_the_picture_beside_the_log()
    {
        var source = Source("tune.vgm", DemoSongs.FourChannel());

        Assert.Equal(0, Run("convert", source, "--wav", "--spectrogram", "--quiet"));

        Assert.Equal("RIFF"u8.ToArray(), File.ReadAllBytes(At("tune.nes.wav"))[..4]);
        Assert.Equal("RIFF"u8.ToArray(), File.ReadAllBytes(At("tune.gb.wav"))[..4]);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, File.ReadAllBytes(At("tune.compare.png"))[..4]);
    }

    [Fact]
    public void A_dpcm_strategy_is_passed_on_to_the_conversion()
    {
        var source = Source("drums.vgm", DemoSongs.DrumKit());

        Assert.Equal(0, Run("convert", source, "--dpcm", "full", "--quiet"));

        Assert.Equal(
            VgmConverter.Convert(DemoSongs.DrumKit(), SoundChip.GameBoyDmg, new ConversionOptions { Dpcm = DpcmStrategy.Full }).Vgm,
            File.ReadAllBytes(At("drums.gb.vgm")));
    }

    [Fact]
    public void Variants_writes_one_named_file_per_strategy_and_a_table_that_does_not_rank_them()
    {
        var source = Source("drums.vgm", DemoSongs.DrumKit());

        Assert.Equal(0, Run("convert", source, "-o", At("out", "drums.gb.vgm"), "--variants"));

        Assert.Equal(
            ["drums.balanced.gb.vgm", "drums.full.gb.vgm", "drums.off.gb.vgm", "drums.safe.gb.vgm"],
            Directory.EnumerateFiles(At("out")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Contains("listen before choosing", output.ToString(), StringComparison.Ordinal);
        Assert.All(new[] { "off", "safe", "balanced", "full" }, strategy => Assert.Contains(strategy, output.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void Variants_keep_the_extension_asked_for()
    {
        var source = Source("drums.vgm", DemoSongs.DrumKit());

        Assert.Equal(0, Run("convert", source, "-o", At("out", "drums.vgz"), "--variants", "--quiet"));

        Assert.Equal(4, Directory.EnumerateFiles(At("out"), "drums.*.vgz").Count());
        Assert.All(Directory.EnumerateFiles(At("out")), path => Assert.Equal(new byte[] { 0x1F, 0x8B }, File.ReadAllBytes(path)[..2]));
    }

    [Fact]
    public void A_role_forced_by_hand_reaches_the_report()
    {
        var source = Source("drums.vgm", DemoSongs.DrumKit());

        Assert.Equal(0, Run("convert", source, "--dpcm", "safe", "--dpcm-role", "0xC000=snare", "--report", At("reports"), "--quiet"));

        using var report = JsonDocument.Parse(File.ReadAllText(At("reports", "drums.json")));
        var kick = report.RootElement.GetProperty("dpcm").GetProperty("samples").EnumerateArray().Single(sample => sample.GetProperty("address").GetString() == "0xC000");
        Assert.Equal("snare", kick.GetProperty("role").GetString());
    }

    [Fact]
    public void An_unknown_role_is_refused_instead_of_silently_ignored()
    {
        var source = Source("drums.vgm", DemoSongs.DrumKit());

        Assert.Equal(1, Run("convert", source, "--dpcm-role", "0xC000=cowbell"));

        Assert.Contains("kick, snare, hat, tom, tonal, noise_sfx, artifact", error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(At("drums.gb.vgm")));
    }

    [Fact]
    public void Dumping_samples_writes_one_audible_file_per_sample()
    {
        var source = Source("drums.vgm", DemoSongs.DrumKit());

        Assert.Equal(0, Run("convert", source, "--dpcm-dump", At("samples"), "--quiet"));

        Assert.Equal(
            ["C000_369_r15_kick.wav", "C180_241_r15_snare.wav", "C280_81_r15_hat.wav"],
            Directory.EnumerateFiles(At("samples")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_directory_converts_every_log_it_contains_and_nothing_else()
    {
        Source("a.vgm", DemoSongs.FourChannel());
        Source("b.vgz", Gzip(DemoSongs.DrumKit()));
        Source("notes.txt", "not a log"u8.ToArray());

        Assert.Equal(0, Run("convert", directory, "-o", At("out"), "--quiet"));

        Assert.Equal(["a.gb.vgm", "b.gb.vgm"], Directory.EnumerateFiles(At("out")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void One_bad_file_among_good_ones_reports_a_partial_failure()
    {
        Source("good.vgm", DemoSongs.FourChannel());
        Source("bad.vgm", "this is not a VGM file at all, only sixty-four bytes of plain text"u8.ToArray());

        Assert.Equal(2, Run("convert", directory, "-o", At("out"), "--quiet"));

        Assert.True(File.Exists(At("out", "good.gb.vgm")));
        Assert.Contains("bad.vgm", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_is_not_a_log_is_an_error()
    {
        var source = Source("bad.vgm", "this is not a VGM file at all, only sixty-four bytes of plain text"u8.ToArray());

        Assert.Equal(1, Run("convert", source));

        Assert.Contains("'Vgm ' signature", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing.vgm")]
    [InlineData("empty")]
    public void An_input_with_no_logs_is_an_error(string name)
    {
        Directory.CreateDirectory(At("empty"));

        Assert.Equal(1, Run("convert", At(name)));

        Assert.Contains("no VGM files found", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--volume-mode", "loud")]
    [InlineData("--to", "snes")]
    [InlineData("--dpcm", "reckless")]
    [InlineData("--tick-hz", "0")]
    public void An_unknown_option_value_is_refused_with_the_usage(string option, string value)
    {
        var source = Source("tune.vgm", DemoSongs.FourChannel());

        Assert.Equal(1, Run("convert", source, option, value));

        Assert.Contains(option, error.ToString(), StringComparison.Ordinal);
        Assert.Contains("usage: soulconverter convert", error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(At("tune.gb.vgm")));
    }

    private int Run(params string[] args) => CommandLine.Run(args, output, error);

    private string At(params string[] parts) => Path.Combine([directory, .. parts]);

    private string Source(string name, byte[] bytes)
    {
        File.WriteAllBytes(At(name), bytes);
        return At(name);
    }

    private static byte[] Gzip(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal))
        {
            gzip.Write(data);
        }

        return buffer.ToArray();
    }

    private static byte[] Gunzip(byte[] data)
    {
        using var gzip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var buffer = new MemoryStream();
        gzip.CopyTo(buffer);
        return buffer.ToArray();
    }
}
