import struct
import unittest

from vgm_nes2gb import gb_vgm_reader, nes_vgm_writer, testing, vgm_reader


class NesWriterTests(unittest.TestCase):
    def test_writes_a_base_nes_vgm_and_preserves_loop_and_metadata(self):
        timeline = nes_vgm_writer.NesTimeline(
            writes=[
                nes_vgm_writer.RegisterWrite(0, 0x15, 0x0F),
                nes_vgm_writer.RegisterWrite(735, 0x00, 0x30),
            ],
            total_samples=2 * testing.FRAME_SAMPLES,
            loop_sample=735,
        )
        source = gb_vgm_reader.parse_bytes(
            testing.build_gb_vgm(
                [[]], gd3_fields=["Song", "", "Game", "", "Game Boy"]
            )
        )
        payload = nes_vgm_writer.encode(timeline, source.gd3)
        self.assertEqual(struct.unpack_from("<I", payload, 0x84)[0], nes_vgm_writer.NES_CLOCK_HZ)
        self.assertEqual(nes_vgm_writer.scan_commands(payload), {0x62, 0x66, 0xB4})
        stream = vgm_reader.parse_bytes(payload)
        self.assertEqual(stream.loop_sample, 735)
        self.assertEqual([write.register for write in stream.writes], [0x15, 0x00])
        gd3 = payload[payload.index(b"Gd3 ") :].decode("utf-16-le", errors="replace")
        self.assertIn("NES", gd3)
        self.assertIn("Song", gd3)
        self.assertIn("Converted from Game Boy DMG", gd3)


if __name__ == "__main__":
    unittest.main()
