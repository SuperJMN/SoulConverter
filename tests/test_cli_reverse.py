import gzip
import os
import struct
import tempfile
import unittest

from vgm_nes2gb import cli, testing, vgm_reader


class ReverseCliTests(unittest.TestCase):
    def test_auto_detects_game_boy_and_can_write_vgz(self):
        with tempfile.TemporaryDirectory() as directory:
            source = os.path.join(directory, "track.vgz")
            destination = os.path.join(directory, "track.nes.vgz")
            with open(source, "wb") as handle:
                handle.write(gzip.compress(testing.build_gb_vgm([[(0x16, 0x80)]])))
            self.assertEqual(
                cli.main(["convert", source, "-o", destination, "--quiet"]), 0
            )
            with open(destination, "rb") as handle:
                payload = gzip.decompress(handle.read())
            self.assertEqual(payload[:4], b"Vgm ")
            self.assertEqual(struct.unpack_from("<I", payload, 0x80)[0], 0)
            self.assertEqual(struct.unpack_from("<I", payload, 0x84)[0], 1789773)
            self.assertEqual(vgm_reader.parse_bytes(payload).cpu_clock_hz, 1789773)


if __name__ == "__main__":
    unittest.main()
