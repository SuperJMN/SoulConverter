import math
import unittest

from vgm_nes2gb import gameboy as gb
from vgm_nes2gb import nes_apu, nes_to_gb
from vgm_nes2gb.nes_apu import NOISE_PERIOD_TABLE, ApuTick, ApuTimeline, NoiseState, PulseState, TriangleState

NTSC_CPU_HZ = 1789772


class SquarePitchTests(unittest.TestCase):
    def test_pulses_stay_in_tune_across_the_musical_range(self):
        # The Game Boy's period resolution coarsens with pitch, exactly as the
        # NES's does; below ~2 kHz the error is far under a just-noticeable
        # difference (about 20 cents for a sustained tone).
        worst = 0.0
        for period in range(8, 0x800):
            source = NTSC_CPU_HZ / (16.0 * (period + 1))
            if not gb.SQUARE_MIN_HZ <= source <= 2000.0:
                continue
            gb_period, octaves = gb.square_period(source)
            self.assertEqual(octaves, 0)
            worst = max(
                worst, abs(1200.0 * math.log2(gb.square_frequency(gb_period) / source))
            )
        self.assertLess(worst, 12.0)

    def test_pulses_never_drift_beyond_a_semitone_even_at_the_extreme_top(self):
        worst = 0.0
        for period in range(8, 0x800):
            source = NTSC_CPU_HZ / (16.0 * (period + 1))
            if source < gb.SQUARE_MIN_HZ:
                continue
            gb_period, _ = gb.square_period(source)
            worst = max(
                worst, abs(1200.0 * math.log2(gb.square_frequency(gb_period) / source))
            )
        self.assertLess(worst, 100.0)

    def test_triangle_bass_stays_in_tune_where_it_is_actually_used(self):
        worst = 0.0
        for period in range(2, 0x800):
            source = NTSC_CPU_HZ / (32.0 * (period + 1))
            if not gb.WAVE_MIN_HZ <= source <= 1000.0:
                continue
            gb_period, octaves = gb.wave_period(source)
            self.assertEqual(octaves, 0)
            worst = max(
                worst, abs(1200.0 * math.log2(gb.wave_frequency(gb_period) / source))
            )
        self.assertLess(worst, 12.0)

    def test_transposes_notes_below_the_game_boy_floor(self):
        period, octaves = gb.square_period(55.0)
        self.assertEqual(octaves, 1)
        self.assertAlmostEqual(gb.square_frequency(period), 110.0, delta=1.0)

    def test_clamping_keeps_the_note_in_range_without_transposing(self):
        period, octaves = gb.square_period(55.0, allow_transpose=False)
        self.assertEqual(octaves, 0)
        self.assertEqual(period, 0)
        self.assertAlmostEqual(gb.square_frequency(period), gb.SQUARE_MIN_HZ, places=6)

    def test_wave_channel_reaches_an_octave_lower_than_the_squares(self):
        self.assertAlmostEqual(gb.WAVE_MIN_HZ * 2, gb.SQUARE_MIN_HZ, places=6)
        period, octaves = gb.wave_period(40.0)
        self.assertEqual(octaves, 0)
        self.assertAlmostEqual(gb.wave_frequency(period), 40.0, delta=0.5)

    def test_transposes_deep_triangle_bass(self):
        _, octaves = gb.wave_period(27.5)
        self.assertEqual(octaves, 1)


class NoiseMappingTests(unittest.TestCase):
    def test_every_nes_noise_period_finds_a_close_game_boy_match(self):
        worst = 0.0
        for index, period in enumerate(NOISE_PERIOD_TABLE):
            shift, divisor, cents = gb.nearest_noise(NTSC_CPU_HZ / period)
            self.assertLessEqual(shift, gb.NOISE_MAX_SHIFT)
            self.assertLessEqual(divisor, 7)
            worst = max(worst, abs(cents))
        self.assertLess(worst, 350.0)

    def test_mapping_preserves_the_ordering_of_nes_noise_pitches(self):
        rates = [
            gb.noise_frequency(*gb.nearest_noise(NTSC_CPU_HZ / period)[:2])
            for period in NOISE_PERIOD_TABLE
        ]
        self.assertEqual(rates, sorted(rates, reverse=True))

    def test_zero_rate_is_handled(self):
        self.assertEqual(gb.nearest_noise(0.0), (0, 0, 0.0))


class WaveRamTests(unittest.TestCase):
    def test_packs_thirty_two_nibbles_into_sixteen_bytes(self):
        packed = gb.pack_wave(gb.TRIANGLE_WAVE_NIBBLES)
        self.assertEqual(len(packed), 16)
        self.assertEqual(packed[0], 0xFE)
        self.assertEqual(packed[-1], 0xEF)

    def test_rejects_a_wrongly_sized_table(self):
        with self.assertRaises(ValueError):
            gb.pack_wave([0] * 31)

    def test_rejects_out_of_range_nibbles(self):
        with self.assertRaises(ValueError):
            gb.pack_wave([16] * 32)


def timeline_from(ticks: list[ApuTick]) -> ApuTimeline:
    return ApuTimeline(
        tick_hz=60.0,
        cpu_clock_hz=NTSC_CPU_HZ,
        ticks=ticks,
        loop_tick=None,
        total_samples=735 * len(ticks),
    )


def pulse_tick(index: int, **kwargs) -> ApuTick:
    return ApuTick(
        index=index,
        sample=index * 735,
        pulse1=PulseState(**kwargs),
        pulse2=PulseState(),
        triangle=TriangleState(),
        noise=NoiseState(),
    )


def registers_of(tick) -> dict[int, int]:
    return dict(tick.writes)


