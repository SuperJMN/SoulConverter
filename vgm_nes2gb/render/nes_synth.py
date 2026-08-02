"""Render the NES side of a conversion from the sampled APU state.

This is a reference render used for comparison only, not an emulator: it takes
the per-tick musical state produced by :mod:`vgm_nes2gb.nes_apu` and drives
phase-continuous oscillators through the NES non-linear mixer.
"""

from __future__ import annotations

import numpy as np

from ..nes_apu import ApuTimeline
from .oscillators import (
    SAMPLE_RATE,
    TRIANGLE_SEQUENCE,
    lfsr_sequence,
    noise_block,
    square_block,
    wave_block,
)

NOISE_LONG = lfsr_sequence(bits=15, tap=1)
NOISE_SHORT = lfsr_sequence(bits=15, tap=6)


def render(timeline: ApuTimeline, dpcm_rom=None) -> np.ndarray:
    """Render ``timeline`` to a mono float array at 44100 Hz.

    Passing ``dpcm_rom`` makes the reference include the sample channel. It
    matters: without it the reference has no drums, so any comparison flatters a
    conversion that also has none, and the metric that is supposed to show what
    the Game Boy is missing shows nothing at all.
    """
    total = max(timeline.total_samples, timeline.ticks[-1].sample if timeline.ticks else 0)
    pulse1 = np.zeros(total)
    pulse2 = np.zeros(total)
    triangle = np.zeros(total)
    noise = np.zeros(total)
    dmc = _render_dpcm(timeline, dpcm_rom, total)

    phases = {"pulse1": 0.0, "pulse2": 0.0, "triangle": 0.0, "noise": 0.0}

    for index, tick in enumerate(timeline.ticks):
        start = tick.sample
        end = timeline.ticks[index + 1].sample if index + 1 < len(timeline.ticks) else total
        count = max(0, min(end, total) - start)
        if count == 0:
            continue

        block, phases["pulse1"] = square_block(
            count, phases["pulse1"], tick.pulse1.frequency_hz, tick.pulse1.duty
        )
        if tick.pulse1.playing:
            pulse1[start : start + count] = block * tick.pulse1.volume

        block, phases["pulse2"] = square_block(
            count, phases["pulse2"], tick.pulse2.frequency_hz, tick.pulse2.duty
        )
        if tick.pulse2.playing:
            pulse2[start : start + count] = block * tick.pulse2.volume

        block, phases["triangle"] = wave_block(
            count, phases["triangle"], tick.triangle.frequency_hz, TRIANGLE_SEQUENCE
        )
        if tick.triangle.playing:
            triangle[start : start + count] = block

        sequence = NOISE_SHORT if tick.noise.mode_short else NOISE_LONG
        block, phases["noise"] = noise_block(
            count, phases["noise"], tick.noise.shift_rate_hz, sequence
        )
        if tick.noise.playing:
            noise[start : start + count] = block * tick.noise.volume

    return mix(pulse1, pulse2, triangle, noise, dmc)


def _render_dpcm(timeline: ApuTimeline, rom, total: int) -> np.ndarray:
    """Replay each triggered sample at its own rate, resampled to 44100 Hz.

    The DMC was monophonic, so a sample is cut where the next one starts rather
    than mixed with it.
    """
    out = np.zeros(total)
    if rom is None or not rom:
        return out

    from ..dpcm import decode

    starts = [
        (tick.sample, tick.dmc.sample)
        for tick in timeline.ticks
        if tick.dmc.started and tick.dmc.sample is not None
    ]
    cache: dict = {}
    for position, (start, ref) in enumerate(starts):
        if ref not in cache:
            waveform = decode(rom, ref).waveform
            length = int(round(waveform.size * SAMPLE_RATE / ref.rate_hz))
            positions = np.linspace(0, waveform.size - 1, max(1, length))
            # The DMC output is a 7-bit level; the mixer expects that scale.
            cache[ref] = np.interp(
                positions, np.arange(waveform.size), waveform
            ) * 64.0 + 64.0
        block = cache[ref]
        limit = starts[position + 1][0] if position + 1 < len(starts) else total
        count = max(0, min(start + block.size, limit, total) - start)
        if count > 0:
            out[start : start + count] = block[:count]
    return out


def mix(
    pulse1: np.ndarray,
    pulse2: np.ndarray,
    triangle: np.ndarray,
    noise: np.ndarray,
    dmc: np.ndarray | None = None,
) -> np.ndarray:
    """The 2A03 non-linear mixer, as documented on nesdev."""
    pulse_sum = pulse1 + pulse2
    if dmc is None:
        dmc = np.zeros_like(triangle)
    with np.errstate(divide="ignore", invalid="ignore"):
        pulse_out = np.where(
            pulse_sum > 0, 95.88 / (8128.0 / np.maximum(pulse_sum, 1e-9) + 100.0), 0.0
        )
        tnd = triangle / 8227.0 + noise / 12241.0 + dmc / 22638.0
        tnd_out = np.where(
            tnd > 0, 159.79 / (1.0 / np.maximum(tnd, 1e-12) + 100.0), 0.0
        )
    return pulse_out + tnd_out


def channel_frequencies(timeline: ApuTimeline) -> dict[str, np.ndarray]:
    """Per-tick frequency of each channel, with 0 where the channel is silent."""
    return {
        "pulse1": np.array(
            [t.pulse1.frequency_hz if t.pulse1.playing else 0.0 for t in timeline.ticks]
        ),
        "pulse2": np.array(
            [t.pulse2.frequency_hz if t.pulse2.playing else 0.0 for t in timeline.ticks]
        ),
        "triangle": np.array(
            [
                t.triangle.frequency_hz if t.triangle.playing else 0.0
                for t in timeline.ticks
            ]
        ),
        "noise": np.array(
            [t.noise.shift_rate_hz if t.noise.playing else 0.0 for t in timeline.ticks]
        ),
    }


def channel_volumes(timeline: ApuTimeline) -> dict[str, np.ndarray]:
    return {
        "pulse1": np.array(
            [t.pulse1.volume if t.pulse1.playing else 0 for t in timeline.ticks],
            dtype=float,
        ),
        "pulse2": np.array(
            [t.pulse2.volume if t.pulse2.playing else 0 for t in timeline.ticks],
            dtype=float,
        ),
        "triangle": np.array(
            [15.0 if t.triangle.playing else 0.0 for t in timeline.ticks]
        ),
        "noise": np.array(
            [t.noise.volume if t.noise.playing else 0 for t in timeline.ticks],
            dtype=float,
        ),
    }


__all__ = [
    "SAMPLE_RATE",
    "channel_frequencies",
    "channel_volumes",
    "mix",
    "render",
]
