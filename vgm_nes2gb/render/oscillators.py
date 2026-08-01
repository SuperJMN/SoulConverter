"""Shared oscillator helpers for the NES and Game Boy software synthesisers."""

from __future__ import annotations

import numpy as np

SAMPLE_RATE = 44100

# 32-step 4-bit triangle, exactly the NES triangle channel sequence.
TRIANGLE_SEQUENCE = np.array(
    list(range(15, -1, -1)) + list(range(0, 16)), dtype=np.float64
)

DUTY_RATIOS = (0.125, 0.25, 0.5, 0.75)


def square_block(count: int, phase: float, frequency: float, duty: int):
    """Render ``count`` samples of a duty-cycled square in the 0..1 range."""
    if count <= 0:
        return np.zeros(0), phase
    increment = frequency / SAMPLE_RATE
    phases = phase + increment * np.arange(count, dtype=np.float64)
    fraction = np.mod(phases, 1.0)
    output = (fraction < DUTY_RATIOS[duty & 0x03]).astype(np.float64)
    return output, float(np.mod(phase + increment * count, 1.0))


def wave_block(count: int, phase: float, frequency: float, table: np.ndarray):
    """Render ``count`` samples of a 32-entry wavetable, values as given."""
    if count <= 0:
        return np.zeros(0), phase
    increment = frequency / SAMPLE_RATE
    phases = phase + increment * np.arange(count, dtype=np.float64)
    indices = np.floor(np.mod(phases, 1.0) * table.size).astype(np.int64)
    return table[indices], float(np.mod(phase + increment * count, 1.0))


def lfsr_sequence(bits: int, tap: int) -> np.ndarray:
    """Precompute one full period of an LFSR output sequence.

    The register is ``bits`` wide, feedback is ``bit0 XOR bit<tap>`` and the
    audible output is the inverted bit 0, which is how both the NES and the
    Game Boy noise channels work.
    """
    register = (1 << bits) - 1
    seen_start = register
    values = []
    while True:
        values.append(1.0 if (register & 1) == 0 else 0.0)
        feedback = (register & 1) ^ ((register >> tap) & 1)
        register = (register >> 1) | (feedback << (bits - 1))
        if register == seen_start:
            break
    return np.array(values, dtype=np.float64)


def noise_block(count: int, position: float, rate_hz: float, sequence: np.ndarray):
    """Render ``count`` samples of an LFSR clocked at ``rate_hz``."""
    if count <= 0:
        return np.zeros(0), position
    step = rate_hz / SAMPLE_RATE
    positions = position + step * np.arange(count, dtype=np.float64)
    indices = np.floor(positions).astype(np.int64) % sequence.size
    return sequence[indices], float((position + step * count) % sequence.size)
