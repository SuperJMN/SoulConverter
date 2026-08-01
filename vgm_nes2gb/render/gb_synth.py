"""Render a Game Boy DMG VGM by replaying the emitted bytes.

This deliberately consumes the *output file*, not the intermediate model, so the
comparison report validates the bytes that will actually ship.
"""

from __future__ import annotations

import struct

import numpy as np

from .oscillators import (
    SAMPLE_RATE,
    lfsr_sequence,
    noise_block,
    square_block,
    wave_block,
)

ENVELOPE_HZ = 64.0
ENVELOPE_PERIOD_SAMPLES = SAMPLE_RATE / ENVELOPE_HZ

NOISE_LONG = lfsr_sequence(bits=15, tap=1)
NOISE_SHORT = lfsr_sequence(bits=7, tap=1)

WAVE_SHIFTS = {0: None, 1: 0, 2: 1, 3: 2}


class GbVgmParseError(Exception):
    """The Game Boy VGM could not be replayed."""


def parse_events(payload: bytes) -> tuple[list[tuple[int, int, int]], int]:
    """Return ``([(sample, register, value)], total_samples)``."""
    if payload[:4] != b"Vgm ":
        raise GbVgmParseError("missing 'Vgm ' signature")
    offset = struct.unpack_from("<I", payload, 0x34)[0] + 0x34
    total_samples = struct.unpack_from("<I", payload, 0x18)[0]

    events: list[tuple[int, int, int]] = []
    sample = 0
    while offset < len(payload):
        command = payload[offset]
        offset += 1
        if command == 0x66:
            break
        if command == 0xB3:
            events.append((sample, payload[offset], payload[offset + 1]))
            offset += 2
        elif command == 0x61:
            sample += struct.unpack_from("<H", payload, offset)[0]
            offset += 2
        elif command == 0x62:
            sample += 735
        elif command == 0x63:
            sample += 882
        elif 0x70 <= command <= 0x7F:
            sample += (command & 0x0F) + 1
        else:
            raise GbVgmParseError(
                f"unexpected command 0x{command:02X} at 0x{offset - 1:X}"
            )
    return events, max(total_samples, sample)


class _Envelope:
    def __init__(self) -> None:
        self.volume = 0
        self.direction = 0
        self.period = 0
        self.timer = 0

    def trigger(self, register: int) -> None:
        self.volume = register >> 4
        self.direction = (register >> 3) & 1
        self.period = register & 0x07
        self.timer = self.period

    def tick(self) -> None:
        if self.period == 0:
            return
        self.timer -= 1
        if self.timer > 0:
            return
        self.timer = self.period
        if self.direction and self.volume < 15:
            self.volume += 1
        elif not self.direction and self.volume > 0:
            self.volume -= 1


