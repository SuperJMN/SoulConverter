"""Who gives up their channel, for how long, and whether they get it back.

The Game Boy has four generators and the NES was already using all four, so the
whole question is what the fifth voice is allowed to interrupt. These tests pin
the answers that matter musically: a free channel is always preferred, a
decorative hat is cheap enough to interrupt, a sustained bass note is not, and
nothing is ever left borrowed at the end.
"""

from __future__ import annotations

import os
import tempfile
import unittest

from vgm_nes2gb import allocator, nes_apu, nes_to_gb, testing, vgm_reader
from vgm_nes2gb import gameboy as gb
from vgm_nes2gb.dpcm import Role
from vgm_nes2gb.percussion import Host


def _timeline(frames, rom):
    handle, path = tempfile.mkstemp(suffix=".vgm")
    try:
        os.write(handle, testing.build_nes_vgm(frames, dpcm_rom=rom))
        os.close(handle)
        return nes_apu.simulate(vgm_reader.parse(path)), vgm_reader.parse(path).dpcm_rom
    finally:
        os.unlink(path)


def _pad(frames, count):
    frames.extend([[] for _ in range(count)])


class HostChoiceTests(unittest.TestCase):
    """Where a hit lands when the NES side is doing nothing else."""

    def setUp(self):
        rom, self.layout = testing.dpcm_kit()
        frames = [[(0x15, 0x0F), (0x17, 0x40)]]
        for piece in ("kick", "snare", "hat"):
            address, length = self.layout[piece]
            frames.append(testing.dpcm_trigger(address, length))
            _pad(frames, 29)
        self.timeline, self.rom = _timeline(frames, rom)

    def _plan(self, preset):
        return allocator.plan(self.timeline, self.rom, preset=preset)

    def test_an_idle_channel_is_taken_for_free(self):
        plan = self._plan("balanced")

        self.assertEqual(plan.stats.placed, 3)
        self.assertEqual(plan.stats.preempted_frames, {})
        self.assertTrue(all(voice.cost == 0.0 for voice in plan.voices))

    def test_the_safe_preset_only_ever_uses_the_noise_channel(self):
        plan = self._plan("safe")

        self.assertEqual({voice.host for voice in plan.voices}, {Host.CH4})

    def test_the_balanced_preset_gives_the_kick_a_tonal_channel(self):
        """A square with a downward sweep has a pitch a noise burst cannot."""
        plan = self._plan("balanced")

        kick = next(v for v in plan.voices if v.analysis.role is Role.KICK)
        self.assertTrue(kick.host.is_square)

    def test_the_off_preset_places_nothing_but_still_says_what_is_lost(self):
        """Knowing what is being dropped is how you decide whether to switch it on."""
        plan = self._plan("off")

        self.assertEqual(plan.voices, [])
        self.assertEqual(plan.borrowed_at(0), set())
        self.assertEqual(plan.stats.placed, 0)
        self.assertEqual(plan.stats.hits, 3)
        self.assertEqual(len(plan.analyses), 3)

    def test_every_hit_is_accounted_for(self):
        plan = self._plan("balanced")
        stats = plan.stats

        self.assertEqual(
            stats.hits,
            stats.placed
            + stats.dropped_artifact
            + stats.dropped_too_costly
            + stats.dropped_no_host,
        )


