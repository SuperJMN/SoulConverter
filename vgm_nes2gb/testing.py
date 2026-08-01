"""Build synthetic NES VGM files for tests and demos.

Nothing here is derived from a commercial ROM, so the fixtures it produces are
safe to commit and to regenerate.
"""

from __future__ import annotations

import struct

NTSC_CPU_HZ = 1789772
VGM_SAMPLE_RATE = 44100
FRAME_SAMPLES = 735

HEADER_SIZE = 0x100

DUTY_12 , DUTY_25, DUTY_50, DUTY_75 = 0, 1, 2, 3


def pulse_period(frequency_hz: float) -> int:
    return max(8, min(0x7FF, int(round(NTSC_CPU_HZ / (16.0 * frequency_hz))) - 1))


def triangle_period(frequency_hz: float) -> int:
    return max(2, min(0x7FF, int(round(NTSC_CPU_HZ / (32.0 * frequency_hz))) - 1))


def build_nes_vgm(
    frames: list[list[tuple[int, int]]],
    *,
    cpu_clock_hz: int = NTSC_CPU_HZ,
    loop_frame: int | None = None,
    gd3_fields: list[str] | None = None,
) -> bytes:
    """Encode per-frame ``(register, value)`` writes as a NES VGM 1.61 file."""
    data = bytearray()
    loop_offset = None
    for index, writes in enumerate(frames):
        if loop_frame is not None and index == loop_frame:
            loop_offset = len(data)
        for register, value in writes:
            data.extend((0xB4, register & 0xFF, value & 0xFF))
        data.extend((0x61, FRAME_SAMPLES & 0xFF, FRAME_SAMPLES >> 8))
    data.append(0x66)

    total_samples = len(frames) * FRAME_SAMPLES
    gd3 = _build_gd3(gd3_fields) if gd3_fields else b""
    gd3_position = HEADER_SIZE + len(data)
    eof_position = gd3_position + len(gd3)

    header = bytearray(HEADER_SIZE)
    header[0:4] = b"Vgm "
    _put(header, 0x04, eof_position - 0x04)
    _put(header, 0x08, 0x00000161)
    _put(header, 0x14, gd3_position - 0x14 if gd3 else 0)
    _put(header, 0x18, total_samples)
    if loop_offset is not None:
        _put(header, 0x1C, HEADER_SIZE + loop_offset - 0x1C)
        _put(header, 0x20, total_samples - loop_frame * FRAME_SAMPLES)
    _put(header, 0x24, 60)
    _put(header, 0x34, HEADER_SIZE - 0x34)
    _put(header, 0x84, cpu_clock_hz)
    return bytes(header) + bytes(data) + gd3


def _build_gd3(fields: list[str]) -> bytes:
    padded = (fields + [""] * 11)[:11]
    body = b"".join(field.encode("utf-16-le") + b"\x00\x00" for field in padded)
    return b"Gd3 " + struct.pack("<II", 0x100, len(body)) + body


def _put(header: bytearray, offset: int, value: int) -> None:
    header[offset : offset + 4] = struct.pack("<I", value & 0xFFFFFFFF)


# --- note helpers ----------------------------------------------------------

def pulse_note(channel: int, frequency_hz: float, volume: int, duty: int, decay: bool = False):
    """Register writes that start a sustained note on pulse 1 (0) or 2 (1)."""
    base = 0x00 if channel == 0 else 0x04
    period = pulse_period(frequency_hz)
    control = (duty << 6) | 0x20 | (0x00 if decay else 0x10) | (volume & 0x0F)
    return [
        (base + 0, control),
        (base + 1, 0x00),  # sweep off
        (base + 2, period & 0xFF),
        (base + 3, (0x1F << 3) | (period >> 8)),
    ]


def pulse_off(channel: int):
    base = 0x00 if channel == 0 else 0x04
    return [(base + 0, 0x30)]


def triangle_note(frequency_hz: float):
    period = triangle_period(frequency_hz)
    return [
        (0x08, 0xFF),
        (0x0A, period & 0xFF),
        (0x0B, (0x1F << 3) | (period >> 8)),
    ]


def triangle_off():
    return [(0x08, 0x00)]


def noise_hit(period_index: int, volume: int, mode_short: bool = False, decay: bool = True):
    return [
        (0x0C, (0x00 if decay else 0x10) | (volume & 0x0F)),
        (0x0E, (0x80 if mode_short else 0x00) | (period_index & 0x0F)),
        (0x0F, 0x1F << 3),
    ]


def demo_song() -> list[list[tuple[int, int]]]:
    """A short four-channel tune covering every feature the converter maps.

    Includes duty changes, hardware sweep, envelope decay, short/long noise and
    deliberately sub-Game-Boy bass notes so octave transposition is exercised.
    """
    melody = [523.25, 587.33, 659.25, 698.46, 783.99, 659.25, 587.33, 523.25]
    harmony = [261.63, 293.66, 329.63, 349.23, 392.00, 329.63, 293.66, 261.63]
    bass = [65.41, 55.00, 32.70, 27.50]  # last two sit below the Game Boy floor

    frames: list[list[tuple[int, int]]] = []
    frames.append([(0x15, 0x0F), (0x17, 0x40)])

    for step in range(16):
        writes: list[tuple[int, int]] = []
        if step % 2 == 0:
            writes += pulse_note(0, melody[step // 2], 12, step % 4)
            writes += pulse_note(1, harmony[step // 2], 10, DUTY_25, decay=True)
        if step % 4 == 0:
            writes += triangle_note(bass[step // 4])
        if step % 2 == 0:
            writes += noise_hit(period_index=8 if step % 4 == 0 else 12,
                                volume=8, mode_short=step % 8 == 4)
        frames.append(writes)
        frames.extend([[] for _ in range(11)])

    # A hardware sweep on pulse 1, which the Game Boy has to receive pre-resolved.
    sweep = pulse_note(0, 440.0, 15, DUTY_50)
    sweep[1] = (0x01, 0x8A)  # enable, period 0, negate off, shift 2
    frames.append(sweep)
    frames.extend([[] for _ in range(23)])

    frames.append(pulse_off(0) + pulse_off(1) + triangle_off())
    frames.extend([[] for _ in range(11)])
    return frames


def demo_vgm(loop: bool = True) -> bytes:
    frames = demo_song()
    return build_nes_vgm(
        frames,
        loop_frame=1 if loop else None,
        gd3_fields=["vgm-nes2gb demo", "", "vgm-nes2gb", "", "NES/Famicom"],
    )
