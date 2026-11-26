using System.Text.Json.Nodes;
using SoulConverter.Tests.Support;

namespace SoulConverter.Tests;

/// <summary>What a log becomes on the Game Boy DMG, including what happens to the NES sample channel.</summary>
public sealed class GameBoyTargetTests
{
    private const int NR10 = 0x00, NR30 = 0x0A, NR32 = 0x0C, NR34 = 0x0E;

    public static TheoryData<string, DpcmStrategy> SongsAndStrategies()
    {
        var data = new TheoryData<string, DpcmStrategy>();
        foreach (var song in new[] { "four-channel", "drum-kit", "sampled-bass" })
        {
            foreach (var strategy in Enum.GetValues<DpcmStrategy>())
            {
                data.Add(song, strategy);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SongsAndStrategies))]
    public void The_log_stays_inside_what_a_strict_importer_reads(string song, DpcmStrategy strategy)
    {
        var result = ToGameBoy(Song(song), new ConversionOptions { Dpcm = strategy });

        Assert.Subset(new SortedSet<int> { 0x61, 0x62, 0x66, 0xB3 }, TestVgm.Commands(result.Vgm));
        Assert.All(TestVgm.Writes(result.Vgm), write => Assert.True(write.Register is <= 0x16 or (>= 0x20 and <= 0x2F), $"register 0x{write.Register:X2}"));
        Assert.Equal(4194304u, TestVgm.Clock(result.Vgm, 0x80));
        Assert.Equal(0u, TestVgm.Clock(result.Vgm, 0x84));
    }

    [Theory]
    [MemberData(nameof(SongsAndStrategies))]
    public void The_length_and_the_loop_point_survive(string song, DpcmStrategy strategy)
    {
        var source = Song(song);

        var result = ToGameBoy(source, new ConversionOptions { Dpcm = strategy });

        Assert.Equal(TestVgm.TotalSamples(source), TestVgm.TotalSamples(result.Vgm));
        Assert.Equal(TestVgm.FrameSamples, TestVgm.LoopSample(result.Vgm));
    }

    [Fact]
    public void Bass_below_the_game_boy_floor_is_raised_by_whole_octaves_and_reported()
    {
        // Triangle timer $7FF: 27.3 Hz, under the wave channel's 32 Hz floor.
        var result = ToGameBoy(TestVgm.Nes([(0x4015, 0x04), (0x4008, 0xFF), (0x400A, 0xFF), (0x400B, 0xFF)], [], []));

        var played = ScoreDecoder.Decode(SoundChip.GameBoyDmg, result.Vgm)[2][2];
        Assert.True(played.Playing);
        Assert.InRange(played.Hz, 54.0, 55.2);
        Assert.Contains(result.Warnings, warning => warning.Code == WarningCodes.Transposed && warning.Message.StartsWith("triangle", StringComparison.Ordinal));
    }

    [Fact]
    public void Clamping_instead_of_transposing_keeps_the_note_on_the_lowest_pitch_and_reports_nothing()
    {
        var result = ToGameBoy(
            TestVgm.Nes([(0x4015, 0x04), (0x4008, 0xFF), (0x400A, 0xFF), (0x400B, 0xFF)], [], []),
            new ConversionOptions { TransposeOutOfRange = false });

        var played = ScoreDecoder.Decode(SoundChip.GameBoyDmg, result.Vgm)[2][2];
        Assert.True(played.Playing);
        Assert.Equal(32.0, played.Hz);
        Assert.DoesNotContain(result.Warnings, warning => warning.Code == WarningCodes.Transposed);
    }

    [Fact]
    public void Envelope_mode_is_smaller_than_retrigger_mode()
    {
        var retrigger = ToGameBoy(DemoSongs.FourChannel(), new ConversionOptions { VolumeMode = VolumeMode.Retrigger });
        var envelope = ToGameBoy(DemoSongs.FourChannel(), new ConversionOptions { VolumeMode = VolumeMode.Envelope });

        Assert.True(envelope.Vgm.Length < retrigger.Vgm.Length, $"envelope {envelope.Vgm.Length} bytes, retrigger {retrigger.Vgm.Length}");
    }

    [Fact]
    public void Envelope_mode_hands_a_decaying_note_to_the_game_boy_envelope_unit()
    {
        // Pulse 1 with a decaying envelope of period 3: (3 + 1) / 240 s per step, which is one
        // Game Boy envelope step of 1/64 s.
        var result = ToGameBoy(
            TestVgm.Nes([(0x4015, 0x01), (0x4000, 0x83), (0x4002, 0xFD), (0x4003, 0x08)], [], [], [], [], []),
            new ConversionOptions { VolumeMode = VolumeMode.Envelope });

        var volumeWrites = TestVgm.Writes(result.Vgm).Where(write => write.Register == 0x02 && write.Value != 0).Select(write => write.Value);
        Assert.Equal([0xF1], volumeWrites);
    }

