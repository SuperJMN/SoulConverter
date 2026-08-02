import unittest

from vgm_nes2gb.nes_apu import LENGTH_TABLE, NesApu

CPU_HZ = 1789772
QUARTER_BOUNDARIES = (7457, 14913, 22371, 29829)
SEQUENCE_CYCLES = 29830


def quarter_frame_cycle(count: int) -> int:
    """CPU cycle of the ``count``-th quarter frame in 4-step mode (1-based)."""
    return (count - 1) // 4 * SEQUENCE_CYCLES + QUARTER_BOUNDARIES[(count - 1) % 4]


def half_frame_cycle(count: int) -> int:
    """CPU cycle of the ``count``-th half frame in 4-step mode (1-based)."""
    return (count - 1) // 2 * SEQUENCE_CYCLES + (14913 if count % 2 else 29829)


def make_apu() -> NesApu:
    apu = NesApu(CPU_HZ)
    apu.write(0x17, 0x40)  # 4-step sequence, IRQ inhibited
    apu.write(0x15, 0x0F)  # enable pulse 1, pulse 2, triangle, noise
    return apu


def start_pulse1(apu: NesApu, control: int, period: int, length_index: int = 0) -> None:
    apu.write(0x00, control)
    apu.write(0x01, 0x00)
    apu.write(0x02, period & 0xFF)
    apu.write(0x03, (length_index << 3) | (period >> 8))


class EnvelopeTests(unittest.TestCase):
    def test_constant_volume_holds_its_level(self):
        apu = make_apu()
        start_pulse1(apu, control=0x3F, period=200)  # halt + constant volume 15
        for count in range(1, 20):
            apu.run_to_cycle(quarter_frame_cycle(count))
            self.assertEqual(apu.snapshot(count, 0).pulse1.volume, 15)

    def test_decay_falls_from_15_to_0_and_stays(self):
        apu = make_apu()
        start_pulse1(apu, control=0x00, period=200)  # envelope, period 0, no loop
        volumes = []
        for count in range(1, 22):
            apu.run_to_cycle(quarter_frame_cycle(count))
            volumes.append(apu.snapshot(count, 0).pulse1.volume)
        self.assertEqual(volumes[0], 15)
        self.assertEqual(volumes[15], 0)
        self.assertEqual(volumes[-1], 0)
        self.assertEqual(volumes, sorted(volumes, reverse=True))

    def test_looping_envelope_wraps_back_to_15(self):
        apu = make_apu()
        start_pulse1(apu, control=0x20, period=200)
        for count in range(1, 18):
            apu.run_to_cycle(quarter_frame_cycle(count))
        self.assertEqual(apu.snapshot(0, 0).pulse1.volume, 15)


class LengthCounterTests(unittest.TestCase):
    def test_length_counter_silences_the_channel(self):
        apu = make_apu()
        start_pulse1(apu, control=0x1F, period=200, length_index=0)
        self.assertEqual(LENGTH_TABLE[0], 10)
        apu.run_to_cycle(half_frame_cycle(9))
        self.assertTrue(apu.snapshot(0, 0).pulse1.playing)
        apu.run_to_cycle(half_frame_cycle(10))
        self.assertFalse(apu.snapshot(0, 0).pulse1.playing)

    def test_halt_flag_keeps_the_note_sounding(self):
        apu = make_apu()
        start_pulse1(apu, control=0x3F, period=200, length_index=0)
        apu.run_to_cycle(half_frame_cycle(40))
        self.assertTrue(apu.snapshot(0, 0).pulse1.playing)

    def test_disabling_via_4015_clears_the_length_counter(self):
        apu = make_apu()
        start_pulse1(apu, control=0x3F, period=200)
        apu.write(0x15, 0x00)
        self.assertFalse(apu.snapshot(0, 0).pulse1.playing)


