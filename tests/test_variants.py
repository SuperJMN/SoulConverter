"""Emitting one file per strategy, and keeping the old behaviour intact.

No metric can say which strategy sounds better, so the tool emits them all.
What can be pinned is that each one is a valid Game Boy VGM in the subset
RetroSharp imports, that turning the fifth voice up never removes drums, and
that `off` is byte-for-byte the converter as it was before any of this existed.
"""

from __future__ import annotations

import os
import shutil
import tempfile
import unittest

from vgm_nes2gb import cli, converter, testing
from vgm_nes2gb.allocator import PRESETS

RUNNER_THEME = (
    "/home/jmn/Repos/RetroSharp/samples/runner/assets/music/runner.nes.vgz"
)

# What RetroSharp's VgmImporter accepts. Anything else makes the file
# unimportable however good it sounds.
ALLOWED_COMMANDS = {0x61, 0x62, 0x63, 0x66, 0x67, 0xB3} | set(range(0x70, 0x80))


def _commands(payload: bytes) -> set[int]:
    offset = int.from_bytes(payload[0x34:0x38], "little") + 0x34
    seen: set[int] = set()
    while offset < len(payload):
        command = payload[offset]
        seen.add(command)
        if command == 0xB3:
            offset += 3
        elif command == 0x61:
            offset += 3
        elif command in (0x62, 0x63, 0x66) or 0x70 <= command <= 0x7F:
            offset += 1
            if command == 0x66:
                break
        elif command == 0x67:
            size = int.from_bytes(payload[offset + 3 : offset + 7], "little")
            offset += 7 + size
        else:  # pragma: no cover - a failure here is what the test reports
            break
    return seen


class VariantTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.mkdtemp(prefix="vgm-variants-")
        cls.source = os.path.join(cls.directory, "demo.vgm")
        with open(cls.source, "wb") as handle:
            handle.write(testing.dpcm_demo_vgm())
        cls.results = converter.convert_variants(cls.source, render_audio=False)

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.directory, ignore_errors=True)

    def test_one_result_per_strategy(self):
        self.assertEqual([r.preset for r in self.results], list(PRESETS))

    def test_every_variant_stays_inside_the_importable_subset(self):
        for result in self.results:
            with self.subTest(preset=result.preset):
                self.assertTrue(_commands(result.payload) <= ALLOWED_COMMANDS)

    def test_every_variant_is_a_valid_game_boy_vgm(self):
        for result in self.results:
            with self.subTest(preset=result.preset):
                payload = result.payload
                self.assertEqual(payload[:4], b"Vgm ")
                dmg_clock = int.from_bytes(payload[0x80:0x84], "little")
                self.assertEqual(dmg_clock, 4194304)

    def test_off_carries_no_drums_and_the_others_do(self):
        by_preset = {r.preset: r for r in self.results}

        self.assertEqual(by_preset["off"].report["dpcm"]["placed"], 0)
        for name in ("safe", "balanced", "full"):
            with self.subTest(preset=name):
                self.assertGreater(by_preset[name].report["dpcm"]["placed"], 0)

    def test_a_bolder_strategy_never_plays_fewer_hits(self):
        placed = [r.report["dpcm"]["placed"] for r in self.results]
        self.assertEqual(placed, sorted(placed))

    def test_adding_drums_costs_bytes_rather_than_saving_them(self):
        by_preset = {r.preset: r for r in self.results}
        self.assertGreater(
            len(by_preset["full"].payload), len(by_preset["off"].payload)
        )

    def test_the_report_names_every_distinct_sample_and_what_it_was_taken_for(self):
        full = next(r for r in self.results if r.preset == "full")
        samples = full.report["dpcm"]["samples"]

        self.assertEqual(len(samples), 3)
        self.assertEqual(
            {entry["role"] for entry in samples}, {"kick", "snare", "hat"}
        )
        for entry in samples:
            self.assertTrue(entry["why"])

    def test_the_comparison_table_lists_every_variant(self):
        table = converter.render_variant_table(self.results)

        for result in self.results:
            self.assertIn(result.preset, table)

    def test_the_table_says_the_ranking_does_not_decide(self):
        """Two different chips cannot match; the ear is the judge, not a number."""
        table = converter.render_variant_table(self.results).lower()
        self.assertTrue("ear" in table or "listen" in table)


