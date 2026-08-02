"""The sample channel: decoding it, recognising it, and playing it anyway.

The classifier is checked against percussion synthesised here rather than
against material from a commercial ROM, so the whole suite stays committable.
The synthetic kit is shaped after what NES rips actually contain - a kick with a
bright transient rather than a studio thud - because that is the population the
rules have to work on.
"""

from __future__ import annotations

import os
import tempfile
import unittest

import numpy as np

from vgm_nes2gb import dpcm, nes_apu, testing, vgm_reader
from vgm_nes2gb.dpcm import Role


def _parse_fixture(payload: bytes) -> vgm_reader.NesCommandStream:
    handle, path = tempfile.mkstemp(suffix=".vgm")
    try:
        os.write(handle, payload)
        os.close(handle)
        return vgm_reader.parse(path)
    finally:
        os.unlink(path)


class DecoderTests(unittest.TestCase):
    def test_a_one_bit_raises_the_level_and_a_zero_bit_lowers_it(self):
        rom = vgm_reader.DpcmRom()
        rom.load(0xC000, bytes([0xFF, 0x00]))
        ref = nes_apu.SampleRef(0xC000, 2, 15)

        decoded = dpcm.decode(rom, ref, initial_level=64)
        levels = decoded.waveform * 64.0 + 64.0

        self.assertEqual(list(levels[:8]), [66, 68, 70, 72, 74, 76, 78, 80])
        self.assertEqual(list(levels[8:]), [78, 76, 74, 72, 70, 68, 66, 64])

    def test_the_level_saturates_instead_of_wrapping(self):
        rom = vgm_reader.DpcmRom()
        rom.load(0xC000, bytes([0xFF] * 64))
        ref = nes_apu.SampleRef(0xC000, 64, 15)

        decoded = dpcm.decode(rom, ref, initial_level=120)
        levels = decoded.waveform * dpcm.DEFAULT_INITIAL_LEVEL + 120

        self.assertEqual(levels.max(), dpcm.LEVEL_MAX - 1)
        self.assertTrue(np.all(np.diff(levels) >= 0))

    def test_bits_are_consumed_least_significant_first(self):
        rom = vgm_reader.DpcmRom()
        rom.load(0xC000, bytes([0x01]))

        levels = dpcm.decode(rom, nes_apu.SampleRef(0xC000, 1, 15)).waveform

        self.assertGreater(levels[0], levels[1])

    def test_missing_rom_pages_decode_as_a_falling_level_rather_than_crashing(self):
        rom = vgm_reader.DpcmRom()
        decoded = dpcm.decode(rom, nes_apu.SampleRef(0xF000, 16, 15))
        self.assertEqual(decoded.waveform.size, 16 * 8)

    def test_a_round_trip_through_the_encoder_recovers_the_shape(self):
        rate = testing.DMC_RATE_HZ[15]
        wanted = np.sin(2 * np.pi * 300.0 * np.arange(2048) / rate) * 0.8
        rom = vgm_reader.DpcmRom()
        rom.load(0xC000, testing.encode_dpcm(wanted))

        got = dpcm.decode(rom, nes_apu.SampleRef(0xC000, 256, 15)).waveform
        overlap = min(got.size, wanted.size)
        correlation = float(
            np.corrcoef(got[:overlap], wanted[:overlap])[0, 1]
        )

        self.assertGreater(correlation, 0.9)


class FeatureTests(unittest.TestCase):
    @staticmethod
    def _features(waveform) -> dpcm.SampleFeatures:
        rom = vgm_reader.DpcmRom()
        payload = testing.encode_dpcm(waveform)
        rom.load(0xC000, payload)
        ref = nes_apu.SampleRef(0xC000, len(payload), 15)
        return dpcm.extract_features(dpcm.decode(rom, ref))

    def test_a_bass_tone_is_not_reported_as_a_treble_one(self):
        """The oversampling trap: a slow wave correlates with itself everywhere."""
        rate = testing.DMC_RATE_HZ[15]
        tone = np.sin(2 * np.pi * 110.0 * np.arange(8192) / rate)

        features = self._features(tone)

        self.assertLess(abs(features.pitch_hz - 110.0), 12.0)
        self.assertGreater(features.pitch_confidence, 0.5)

    def test_a_struck_sound_is_marked_as_percussive_and_a_held_one_is_not(self):
        rate = testing.DMC_RATE_HZ[15]
        held = np.sin(2 * np.pi * 220.0 * np.arange(int(rate * 0.4)) / rate)

        self.assertTrue(self._features(testing.synth_snare()).percussive_decay)
        self.assertFalse(self._features(held).percussive_decay)

    def test_band_energies_follow_where_the_sound_actually_sits(self):
        kick = self._features(testing.synth_kick())
        hat = self._features(testing.synth_hat())

        self.assertGreater(kick.low, hat.low)
        self.assertGreater(hat.high, kick.high)
        self.assertGreater(hat.centroid_hz, kick.centroid_hz)


