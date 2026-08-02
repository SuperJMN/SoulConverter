"""Build synthetic NES VGM files for tests and demos.

Nothing here is derived from a commercial ROM, so the fixtures it produces are
safe to commit and to regenerate.
"""

from __future__ import annotations

import struct

import numpy as np

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
    dpcm_rom: dict[int, bytes] | None = None,
) -> bytes:
    """Encode per-frame ``(register, value)`` writes as a NES VGM 1.61 file.

    ``dpcm_rom`` maps a base address to sample bytes and is emitted as ``0xC2``
    data blocks ahead of the first frame, which is where a real rip puts them.
    """
    data = bytearray()
    for address, payload in (dpcm_rom or {}).items():
        body = struct.pack("<H", address & 0xFFFF) + payload
        data.extend(b"\x67\x66\xc2" + struct.pack("<I", len(body)) + body)
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


# --- DPCM fixtures ---------------------------------------------------------

DMC_RATE_HZ = (
    NTSC_CPU_HZ / 428, NTSC_CPU_HZ / 380, NTSC_CPU_HZ / 340, NTSC_CPU_HZ / 320,
    NTSC_CPU_HZ / 286, NTSC_CPU_HZ / 254, NTSC_CPU_HZ / 226, NTSC_CPU_HZ / 214,
    NTSC_CPU_HZ / 190, NTSC_CPU_HZ / 160, NTSC_CPU_HZ / 142, NTSC_CPU_HZ / 128,
    NTSC_CPU_HZ / 106, NTSC_CPU_HZ / 84, NTSC_CPU_HZ / 72, NTSC_CPU_HZ / 54,
)
DMC_BASE = 0xC000


def encode_dpcm(waveform, initial_level: int = 64) -> bytes:
    """Delta-modulate a waveform the way a DPCM sample is built.

    At each step the encoder picks whichever of the two available moves lands
    closer to the target, which is the best a 1-bit encoder can do and is what
    every real DPCM tool does. Encoding rather than shipping raw bytes means the
    fixture exercises the decoder against material with the same slew-rate limit
    as the real thing.
    """
    target = np.clip(np.asarray(waveform, dtype=float), -1.0, 1.0) * 63.0 + 64.0
    level = int(np.clip(initial_level, 0, 127))
    bits: list[int] = []
    for wanted in target:
        up = min(127, level + 2)
        down = max(0, level - 2)
        if abs(up - wanted) <= abs(down - wanted):
            bits.append(1)
            level = up
        else:
            bits.append(0)
            level = down
    while len(bits) % 8:
        bits.append(bits[-1] if bits else 0)
    return bytes(
        sum(bit << index for index, bit in enumerate(bits[offset : offset + 8]))
        for offset in range(0, len(bits), 8)
    )


def _decay(count: int, seconds: float, rate: float, shape: float = 1.0):
    return np.exp(-np.linspace(0.0, count / rate, count) / (seconds / 3.0)) ** shape


def synth_kick(seconds: float = 0.09, rate: float = DMC_RATE_HZ[15]):
    """A pitch-swept sine with a click on the front.

    Shaped after the kicks actually found in NES rips rather than after a
    studio kick: those sit higher and carry a bright transient, because the
    slew rate of 1-bit delta modulation cannot render a deep, clean thud.
    """
    count = int(seconds * rate)
    time = np.arange(count) / rate
    sweep = 260.0 * np.exp(-time / 0.030) + 80.0
    phase = 2 * np.pi * np.cumsum(sweep) / rate
    body = np.sin(phase) * _decay(count, seconds, rate)
    click = np.zeros(count)
    edge = min(count, int(rate * 0.004))
    rng = np.random.default_rng(20240102)
    click[:edge] = rng.standard_normal(edge) * np.linspace(0.7, 0.0, edge)
    mixed = body + click
    return mixed / (np.abs(mixed).max() + 1e-9)


def synth_snare(seconds: float = 0.06, rate: float = DMC_RATE_HZ[15]):
    """Band-passed noise centred near 1.5 kHz over a short shell tone."""
    count = int(seconds * rate)
    rng = np.random.default_rng(20240607)
    noise = rng.standard_normal(count)
    window = max(2, int(rate / 4000.0))
    smoothed = np.convolve(noise, np.ones(window) / window, mode="same")
    # Subtracting a slower average leaves a band rather than a low-passed blur,
    # which is what separates a snare from a tom in the classifier.
    slow = np.convolve(smoothed, np.ones(window * 6) / (window * 6), mode="same")
    body = smoothed - slow
    tone = np.sin(2 * np.pi * 440.0 * np.arange(count) / rate) * 0.3
    mixed = body / (np.abs(body).max() + 1e-9) + tone
    return mixed / (np.abs(mixed).max() + 1e-9) * _decay(count, seconds, rate)


