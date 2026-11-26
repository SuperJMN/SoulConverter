using SoulConverter.Tests.Support;

namespace SoulConverter.Tests;

/// <summary>What a DMG or SN76489 log becomes on the NES 2A03.</summary>
public sealed class NesTargetTests
{
    private static readonly (int, int)[] PowerOn = [(0xFF26, 0x80), (0xFF24, 0x77), (0xFF25, 0xFF)];

    [Fact]
    public void The_channels_are_enabled_before_anything_else_is_written()
    {
        var result = ToNes(TestVgm.Dmg([.. PowerOn, (0xFF11, 0x80), (0xFF12, 0xF0), (0xFF13, 0xD6), (0xFF14, 0x86)], []));

        Assert.Equal(new Write(0, 0x15, 0x0F), TestVgm.Writes(result.Vgm)[0]);
        Assert.Equal(1789773u, TestVgm.Clock(result.Vgm, 0x84));
        Assert.Equal(0u, TestVgm.Clock(result.Vgm, 0x80));
    }

    [Fact]
    public void A_game_boy_length_counter_ends_the_note()
    {
        // Square 1 with a length of 16 steps of 1/256 s: 62.5 ms, under four frames.
        var result = ToNes(TestVgm.Dmg(
            [.. PowerOn, (0xFF11, 0x80 | (64 - 16)), (0xFF12, 0xF0), (0xFF13, 0xD6), (0xFF14, 0xC6)],
            [], [], [], [], [], [], []));

        var pulse = ScoreDecoder.Decode(SoundChip.Nes2A03, result.Vgm).Select(frame => frame[0].Playing).ToArray();
        Assert.True(pulse[1], "the note plays");
        Assert.False(pulse[5], "the length counter stopped it");
    }

    [Fact]
    public void A_game_boy_sweep_arrives_as_pitch_changes()
    {
        // Square 1 sweeping up: every 1/128 s the period grows by 1/128 of itself.
        var result = ToNes(TestVgm.Dmg(
            [.. PowerOn, (0xFF10, 0x17), (0xFF11, 0x80), (0xFF12, 0xF0), (0xFF13, 0x00), (0xFF14, 0x84)],
            [], [], [], [], [], [], []));

        var pitch = ScoreDecoder.Decode(SoundChip.Nes2A03, result.Vgm).Select(frame => frame[0].Hz).ToArray();
        Assert.True(pitch[6] > pitch[0] * 1.05, $"{pitch[0]:0.0} Hz then {pitch[6]:0.0} Hz");
    }

    [Fact]
    public void A_game_boy_envelope_arrives_as_volume_steps()
    {
        // Square 1 fading from 15 one step every 1/64 s: one step inside the first frame, and silent
        // after a quarter of a second.
        var frames = Enumerable.Repeat(Array.Empty<(int, int)>(), 20).ToArray();
        var result = ToNes(TestVgm.Dmg([[.. PowerOn, (0xFF11, 0x80), (0xFF12, 0xF1), (0xFF13, 0xD6), (0xFF14, 0x86)], .. frames]));

        var volumes = ScoreDecoder.Decode(SoundChip.Nes2A03, result.Vgm).Select(frame => frame[0].Volume).ToArray();
        Assert.Equal(14, volumes[0]);
        Assert.InRange(volumes[7], 6, 9);
        Assert.Equal(0, volumes[16]);
        Assert.Equal(volumes.OrderDescending(), volumes);
    }

    [Fact]
    public void A_wave_channel_that_rests_comes_back_on_the_same_note()
    {
        // The wave channel is turned down for half a second and up again, without a trigger: the
        // triangle's counters ran out meanwhile and have to be loaded again.
        var rest = Enumerable.Repeat(Array.Empty<(int, int)>(), 30).ToArray();
        var result = ToNes(TestVgm.Dmg(
        [
            [.. PowerOn, (0xFF1A, 0x80), (0xFF1C, 0x20), (0xFF1D, 0xD6), (0xFF1E, 0x86)],
            [], [],
            [(0xFF1C, 0x00)],
            .. rest,
            [(0xFF1C, 0x20)],
            [], [], [],
        ]));

        var triangle = ScoreDecoder.Decode(SoundChip.Nes2A03, result.Vgm).Select(frame => frame[2].Playing).ToArray();
        Assert.True(triangle[1], "the first note plays");
        Assert.False(triangle[20], "the rest is silent");
        Assert.True(triangle[36], "the note comes back");
    }

    [Fact]
    public void A_low_square_note_is_not_muted_by_the_sweep_unit()
    {
        // Square 1 at 70 Hz: a 2A03 timer above $400, which the sweep unit silences unless it negates.
        var period = (int)Math.Round(2048 - (131072 / 70.0));
        var result = ToNes(TestVgm.Dmg(
            [.. PowerOn, (0xFF11, 0x80), (0xFF12, 0xF0), (0xFF13, period & 0xFF), (0xFF14, 0x80 | (period >> 8))],
            [], []));

        var pulse = ScoreDecoder.Decode(SoundChip.Nes2A03, result.Vgm)[1][0];
        Assert.True(pulse.Playing);
        Assert.InRange(pulse.Hz, 69.0, 71.0);
    }

