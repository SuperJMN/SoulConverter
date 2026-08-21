"""Translate a Game Boy DMG VGM into the closest base NES 2A03 stream.

This is a state translation rather than a register renumbering.  The two APUs
share two square generators and a noise generator, but their timers, envelope
units and wave channels differ.  GB envelopes are therefore replayed and
written as NES constant-volume updates, while the GB wave table is represented
by the NES triangle's fixed waveform.
"""

from __future__ import annotations

import math
import os
from collections import defaultdict
from dataclasses import dataclass, field

import numpy as np

from . import analyze, gameboy, gb_vgm_reader, nes_apu, nes_vgm_writer, vgm_reader
from .render import gb_synth, nes_synth

ENVELOPE_HZ = 64.0
ENVELOPE_PERIOD = vgm_reader.VGM_SAMPLE_RATE / ENVELOPE_HZ
NES_CPU_HZ = nes_vgm_writer.NES_CLOCK_HZ

@dataclass
class ReverseStats:
    wave_ram_writes: int = 0
    sweep_writes: int = 0
    ignored_global_writes: int = 0
    envelope_updates: int = 0
    pulse_periods: int = 0
    wave_periods: int = 0
    noise_periods: int = 0
    noise_error_cents: dict[int, float] = field(default_factory=dict)


@dataclass
class ReverseConversionResult:
    payload: bytes
    report: dict
    nes_audio: np.ndarray
    gb_audio: np.ndarray
    warnings: list[str] = field(default_factory=list)
    preset: str = "gb-to-nes"


@dataclass
class _Envelope:
    initial: int = 0
    current: int = 0
    increase: bool = False
    pace: int = 0
    timer: int = 0

    def configure(self, value: int) -> None:
        self.initial = (value >> 4) & 0x0F
        self.increase = bool(value & 0x08)
        self.pace = value & 0x07
        self.current = self.initial
        self.timer = self.pace

    def trigger(self) -> None:
        self.current = self.initial
        self.timer = self.pace

    def tick(self) -> bool:
        if self.pace == 0:
            return False
        self.timer -= 1
        if self.timer > 0:
            return False
        self.timer = self.pace
        before = self.current
        if self.increase:
            self.current = min(15, self.current + 1)
        else:
            self.current = max(0, self.current - 1)
        return before != self.current


@dataclass
class _Channel:
    kind: str
    envelope: _Envelope | None = None
    started: bool = False
    dac_enabled: bool = False
    last_period: int | None = None
    last_control: int | None = None
    last_noise: int | None = None


def convert(stream: gb_vgm_reader.GbCommandStream) -> tuple[nes_vgm_writer.NesTimeline, ReverseStats]:
    """Translate one parsed DMG stream, preserving absolute VGM timing."""
    grouped: dict[int, list[gb_vgm_reader.RegisterWrite]] = defaultdict(list)
    for write in stream.writes:
        grouped[write.sample].append(write)

    state = [0] * 0x30
    # A VGM often omits the initial mixer writes because they are hardware
    # defaults.  The DMG's useful defaults are stereo/full volume.
    state[gameboy.NR50] = 0x77
    state[gameboy.NR51] = 0xFF
    channels = {
        "pulse1": _Channel("pulse1", _Envelope()),
        "pulse2": _Channel("pulse2", _Envelope()),
        "wave": _Channel("wave"),
        "noise": _Channel("noise", _Envelope()),
    }
    output: list[nes_vgm_writer.RegisterWrite] = []
    stats = ReverseStats()
    powered = True
    status_written = False
    next_envelope = ENVELOPE_PERIOD

    def emit(sample: int, register: int, value: int) -> None:
        output.append(nes_vgm_writer.RegisterWrite(sample, register, value))

    def ensure_power(sample: int) -> None:
        nonlocal status_written
        if not status_written:
            emit(sample, 0x15, 0x0F)
            status_written = True

    def reset_channels() -> None:
        for channel in channels.values():
            channel.started = False
            channel.dac_enabled = False
            channel.last_period = None
            channel.last_control = None
            channel.last_noise = None

    def envelope_tick(sample: int) -> None:
        for channel in channels.values():
            if channel.envelope is None or not channel.started:
                continue
            if channel.envelope.tick():
                stats.envelope_updates += 1
                _emit_channel_volume(
                    sample, channel, state, powered, emit, channels
                )

    def process_group(sample: int, writes: list[gb_vgm_reader.RegisterWrite]) -> None:
        nonlocal powered, status_written
        touched: set[str] = set()
        triggers: set[str] = set()
        for write in writes:
            register, value = write.register, write.value
            state[register] = value
            if register == gameboy.NR52:
                powered = bool(value & 0x80)
                emit(sample, 0x15, 0x0F if powered else 0x00)
                status_written = True
                if not powered:
                    reset_channels()
                continue
            if register in (gameboy.NR50, gameboy.NR51):
                touched.update(channels)
                continue
            channel_name = _channel_for_register(register)
            if channel_name is None:
                if gameboy.WAVE_RAM_BASE <= register < gameboy.WAVE_RAM_BASE + gameboy.WAVE_RAM_SIZE:
                    stats.wave_ram_writes += 1
                else:
                    stats.ignored_global_writes += 1
                continue
            touched.add(channel_name)
            if register in (gameboy.NR12, gameboy.NR22, gameboy.NR42):
                channels[channel_name].envelope.configure(value)
            if register in (gameboy.NR14, gameboy.NR24, gameboy.NR34, gameboy.NR44) and value & 0x80:
                triggers.add(channel_name)
            if register in (gameboy.NR10,):
                stats.sweep_writes += 1

        if not powered:
            return
        for name in touched:
            ensure_power(sample)
            channel = channels[name]
            if name in ("pulse1", "pulse2"):
                _update_pulse(
                    sample, name, channel, state, name in triggers, emit, stats
                )
            elif name == "wave":
                _update_wave(sample, channel, state, name in triggers, emit, stats)
            else:
                _update_noise(sample, channel, state, name in triggers, emit, stats)

    for sample in sorted(grouped):
        while round(next_envelope) < sample:
            envelope_tick(int(round(next_envelope)))
            next_envelope += ENVELOPE_PERIOD
        process_group(sample, grouped[sample])

    while round(next_envelope) < stream.total_samples:
        envelope_tick(int(round(next_envelope)))
        next_envelope += ENVELOPE_PERIOD

    return (
        nes_vgm_writer.NesTimeline(
            writes=output,
            total_samples=stream.total_samples,
            loop_sample=stream.loop_sample,
        ),
        stats,
    )