    [Fact]
    public void A_finer_grid_costs_more_bytes_and_keeps_the_length()
    {
        var coarse = ToGameBoy(DemoSongs.FourChannel(), new ConversionOptions { TickHz = 60 });
        var fine = ToGameBoy(DemoSongs.FourChannel(), new ConversionOptions { TickHz = 240 });

        Assert.True(fine.Vgm.Length > coarse.Vgm.Length, $"240 Hz {fine.Vgm.Length} bytes, 60 Hz {coarse.Vgm.Length}");
        Assert.Equal(TestVgm.TotalSamples(coarse.Vgm), TestVgm.TotalSamples(fine.Vgm));
    }

    [Fact]
    public void The_bass_rests_through_the_output_level_and_the_wave_channel_is_started_once()
    {
        // A triangle note, a rest, and another note. Triggering CH3 again or switching its DAC off
        // would click on every note of the bass line.
        (int, int)[] note = [(0x4008, 0xFF), (0x400A, 0x7E), (0x400B, 0x08)];
        var result = ToGameBoy(TestVgm.Nes([(0x4015, 0x04), .. note], [], [], [(0x4008, 0x80)], [], [], note, [], []));

        var writes = TestVgm.Writes(result.Vgm);
        Assert.Equal([0x00, 0x80], writes.Where(write => write.Register == NR30).Select(write => write.Value));
        Assert.Single(writes, write => write.Register == NR34 && (write.Value & 0x80) != 0);
        Assert.Equal([0x20, 0x00, 0x20], writes.Where(write => write.Register == NR32).Select(write => write.Value));
    }

    [Fact]
    public void A_square_third_tone_loads_a_square_into_wave_ram()
    {
        var result = ToGameBoy(TestVgm.Sn([0xC0 | 0x0E, 0x0F, 0xD0]));

        var waveRam = TestVgm.Writes(result.Vgm).Where(write => write.Register >= 0x20).Select(write => write.Value);
        Assert.Equal([.. Enumerable.Repeat(0xFF, 8), .. Enumerable.Repeat(0x00, 8)], waveRam);
    }

    [Fact]
    public void The_sample_channel_is_dropped_by_default_and_the_loss_is_reported()
    {
        var result = ToGameBoy(DemoSongs.DrumKit());

        Assert.Equal(DpcmStrategy.Off, result.Dpcm);
        Assert.Equal(16, Dpcm(result)["hits"]!.GetValue<int>());
        Assert.Equal(0, Dpcm(result)["placed"]!.GetValue<int>());
        Assert.Contains(result.Warnings, warning => warning.Code == WarningCodes.DpcmDropped);
    }

    [Fact]
    public void The_report_names_every_distinct_sample_and_what_it_is_taken_for()
    {
        var result = ToGameBoy(DemoSongs.DrumKit());

        var roles = Dpcm(result)["samples"]!.AsArray().ToDictionary(sample => sample!["address"]!.GetValue<string>(), sample => sample!["role"]!.GetValue<string>());
        Assert.Equal(new Dictionary<string, string> { ["0xC000"] = "kick", ["0xC180"] = "snare", ["0xC280"] = "hat" }, roles);
    }

    [Fact]
    public void A_bolder_strategy_never_plays_fewer_hits()
    {
        var placed = new[] { DpcmStrategy.Off, DpcmStrategy.Safe, DpcmStrategy.Balanced, DpcmStrategy.Full }
            .Select(strategy => Dpcm(ToGameBoy(DemoSongs.DrumKit(), new ConversionOptions { Dpcm = strategy }))["placed"]!.GetValue<int>())
            .ToArray();

        Assert.Equal(0, placed[0]);
        Assert.True(placed[1] > 0, "the safe strategy plays drums");
        Assert.Equal(placed.Order(), placed);
    }

    [Fact]
    public void The_safe_strategy_only_ever_borrows_the_noise_channel()
    {
        var result = ToGameBoy(DemoSongs.DrumKit(), new ConversionOptions { Dpcm = DpcmStrategy.Safe });

        Assert.Equal(["ch4"], Dpcm(result)["per_host"]!.AsObject().Select(entry => entry.Key));
    }