class PitchTests(unittest.TestCase):
    def test_pulse_frequency_follows_the_timer_period(self):
        apu = make_apu()
        start_pulse1(apu, control=0x3F, period=253)
        apu.run_to_cycle(quarter_frame_cycle(1))
        expected = CPU_HZ / (16.0 * 254)
        self.assertAlmostEqual(apu.snapshot(0, 0).pulse1.frequency_hz, expected, places=6)

    def test_periods_below_eight_mute_the_pulse(self):
        apu = make_apu()
        start_pulse1(apu, control=0x3F, period=7)
        apu.run_to_cycle(quarter_frame_cycle(1))
        self.assertFalse(apu.snapshot(0, 0).pulse1.playing)

    def test_triangle_sounds_an_octave_below_a_pulse_of_equal_period(self):
        apu = make_apu()
        apu.write(0x08, 0xFF)
        apu.write(0x0A, 200)
        apu.write(0x0B, (0x1F << 3) | 0)
        start_pulse1(apu, control=0x3F, period=200)
        apu.run_to_cycle(quarter_frame_cycle(1))
        tick = apu.snapshot(0, 0)
        self.assertTrue(tick.triangle.playing)
        self.assertAlmostEqual(
            tick.pulse1.frequency_hz / tick.triangle.frequency_hz, 2.0, places=6
        )


class SweepTests(unittest.TestCase):
    def _swept_frequency(self, sweep_register: int) -> tuple[float, float]:
        apu = make_apu()
        start_pulse1(apu, control=0x3F, period=400)
        apu.write(0x01, sweep_register)
        apu.run_to_cycle(quarter_frame_cycle(1))
        before = apu.snapshot(0, 0).pulse1.frequency_hz
        apu.run_to_cycle(half_frame_cycle(4))
        return before, apu.snapshot(0, 0).pulse1.frequency_hz

    def test_positive_sweep_lowers_the_pitch(self):
        before, after = self._swept_frequency(0x81)  # enable, shift 1, add
        self.assertLess(after, before)

    def test_negated_sweep_raises_the_pitch(self):
        before, after = self._swept_frequency(0x89)  # enable, shift 1, negate
        self.assertGreater(after, before)

    def test_sweep_overflow_mutes_the_channel(self):
        apu = make_apu()
        start_pulse1(apu, control=0x3F, period=0x7F0)
        apu.write(0x01, 0x81)
        apu.run_to_cycle(quarter_frame_cycle(1))
        self.assertFalse(apu.snapshot(0, 0).pulse1.playing)


class TriangleTests(unittest.TestCase):
    def test_linear_counter_silences_the_triangle(self):
        apu = make_apu()
        apu.write(0x08, 0x02)  # control clear, reload value 2
        apu.write(0x0A, 200)
        apu.write(0x0B, (0x1F << 3) | 0)
        apu.run_to_cycle(quarter_frame_cycle(1))
        self.assertTrue(apu.snapshot(0, 0).triangle.playing)
        apu.run_to_cycle(quarter_frame_cycle(3))
        self.assertFalse(apu.snapshot(0, 0).triangle.playing)


class RetriggerTests(unittest.TestCase):
    def test_retrigger_flag_is_reported_once_per_snapshot(self):
        apu = make_apu()
        start_pulse1(apu, control=0x3F, period=200)
        self.assertTrue(apu.snapshot(0, 0).pulse1.retriggered)
        self.assertFalse(apu.snapshot(1, 0).pulse1.retriggered)


class DmcTriggerTests(unittest.TestCase):
    """What a consumer is told about a sample that starts and ends immediately."""

    def test_a_one_byte_sample_still_says_which_sample_started(self):
        """Drivers fire this at boot by enabling the DMC with the length at zero.

        The trigger is real, so hiding the reference behind a None leaves the
        caller an event it cannot look up - and anything that tried would fail
        on a sample it never asked to be told about.
        """
        apu = NesApu(CPU_HZ)
        apu.write(0x17, 0x40)
        apu.write(0x10, 0x0F)
        apu.write(0x12, 0x00)
        apu.write(0x13, 0x00)
        apu.write(0x15, 0x1F)
        apu.run_to_cycle(CPU_HZ // 60)

        state = apu.snapshot(1, CPU_HZ // 60).dmc

        self.assertTrue(state.started)
        self.assertIsNotNone(state.sample)
        self.assertEqual(state.sample.length, 1)
        self.assertFalse(state.playing)


if __name__ == "__main__":
    unittest.main()