class GameBoyApu:
    """Just enough DMG APU to render the channels this converter emits."""

    def __init__(self) -> None:
        self.registers = [0] * 0x30
        self.enabled = [False, False, False, False]
        self.phases = [0.0, 0.0, 0.0, 0.0]
        self.envelopes = [_Envelope(), _Envelope(), None, _Envelope()]
        self.noise_position = 0.0

    def write(self, register: int, value: int) -> None:
        if register >= len(self.registers):
            return
        self.registers[register] = value & 0xFF
        if register == 0x16 and not value & 0x80:
            self.enabled = [False] * 4
            return
        if register == 0x02 and (value & 0xF8) == 0:
            self.enabled[0] = False
        if register == 0x07 and (value & 0xF8) == 0:
            self.enabled[1] = False
        if register == 0x0A and not value & 0x80:
            self.enabled[2] = False
        if register == 0x11 and (value & 0xF8) == 0:
            self.enabled[3] = False
        if register == 0x04 and value & 0x80:
            self._trigger(0, 0x02)
        elif register == 0x09 and value & 0x80:
            self._trigger(1, 0x07)
        elif register == 0x0E and value & 0x80:
            if self.registers[0x0A] & 0x80:
                self.enabled[2] = True
                self.phases[2] = 0.0
        elif register == 0x13 and value & 0x80:
            self._trigger(3, 0x11)

    def _trigger(self, channel: int, envelope_register: int) -> None:
        register = self.registers[envelope_register]
        if (register & 0xF8) == 0:
            self.enabled[channel] = False
            return
        self.enabled[channel] = True
        self.phases[channel] = 0.0
        self.envelopes[channel].trigger(register)
        if channel == 3:
            self.noise_position = 0.0

    def tick_envelopes(self) -> None:
        for envelope in (self.envelopes[0], self.envelopes[1], self.envelopes[3]):
            envelope.tick()

    def square_frequency(self, low_register: int, high_register: int) -> float:
        period = self.registers[low_register] | ((self.registers[high_register] & 0x07) << 8)
        return 131072.0 / (2048 - period)

    def wave_frequency(self) -> float:
        period = self.registers[0x0D] | ((self.registers[0x0E] & 0x07) << 8)
        return 65536.0 / (2048 - period)

    def wave_table(self) -> np.ndarray:
        shift = WAVE_SHIFTS[(self.registers[0x0C] >> 5) & 0x03]
        nibbles = []
        for index in range(16):
            byte = self.registers[0x20 + index]
            nibbles.append(byte >> 4)
            nibbles.append(byte & 0x0F)
        table = np.array(nibbles, dtype=np.float64)
        if shift is None:
            return np.zeros_like(table)
        return table / (2**shift)

    def noise_rate(self) -> tuple[float, bool]:
        register = self.registers[0x12]
        shift = register >> 4
        width_short = bool(register & 0x08)
        divisor_code = register & 0x07
        divisor = 0.5 if divisor_code == 0 else float(divisor_code)
        if shift > 13:
            return 0.0, width_short
        return 262144.0 / (divisor * (2**shift)), width_short

    def render_block(self, count: int) -> np.ndarray:
        if count <= 0:
            return np.zeros(0)
        total = np.zeros(count)

        block, self.phases[0] = square_block(
            count, self.phases[0], self.square_frequency(0x03, 0x04),
            (self.registers[0x01] >> 6) & 0x03,
        )
        if self.enabled[0]:
            total += block * self.envelopes[0].volume

        block, self.phases[1] = square_block(
            count, self.phases[1], self.square_frequency(0x08, 0x09),
            (self.registers[0x06] >> 6) & 0x03,
        )
        if self.enabled[1]:
            total += block * self.envelopes[1].volume

        block, self.phases[2] = wave_block(
            count, self.phases[2], self.wave_frequency(), self.wave_table()
        )
        if self.enabled[2]:
            total += block

        rate, width_short = self.noise_rate()
        sequence = NOISE_SHORT if width_short else NOISE_LONG
        block, self.noise_position = noise_block(
            count, self.noise_position, rate, sequence
        )
        if self.enabled[3]:
            total += block * self.envelopes[3].volume

        return total / 60.0


def render(payload: bytes) -> np.ndarray:
    """Render a Game Boy VGM to a mono float array at 44100 Hz."""
    events, total_samples = parse_events(payload)
    apu = GameBoyApu()
    output = np.zeros(total_samples)

    position = 0
    event_index = 0
    next_envelope = ENVELOPE_PERIOD_SAMPLES

    while position < total_samples:
        while event_index < len(events) and events[event_index][0] <= position:
            _, register, value = events[event_index]
            apu.write(register, value)
            event_index += 1

        boundary = total_samples
        if event_index < len(events):
            boundary = min(boundary, events[event_index][0])
        boundary = min(boundary, int(round(next_envelope)))
        boundary = max(boundary, position + 1)
        boundary = min(boundary, total_samples)

        count = boundary - position
        output[position:boundary] = apu.render_block(count)
        position = boundary

        if position >= int(round(next_envelope)):
            apu.tick_envelopes()
            next_envelope += ENVELOPE_PERIOD_SAMPLES

    return output


def channel_frequencies(payload: bytes, tick_hz: float) -> dict[str, np.ndarray]:
    """Sample the emitted stream's per-channel pitch on the conversion tick grid."""
    events, total_samples = parse_events(payload)
    apu = GameBoyApu()
    samples_per_tick = SAMPLE_RATE / tick_hz
    tick_count = max(1, int(round(total_samples / samples_per_tick)))

    series = {name: np.zeros(tick_count) for name in
              ("pulse1", "pulse2", "triangle", "noise")}
    event_index = 0
    for tick in range(tick_count):
        sample = int(round(tick * samples_per_tick))
        while event_index < len(events) and events[event_index][0] <= sample:
            _, register, value = events[event_index]
            apu.write(register, value)
            event_index += 1
        series["pulse1"][tick] = (
            apu.square_frequency(0x03, 0x04) if apu.enabled[0] else 0.0
        )
        series["pulse2"][tick] = (
            apu.square_frequency(0x08, 0x09) if apu.enabled[1] else 0.0
        )
        series["triangle"][tick] = apu.wave_frequency() if apu.enabled[2] else 0.0
        series["noise"][tick] = apu.noise_rate()[0] if apu.enabled[3] else 0.0
    return series
