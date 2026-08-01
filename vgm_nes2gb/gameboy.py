"""Game Boy DMG APU facts: register indices, frequency maths and noise mapping.

Register indices are VGM ``0xB3`` register numbers, i.e. ``address - 0xFF10``.
Only ``0x00-0x16`` and ``0x20-0x2F`` exist, which is exactly the range accepted by
RetroSharp's ``VgmImporter``.
"""

from __future__ import annotations

import math

GB_CLOCK_HZ = 4194304

NR10, NR11, NR12, NR13, NR14 = 0x00, 0x01, 0x02, 0x03, 0x04
NR21, NR22, NR23, NR24 = 0x06, 0x07, 0x08, 0x09
NR30, NR31, NR32, NR33, NR34 = 0x0A, 0x0B, 0x0C, 0x0D, 0x0E
NR41, NR42, NR43, NR44 = 0x10, 0x11, 0x12, 0x13
NR50, NR51, NR52 = 0x14, 0x15, 0x16
WAVE_RAM_BASE = 0x20
WAVE_RAM_SIZE = 16

SQUARE_FREQ_NUMERATOR = 131072.0
WAVE_FREQ_NUMERATOR = 65536.0

SQUARE_MIN_HZ = SQUARE_FREQ_NUMERATOR / 2048.0  # 64 Hz
WAVE_MIN_HZ = WAVE_FREQ_NUMERATOR / 2048.0      # 32 Hz

NOISE_MAX_SHIFT = 13

# A 32-step 4-bit triangle, matching the NES triangle channel's own sequence.
TRIANGLE_WAVE_NIBBLES = tuple(list(range(15, -1, -1)) + list(range(0, 16)))


def pack_wave(nibbles) -> bytes:
    """Pack 32 nibbles into the 16 bytes of Game Boy wave RAM."""
    values = list(nibbles)
    if len(values) != 32:
        raise ValueError("wave RAM needs exactly 32 nibbles")
    if any(not 0 <= v <= 15 for v in values):
        raise ValueError("wave RAM nibbles must be in 0..15")
    return bytes(
        (values[i * 2] << 4) | values[i * 2 + 1] for i in range(WAVE_RAM_SIZE)
    )


def _period(frequency_hz: float, numerator: float, minimum_hz: float, allow_transpose: bool):
    """Return ``(period, octaves_up)`` for a target frequency.

    The Game Boy bottoms out at 64 Hz (square) / 32 Hz (wave), below the NES.
    Notes under that floor are transposed up whole octaves so they stay in tune,
    and the shift is reported so the conversion report can flag it.
    """
    if frequency_hz <= 0:
        return 0, 0

    octaves = 0
    frequency = frequency_hz
    if allow_transpose:
        while frequency < minimum_hz:
            frequency *= 2.0
            octaves += 1

    period = int(round(2048.0 - numerator / frequency))
    return max(0, min(2047, period)), octaves


def square_period(frequency_hz: float, allow_transpose: bool = True):
    return _period(frequency_hz, SQUARE_FREQ_NUMERATOR, SQUARE_MIN_HZ, allow_transpose)


def wave_period(frequency_hz: float, allow_transpose: bool = True):
    return _period(frequency_hz, WAVE_FREQ_NUMERATOR, WAVE_MIN_HZ, allow_transpose)


def square_frequency(period: int) -> float:
    return SQUARE_FREQ_NUMERATOR / (2048 - period) if period < 2048 else 0.0


def wave_frequency(period: int) -> float:
    return WAVE_FREQ_NUMERATOR / (2048 - period) if period < 2048 else 0.0


def noise_frequency(shift: int, divisor_code: int) -> float:
    divisor = 0.5 if divisor_code == 0 else float(divisor_code)
    return 262144.0 / (divisor * (2**shift))


def _build_noise_table():
    entries = []
    for shift in range(NOISE_MAX_SHIFT + 1):
        for divisor_code in range(8):
            entries.append((noise_frequency(shift, divisor_code), shift, divisor_code))
    entries.sort()
    return tuple(entries)


NOISE_TABLE = _build_noise_table()


def nearest_noise(target_hz: float):
    """Pick the ``(shift, divisor_code)`` whose rate is closest to ``target_hz``.

    Closeness is measured in octaves, so the error is perceptually even across
    the whole range rather than biased towards the high end.
    """
    if target_hz <= 0:
        return 0, 0, 0.0
    best = None
    for frequency, shift, divisor_code in NOISE_TABLE:
        error = abs(math.log2(frequency / target_hz))
        if best is None or error < best[0]:
            best = (error, shift, divisor_code, frequency)
    _, shift, divisor_code, frequency = best
    return shift, divisor_code, 1200.0 * math.log2(frequency / target_hz)
