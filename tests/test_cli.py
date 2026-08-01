"""Command line behaviour: file in, file out, and honest exit codes.

Warnings never fail a conversion — the report exists to inform, not to gate — so
a successful run with warnings must still exit 0. Only a file the tool genuinely
cannot convert is an error.
"""

from __future__ import annotations

import gzip
import io
import json
import os
import shutil
import struct
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout

from vgm_nes2gb import cli, gb_vgm_writer, testing


class CliTestCase(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp(prefix="vgm-nes2gb-cli-")
        self.addCleanup(shutil.rmtree, self.directory, True)

    def path(self, *parts: str) -> str:
        return os.path.join(self.directory, *parts)

    def write(self, name: str, payload: bytes) -> str:
        destination = self.path(name)
        os.makedirs(os.path.dirname(destination), exist_ok=True)
        with open(destination, "wb") as handle:
            handle.write(payload)
        return destination

    def run_cli(self, *argv: str) -> tuple[int, str, str]:
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = cli.main(list(argv))
        return code, out.getvalue(), err.getvalue()


class DemoCommandTests(CliTestCase):
    def test_demo_writes_a_playable_nes_vgm(self):
        destination = self.path("demo.vgm")
        code, stdout, _ = self.run_cli("demo", "-o", destination)
        self.assertEqual(code, 0)
        self.assertIn(destination, stdout)
        with open(destination, "rb") as handle:
            payload = handle.read()
        self.assertEqual(payload[0:4], b"Vgm ")
        self.assertEqual(struct.unpack_from("<I", payload, 0x84)[0], testing.NTSC_CPU_HZ)

    def test_demo_creates_missing_directories(self):
        destination = self.path("nested", "deeper", "demo.vgm")
        self.assertEqual(self.run_cli("demo", "-o", destination)[0], 0)
        self.assertTrue(os.path.isfile(destination))

    def test_the_demo_output_is_immediately_convertible(self):
        source = self.path("demo.vgm")
        self.run_cli("demo", "-o", source)
        code, _, _ = self.run_cli("convert", source, "-o", self.path("demo.gb.vgm"), "--quiet")
        self.assertEqual(code, 0)


class ConvertCommandTests(CliTestCase):
    def setUp(self):
        super().setUp()
        self.source = self.write("demo.vgm", testing.demo_vgm())

    def test_converting_one_file_writes_an_importable_game_boy_vgm(self):
        destination = self.path("demo.gb.vgm")
        code, _, _ = self.run_cli("convert", self.source, "-o", destination, "--quiet")
        self.assertEqual(code, 0)
        with open(destination, "rb") as handle:
            payload = handle.read()
        self.assertEqual(struct.unpack_from("<I", payload, 0x80)[0], 4194304)
        self.assertTrue(gb_vgm_writer.scan_commands(payload) <= gb_vgm_writer.ALLOWED_COMMANDS)

    def test_without_an_output_the_file_lands_beside_the_source(self):
        self.assertEqual(self.run_cli("convert", self.source, "--quiet")[0], 0)
        self.assertTrue(os.path.isfile(self.path("demo.gb.vgm")))

    def test_a_gzipped_vgz_source_is_accepted(self):
        source = self.write("demo.vgz", gzip.compress(testing.demo_vgm()))
        destination = self.path("fromvgz.gb.vgm")
        self.assertEqual(
            self.run_cli("convert", source, "-o", destination, "--quiet")[0], 0
        )
        self.assertTrue(os.path.isfile(destination))

    def test_the_summary_is_printed_unless_silenced(self):
        _, stdout, _ = self.run_cli("convert", self.source, "-o", self.path("a.gb.vgm"))
        self.assertIn("pulse1", stdout)
        self.assertIn("triangle", stdout)

    def test_quiet_prints_nothing_on_success(self):
        _, stdout, _ = self.run_cli(
            "convert", self.source, "-o", self.path("b.gb.vgm"), "--quiet"
        )
        self.assertEqual(stdout, "")

    def test_warnings_do_not_turn_into_a_failure(self):
        # The demo song deliberately contains bass below the Game Boy range.
        code, stdout, _ = self.run_cli("convert", self.source, "-o", self.path("c.gb.vgm"))
        self.assertEqual(code, 0)
        self.assertIn("transposed", stdout)


class SidecarTests(CliTestCase):
    def setUp(self):
        super().setUp()
        self.source = self.write("demo.vgm", testing.demo_vgm())
        self.destination = self.path("out", "demo.gb.vgm")

    def test_report_writes_one_json_per_file(self):
        reports = self.path("reports")
        code, _, _ = self.run_cli(
            "convert", self.source, "-o", self.destination, "--report", reports, "--quiet"
        )
        self.assertEqual(code, 0)
        with open(os.path.join(reports, "demo.json"), encoding="utf-8") as handle:
            report = json.load(handle)
        self.assertEqual(report["output"]["path"], os.path.abspath(self.destination))
        self.assertIn("channels", report)
        self.assertIn("metrics", report)

    def test_wav_writes_both_sides_for_listening(self):
        self.run_cli("convert", self.source, "-o", self.destination, "--wav", "--quiet")
        directory = os.path.dirname(self.destination)
        for name in ("demo.nes.wav", "demo.gb.wav"):
            with self.subTest(file=name):
                path = os.path.join(directory, name)
                self.assertTrue(os.path.isfile(path))
                with open(path, "rb") as handle:
                    self.assertEqual(handle.read(4), b"RIFF")

    def test_spectrogram_writes_a_comparison_png(self):
        self.run_cli(
            "convert", self.source, "-o", self.destination, "--spectrogram", "--quiet"
        )
        path = os.path.join(os.path.dirname(self.destination), "demo.compare.png")
        self.assertTrue(os.path.isfile(path))
        with open(path, "rb") as handle:
            self.assertEqual(handle.read(8), b"\x89PNG\r\n\x1a\n")


class OptionTests(CliTestCase):
    def setUp(self):
        super().setUp()
        self.source = self.write("demo.vgm", testing.demo_vgm())

    def test_envelope_mode_is_recorded_in_the_report(self):
        reports = self.path("reports")
        self.run_cli(
            "convert", self.source, "-o", self.path("e.gb.vgm"),
            "--volume-mode", "envelope", "--report", reports, "--quiet",
        )
        with open(os.path.join(reports, "e.json"), encoding="utf-8") as handle:
            self.assertEqual(json.load(handle)["settings"]["volume_mode"], "envelope")

    def test_tick_hz_is_recorded_in_the_report(self):
        reports = self.path("reports")
        self.run_cli(
            "convert", self.source, "-o", self.path("t.gb.vgm"),
            "--tick-hz", "120", "--report", reports, "--quiet",
        )
        with open(os.path.join(reports, "t.json"), encoding="utf-8") as handle:
            self.assertEqual(json.load(handle)["settings"]["tick_hz"], 120.0)

    def test_no_transpose_suppresses_the_octave_shift(self):
        reports = self.path("reports")
        self.run_cli(
            "convert", self.source, "-o", self.path("n.gb.vgm"),
            "--no-transpose", "--report", reports, "--quiet",
        )
        with open(os.path.join(reports, "n.json"), encoding="utf-8") as handle:
            report = json.load(handle)
        self.assertFalse(report["settings"]["transpose_out_of_range"])
        self.assertEqual(report["channels"]["triangle"]["transposed_ticks"], 0)

    def test_an_unknown_volume_mode_is_rejected_by_the_parser(self):
        with self.assertRaises(SystemExit):
            self.run_cli("convert", self.source, "--volume-mode", "nope")


class BatchTests(CliTestCase):
    def setUp(self):
        super().setUp()
        self.inputs = self.path("in")
        os.makedirs(self.inputs)
        for name in ("first.vgm", "second.vgm"):
            with open(os.path.join(self.inputs, name), "wb") as handle:
                handle.write(testing.demo_vgm())
        with open(os.path.join(self.inputs, "notes.txt"), "w", encoding="utf-8") as handle:
            handle.write("ignore me")

    def test_a_directory_converts_every_vgm_it_contains(self):
        outputs = self.path("out")
        code, _, _ = self.run_cli("convert", self.inputs, "-o", outputs, "--quiet")
        self.assertEqual(code, 0)
        self.assertEqual(
            sorted(os.listdir(outputs)), ["first.gb.vgm", "second.gb.vgm"]
        )

    def test_non_vgm_files_are_ignored(self):
        outputs = self.path("out")
        self.run_cli("convert", self.inputs, "-o", outputs, "--quiet")
        self.assertNotIn("notes.gb.vgm", os.listdir(outputs))

    def test_one_bad_file_among_good_ones_reports_a_partial_failure(self):
        with open(os.path.join(self.inputs, "broken.vgm"), "wb") as handle:
            handle.write(b"NOTVGM" + bytes(0x200))
        code, _, stderr = self.run_cli(
            "convert", self.inputs, "-o", self.path("out"), "--quiet"
        )
        self.assertEqual(code, 2)
        self.assertIn("broken.vgm", stderr)
        self.assertEqual(len(os.listdir(self.path("out"))), 2)


class FailureTests(CliTestCase):
    def test_a_missing_input_is_an_error(self):
        code, _, stderr = self.run_cli("convert", self.path("nope.vgm"))
        self.assertEqual(code, 1)
        self.assertIn("no VGM files found", stderr)

    def test_an_empty_directory_is_an_error(self):
        empty = self.path("empty")
        os.makedirs(empty)
        self.assertEqual(self.run_cli("convert", empty)[0], 1)

    def test_a_file_that_is_not_a_vgm_is_an_error(self):
        source = self.write("junk.vgm", b"this is not a VGM file at all")
        code, _, stderr = self.run_cli("convert", source, "-o", self.path("x.gb.vgm"))
        self.assertEqual(code, 1)
        self.assertIn("error:", stderr)

    def test_an_expansion_chip_is_refused_by_name(self):
        # VRC6 shares the NES 0xB4 command; it is identified by register range.
        source = self.write(
            "vrc6.vgm", testing.build_nes_vgm([[(0x4015, 0x0F), (0xA0, 0x30)]])
        )
        code, _, stderr = self.run_cli("convert", source, "-o", self.path("y.gb.vgm"))
        self.assertEqual(code, 1)
        self.assertIn("VRC6", stderr)

    def test_a_non_nes_vgm_is_refused_by_name(self):
        payload = bytearray(testing.demo_vgm())
        payload[0x2C : 0x2C + 4] = struct.pack("<I", 7670453)  # YM2612
        source = self.write("genesis.vgm", bytes(payload))
        code, _, stderr = self.run_cli("convert", source, "-o", self.path("z.gb.vgm"))
        self.assertEqual(code, 1)
        self.assertIn("YM2612", stderr)

    def test_a_vgm_without_a_nes_clock_is_refused(self):
        payload = bytearray(testing.demo_vgm())
        payload[0x84 : 0x84 + 4] = bytes(4)
        source = self.write("noclock.vgm", bytes(payload))
        code, _, stderr = self.run_cli("convert", source, "-o", self.path("w.gb.vgm"))
        self.assertEqual(code, 1)
        self.assertIn("2A03", stderr)


if __name__ == "__main__":
    unittest.main()
