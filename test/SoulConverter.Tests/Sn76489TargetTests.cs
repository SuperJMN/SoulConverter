using SoulConverter.Tests.Support;

namespace SoulConverter.Tests;

/// <summary>What a 2A03 or DMG log becomes on the SN76489 of the Master System and Game Gear.</summary>
public sealed class Sn76489TargetTests
{
    [Fact]
    public void A_nes_pulse_becomes_a_psg_tone_at_its_frequency_and_volume()
    {
        // Pulse 1: constant volume 15, 50% duty, timer 253 (440 Hz).
        var result = ToPsg(TestVgm.Nes(
            [(0x4015, 0x01), (0x4000, 0xBF), (0x4002, 0xFD), (0x4003, 0x08)],
            []));

        var states = Psg.States(result.Vgm);
        Assert.Equal(Psg.Period(TestVgm.NesClock / (16.0 * 254)), states[0][0]);
        Assert.Equal(0, states[0][1]);
        Assert.Equal(states[0], states[1]);
        Assert.DoesNotContain(result.Warnings, warning => warning.Code == WarningCodes.Duty);
    }

    [Fact]
    public void A_nes_envelope_decays_and_the_length_counter_ends_the_note()
    {
        // Pulse 2: a decaying envelope (period 0: one step per quarter frame) with a long length,
        // then a short note: length index 3 (2 half frames).
        var result = ToPsg(TestVgm.Nes(
            [(0x4015, 0x02), (0x4004, 0x80), (0x4006, 0xFD), (0x4007, 0x08)],
            [],
            [],
            [(0x4007, 0x18)],
            [],
            [],
            []));

        var attenuations = Psg.States(result.Vgm).Select(state => state[3]).ToArray();
        Assert.True(attenuations[1] < attenuations[2] && attenuations[2] < attenuations[3], $"attenuations {string.Join(',', attenuations)}");
        Assert.Equal(15, attenuations[6]);
    }

    [Fact]
    public void The_nes_triangle_and_noise_take_the_third_tone_and_the_noise_channel()
    {
        var result = ToPsg(TestVgm.Nes(
            [(0x4015, 0x0C), (0x4008, 0xFF), (0x400A, 0x7E), (0x400B, 0x08), (0x400C, 0x3A), (0x400E, 0x83), (0x400F, 0x08)],
            [],
            []));

        var state = Psg.States(result.Vgm)[1];
        Assert.Equal(Psg.Period(TestVgm.NesClock / (32.0 * 127)), state[4]);
        Assert.Equal(Psg.Attenuation(12), state[5]);  // the triangle has no volume; it plays at 12
        Assert.Equal(0, state[6] & 0x04);             // short mode: periodic noise
        Assert.Equal(Psg.Attenuation(10), state[7]);  // constant volume 10
    }

    [Fact]
    public void What_the_psg_cannot_play_is_reported_once_each()
    {
        // A 12.5% pulse below the PSG's range, over two frames, while a DPCM sample plays.
        var result = ToPsg(TestVgm.Nes(
            [(0x4010, 0x0F), (0x4013, 0x10), (0x4015, 0x11), (0x4000, 0x3F), (0x4001, 0x08), (0x4002, 0xFF), (0x4003, 0x0F)],
            [(0x4000, 0x7F)],
            []));

        Assert.Single(result.Warnings, warning => warning.Code == WarningCodes.DpcmDropped);
        Assert.Single(result.Warnings, warning => warning.Code == WarningCodes.Duty);
        Assert.Single(result.Warnings, warning => warning.Code == WarningCodes.Transposed);
    }

    [Fact]
    public void Dmc_registers_without_a_sample_playing_lose_nothing()
    {
        var result = ToPsg(TestVgm.Nes(
            [(0x4015, 0x11), (0x4000, 0xBF), (0x4002, 0xFD), (0x4003, 0x08), (0x4010, 0x00)],
            [(0x4010, 0x00)]));

        Assert.DoesNotContain(result.Warnings, warning => warning.Code == WarningCodes.DpcmDropped);
    }

