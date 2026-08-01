import gzip
import struct
import unittest

from vgm_nes2gb import testing
from vgm_nes2gb.vgm_reader import (
    UnsupportedChipError,
    VgmFormatError,
    parse_bytes,
    read_bytes,
)


def minimal_vgm(commands: bytes, *, nes_clock: int = 1789772, extra_clocks=()) -> bytes:
    header = bytearray(0x100)
    header[0:4] = b"Vgm "
    struct.pack_into("<I", header, 0x08, 0x00000161)
    struct.pack_into("<I", header, 0x18, 735)
    struct.pack_into("<I", header, 0x34, 0x100 - 0x34)
    struct.pack_into("<I", header, 0x84, nes_clock)
    for offset, value in extra_clocks:
        struct.pack_into("<I", header, offset, value)
    struct.pack_into("<I", header, 0x04, 0x100 + len(commands) - 4)
    return bytes(header) + commands


class HeaderTests(unittest.TestCase):
    def test_rejects_files_without_the_vgm_signature(self):
        with self.assertRaises(VgmFormatError):
            parse_bytes(b"NOPE" + bytes(0x100))

    def test_rejects_files_without_a_nes_clock(self):
        with self.assertRaises(UnsupportedChipError) as raised:
            parse_bytes(minimal_vgm(b"\x66", nes_clock=0))
        self.assertIn("2A03", str(raised.exception))

    def test_rejects_a_declared_expansion_or_foreign_chip(self):
        with self.assertRaises(UnsupportedChipError) as raised:
            parse_bytes(minimal_vgm(b"\x66", extra_clocks=[(0x80, 4194304)]))
        self.assertIn("Game Boy DMG", str(raised.exception))

    def test_reads_the_loop_point(self):
        stream = parse_bytes(testing.demo_vgm(loop=True))
        self.assertEqual(stream.loop_sample, testing.FRAME_SAMPLES)

    def test_no_loop_when_the_source_declares_none(self):
        self.assertIsNone(parse_bytes(testing.demo_vgm(loop=False)).loop_sample)

    def test_ignores_header_fields_that_overlap_the_command_data(self):
        # A short 0x40-byte header must not read chip clocks out of the commands.
        header = bytearray(0x40)
        header[0:4] = b"Vgm "
        struct.pack_into("<I", header, 0x08, 0x00000101)
        struct.pack_into("<I", header, 0x34, 0)
        payload = bytes(header) + b"\x66"
        with self.assertRaises(UnsupportedChipError) as raised:
            parse_bytes(payload)
        self.assertIn("2A03", str(raised.exception))


class CommandTests(unittest.TestCase):
    def test_accumulates_every_wait_command(self):
        commands = (
            b"\xb4\x00\x01"      # write at sample 0
            b"\x61\x10\x00"      # wait 16
            b"\x62"              # wait 735
            b"\x63"              # wait 882
            b"\x73"              # wait 4
            b"\xb4\x00\x02"
            b"\x66"
        )
        stream = parse_bytes(minimal_vgm(commands))
        self.assertEqual([w.sample for w in stream.writes], [0, 16 + 735 + 882 + 4])

    def test_drops_and_counts_dpcm_register_writes(self):
        commands = b"\xb4\x10\x0f\xb4\x12\x40\xb4\x00\x01\x66"
        stream = parse_bytes(minimal_vgm(commands))
        self.assertEqual([w.register for w in stream.writes], [0x00])
        self.assertEqual(stream.dropped.dpcm_register_writes, 2)

    def test_drops_and_counts_dpcm_data_blocks(self):
        block = b"\x67\x66\x07" + struct.pack("<I", 4) + b"\xde\xad\xbe\xef"
        stream = parse_bytes(minimal_vgm(block + b"\x66"))
        self.assertEqual(stream.dropped.dpcm_block_count, 1)
        self.assertEqual(stream.dropped.dpcm_block_bytes, 4)

    def test_rejects_expansion_audio_registers(self):
        with self.assertRaises(UnsupportedChipError) as raised:
            parse_bytes(minimal_vgm(b"\xb4\xa0\x00\x66"))
        self.assertIn("VRC6", str(raised.exception))

    def test_rejects_foreign_chip_commands(self):
        with self.assertRaises(UnsupportedChipError):
            parse_bytes(minimal_vgm(b"\x50\x00\x66"))

    def test_rejects_a_stream_without_an_end_marker(self):
        with self.assertRaises(VgmFormatError):
            parse_bytes(minimal_vgm(b"\xb4\x00\x01"))

    def test_rejects_a_truncated_command(self):
        with self.assertRaises(VgmFormatError):
            parse_bytes(minimal_vgm(b"\xb4\x00"))


class CompressionTests(unittest.TestCase):
    def test_reads_gzip_compressed_sources(self):
        import os
        import tempfile

        payload = testing.demo_vgm()
        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "song.vgz")
            with open(path, "wb") as handle:
                handle.write(gzip.compress(payload))
            self.assertEqual(read_bytes(path), payload)


if __name__ == "__main__":
    unittest.main()
