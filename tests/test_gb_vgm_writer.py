"""The output must stay inside the command subset RetroSharp can import.

RetroSharp's ``VgmImporter`` refuses any command outside
``0x61 0x62 0x63 0x66 0x67 0x70-0x7F 0xB3``, only understands DMG registers
``0x00-0x16`` and ``0x20-0x2F``, and reads the DMG clock from header offset
``0x80``. These tests pin that contract so a future change to the writer cannot
silently produce a file RetroSharp rejects.
"""

from __future__ import annotations

import struct
import unittest

from vgm_nes2gb import gb_vgm_writer as writer
from vgm_nes2gb import gameboy as gb
from vgm_nes2gb import testing
from vgm_nes2gb.nes_to_gb import GbTick, GbTimeline, MappingStats
from vgm_nes2gb.vgm_reader import VGM_SAMPLE_RATE

FRAME = 735

# The exact set RetroSharp's importer accepts, for reference in assertions.
RETROSHARP_COMMANDS = frozenset({0x61, 0x62, 0x63, 0x66, 0x67}) | frozenset(
    range(0x70, 0x80)
) | frozenset({0xB3})


def timeline(ticks, *, loop_tick=None, total_samples=None, tick_hz=60.0) -> GbTimeline:
    built = [
        GbTick(index=index, sample=sample, writes=list(writes))
        for index, (sample, writes) in enumerate(ticks)
    ]
    last = built[-1].sample if built else 0
    return GbTimeline(
        tick_hz=tick_hz,
        ticks=built,
        loop_tick=loop_tick,
        total_samples=total_samples if total_samples is not None else last + FRAME,
        stats=MappingStats(),
    )


def u32(payload: bytes, offset: int) -> int:
    return struct.unpack_from("<I", payload, offset)[0]


class HeaderTests(unittest.TestCase):
    def setUp(self):
        self.payload = writer.encode(
            timeline([(0, [(gb.NR52, 0x80)]), (FRAME, [(gb.NR50, 0x77)])])
        )

    def test_starts_with_the_vgm_signature(self):
        self.assertEqual(self.payload[0:4], b"Vgm ")

    def test_declares_version_1_61(self):
        self.assertEqual(u32(self.payload, 0x08), 0x161)

    def test_publishes_the_dmg_clock_where_retrosharp_reads_it(self):
        self.assertEqual(u32(self.payload, 0x80), gb.GB_CLOCK_HZ)
        self.assertEqual(gb.GB_CLOCK_HZ, 4194304)

    def test_leaves_the_nes_clock_slot_empty(self):
        # A non-zero 2A03 clock would make the file claim to be a NES VGM.
        self.assertEqual(u32(self.payload, 0x84), 0)

    def test_data_offset_points_just_past_the_256_byte_header(self):
        self.assertEqual(u32(self.payload, 0x34) + 0x34, writer.HEADER_SIZE)

    def test_eof_offset_covers_the_whole_file(self):
        self.assertEqual(u32(self.payload, 0x04) + 0x04, len(self.payload))

    def test_declares_a_60_hz_rate(self):
        self.assertEqual(u32(self.payload, 0x24), 60)

    def test_total_samples_reach_the_end_of_the_last_tick(self):
        self.assertEqual(u32(self.payload, 0x18), 2 * FRAME)


class CommandSubsetTests(unittest.TestCase):
    def test_a_realistic_conversion_uses_only_importable_commands(self):
        from vgm_nes2gb import converter

        payload = converter.convert_file(
            _write_demo(self), render_audio=False
        ).payload
        commands = writer.scan_commands(payload)
        self.assertTrue(commands)
        self.assertTrue(
            commands <= RETROSHARP_COMMANDS,
            f"unsupported commands: {sorted(hex(c) for c in commands - RETROSHARP_COMMANDS)}",
        )

    def test_the_writer_only_ever_emits_four_command_bytes(self):
        payload = writer.encode(
            timeline(
                [
                    (0, [(gb.NR52, 0x80), (gb.NR51, 0xFF)]),
                    (FRAME, [(gb.NR11, 0x80)]),
                    (5 * FRAME, [(gb.NR12, 0xF0)]),
                ]
            )
        )
        self.assertTrue(writer.scan_commands(payload) <= writer.ALLOWED_COMMANDS)

    def test_a_one_frame_wait_uses_the_single_byte_form(self):
        payload = writer.encode(timeline([(0, [(gb.NR52, 0x80)]), (FRAME, [])]))
        self.assertIn(0x62, writer.scan_commands(payload))

    def test_an_irregular_wait_uses_the_16_bit_form(self):
        payload = writer.encode(timeline([(0, [(gb.NR52, 0x80)]), (1000, [])]))
        self.assertIn(0x61, writer.scan_commands(payload))

    def test_a_wait_longer_than_65535_samples_is_split(self):
        payload = writer.encode(
            timeline([(0, [(gb.NR52, 0x80)]), (200000, [(gb.NR50, 0x77)])])
        )
        self.assertTrue(writer.scan_commands(payload) <= writer.ALLOWED_COMMANDS)
        self.assertAlmostEqual(
            writer.duration_seconds(payload),
            (200000 + FRAME) / VGM_SAMPLE_RATE,
            places=4,
        )

    def test_the_stream_ends_with_the_end_of_data_command(self):
        payload = writer.encode(timeline([(0, [(gb.NR52, 0x80)])]))
        end = payload.index(b"Gd3 ")
        self.assertEqual(payload[end - 1], 0x66)