    [Fact]
    public void Writes_that_only_set_up_or_silence_a_channel_do_not_claim_it()
    {
        // Pulse 1, the triangle and noise are silenced; only pulse 2 plays.
        var result = ToPsg(TestVgm.Nes(
            [(0x4015, 0x0F), (0x4000, 0x30), (0x4008, 0x80), (0x400C, 0x30), (0x4004, 0xBF), (0x4006, 0xFD), (0x4007, 0x08)],
            []));

        Assert.Equal([1], Psg.ChannelsWritten(result.Vgm));
        Assert.DoesNotContain(result.Warnings, warning => warning.Code is WarningCodes.Duty or WarningCodes.Timbre);
    }

    [Fact]
    public void A_converted_effect_writes_only_the_channels_its_source_uses()
    {
        var result = ToPsg(TestVgm.Nes([(0x4015, 0x08), (0x400C, 0x3F), (0x400E, 0x04), (0x400F, 0x08)], []));

        Assert.Equal([3], Psg.ChannelsWritten(result.Vgm));
    }

    [Fact]
    public void The_nes_five_step_frame_counter_clocks_envelopes_more_slowly()
    {
        // A decaying envelope steps once per quarter frame: 240 times a second in 4-step mode, 192
        // in 5-step mode, so after a few frames the 5-step one is louder.
        (int, int)[] note = [(0x4015, 0x01), (0x4000, 0x80), (0x4001, 0x08), (0x4002, 0xFD), (0x4003, 0x08)];
        var fourStep = Psg.States(ToPsg(TestVgm.Nes(note, [], [], [])).Vgm);
        var fiveStep = Psg.States(ToPsg(TestVgm.Nes([(0x4017, 0x80), .. note], [], [], [])).Vgm);

        Assert.True(fiveStep[3][1] < fourStep[3][1], $"5-step attenuation {fiveStep[3][1]}, 4-step {fourStep[3][1]}");
    }

    [Fact]
    public void A_low_nes_note_rises_by_octaves_to_fit_the_psg_period()
    {
        // Timer $6FF: about 62.4 Hz, under the PSG's 109.3 Hz floor; one octave up fits. The sweep
        // negates so its target stays in range and the note plays, as games set it.
        var result = ToPsg(TestVgm.Nes([(0x4015, 0x01), (0x4000, 0xBF), (0x4001, 0x08), (0x4002, 0xFF), (0x4003, 0x0E)]));

        var octaveUp = 2 * TestVgm.NesClock / (16.0 * 0x700);
        Assert.InRange(TestVgm.PsgClock / (32 * Psg.States(result.Vgm)[0][0]), octaveUp * 0.99, octaveUp * 1.01);
        Assert.Contains(result.Warnings, warning => warning.Code == WarningCodes.Transposed);
    }

    [Fact]
    public void A_low_note_stays_on_the_lowest_period_when_transposing_is_off()
    {
        var result = VgmConverter.Convert(
            TestVgm.Nes([(0x4015, 0x01), (0x4000, 0xBF), (0x4001, 0x08), (0x4002, 0xFF), (0x4003, 0x0E)]),
            SoundChip.Sn76489,
            new ConversionOptions { TransposeOutOfRange = false });

        Assert.Equal(0x3FF, Psg.States(result.Vgm)[0][0]);
        Assert.DoesNotContain(result.Warnings, warning => warning.Code == WarningCodes.Transposed);
    }

    [Fact]
    public void A_game_boy_square_wave_and_noise_take_the_psg_channels()
    {
        var result = ToPsg(TestVgm.Dmg(
            [
                (0xFF26, 0x80), (0xFF25, 0xFF),
                (0xFF11, 0x80), (0xFF12, 0xF0), (0xFF13, 0xD6), (0xFF14, 0x86),  // square 1: 1750 (439.8 Hz), volume 15
                (0xFF1A, 0x80), (0xFF1C, 0x40), (0xFF1D, 0xD6), (0xFF1E, 0x86),  // wave: 50% level
                (0xFF21, 0xA0), (0xFF22, 0x31), (0xFF23, 0x80),                   // noise: volume 10, shift 3, divisor 1
            ],
            []));

        var state = Psg.States(result.Vgm)[0];
        Assert.Equal(Psg.Period(131072.0 / 298), state[0]);
        Assert.Equal(0, state[1]);
        Assert.Equal(Psg.Period(65536.0 / 298), state[4]);
        Assert.Equal(Psg.Attenuation(7), state[5]);
        Assert.Equal(0x04, state[6] & 0x04); // 15-bit LFSR: white noise
        Assert.Equal(Psg.Attenuation(10), state[7]);
        Assert.Contains(result.Warnings, warning => warning.Code == WarningCodes.Timbre && warning.Message.Contains("wave channel", StringComparison.Ordinal));
    }