class OffIsUnchangedTests(unittest.TestCase):
    """The fifth voice is opt-in: by default nothing about the output moves."""

    def setUp(self):
        self.directory = tempfile.mkdtemp(prefix="vgm-default-")
        self.source = os.path.join(self.directory, "demo.vgm")
        with open(self.source, "wb") as handle:
            handle.write(testing.dpcm_demo_vgm())

    def tearDown(self):
        shutil.rmtree(self.directory, ignore_errors=True)

    def test_the_default_strategy_is_off(self):
        result = converter.convert_file(self.source, render_audio=False)
        self.assertEqual(result.preset, "off")

    def test_off_matches_a_conversion_that_never_heard_of_dpcm(self):
        from vgm_nes2gb import gb_vgm_writer, nes_apu, nes_to_gb, vgm_reader

        stream = vgm_reader.parse(self.source)
        timeline = nes_apu.simulate(stream)
        expected = gb_vgm_writer.encode(nes_to_gb.convert(timeline), stream.gd3)

        result = converter.convert_file(self.source, dpcm="off", render_audio=False)

        self.assertEqual(result.payload, expected)


class CommandLineTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp(prefix="vgm-cli-")
        self.source = os.path.join(self.directory, "demo.vgm")
        with open(self.source, "wb") as handle:
            handle.write(testing.dpcm_demo_vgm())

    def tearDown(self):
        shutil.rmtree(self.directory, ignore_errors=True)

    def _run(self, *extra, expect=0):
        out = os.path.join(self.directory, "out.gb.vgm")
        code = cli.main(["convert", self.source, "-o", out, "--quiet", *extra])
        self.assertEqual(code, expect)
        return out

    def test_variants_writes_one_named_file_per_strategy(self):
        self._run("--variants")

        for name in PRESETS:
            path = os.path.join(self.directory, f"out.{name}.gb.vgm")
            with self.subTest(preset=name):
                self.assertTrue(os.path.exists(path), path)
                self.assertGreater(os.path.getsize(path), 0)

    def test_a_single_strategy_writes_exactly_the_file_asked_for(self):
        out = self._run("--dpcm", "balanced")

        self.assertTrue(os.path.exists(out))
        self.assertEqual(
            sorted(name for name in os.listdir(self.directory) if name.endswith(".vgm")),
            ["demo.vgm", "out.gb.vgm"],
        )

    def test_dumping_samples_writes_one_audible_file_per_sample(self):
        dump = os.path.join(self.directory, "samples")
        self._run("--dpcm", "full", "--dpcm-dump", dump)

        written = sorted(os.listdir(dump))
        self.assertEqual(len(written), 3)
        self.assertTrue(all(name.endswith(".wav") for name in written))
        self.assertTrue(any("kick" in name for name in written))

    def test_an_unknown_role_is_refused_instead_of_silently_ignored(self):
        code = cli.main(
            [
                "convert",
                self.source,
                "-o",
                os.path.join(self.directory, "out.gb.vgm"),
                "--quiet",
                "--dpcm-role",
                "0xC000=cowbell",
            ]
        )

        self.assertNotEqual(code, 0)
        self.assertFalse(os.path.exists(os.path.join(self.directory, "out.gb.vgm")))

    def test_a_hand_written_role_reaches_the_report(self):
        out = os.path.join(self.directory, "out.gb.vgm")
        report = os.path.join(self.directory, "reports")
        os.makedirs(report)
        cli.main(
            [
                "convert",
                self.source,
                "-o",
                out,
                "--quiet",
                "--dpcm",
                "full",
                "--dpcm-role",
                "0xC000=tom",
                "--report",
                report,
            ]
        )

        import json

        with open(os.path.join(report, "out.json"), encoding="utf-8") as handle:
            data = json.load(handle)

        roles = {entry["role"] for entry in data["dpcm"]["samples"]}
        self.assertIn("tom", roles)


@unittest.skipUnless(os.path.exists(RUNNER_THEME), "runner theme not available")
class RealThemeVariantTests(unittest.TestCase):
    """The strategies on real material, where the trade-off is not synthetic."""

    @classmethod
    def setUpClass(cls):
        cls.results = converter.convert_variants(RUNNER_THEME, render_audio=False)

    def test_every_variant_stays_importable(self):
        for result in self.results:
            with self.subTest(preset=result.preset):
                self.assertTrue(_commands(result.payload) <= ALLOWED_COMMANDS)

    def test_the_active_strategies_play_almost_every_hit(self):
        for result in self.results:
            if result.preset == "off":
                continue
            section = result.report["dpcm"]
            playable = section["hits"] - section["dropped"]["artifact"]
            with self.subTest(preset=result.preset):
                self.assertGreaterEqual(section["placed"] / playable, 0.95)

    def test_the_startup_blip_is_never_played_as_a_drum(self):
        for result in self.results:
            if result.preset == "off":
                continue
            with self.subTest(preset=result.preset):
                self.assertGreaterEqual(result.report["dpcm"]["dropped"]["artifact"], 1)


if __name__ == "__main__":  # pragma: no cover
    unittest.main()