class CostTests(unittest.TestCase):
    """What the fifth voice is allowed to interrupt, and what it is not."""

    def _plan_over(self, background, preset="balanced", piece="snare"):
        rom, layout = testing.dpcm_kit()
        address, length = layout[piece]
        frames = [[(0x15, 0x0F), (0x17, 0x40)]]
        frames.append(background + testing.dpcm_trigger(address, length))
        _pad(frames, 29)
        timeline, parsed = _timeline(frames, rom)
        return allocator.plan(timeline, parsed, preset=preset)

    def test_a_one_frame_hat_is_cheap_enough_to_interrupt(self):
        """The measurement the whole design rests on: NES noise is decoration."""
        plan = self._plan_over(testing.noise_hit(period_index=2, volume=8))

        self.assertEqual(plan.stats.placed, 1)
        self.assertEqual(plan.voices[0].host, Host.CH4)

    def test_a_sustained_loud_noise_run_is_left_alone(self):
        held = testing.noise_hit(period_index=6, volume=15, decay=False)
        plan = self._plan_over(held, preset="safe")

        self.assertEqual(plan.stats.placed, 0)
        self.assertEqual(plan.stats.dropped_too_costly, 1)

    def test_a_sustained_bass_line_is_not_taken_for_a_kick(self):
        busy = (
            testing.pulse_note(0, 220.0, 15, testing.DUTY_50)
            + testing.pulse_note(1, 330.0, 15, testing.DUTY_50)
            + testing.triangle_note(82.41)
            + testing.noise_hit(period_index=6, volume=15, decay=False)
        )
        plan = self._plan_over(busy, piece="kick")

        for voice in plan.voices:
            self.assertEqual(voice.host, Host.CH4)

    def test_the_bolder_preset_pays_more_for_the_same_hit(self):
        busy = testing.pulse_note(0, 220.0, 12, testing.DUTY_50)
        balanced = self._plan_over(busy, preset="balanced", piece="kick")
        full = self._plan_over(busy, preset="full", piece="kick")

        self.assertGreaterEqual(
            full.voices[0].benefit if full.voices else 0.0,
            balanced.voices[0].benefit if balanced.voices else 0.0,
        )

    def test_an_artifact_is_dropped_rather_than_played(self):
        payload = testing.encode_dpcm(testing.synth_dac_blip())
        rom = {0xC000: payload}
        frames = [[(0x15, 0x0F), (0x17, 0x40)], testing.dpcm_trigger(0xC000, len(payload))]
        _pad(frames, 29)
        timeline, parsed = _timeline(frames, rom)

        plan = allocator.plan(timeline, parsed, preset="full")

        self.assertEqual(plan.stats.dropped_artifact, 1)
        self.assertEqual(plan.voices, [])

    def test_a_hand_written_role_changes_the_placement(self):
        rom, layout = testing.dpcm_kit()
        address, length = layout["hat"]
        frames = [[(0x15, 0x0F), (0x17, 0x40)], testing.dpcm_trigger(address, length)]
        _pad(frames, 59)
        timeline, parsed = _timeline(frames, rom)

        plan = allocator.plan(
            timeline, parsed, preset="full", overrides={address: Role.TONAL}
        )

        self.assertTrue(plan.voices)
        self.assertEqual(plan.voices[0].analysis.role, Role.TONAL)


class MonophonyTests(unittest.TestCase):
    """The DMC played one sample at a time, and so must its stand-in."""

    def test_a_hit_is_truncated_by_the_next_one(self):
        rom, layout = testing.dpcm_kit()
        address, length = layout["kick"]
        frames = [[(0x15, 0x0F), (0x17, 0x40)]]
        for _ in range(4):
            frames.append(testing.dpcm_trigger(address, length))
            _pad(frames, 2)
        _pad(frames, 30)
        timeline, parsed = _timeline(frames, rom)

        plan = allocator.plan(timeline, parsed, preset="full")

        starts = sorted(voice.tick for voice in plan.voices if voice.layer_of is None)
        for voice in plan.voices:
            later = [tick for tick in starts if tick > voice.tick]
            if later:
                self.assertLessEqual(voice.end, later[0])

    def test_no_two_voices_share_a_channel_at_the_same_time(self):
        stream = self._demo()
        plan = allocator.plan(*stream, preset="full")

        for host in Host:
            spans = [
                range(v.tick, v.end) for v in plan.voices if v.host is host
            ]
            occupied: set[int] = set()
            for span in spans:
                self.assertFalse(occupied & set(span))
                occupied |= set(span)

    @staticmethod
    def _demo():
        handle, path = tempfile.mkstemp(suffix=".vgm")
        try:
            os.write(handle, testing.dpcm_demo_vgm())
            os.close(handle)
            stream = vgm_reader.parse(path)
            return nes_apu.simulate(stream), stream.dpcm_rom
        finally:
            os.unlink(path)