def synth_hat(seconds: float = 0.02, rate: float = DMC_RATE_HZ[15]):
    """High-passed noise, over almost as soon as it starts."""
    count = int(seconds * rate)
    rng = np.random.default_rng(19890421)
    noise = rng.standard_normal(count)
    bright = np.diff(noise, prepend=noise[0])
    return bright / (np.abs(bright).max() + 1e-9) * _decay(count, seconds, rate, 1.5)


def synth_dac_blip(seconds: float = 0.03, rate: float = DMC_RATE_HZ[15]):
    """The levelling glitch a driver emits when it walks the DAC to mid-scale.

    It is not an instrument and must never be re-synthesised: it has no attack,
    all of its motion is one-way, and what little it puts in the audible band
    sits below anything the DMG can produce.
    """
    count = int(seconds * rate)
    return np.linspace(-1.0, 1.0, count)


def dpcm_trigger(address: int, length_bytes: int, rate_index: int = 15, loop: bool = False):
    """Register writes that start a DPCM sample, as a driver would emit them."""
    return [
        (0x10, (0x40 if loop else 0x00) | (rate_index & 0x0F)),
        (0x12, ((address - DMC_BASE) // 64) & 0xFF),
        (0x13, max(0, (length_bytes - 1) // 16) & 0xFF),
        (0x15, 0x1F),
    ]


def dpcm_stop():
    return [(0x15, 0x0F)]


def dpcm_kit(base: int = 0xC000) -> tuple[dict[int, bytes], dict[str, tuple[int, int]]]:
    """A three-piece sampled drum kit and where each piece lives.

    Addresses are 64-byte aligned because ``$4012`` can only express that.
    """
    rom: dict[int, bytes] = {}
    layout: dict[str, tuple[int, int]] = {}
    address = base
    for name, waveform in (
        ("kick", synth_kick()),
        ("snare", synth_snare()),
        ("hat", synth_hat()),
    ):
        payload = encode_dpcm(waveform)
        rom[address] = payload
        layout[name] = (address, len(payload))
        address += (len(payload) + 63) // 64 * 64
    return rom, layout


def dpcm_demo_song() -> tuple[list[list[tuple[int, int]]], dict[int, bytes]]:
    """A two-bar beat over a bass line, with the drums as DPCM samples.

    The tonal channels stay busy throughout so the allocator has to make a real
    decision rather than finding everything idle.
    """
    rom, layout = dpcm_kit()
    pattern = ["kick", "hat", "snare", "hat", "kick", "hat", "snare", "hat"]
    bass = [82.41, 82.41, 110.0, 98.0]

    # Enable the four tonal channels but leave the DMC off: with $4012/$4013
    # still zero, setting bit 4 here would fire a one-byte sample that no
    # driver intends and that would show up as a spurious hit.
    frames: list[list[tuple[int, int]]] = [[(0x15, 0x0F), (0x17, 0x40)]]
    for step, piece in enumerate(pattern * 2):
        address, length = layout[piece]
        writes = list(dpcm_trigger(address, length))
        if step % 2 == 0:
            writes += triangle_note(bass[(step // 2) % len(bass)])
            writes += pulse_note(0, 329.63 if step % 4 == 0 else 392.0, 10, DUTY_50)
            writes += pulse_note(1, 246.94, 8, DUTY_25, decay=True)
        frames.append(writes)
        frames.extend([[] for _ in range(11)])

    frames.append(pulse_off(0) + pulse_off(1) + triangle_off() + dpcm_stop())
    frames.extend([[] for _ in range(11)])
    return frames, rom


def dpcm_demo_vgm(loop: bool = True) -> bytes:
    frames, rom = dpcm_demo_song()
    return build_nes_vgm(
        frames,
        loop_frame=1 if loop else None,
        dpcm_rom=rom,
        gd3_fields=["vgm-nes2gb dpcm demo", "", "vgm-nes2gb", "", "NES/Famicom"],
    )
