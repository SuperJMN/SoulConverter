"""Whole-pipeline checks: NES VGM in, audible-enough Game Boy VGM out.

The thresholds here are deliberately lax. Fidelity metrics are a report, never a
gate: the two chips are not equivalent, so an exact match is impossible and
chasing one would be the wrong goal. These assertions only catch a pipeline that
has genuinely broken — silence, dead channels, wrong octaves, lost loops.
"""

from __future__ import annotations

import os
import shutil
import tempfile
import unittest

import numpy as np

from vgm_nes2gb import converter, gb_vgm_writer, testing
from vgm_nes2gb.nes_to_gb import VOLUME_MODE_ENVELOPE, VOLUME_MODE_RETRIGGER

TONAL_CHANNELS = ("pulse1", "pulse2", "triangle")


class ConversionFixture(unittest.TestCase):
    """Converts the synthetic demo song once and shares the result."""

    volume_mode = VOLUME_MODE_RETRIGGER
    result: converter.ConversionResult

    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.mkdtemp(prefix="vgm-nes2gb-")
        cls.source = os.path.join(cls.directory, "demo.vgm")
        with open(cls.source, "wb") as handle:
            handle.write(testing.demo_vgm())
        cls.result = converter.convert_file(cls.source, volume_mode=cls.volume_mode)

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.directory, ignore_errors=True)


class DemoSongTests(ConversionFixture):
    def test_the_output_is_a_valid_game_boy_vgm(self):
        payload = self.result.payload
        self.assertEqual(payload[0:4], b"Vgm ")
        self.assertTrue(gb_vgm_writer.scan_commands(payload) <= gb_vgm_writer.ALLOWED_COMMANDS)

    def test_the_duration_matches_the_source(self):
        source = self.result.report["source"]["duration_seconds"]
        output = self.result.report["output"]["duration_seconds"]
        self.assertAlmostEqual(source, output, delta=0.05)

    def test_every_channel_produces_notes(self):
        for name, channel in self.result.report["channels"].items():
            with self.subTest(channel=name):
                self.assertGreater(channel["note_ons"], 0)
                self.assertGreater(channel["active_ticks"], 0)

    def test_the_loop_point_survives(self):
        self.assertIsNotNone(self.result.report["output"]["loop_tick"])

    def test_the_game_boy_side_actually_makes_sound(self):
        self.assertGreater(self.result.report["metrics"]["gb_rms"], 0.01)

    def test_neither_side_clips_into_silence_or_saturation(self):
        ratio = self.result.report["metrics"]["rms_ratio"]
        self.assertGreater(ratio, 0.25)
        self.assertLess(ratio, 4.0)


class FidelityTests(ConversionFixture):
    def test_the_two_renders_rise_and_fall_together(self):
        self.assertGreater(self.result.report["metrics"]["envelope_correlation"], 0.7)

    def test_the_overall_tone_colour_is_recognisably_the_same(self):
        self.assertGreater(self.result.report["metrics"]["spectral_correlation"], 0.9)

    def test_tonal_channels_land_on_the_right_pitch_class(self):
        # Compared modulo the octave, because out-of-range bass is transposed up
        # on purpose and that is a reported feature, not an error.
        for name in TONAL_CHANNELS:
            with self.subTest(channel=name):
                channel = self.result.report["metrics"]["channels"][name]
                self.assertLess(channel["mean_abs_cents_mod_octave"], 25.0)

    def test_channels_play_and_rest_at_the_same_times(self):
        for name, channel in self.result.report["metrics"]["channels"].items():
            with self.subTest(channel=name):
                self.assertGreater(channel["activity_agreement"], 0.9)

    def test_noise_lands_within_an_octave_of_the_nes_grid(self):
        # The two noise dividers simply do not line up; nearest-neighbour is the
        # best available and its drift is reported per period index.
        channel = self.result.report["metrics"]["channels"]["noise"]
        self.assertLess(channel["max_abs_cents"], 600.0)


class TransposeTests(ConversionFixture):
    def test_bass_below_the_game_boy_floor_is_raised_by_whole_octaves(self):
        triangle = self.result.report["channels"]["triangle"]
        self.assertGreater(triangle["transposed_ticks"], 0)
        self.assertGreaterEqual(triangle["max_octaves_up"], 1)

    def test_transposition_is_reported_to_the_user(self):
        self.assertTrue(
            any("transposed" in warning for warning in self.result.warnings),
            self.result.warnings,
        )

    def test_channels_inside_the_range_are_left_alone(self):
        for name in ("pulse1", "pulse2", "noise"):
            with self.subTest(channel=name):
                self.assertEqual(self.result.report["channels"][name]["transposed_ticks"], 0)

    def test_clamping_instead_of_transposing_keeps_the_note_audible(self):
        clamped = converter.convert_file(self.source, allow_transpose=False)
        self.assertEqual(clamped.report["channels"]["triangle"]["transposed_ticks"], 0)
        self.assertGreater(clamped.report["channels"]["triangle"]["note_ons"], 0)
        self.assertGreater(clamped.report["metrics"]["gb_rms"], 0.01)