class ClassifierTests(unittest.TestCase):
    @staticmethod
    def _role(waveform) -> dpcm.SampleAnalysis:
        rom = vgm_reader.DpcmRom()
        payload = testing.encode_dpcm(waveform)
        rom.load(0xC000, payload)
        return dpcm.analyze(rom, nes_apu.SampleRef(0xC000, len(payload), 15))

    def test_the_three_drums_are_told_apart(self):
        for name, waveform, expected in (
            ("kick", testing.synth_kick(), Role.KICK),
            ("snare", testing.synth_snare(), Role.SNARE),
            ("hat", testing.synth_hat(), Role.HAT),
        ):
            with self.subTest(drum=name):
                self.assertEqual(self._role(waveform).role, expected)

    def test_a_held_note_is_tonal_rather_than_percussive(self):
        rate = testing.DMC_RATE_HZ[15]
        note = np.sin(2 * np.pi * 165.0 * np.arange(int(rate * 0.5)) / rate) * 0.9

        analysis = self._role(note)

        self.assertEqual(analysis.role, Role.TONAL)
        self.assertLess(abs(analysis.features.pitch_hz - 165.0), 15.0)

    def test_a_dac_levelling_blip_is_rejected_instead_of_played_as_a_drum(self):
        """It has no attack: all its motion is one way and it peaks at the end."""
        analysis = self._role(testing.synth_dac_blip())

        self.assertEqual(analysis.role, Role.ARTIFACT)
        self.assertFalse(analysis.role.is_playable)

    def test_encoded_silence_is_rejected_rather_than_heard_as_a_hi_hat(self):
        """Silence encodes as alternating bits, so it decodes to a bright buzz."""
        analysis = self._role(np.zeros(4096))

        self.assertEqual(analysis.role, Role.ARTIFACT)
        self.assertLessEqual(analysis.features.amplitude, 0.05)

    def test_every_verdict_carries_the_evidence_behind_it(self):
        analysis = self._role(testing.synth_snare())
        self.assertTrue(analysis.reasons)
        self.assertTrue(all(isinstance(reason, str) for reason in analysis.reasons))

    def test_a_hand_written_role_overrides_the_rules_and_is_flagged(self):
        rom = vgm_reader.DpcmRom()
        payload = testing.encode_dpcm(testing.synth_hat())
        rom.load(0xC000, payload)
        ref = nes_apu.SampleRef(0xC000, len(payload), 15)

        analysis = dpcm.analyze(rom, ref, role_override=Role.TOM)

        self.assertEqual(analysis.role, Role.TOM)
        self.assertTrue(analysis.overridden)
        self.assertIn("hat", analysis.reasons[0])


class WaveTableTests(unittest.TestCase):
    def _analysis(self, waveform) -> dpcm.SampleAnalysis:
        rom = vgm_reader.DpcmRom()
        payload = testing.encode_dpcm(waveform)
        rom.load(0xC000, payload)
        return dpcm.analyze(rom, nes_apu.SampleRef(0xC000, len(payload), 15))

    def test_the_derived_table_fits_game_boy_wave_ram(self):
        rate = testing.DMC_RATE_HZ[15]
        note = np.sin(2 * np.pi * 200.0 * np.arange(int(rate * 0.3)) / rate)

        table = dpcm.wave_table(self._analysis(note))

        self.assertEqual(len(table), 32)
        self.assertTrue(all(0 <= value <= 15 for value in table))

    def test_the_table_keeps_the_shape_of_the_sample(self):
        """A sine in should come out as one hump up and one down, not as noise."""
        rate = testing.DMC_RATE_HZ[15]
        note = np.sin(2 * np.pi * 200.0 * np.arange(int(rate * 0.3)) / rate)

        table = np.array(dpcm.wave_table(self._analysis(note)), dtype=float)
        crossings = np.count_nonzero(np.diff(np.sign(table - table.mean())) != 0)

        self.assertLessEqual(crossings, 3)

    def test_a_sample_too_short_for_a_period_still_returns_a_usable_table(self):
        rom = vgm_reader.DpcmRom()
        rom.load(0xC000, bytes([0x55, 0xAA]))
        analysis = dpcm.analyze(rom, nes_apu.SampleRef(0xC000, 2, 15))

        table = dpcm.wave_table(analysis)

        self.assertEqual(len(table), 32)
        self.assertTrue(all(0 <= value <= 15 for value in table))