    [Fact]
    public void A_voice_the_source_never_addressed_is_not_written()
    {
        var result = ToNes(TestVgm.Dmg([(0xFF26, 0x80), (0xFF16, 0x80), (0xFF17, 0xF0), (0xFF18, 0xD6), (0xFF19, 0x86)], []));

        Assert.Equal([0x04, 0x05, 0x06, 0x07, 0x15], TestVgm.Writes(result.Vgm).Select(write => write.Register).Distinct().Order());
    }

    [Fact]
    public void Turning_the_game_boy_apu_off_turns_the_nes_channels_off()
    {
        var result = ToNes(TestVgm.Dmg(
            [.. PowerOn, (0xFF11, 0x80), (0xFF12, 0xF0), (0xFF13, 0xD6), (0xFF14, 0x86)],
            [],
            [(0xFF26, 0x00)],
            []));

        Assert.Equal(new Write(2 * TestVgm.FrameSamples, 0x15, 0x00), TestVgm.Writes(result.Vgm)[^1]);
    }

    [Fact]
    public void A_write_keeps_its_own_sample_position()
    {
        // A note 300 samples into the log, which no 60 Hz grid has a line for.
        byte[] commands = [0xB3, 0x16, 0x80, 0x61, 0x2C, 0x01, 0xB3, 0x02, 0xF0, 0xB3, 0x04, 0x87, 0x62, 0x66];
        var source = TestVgm.Raw(0x80, commands);

        var result = ToNes(source);

        Assert.Contains(new Write(300, 0x00, 0x3F), TestVgm.Writes(result.Vgm));
    }

    [Fact]
    public void The_loop_point_keeps_its_sample()
    {
        var result = ToNes(TestVgm.DmgLooping(
            2,
            [.. PowerOn, (0xFF11, 0x80), (0xFF12, 0xF0), (0xFF13, 0xD6), (0xFF14, 0x86)],
            [], [], [], []));

        Assert.Equal(2 * TestVgm.FrameSamples, TestVgm.LoopSample(result.Vgm));
        Assert.Equal(5 * TestVgm.FrameSamples, TestVgm.TotalSamples(result.Vgm));
    }

    [Fact]
    public void The_wave_table_is_reported_as_lost()
    {
        var result = ToNes(TestVgm.Dmg([.. PowerOn, (0xFF1A, 0x80), (0xFF1C, 0x20), (0xFF1D, 0xD6), (0xFF1E, 0x86)], []));

        Assert.Contains(result.Warnings, warning => warning.Code == WarningCodes.Timbre);
    }

    [Fact]
    public void Periodic_psg_noise_becomes_short_mode_noise_and_is_reported()
    {
        // Noise control %011: periodic, rate 3 is not used here; rate 0, attenuation 0.
        var result = ToNes(TestVgm.Sn([0xE0, 0xF0], []));

        var mode = TestVgm.Writes(result.Vgm).Single(write => write.Register == 0x0E).Value & 0x80;
        Assert.Equal(0x80, mode);
        Assert.Contains(result.Warnings, warning => warning.Code == WarningCodes.PeriodicNoise);
    }

    [Fact]
    public void A_psg_noise_clocked_by_the_third_tone_follows_its_pitch()
    {
        // Tone 2 at period $100 (437 Hz), silent, clocking white noise (control %111).
        var result = ToNes(TestVgm.Sn([0xC0, 0x10, 0xE7, 0xF0], []));

        var noise = ScoreDecoder.Decode(SoundChip.Nes2A03, result.Vgm)[0][3];
        Assert.True(noise.Playing);
        Assert.InRange(noise.Hz, 437.0 / 2, 437.0 * 2);
    }

    [Fact]
    public void A_game_gear_channel_routed_to_one_side_plays_and_the_panning_is_reported()
    {
        // Command 0x4F is the Game Gear stereo byte: tone 0 to the right speaker only.
        byte[] commands = [0x4F, 0x01, 0x50, 0x8E, 0x50, 0x0F, 0x50, 0x90, 0x62, 0x62, 0x66];
        var result = ToNes(TestVgm.Raw(0x0C, commands));

        Assert.True(ScoreDecoder.Decode(SoundChip.Nes2A03, result.Vgm)[0][0].Playing);
        Assert.Contains(result.Warnings, warning => warning.Code == WarningCodes.Stereo);
    }

    private static ConversionResult ToNes(byte[] source) => VgmConverter.Convert(source, SoundChip.Nes2A03);
}