def _channel_for_register(register: int) -> str | None:
    if register in (gameboy.NR10, gameboy.NR11, gameboy.NR12, gameboy.NR13, gameboy.NR14):
        return "pulse1"
    if register in (gameboy.NR21, gameboy.NR22, gameboy.NR23, gameboy.NR24):
        return "pulse2"
    if register in (gameboy.NR30, gameboy.NR31, gameboy.NR32, gameboy.NR33, gameboy.NR34):
        return "wave"
    if register in (gameboy.NR41, gameboy.NR42, gameboy.NR43, gameboy.NR44):
        return "noise"
    return None


def _pulse_period(state: list[int], low: int, high: int) -> int:
    gb_period = state[low] | ((state[high] & 0x07) << 8)
    if gb_period >= 2048:
        return 0x7FF
    frequency = gameboy.SQUARE_FREQ_NUMERATOR / (2048 - gb_period)
    return max(0, min(0x7FF, int(round(NES_CPU_HZ / (16.0 * frequency) - 1))))


def _wave_period(state: list[int]) -> int:
    gb_period = state[gameboy.NR33] | ((state[gameboy.NR34] & 0x07) << 8)
    if gb_period >= 2048:
        return 0x7FF
    frequency = gameboy.WAVE_FREQ_NUMERATOR / (2048 - gb_period)
    return max(0, min(0x7FF, int(round(NES_CPU_HZ / (32.0 * frequency) - 1))))


def _scaled_volume(channel_index: int, volume: int, state: list[int], active: bool) -> int:
    if not active:
        return 0
    routing = state[gameboy.NR51]
    right = bool(routing & (1 << channel_index))
    left = bool(routing & (1 << (channel_index + 4)))
    levels = []
    if right:
        levels.append(state[gameboy.NR50] & 0x07)
    if left:
        levels.append((state[gameboy.NR50] >> 4) & 0x07)
    if not levels:
        return 0
    return max(0, min(15, int(round(volume * max(levels) / 7.0))))


def _pulse_control(name: str, channel: _Channel, state: list[int]) -> int:
    duty_register = gameboy.NR11 if name == "pulse1" else gameboy.NR21
    volume_register = gameboy.NR12 if name == "pulse1" else gameboy.NR22
    channel_index = 0 if name == "pulse1" else 1
    dac = bool(state[volume_register] & 0xF8)
    channel.dac_enabled = dac
    active = channel.started and dac
    volume = _scaled_volume(channel_index, channel.envelope.current, state, active)
    return ((state[duty_register] >> 6) & 0x03) << 6 | 0x30 | volume


