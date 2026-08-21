import unittest

from vgm_nes2gb import gb_to_nes, gameboy, nes_vgm_writer, testing, vgm_reader
from vgm_nes2gb.gb_vgm_reader import parse_bytes


def source_payload():
    pulse_period = 1000
    wave_period = 700
    return testing.build_gb_vgm(
        [
            [
                (gameboy.NR52, 0x80),
                (gameboy.NR50, 0x77),
                (gameboy.NR51, 0xFF),
                (gameboy.WAVE_RAM_BASE, 0xF0),
                (gameboy.NR11, 0x80),
                (gameboy.NR12, 0xF0),
                (gameboy.NR13, pulse_period & 0xFF),
                (gameboy.NR14, 0x80 | (pulse_period >> 8)),
                (gameboy.NR21, 0x40),
                (gameboy.NR22, 0xA0),
                (gameboy.NR23, pulse_period & 0xFF),
                (gameboy.NR24, 0x80 | (pulse_period >> 8)),
                (gameboy.NR30, 0x80),
                (gameboy.NR32, 0x60),
                (gameboy.NR33, wave_period & 0xFF),
                (gameboy.NR34, 0x80 | (wave_period >> 8)),
                (gameboy.NR42, 0xF0),
                (gameboy.NR43, 0x8D),
                (gameboy.NR44, 0x80),
            ],
            [
                (gameboy.NR13, (pulse_period + 2) & 0xFF),
                (gameboy.NR14, (pulse_period + 2) >> 8),
                (gameboy.NR33, (wave_period + 3) & 0xFF),
                (gameboy.NR34, (wave_period + 3) >> 8),
            ],
            [(gameboy.NR44, 0x80)],
            [(gameboy.NR52, 0x00)],
        ],
        loop_frame=1,
        gd3_fields=["Song", "", "Snow Bros. Jr.", "", "Game Boy"],
    )


class GbToNesTests(unittest.TestCase):
    def test_maps_all_four_dmg_channels_and_keeps_duration(self):
        source = parse_bytes(source_payload())
        timeline, stats = gb_to_nes.convert(source)
        self.assertEqual(timeline.total_samples, source.total_samples)
        self.assertEqual(timeline.loop_sample, source.loop_sample)
        self.assertGreater(len(timeline.writes), 0)
        self.assertEqual(stats.wave_ram_writes, 1)
        self.assertGreater(stats.pulse_periods, 0)
        self.assertGreater(stats.wave_periods, 0)
        self.assertGreater(stats.noise_periods, 0)

        registers = {write.register for write in timeline.writes}
        self.assertTrue({0x00, 0x04, 0x08, 0x0C, 0x15} <= registers)
        payload = nes_vgm_writer.encode(timeline, source.gd3)
        imported = vgm_reader.parse_bytes(payload)
        self.assertEqual(imported.total_samples, source.total_samples)
        self.assertEqual(imported.loop_sample, source.loop_sample)
        self.assertEqual(imported.cpu_clock_hz, nes_vgm_writer.NES_CLOCK_HZ)

    def test_file_conversion_renders_both_sides(self):
        import os
        import tempfile

        with tempfile.TemporaryDirectory() as directory:
            path = os.path.join(directory, "song.vgm")
            with open(path, "wb") as handle:
                handle.write(source_payload())
            result = gb_to_nes.convert_file(path)
        self.assertGreater(result.gb_audio.size, 0)
        self.assertEqual(result.gb_audio.shape, result.nes_audio.shape)
        self.assertIn("wave", result.report["mapping"])


if __name__ == "__main__":
    unittest.main()