class RestorationTests(unittest.TestCase):
    """A borrowed channel is always handed back in a usable state.

    The song here leaves the tonal channels idle at the downbeats so the kick
    actually reaches CH1, and starts a pulse note right after each borrowed
    span so there is something whose return can be checked.
    """

    def setUp(self):
        rom, layout = testing.dpcm_kit()
        address, length = layout["kick"]
        frames = [[(0x15, 0x0F), (0x17, 0x40)]]
        for _ in range(3):
            frames.append(testing.pulse_off(0) + testing.dpcm_trigger(address, length))
            _pad(frames, 7)
            frames.append(
                testing.pulse_note(0, 261.63, 10, testing.DUTY_50)
                + testing.triangle_note(98.0)
            )
            _pad(frames, 15)
        frames.append(testing.pulse_off(0) + testing.triangle_off())
        _pad(frames, 8)
        self.timeline, self.rom = _timeline(frames, rom)

    def _convert(self, preset):
        plan = allocator.plan(self.timeline, self.rom, preset=preset)
        return plan, nes_to_gb.convert(self.timeline, dpcm_plan=plan)

    def test_every_borrowed_span_ends_with_a_release(self):
        plan, converted = self._convert("full")

        self.assertGreater(len(plan.voices), 0)
        self.assertEqual(
            converted.stats.dpcm_restores,
            sum(len(plan.released_at(tick)) for tick in range(len(self.timeline.ticks))),
        )

    def test_nothing_is_still_borrowed_when_the_song_ends(self):
        plan, _ = self._convert("full")
        last = len(self.timeline.ticks) - 1

        self.assertEqual(plan.borrowed_at(last + 1), set())

    def test_giving_back_ch1_clears_the_sweep_that_would_detune_it(self):
        """NR10 survives a trigger, so a drum's sweep would bend the next note."""
        plan, converted = self._convert("full")
        borrowed_ch1 = [v for v in plan.voices if v.host is Host.CH1]
        self.assertTrue(borrowed_ch1, "fixture no longer exercises CH1")

        for voice in borrowed_ch1:
            writes = dict(converted.ticks[voice.end].writes)
            self.assertEqual(writes.get(gb.NR10), 0x00)

    def test_the_interrupted_channel_is_written_again_rather_than_left_silent(self):
        plan, converted = self._convert("full")

        for voice in plan.voices:
            source = allocator.HOST_TO_NES[voice.host]
            if voice.end >= len(self.timeline.ticks):
                continue
            after = self.timeline.ticks[voice.end]
            if not getattr(after, source).playing:
                continue
            with self.subTest(tick=voice.tick, host=voice.host.value):
                self.assertTrue(converted.ticks[voice.end].writes)

    def test_the_borrowed_channel_is_not_driven_by_the_nes_part_meanwhile(self):
        """Two writers on one channel would fight and produce neither sound."""
        plan, converted = self._convert("full")
        owned = {
            Host.CH1: (gb.NR11, gb.NR13, gb.NR14),
            Host.CH2: (gb.NR21, gb.NR23, gb.NR24),
            Host.CH3: (gb.NR32, gb.NR33, gb.NR34),
            Host.CH4: (gb.NR41, gb.NR43, gb.NR44),
        }

        for voice in plan.voices:
            for tick in range(voice.tick + 1, voice.end):
                written = {register for register, _ in converted.ticks[tick].writes}
                with self.subTest(tick=tick, host=voice.host.value):
                    self.assertFalse(written & set(owned[voice.host]))


class OccupancyTests(unittest.TestCase):
    """The cost model's view of what the NES side is doing."""

    def test_a_burst_is_measured_from_its_start_to_its_end(self):
        lengths = allocator._Occupancy._burst_lengths([0.0, 1.0, 1.0, 0.0, 0.5])

        self.assertEqual(lengths, [0, 2, 2, 0, 1])

    def test_silence_costs_nothing(self):
        occupancy = allocator._Occupancy(
            volume={host: [0.0] * 8 for host in Host},
            attack={host: [False] * 8 for host in Host},
            burst={host: [0] * 8 for host in Host},
        )

        self.assertEqual(occupancy.cost(Host.CH2, 0, 8), 0.0)

    def test_a_long_noise_run_is_priced_far_above_a_decorative_one(self):
        def occupancy(frames):
            levels = [1.0] * frames
            return allocator._Occupancy(
                volume={host: levels for host in Host},
                attack={host: [False] * frames for host in Host},
                burst={host: [frames] * frames for host in Host},
            )

        decorative = occupancy(2).cost(Host.CH4, 0, 2)
        sustained = occupancy(30).cost(Host.CH4, 0, 2)

        self.assertAlmostEqual(
            decorative, sustained * allocator.DECORATIVE_NOISE_DISCOUNT
        )

    def test_cutting_a_note_off_at_its_attack_costs_more_than_at_its_tail(self):
        frames = 4
        base = {host: [1.0] * frames for host in Host}
        attacking = allocator._Occupancy(
            volume=base,
            attack={host: [True] + [False] * (frames - 1) for host in Host},
            burst={host: [frames] * frames for host in Host},
        )
        steady = allocator._Occupancy(
            volume=base,
            attack={host: [False] * frames for host in Host},
            burst={host: [frames] * frames for host in Host},
        )

        self.assertGreater(
            attacking.cost(Host.CH2, 0, frames), steady.cost(Host.CH2, 0, frames)
        )

    def test_a_fading_noise_run_is_decoration_however_long_its_tail(self):
        """A struck hit rings on; what makes it decoration is that it fades."""
        frames = 12
        fading = [1.0 * (0.8**n) for n in range(frames)]
        held = [1.0] * frames

        def occupancy(levels):
            return allocator._Occupancy(
                volume={host: levels for host in Host},
                attack={host: [False] * frames for host in Host},
                burst={host: [frames] * frames for host in Host},
            )

        self.assertLess(
            occupancy(fading).cost(Host.CH4, 0, 1),
            occupancy(held).cost(Host.CH4, 0, 1),
        )

    def test_cost_per_frame_is_what_a_benefit_per_hit_compares_against(self):
        frames = 20
        occupancy = allocator._Occupancy(
            volume={host: [0.5] * frames for host in Host},
            attack={host: [False] * frames for host in Host},
            burst={host: [frames] * frames for host in Host},
        )

        self.assertAlmostEqual(
            occupancy.rate(Host.CH2, 0, frames),
            occupancy.cost(Host.CH2, 0, frames) / frames,
        )
        # The point of the rate: a long note is not unaffordable merely for
        # being long, which is what comparing an integral with an instant did.
        self.assertAlmostEqual(
            occupancy.rate(Host.CH2, 0, frames), occupancy.rate(Host.CH2, 0, 2)
        )