def _update_pulse(sample, name, channel, state, trigger, emit, stats) -> None:
    low, high = (
        (gameboy.NR13, gameboy.NR14)
        if name == "pulse1"
        else (gameboy.NR23, gameboy.NR24)
    )
    if state[gameboy.NR12 if name == "pulse1" else gameboy.NR22] & 0xF8:
        channel.dac_enabled = True
    if trigger:
        channel.started = True
        channel.envelope.trigger()

    period = _pulse_period(state, low, high)
    control = _pulse_control(name, channel, state)
    nes_base = 0x00 if name == "pulse1" else 0x04
    if trigger:
        emit(sample, nes_base, control)
        emit(sample, nes_base + 1, 0x00)  # GB sweep is already in the pitch writes.
        emit(sample, nes_base + 2, period & 0xFF)
        emit(sample, nes_base + 3, 0xF8 | ((period >> 8) & 0x07))
        channel.last_period = period
        channel.last_control = control
        stats.pulse_periods += 1
        return

    if channel.last_control != control:
        emit(sample, nes_base, control)
        channel.last_control = control
    if channel.started and channel.last_period != period:
        emit(sample, nes_base + 2, period & 0xFF)
        emit(sample, nes_base + 3, 0xF8 | ((period >> 8) & 0x07))
        channel.last_period = period
        stats.pulse_periods += 1


def _wave_active(channel: _Channel, state: list[int]) -> bool:
    return (
        channel.started
        and bool(state[gameboy.NR30] & 0x80)
        and ((state[gameboy.NR32] >> 5) & 0x03) != 0
    )


def _update_wave(sample, channel, state, trigger, emit, stats) -> None:
    if trigger:
        channel.started = True
    period = _wave_period(state)
    active = _wave_active(channel, state)
    control = 0xFF if active else 0x00
    if trigger:
        emit(sample, 0x08, control)
        emit(sample, 0x0A, period & 0xFF)
        emit(sample, 0x0B, 0xF8 | ((period >> 8) & 0x07))
        channel.last_period = period
        channel.last_control = control
        stats.wave_periods += 1
        return
    if channel.last_control != control:
        emit(sample, 0x08, control)
        channel.last_control = control
    if channel.started and channel.last_period != period:
        emit(sample, 0x0A, period & 0xFF)
        emit(sample, 0x0B, 0xF8 | ((period >> 8) & 0x07))
        channel.last_period = period
        stats.wave_periods += 1


def _nearest_noise(target_hz: float) -> tuple[int, float]:
    if target_hz <= 0:
        return 0, 0.0
    best = min(
        range(len(nes_apu.NOISE_PERIOD_TABLE)),
        key=lambda index: abs(
            math.log2((NES_CPU_HZ / nes_apu.NOISE_PERIOD_TABLE[index]) / target_hz)
        ),
    )
    actual = NES_CPU_HZ / nes_apu.NOISE_PERIOD_TABLE[best]
    return best, 1200.0 * math.log2(actual / target_hz)


def _noise_parameters(state: list[int]) -> tuple[int, float]:
    register = state[gameboy.NR43]
    shift = register >> 4
    divisor = register & 0x07
    return _nearest_noise(gameboy.noise_frequency(shift, divisor))


def _noise_control(channel: _Channel, state: list[int]) -> int:
    channel.dac_enabled = bool(state[gameboy.NR42] & 0xF8)
    active = channel.started and channel.dac_enabled
    volume = _scaled_volume(3, channel.envelope.current, state, active)
    return 0x30 | volume


def _update_noise(sample, channel, state, trigger, emit, stats) -> None:
    if trigger:
        channel.started = True
        channel.envelope.trigger()
    period, cents = _noise_parameters(state)
    mode_short = bool(state[gameboy.NR43] & 0x08)
    noise = period | (0x80 if mode_short else 0)
    control = _noise_control(channel, state)
    if trigger:
        emit(sample, 0x0C, control)
        emit(sample, 0x0E, noise)
        emit(sample, 0x0F, 0xF8)
        channel.last_noise = noise
        channel.last_control = control
        stats.noise_periods += 1
        stats.noise_error_cents[period] = cents
        return
    if channel.last_control != control:
        emit(sample, 0x0C, control)
        channel.last_control = control
    if channel.started and channel.last_noise != noise:
        emit(sample, 0x0E, noise)
        channel.last_noise = noise
        stats.noise_periods += 1
        stats.noise_error_cents[period] = cents