class RegisterRangeTests(unittest.TestCase):
    def test_accepts_the_audio_register_block(self):
        for register in range(0x00, 0x17):
            writer.encode(timeline([(0, [(register, 0x00)])]))

    def test_accepts_wave_ram(self):
        for register in range(0x20, 0x30):
            writer.encode(timeline([(0, [(register, 0x00)])]))

    def test_rejects_the_gap_retrosharp_does_not_map(self):
        for register in range(0x17, 0x20):
            with self.assertRaises(writer.GbVgmWriteError):
                writer.encode(timeline([(0, [(register, 0x00)])]))

    def test_rejects_registers_past_wave_ram(self):
        with self.assertRaises(writer.GbVgmWriteError):
            writer.encode(timeline([(0, [(0x30, 0x00)])]))


class LoopTests(unittest.TestCase):
    def test_a_file_without_a_loop_declares_no_loop(self):
        payload = writer.encode(timeline([(0, [(gb.NR52, 0x80)]), (FRAME, [])]))
        self.assertEqual(u32(payload, 0x1C), 0)
        self.assertEqual(u32(payload, 0x20), 0)

    def test_the_loop_offset_points_at_real_command_data(self):
        payload = writer.encode(
            timeline(
                [(0, [(gb.NR52, 0x80)]), (FRAME, [(gb.NR11, 0x80)]), (2 * FRAME, [])],
                loop_tick=1,
            )
        )
        absolute = u32(payload, 0x1C) + 0x1C
        self.assertGreaterEqual(absolute, writer.HEADER_SIZE)
        self.assertLess(absolute, len(payload))
        self.assertEqual(payload[absolute], 0xB3)

    def test_loop_samples_measure_the_tail_after_the_loop_point(self):
        payload = writer.encode(
            timeline(
                [(0, []), (FRAME, []), (2 * FRAME, [])],
                loop_tick=1,
                total_samples=3 * FRAME,
            )
        )
        self.assertEqual(u32(payload, 0x20), 2 * FRAME)


class Gd3Tests(unittest.TestCase):
    def test_the_gd3_sits_after_the_end_of_data_marker(self):
        payload = writer.encode(timeline([(0, [(gb.NR52, 0x80)])]))
        self.assertGreater(payload.index(b"Gd3 "), writer.HEADER_SIZE)
        self.assertEqual(u32(payload, 0x14) + 0x14, payload.index(b"Gd3 "))

    def test_the_system_is_relabelled_as_game_boy(self):
        payload = writer.encode(timeline([(0, [])]))
        text = payload[payload.index(b"Gd3 ") :].decode("utf-16-le", errors="replace")
        self.assertIn("Game Boy", text)

    def test_the_source_title_survives_the_conversion(self):
        source = testing.build_nes_vgm(
            [[(0x4015, 0x0F)]],
            gd3_fields=["Test Theme", "", "", "", "NES", "", "", "", "", "", ""],
        )
        from vgm_nes2gb import vgm_reader

        stream = vgm_reader.parse_bytes(source)
        payload = writer.encode(timeline([(0, [])]), stream.gd3)
        text = payload[payload.index(b"Gd3 ") :].decode("utf-16-le", errors="replace")
        self.assertIn("Test Theme", text)
        self.assertIn("Game Boy", text)

    def test_a_provenance_note_records_the_dropped_dpcm_channel(self):
        payload = writer.encode(timeline([(0, [])]))
        text = payload[payload.index(b"Gd3 ") :].decode("utf-16-le", errors="replace")
        self.assertIn("vgm-nes2gb", text)
        self.assertIn("DPCM", text)


class TimeOrderTests(unittest.TestCase):
    def test_ticks_that_move_backwards_are_rejected(self):
        with self.assertRaises(writer.GbVgmWriteError):
            writer.encode(timeline([(FRAME, []), (0, [])]))


def _write_demo(case: unittest.TestCase) -> str:
    import os
    import tempfile

    directory = tempfile.mkdtemp()
    case.addCleanup(lambda: __import__("shutil").rmtree(directory, ignore_errors=True))
    path = os.path.join(directory, "demo.vgm")
    with open(path, "wb") as handle:
        handle.write(testing.demo_vgm())
    return path


if __name__ == "__main__":
    unittest.main()