    [Fact]
    public void The_game_boy_master_volume_scales_every_channel()
    {
        var result = ToPsg(TestVgm.Dmg(
            [(0xFF26, 0x80), (0xFF24, 0x33), (0xFF25, 0xFF), (0xFF11, 0x80), (0xFF12, 0xF0), (0xFF13, 0xD6), (0xFF14, 0x86)]));

        Assert.Equal(Psg.Attenuation(8), Psg.States(result.Vgm)[0][1]); // volume 15 at half master volume
    }

    [Fact]
    public void Turning_the_game_boy_apu_off_clears_its_channels()
    {
        var result = ToPsg(TestVgm.Dmg(
            [(0xFF26, 0x80), (0xFF25, 0xFF), (0xFF24, 0x77), (0xFF11, 0x80), (0xFF12, 0xF0), (0xFF13, 0xD6), (0xFF14, 0x86)],
            [(0xFF26, 0x00), (0xFF26, 0x80), (0xFF14, 0x86)]));

        var states = Psg.States(result.Vgm);
        Assert.Equal(0, states[0][1]);
        Assert.Equal(15, states[1][1]); // the retrigger finds NR12, NR51 and NR50 cleared
    }

    [Fact]
    public void A_game_boy_channel_routed_to_neither_output_is_silent()
    {
        var result = ToPsg(TestVgm.Dmg(
            [(0xFF26, 0x80), (0xFF25, 0xDD), (0xFF16, 0x80), (0xFF17, 0xF0), (0xFF18, 0xD6), (0xFF19, 0x86)]));

        Assert.Equal(15, Psg.States(result.Vgm)[0][3]); // square 2 is channel 1, routed nowhere
        Assert.True(result.IsSilent);
    }

    [Fact]
    public void A_game_boy_channel_on_one_side_only_plays_and_the_lost_panning_is_reported()
    {
        var result = ToPsg(TestVgm.Dmg(
            [(0xFF26, 0x80), (0xFF25, 0x01), (0xFF11, 0x80), (0xFF12, 0xF0), (0xFF13, 0xD6), (0xFF14, 0x86)]));

        Assert.Equal(0, Psg.States(result.Vgm)[0][1]);
        Assert.Contains(result.Warnings, warning => warning.Code == WarningCodes.Stereo);
    }

    [Fact]
    public void A_held_note_is_written_once_not_every_frame()
    {
        var result = ToPsg(TestVgm.Nes(
            [(0x4015, 0x01), (0x4000, 0xBF), (0x4002, 0xFD), (0x4003, 0x08)],
            [], [], [], [], [], [], []));

        Assert.Equal(3, TestVgm.Writes(result.Vgm).Count); // the period's two bytes and the attenuation
    }

    [Fact]
    public void A_noise_restart_rewrites_the_noise_control_to_restart_the_psg_generator()
    {
        (int, int)[] hit = [(0x400C, 0x3F), (0x400E, 0x04), (0x400F, 0x08)];
        var result = ToPsg(TestVgm.Nes([(0x4015, 0x08), .. hit], [], hit, []));

        var controlWrites = TestVgm.Writes(result.Vgm).Where(write => (write.Value & 0xF0) == 0xE0).Select(write => write.Sample);
        Assert.Equal([0, 2 * TestVgm.FrameSamples], controlWrites);
    }

    [Fact]
    public void The_loop_point_lands_on_the_same_frame()
    {
        var result = ToPsg(TestVgm.NesLooping(
            2,
            [(0x4015, 0x01), (0x4000, 0xBF), (0x4002, 0xFD), (0x4003, 0x08)],
            [],
            [(0x4000, 0xB8)],
            []));

        Assert.Equal(2 * TestVgm.FrameSamples, TestVgm.LoopSample(result.Vgm));
    }

    private static ConversionResult ToPsg(byte[] source) => VgmConverter.Convert(source, SoundChip.Sn76489);
}