class ChannelMappingTests(unittest.TestCase):
    def test_duty_maps_one_to_one(self):
        for duty in range(4):
            timeline = timeline_from(
                [pulse_tick(0, playing=True, frequency_hz=440.0, volume=10, duty=duty)]
            )
            written = registers_of(nes_to_gb.convert(timeline).ticks[0])
            self.assertEqual(written[gb.NR11] >> 6, duty)

    def test_note_on_triggers_the_channel(self):
        timeline = timeline_from(
            [pulse_tick(0, playing=True, frequency_hz=440.0, volume=10, duty=2)]
        )
        written = registers_of(nes_to_gb.convert(timeline).ticks[0])
        self.assertTrue(written[gb.NR14] & 0x80)
        self.assertEqual(written[gb.NR12], 10 << 4)

    def test_note_off_powers_the_dac_down(self):
        timeline = timeline_from([
            pulse_tick(0, playing=True, frequency_hz=440.0, volume=10, duty=2),
            pulse_tick(1),
        ])
        written = registers_of(nes_to_gb.convert(timeline).ticks[1])
        self.assertEqual(written[gb.NR12], 0x00)

    def test_a_held_note_is_not_retriggered(self):
        state = dict(playing=True, frequency_hz=440.0, volume=10, duty=2)
        timeline = timeline_from([pulse_tick(0, **state), pulse_tick(1, **state)])
        self.assertEqual(nes_to_gb.convert(timeline).ticks[1].writes, [])

    def test_a_pitch_slide_updates_frequency_without_retriggering(self):
        timeline = timeline_from([
            pulse_tick(0, playing=True, frequency_hz=440.0, volume=10, duty=2),
            pulse_tick(1, playing=True, frequency_hz=466.0, volume=10, duty=2),
        ])
        written = registers_of(nes_to_gb.convert(timeline).ticks[1])
        self.assertIn(gb.NR13, written)
        self.assertNotIn(gb.NR14, written)  # unchanged high bits, no trigger

    def test_retrigger_mode_reapplies_volume_changes(self):
        timeline = timeline_from([
            pulse_tick(0, playing=True, frequency_hz=440.0, volume=10, duty=2),
            pulse_tick(1, playing=True, frequency_hz=440.0, volume=6, duty=2),
        ])
        written = registers_of(
            nes_to_gb.convert(
                timeline, volume_mode=nes_to_gb.VOLUME_MODE_RETRIGGER
            ).ticks[1]
        )
        self.assertEqual(written[gb.NR12], 6 << 4)
        self.assertTrue(written[gb.NR14] & 0x80)

    def test_envelope_mode_maps_a_decay_onto_the_game_boy_envelope(self):
        timeline = timeline_from([
            pulse_tick(
                0, playing=True, frequency_hz=440.0, volume=15, duty=2,
                constant_volume=False, envelope_period=3,
            )
        ])
        written = registers_of(
            nes_to_gb.convert(
                timeline, volume_mode=nes_to_gb.VOLUME_MODE_ENVELOPE
            ).ticks[0]
        )
        self.assertEqual(written[gb.NR12] >> 4, 15)
        self.assertEqual(written[gb.NR12] & 0x08, 0)  # counts down
        self.assertEqual(written[gb.NR12] & 0x07, 1)  # 4/240 s ~= 1/64 s

    def test_envelope_mode_does_not_retrigger_while_a_note_decays(self):
        state = dict(
            playing=True, frequency_hz=440.0, duty=2,
            constant_volume=False, envelope_period=3,
        )
        timeline = timeline_from([
            pulse_tick(0, volume=15, **state),
            pulse_tick(1, volume=9, **state),
        ])
        self.assertEqual(
            nes_to_gb.convert(
                timeline, volume_mode=nes_to_gb.VOLUME_MODE_ENVELOPE
            ).ticks[1].writes,
            [],
        )

    def test_wave_ram_is_loaded_once_before_the_first_note(self):
        timeline = timeline_from([pulse_tick(0)])
        writes = nes_to_gb.convert(timeline).ticks[0].writes
        wave_registers = [r for r, _ in writes if r >= gb.WAVE_RAM_BASE]
        self.assertEqual(len(wave_registers), gb.WAVE_RAM_SIZE)
        power_index = [r for r, _ in writes].index(gb.NR52)
        self.assertEqual(power_index, 0)

    def test_unknown_volume_mode_is_rejected(self):
        with self.assertRaises(ValueError):
            nes_to_gb.convert(timeline_from([pulse_tick(0)]), volume_mode="nope")


class TranspositionReportingTests(unittest.TestCase):
    def test_transposed_ticks_are_counted_per_channel(self):
        timeline = timeline_from(
            [pulse_tick(0, playing=True, frequency_hz=55.0, volume=10, duty=0)]
        )
        stats = nes_to_gb.convert(timeline).stats
        self.assertEqual(stats.transposed_ticks["pulse1"], 1)
        self.assertEqual(stats.max_octaves_up["pulse1"], 1)

    def test_no_transposition_is_reported_when_clamping(self):
        timeline = timeline_from(
            [pulse_tick(0, playing=True, frequency_hz=55.0, volume=10, duty=0)]
        )
        stats = nes_to_gb.convert(timeline, allow_transpose=False).stats
        self.assertEqual(stats.transposed_ticks["pulse1"], 0)


class SimulationGridTests(unittest.TestCase):
    def test_tick_rate_must_be_positive(self):
        from vgm_nes2gb import testing, vgm_reader

        stream = vgm_reader.parse_bytes(testing.demo_vgm())
        with self.assertRaises(ValueError):
            nes_apu.simulate(stream, tick_hz=0)


if __name__ == "__main__":
    unittest.main()