    [Fact]
    public void Re_synthesised_drums_sound_where_the_noise_channel_was_free()
    {
        // The drum kit song plays no NES noise at all, so every noise frame is a drum.
        var off = ScoreDecoder.Decode(SoundChip.GameBoyDmg, ToGameBoy(DemoSongs.DrumKit()).Vgm);
        var safe = ScoreDecoder.Decode(SoundChip.GameBoyDmg, ToGameBoy(DemoSongs.DrumKit(), new ConversionOptions { Dpcm = DpcmStrategy.Safe }).Vgm);

        Assert.DoesNotContain(off, frame => frame[3].Playing);
        Assert.True(safe[1][3].Playing, "the first kick sounds on the frame it was triggered");
    }

    [Fact]
    public void A_borrowed_square_is_handed_back_with_its_sweep_cleared()
    {
        var result = ToGameBoy(DemoSongs.DrumKit(), new ConversionOptions { Dpcm = DpcmStrategy.Full });

        Assert.Equal(0x00, TestVgm.Writes(result.Vgm).Last(write => write.Register == NR10).Value);
    }

    [Fact]
    public void A_sample_played_at_many_rates_is_recognised_as_an_instrument_and_played_on_the_wave_channel()
    {
        var result = ToGameBoy(DemoSongs.SampledBass(), new ConversionOptions { Dpcm = DpcmStrategy.Balanced });

        Assert.All(Dpcm(result)["samples"]!.AsArray(), sample => Assert.Equal("tonal", sample!["role"]!.GetValue<string>()));
        var hosts = Dpcm(result)["per_host"]!.AsObject();
        Assert.True(hosts["ch3"]!.GetValue<int>() > Dpcm(result)["placed"]!.GetValue<int>() / 2, hosts.ToJsonString());
    }

    [Fact]
    public void A_sampled_bass_is_left_out_by_the_strategy_that_never_touches_the_melody()
    {
        var result = ToGameBoy(DemoSongs.SampledBass(), new ConversionOptions { Dpcm = DpcmStrategy.Safe });

        Assert.Equal(0, Dpcm(result)["placed"]!.GetValue<int>());
    }

    [Fact]
    public void A_role_forced_by_hand_reaches_the_report()
    {
        var result = ToGameBoy(
            DemoSongs.DrumKit(),
            new ConversionOptions { Dpcm = DpcmStrategy.Safe, DpcmRoles = new Dictionary<int, DpcmRole> { [0xC000] = DpcmRole.Snare } });

        var kick = Dpcm(result)["samples"]!.AsArray().Single(sample => sample!["address"]!.GetValue<string>() == "0xC000")!;
        Assert.Equal("snare", kick["role"]!.GetValue<string>());
        Assert.True(kick["overridden"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(0xFF)] // the DAC walked one way to a new level
    [InlineData(0x55)] // encoded silence: the level only dithers by one step
    public void A_sample_that_is_not_a_sound_is_never_played_as_a_drum(int fill)
    {
        var rom = Enumerable.Repeat((byte)fill, 65).ToArray();
        var source = NesWithSample(rom, [(0x4015, 0x0F)], [(0x4010, 0x0F), (0x4012, 0x00), (0x4013, 0x04), (0x4015, 0x1F)], [], [], [], []);

        var result = ToGameBoy(source, new ConversionOptions { Dpcm = DpcmStrategy.Full });

        Assert.Equal("artifact", Dpcm(result)["samples"]![0]!["role"]!.GetValue<string>());
        Assert.Equal(0, Dpcm(result)["placed"]!.GetValue<int>());
        Assert.True(result.IsSilent);
    }

    private static ConversionResult ToGameBoy(byte[] source, ConversionOptions? options = null) =>
        VgmConverter.Convert(source, SoundChip.GameBoyDmg, options);

    private static JsonObject Dpcm(ConversionResult result) => result.Report["dpcm"]!.AsObject();

    private static byte[] Song(string name) => name switch
    {
        "four-channel" => DemoSongs.FourChannel(),
        "drum-kit" => DemoSongs.DrumKit(),
        _ => DemoSongs.SampledBass(),
    };

    // A 2A03 log with sample data at $C000, as a 0xC2 data block ahead of the first frame.
    private static byte[] NesWithSample(byte[] rom, params (int Address, int Value)[][] frames)
    {
        var plain = TestVgm.Nes(frames);
        byte[] block = [0x67, 0x66, 0xC2, .. BitConverter.GetBytes((uint)(rom.Length + 2)), 0x00, 0xC0, .. rom];
        return [.. plain[..0x100], .. block, .. plain[0x100..]];
    }
}
