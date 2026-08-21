import unittest

from vgm_nes2gb import testing
from vgm_nes2gb.gb_vgm_reader import parse_bytes
from vgm_nes2gb.vgm_reader import UnsupportedChipError


class GbReaderTests(unittest.TestCase):
    def test_reads_dmg_clock_writes_timing_and_loop(self):
        payload = testing.build_gb_vgm(
            [[(0x16, 0x80)], [(0x01, 0x80), (0x02, 0xF0)]],
            loop_frame=1,
        )
        stream = parse_bytes(payload)
        self.assertEqual(stream.cpu_clock_hz, testing.GB_CLOCK_HZ)
        self.assertEqual([write.sample for write in stream.writes], [0, 735, 735])
        self.assertEqual(stream.loop_sample, 735)
        self.assertEqual(stream.total_samples, 2 * testing.FRAME_SAMPLES)

    def test_rejects_a_nes_vgm(self):
        with self.assertRaises(UnsupportedChipError) as raised:
            parse_bytes(testing.demo_vgm())
        self.assertIn("Game Boy DMG", str(raised.exception))


if __name__ == "__main__":
    unittest.main()