class DmcChannelTests(unittest.TestCase):
    def setUp(self):
        self.stream = _parse_fixture(testing.dpcm_demo_vgm())
        self.timeline = nes_apu.simulate(self.stream)
        self.hits = [
            tick.dmc.sample
            for tick in self.timeline.ticks
            if tick.dmc.started and tick.dmc.sample is not None
        ]

    def test_the_sample_rom_survives_parsing(self):
        self.assertTrue(self.stream.dpcm_rom)
        self.assertEqual(self.stream.dpcm_rom.block_count, 3)

    def test_every_scheduled_hit_is_seen_exactly_once(self):
        self.assertEqual(len(self.hits), 16)

    def test_addresses_and_lengths_come_back_as_written(self):
        _, layout = testing.dpcm_kit()
        addresses = {ref.address for ref in self.hits}
        self.assertEqual(addresses, {address for address, _ in layout.values()})

    def test_a_sample_occupies_the_channel_for_its_own_duration(self):
        playing = sum(1 for tick in self.timeline.ticks if tick.dmc.playing)
        self.assertGreater(playing, 0)
        self.assertLess(playing, len(self.timeline.ticks))

    def test_the_whole_kit_is_classified_correctly(self):
        _, layout = testing.dpcm_kit()
        expected = {address: name for name, (address, _) in layout.items()}
        analyses = dpcm.analyze_all(self.stream.dpcm_rom, self.hits)
        for ref, analysis in analyses.items():
            with self.subTest(address=f"0x{ref.address:04X}"):
                self.assertEqual(analysis.role.value, expected[ref.address])


class SongContextTests(unittest.TestCase):
    """Evidence that is in the arrangement rather than in the waveform.

    A short sampled bass note is indistinguishable from a kick drum on its own:
    both are low, dark and struck. What separates them is that the driver
    transposes one of them and not the other, and transposition is only visible
    across the whole set of references.
    """

    @staticmethod
    def _analyses(rates, waveform=None):
        rom = vgm_reader.DpcmRom()
        payload = testing.encode_dpcm(
            testing.synth_bass_note() if waveform is None else waveform
        )
        rom.load(0xC000, payload)
        refs = [nes_apu.SampleRef(0xC000, len(payload), rate) for rate in rates]
        return dpcm.analyze_all(rom, refs), refs

    def test_one_sample_played_at_many_rates_is_an_instrument(self):
        analyses, refs = self._analyses([10, 12, 14, 15])

        for ref in refs:
            with self.subTest(rate=ref.rate_index):
                self.assertEqual(analyses[ref].role, Role.TONAL)

    def test_the_verdict_says_why_it_overruled_the_waveform(self):
        analyses, refs = self._analyses([10, 12, 14, 15])

        reasons = " ".join(analyses[refs[0]].reasons)
        self.assertIn("4 different rates", reasons)
        self.assertIn("kick", reasons)

    def test_the_same_sample_alone_reads_as_a_drum(self):
        """The premise: nothing in the waveform gives the instrument away."""
        analyses, refs = self._analyses([15])

        self.assertEqual(analyses[refs[0]].role, Role.KICK)

    def test_a_drum_pitched_down_once_is_still_a_drum(self):
        """Two rates is a normal and a low variant, which is how kits are used."""
        analyses, refs = self._analyses([14, 15])

        for ref in refs:
            with self.subTest(rate=ref.rate_index):
                self.assertEqual(analyses[ref].role, Role.KICK)

    def test_transposing_something_with_no_pitch_proves_nothing(self):
        """Retuning noise does not make it melodic, so a real kit stays a kit."""
        analyses, refs = self._analyses([10, 12, 14, 15], testing.synth_kick())

        for ref in refs:
            with self.subTest(rate=ref.rate_index):
                self.assertNotEqual(analyses[ref].role, Role.TONAL)

    def test_a_hand_override_still_wins(self):
        rom = vgm_reader.DpcmRom()
        payload = testing.encode_dpcm(testing.synth_bass_note())
        rom.load(0xC000, payload)
        refs = [nes_apu.SampleRef(0xC000, len(payload), rate) for rate in (10, 12, 15)]

        analyses = dpcm.analyze_all(rom, refs, overrides={0xC000: Role.KICK})

        self.assertEqual({a.role for a in analyses.values()}, {Role.KICK})


if __name__ == "__main__":  # pragma: no cover
    unittest.main()