class EnvelopeVolumeModeTests(ConversionFixture):
    volume_mode = VOLUME_MODE_ENVELOPE

    def test_envelope_mode_is_smaller_than_retrigger_mode(self):
        retrigger = converter.convert_file(
            self.source, volume_mode=VOLUME_MODE_RETRIGGER, render_audio=False
        )
        self.assertLess(len(self.result.payload), len(retrigger.payload))

    def test_envelope_mode_barely_retriggers(self):
        for name, channel in self.result.report["channels"].items():
            with self.subTest(channel=name):
                self.assertEqual(channel["retriggers"], channel["note_ons"])

    def test_envelope_mode_still_tracks_the_source_dynamics(self):
        self.assertGreater(self.result.report["metrics"]["envelope_correlation"], 0.7)

    def test_envelope_mode_is_still_importable(self):
        self.assertTrue(
            gb_vgm_writer.scan_commands(self.result.payload) <= gb_vgm_writer.ALLOWED_COMMANDS
        )


class TickRateTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp(prefix="vgm-nes2gb-")
        self.addCleanup(shutil.rmtree, self.directory, True)
        self.source = os.path.join(self.directory, "demo.vgm")
        with open(self.source, "wb") as handle:
            handle.write(testing.demo_vgm())

    def test_a_finer_grid_costs_more_bytes_but_keeps_the_duration(self):
        coarse = converter.convert_file(self.source, tick_hz=60.0, render_audio=False)
        fine = converter.convert_file(self.source, tick_hz=240.0, render_audio=False)
        self.assertGreater(len(fine.payload), len(coarse.payload))
        self.assertAlmostEqual(
            coarse.report["output"]["duration_seconds"],
            fine.report["output"]["duration_seconds"],
            delta=0.05,
        )

    def test_a_finer_grid_stays_importable(self):
        fine = converter.convert_file(self.source, tick_hz=240.0, render_audio=False)
        self.assertTrue(
            gb_vgm_writer.scan_commands(fine.payload) <= gb_vgm_writer.ALLOWED_COMMANDS
        )


class SilentSourceTests(unittest.TestCase):
    def test_a_song_that_never_plays_a_note_converts_without_crashing(self):
        payload = testing.build_nes_vgm([[(0x4015, 0x00)] for _ in range(10)])
        directory = tempfile.mkdtemp(prefix="vgm-nes2gb-")
        self.addCleanup(shutil.rmtree, directory, True)
        path = os.path.join(directory, "silence.vgm")
        with open(path, "wb") as handle:
            handle.write(payload)

        result = converter.convert_file(path)
        self.assertEqual(result.payload[0:4], b"Vgm ")
        for channel in result.report["channels"].values():
            self.assertEqual(channel["note_ons"], 0)
        self.assertTrue(np.all(np.abs(result.gb_audio) < 1e-6))


class DrumFixture(unittest.TestCase):
    """The demo song that also has a sampled drum kit, converted both ways."""

    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.mkdtemp(prefix="vgm-nes2gb-drums-")
        cls.source = os.path.join(cls.directory, "drums.vgm")
        with open(cls.source, "wb") as handle:
            handle.write(testing.dpcm_demo_vgm())
        cls.off = converter.convert_file(cls.source, dpcm="off")
        cls.full = converter.convert_file(cls.source, dpcm="full")

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.directory, ignore_errors=True)


class DpcmPipelineTests(DrumFixture):
    def test_the_reference_render_plays_the_samples_the_nes_played(self):
        """Without this the comparison flatters the conversion: no drums, no gap."""
        plain = converter.convert_file(self.source, dpcm="off")
        self.assertGreater(float(np.abs(plain.nes_audio).max()), 0.0)
        self.assertGreater(
            self.off.report["dpcm"]["hits"], 0, "the fixture lost its drums"
        )

    def test_re_synthesised_drums_land_on_the_beats_the_samples_did(self):
        before = self.off.report["metrics"]["onsets"]["recall"]
        after = self.full.report["metrics"]["onsets"]["recall"]

        self.assertGreater(after, before)

    def test_dropping_the_sample_channel_is_reported_rather_than_hidden(self):
        text = converter.render_text(self.off.report).lower()
        self.assertIn("dpcm", text)

    def test_the_drums_are_audible_in_the_game_boy_render(self):
        quiet = float(np.abs(self.off.gb_audio).mean())
        loud = float(np.abs(self.full.gb_audio).mean())

        self.assertGreater(loud, quiet)

    def test_both_versions_remain_importable(self):
        allowed = {"0x61", "0x62", "0x63", "0x66", "0x67", "0xB3"}
        for result in (self.off, self.full):
            with self.subTest(preset=result.preset):
                self.assertEqual(result.payload[:4], b"Vgm ")
                self.assertTrue(set(result.report["output"]["commands"]) <= allowed)


class ReportTests(ConversionFixture):
    def test_the_report_names_the_source_and_the_settings(self):
        report = self.result.report
        self.assertEqual(report["source"]["path"], os.path.abspath(self.source))
        self.assertEqual(report["source"]["cpu_clock_hz"], testing.NTSC_CPU_HZ)
        self.assertEqual(report["settings"]["volume_mode"], self.volume_mode)

    def test_the_report_is_json_serialisable(self):
        import json

        json.loads(json.dumps(self.result.report))

    def test_the_text_report_mentions_every_channel(self):
        text = converter.render_text(self.result.report)
        for name in ("pulse1", "pulse2", "triangle", "noise"):
            self.assertIn(name, text)


if __name__ == "__main__":
    unittest.main()
