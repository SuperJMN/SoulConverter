"""Optional regression pass over real NES themes.

Nothing derived from a commercial ROM is versioned here, so these tests look for
source material on the machine and skip cleanly when there is none. Point
``VGM_NES2GB_SAMPLES`` at a file or a directory of NES ``.vgm``/``.vgz`` files to
run them::

    VGM_NES2GB_SAMPLES=~/themes python3 -m unittest discover -s tests -t .

The thresholds are the same lax ones used for the synthetic demo: these catch a
broken pipeline, not an imperfect one. Two chips that are not equivalent cannot
produce an equal render, and pretending otherwise would be the wrong goal.
"""

from __future__ import annotations

import os
import unittest

from vgm_nes2gb import converter, gb_vgm_writer
from vgm_nes2gb.nes_to_gb import VOLUME_MODE_ENVELOPE

SAMPLE_ENV = "VGM_NES2GB_SAMPLES"
DEFAULT_SAMPLES = (
    "~/Repos/RetroSharp/samples/runner/assets/music/runner.nes.vgz",
)
TONAL_CHANNELS = ("pulse1", "pulse2", "triangle")


def discover_samples() -> list[str]:
    configured = os.environ.get(SAMPLE_ENV)
    candidates = [configured] if configured else list(DEFAULT_SAMPLES)

    found: list[str] = []
    for candidate in candidates:
        path = os.path.expanduser(candidate)
        if os.path.isfile(path):
            found.append(path)
        elif os.path.isdir(path):
            found.extend(
                os.path.join(path, entry)
                for entry in sorted(os.listdir(path))
                if entry.lower().endswith((".vgm", ".vgz"))
            )
    return found


SAMPLES = discover_samples()


@unittest.skipUnless(SAMPLES, f"no NES VGM samples found; set {SAMPLE_ENV}")
class RealThemeTests(unittest.TestCase):
    def test_every_sample_converts_to_an_importable_game_boy_vgm(self):
        for path in SAMPLES:
            with self.subTest(theme=os.path.basename(path)):
                result = converter.convert_file(path, render_audio=False)
                self.assertEqual(result.payload[0:4], b"Vgm ")
                self.assertTrue(
                    gb_vgm_writer.scan_commands(result.payload)
                    <= gb_vgm_writer.ALLOWED_COMMANDS
                )

    def test_the_arrangement_survives_the_move(self):
        for path in SAMPLES:
            with self.subTest(theme=os.path.basename(path)):
                report = converter.convert_file(path).report
                self.assertAlmostEqual(
                    report["source"]["duration_seconds"],
                    report["output"]["duration_seconds"],
                    delta=0.1,
                )
                self.assertGreater(report["metrics"]["gb_rms"], 0.005)
                self.assertGreater(report["metrics"]["envelope_correlation"], 0.7)
                self.assertGreater(report["metrics"]["spectral_correlation"], 0.9)

    def test_tonal_channels_keep_their_pitch_class(self):
        for path in SAMPLES:
            report = converter.convert_file(path).report
            for name in TONAL_CHANNELS:
                channel = report["metrics"]["channels"][name]
                if channel["compared_ticks"] == 0:
                    continue
                with self.subTest(theme=os.path.basename(path), channel=name):
                    self.assertLess(channel["mean_abs_cents_mod_octave"], 25.0)
                    self.assertGreater(channel["activity_agreement"], 0.9)

    def test_a_looping_theme_keeps_its_loop(self):
        for path in SAMPLES:
            report = converter.convert_file(path, render_audio=False).report
            if report["source"]["loop_sample"] is None:
                continue
            with self.subTest(theme=os.path.basename(path)):
                self.assertIsNotNone(report["output"]["loop_tick"])

    def test_envelope_mode_is_the_compact_option(self):
        for path in SAMPLES:
            with self.subTest(theme=os.path.basename(path)):
                default = converter.convert_file(path, render_audio=False)
                compact = converter.convert_file(
                    path, volume_mode=VOLUME_MODE_ENVELOPE, render_audio=False
                )
                self.assertLessEqual(len(compact.payload), len(default.payload))
                self.assertTrue(
                    gb_vgm_writer.scan_commands(compact.payload)
                    <= gb_vgm_writer.ALLOWED_COMMANDS
                )


if __name__ == "__main__":
    unittest.main()