def _emit_channel_volume(sample, channel, state, powered, emit, channels) -> None:
    if not powered:
        return
    if channel.kind == "pulse1":
        control = _pulse_control("pulse1", channel, state)
        if channel.last_control != control:
            emit(sample, 0x00, control)
            channel.last_control = control
    elif channel.kind == "pulse2":
        control = _pulse_control("pulse2", channel, state)
        if channel.last_control != control:
            emit(sample, 0x04, control)
            channel.last_control = control
    elif channel.kind == "noise":
        control = _noise_control(channel, state)
        if channel.last_control != control:
            emit(sample, 0x0C, control)
            channel.last_control = control


def convert_file(path: str, *, render_audio: bool = True) -> ReverseConversionResult:
    stream = gb_vgm_reader.parse(path)
    timeline, stats = convert(stream)
    payload = nes_vgm_writer.encode(timeline, stream.gd3)

    gb_audio = np.zeros(0)
    nes_audio = np.zeros(0)
    metrics: dict = {}
    if render_audio:
        source_payload = vgm_reader.read_bytes(path)
        gb_audio = gb_synth.render(source_payload)
        nes_stream = vgm_reader.parse_bytes(payload)
        nes_timeline = nes_apu.simulate(nes_stream)
        nes_audio = nes_synth.render(nes_timeline)
        metrics = analyze.compare(
            gb_audio,
            nes_audio,
            gb_synth.channel_frequencies(source_payload, 60.0),
            nes_synth.channel_frequencies(nes_timeline),
        )

    report = {
        "source": {
            "path": os.path.abspath(path),
            "system": "Game Boy DMG",
            "cpu_clock_hz": stream.cpu_clock_hz,
            "vgm_version": f"{stream.version >> 8:x}.{stream.version & 0xFF:02x}",
            "total_samples": stream.total_samples,
            "duration_seconds": round(stream.duration_seconds, 3),
            "loop_sample": stream.loop_sample,
            "register_writes": len(stream.writes),
        },
        "output": {
            "bytes": len(payload),
            "duration_seconds": round(nes_vgm_writer.duration_seconds(payload), 3),
            "register_writes": len(timeline.writes),
            "commands": sorted(f"0x{command:02X}" for command in nes_vgm_writer.scan_commands(payload)),
            "loop_sample": timeline.loop_sample,
        },
        "mapping": {
            "pulse1": "DMG CH1 -> NES pulse 1",
            "pulse2": "DMG CH2 -> NES pulse 2",
            "wave": "DMG CH3 -> NES triangle; custom wave RAM is approximated",
            "noise": "DMG CH4 -> NES noise",
            "wave_ram_writes": stats.wave_ram_writes,
            "sweep_writes_ignored": stats.sweep_writes,
            "envelope_updates": stats.envelope_updates,
            "pulse_period_updates": stats.pulse_periods,
            "wave_period_updates": stats.wave_periods,
            "noise_period_updates": stats.noise_periods,
            "noise_error_cents": {
                str(index): round(cents, 1)
                for index, cents in sorted(stats.noise_error_cents.items())
            },
        },
        "metrics": metrics,
    }
    warnings = [
        "The Game Boy wave table has no NES equivalent; it was rendered with the fixed 2A03 triangle waveform.",
    ]
    report["warnings"] = warnings
    return ReverseConversionResult(
        payload=payload,
        report=report,
        nes_audio=nes_audio,
        gb_audio=gb_audio,
        warnings=warnings,
    )


def render_text(report: dict) -> str:
    source, output, mapping = report["source"], report["output"], report["mapping"]
    lines = [
        f"source : {source['path']}",
        f"         VGM {source['vgm_version']}, DMG @ {source['cpu_clock_hz']} Hz, "
        f"{source['duration_seconds']:.2f}s, {source['register_writes']} writes",
        f"output : {output['bytes']} bytes, {output['duration_seconds']:.2f}s, "
        f"{output['register_writes']} writes, commands {' '.join(output['commands'])}",
        "mapping: GB CH1/CH2 -> NES pulse 1/2; CH3 wave -> NES triangle; CH4 -> NES noise",
        f"         wave RAM writes ignored {mapping['wave_ram_writes']}, "
        f"envelope updates {mapping['envelope_updates']}, "
        f"noise period updates {mapping['noise_period_updates']}",
    ]
    metrics = report.get("metrics") or {}
    if metrics:
        lines.append(f"spectral correlation: {metrics['spectral_correlation']:.3f}")
    for warning in report.get("warnings", []):
        lines.append(f"warning: {warning}")
    return "\n".join(lines)


__all__ = ["ReverseConversionResult", "convert", "convert_file", "render_text"]