class PlacementTradeOffTests(unittest.TestCase):
    """Timbre decides between comparable hosts; a much cheaper host overrules it."""

    def _plan(self, background, preset="full"):
        """A three-note bass line, which is what makes it read as an instrument."""
        rom, layout = _bass_kit()
        address, length = layout["bass"]
        frames = [[(0x15, 0x0F), (0x17, 0x40)]]
        for rate in (15, 14, 12):
            frames.append(
                list(background) + testing.dpcm_trigger(address, length, rate_index=rate)
            )
            _pad(frames, 24)
        _pad(frames, 30)
        timeline, parsed = _timeline(frames, rom)
        return allocator.plan(timeline, parsed, preset=preset)

    def test_the_line_is_recognised_as_melodic_before_anything_is_placed(self):
        plan = self._plan([])

        self.assertTrue(plan.voices)
        self.assertEqual({v.analysis.role for v in plan.voices}, {Role.TONAL})

    def test_a_bass_note_prefers_the_wave_channel_when_nothing_is_playing(self):
        plan = self._plan([])

        primary = [v for v in plan.voices if v.layer_of is None]
        self.assertEqual({v.host for v in primary}, {Host.CH3})

    def test_it_moves_aside_rather_than_evict_a_busy_triangle(self):
        """The failure this fixes: the bass line silenced the triangle all song."""
        plan = self._plan(testing.triangle_note(147.0))

        primary = [v for v in plan.voices if v.layer_of is None]
        self.assertTrue(primary)
        self.assertNotIn(Host.CH3, {v.host for v in primary})

    def test_a_sustained_note_is_affordable_at_all(self):
        """Integrating cost while benefit stayed flat made every long note lose."""
        plan = self._plan([])

        self.assertGreater(plan.voices[0].frames, 4)

    def test_a_note_never_outlasts_the_sample_it_stands_in_for(self):
        rom, layout = _bass_kit()
        address, length = layout["bass"]
        frames = [[(0x15, 0x0F), (0x17, 0x40)]]
        for rate in (15, 14, 12):
            frames.append(testing.dpcm_trigger(address, length, rate_index=rate))
            _pad(frames, 120)
        timeline, parsed = _timeline(frames, rom)

        plan = allocator.plan(timeline, parsed, preset="full")

        self.assertTrue(plan.voices)
        for voice in plan.voices:
            sample_frames = voice.analysis.decoded.duration_seconds * 60.0
            with self.subTest(tick=voice.tick):
                self.assertLessEqual(voice.frames, round(sample_frames) + 1)
                self.assertLess(voice.frames, 100)


def _bass_kit():
    """One plucked bass sample, played at enough rates to read as an instrument."""
    payload = testing.encode_dpcm(testing.synth_bass_note())
    return {0xC000: payload}, {"bass": (0xC000, len(payload))}


if __name__ == "__main__":  # pragma: no cover
    unittest.main()
