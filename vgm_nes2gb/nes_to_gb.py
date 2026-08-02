"""Translate NES musical state into Game Boy DMG register writes.

Channel assignment (DPCM has no Game Boy counterpart and is dropped upstream):

    NES pulse 1  -> CH1 square
    NES pulse 2  -> CH2 square
    NES triangle -> CH3 wave (wave RAM holds a 32-step triangle)
    NES noise    -> CH4 noise
"""

from __future__ import annotations

from dataclasses import dataclass, field

from . import gameboy as gb
from .nes_apu import ApuTimeline, NoiseState, PulseState, TriangleState

VOLUME_MODE_RETRIGGER = "retrigger"
VOLUME_MODE_ENVELOPE = "envelope"
VOLUME_MODES = (VOLUME_MODE_RETRIGGER, VOLUME_MODE_ENVELOPE)

CHANNEL_NAMES = ("pulse1", "pulse2", "triangle", "noise")

TRIGGER_REGISTERS = frozenset({gb.NR14, gb.NR24, gb.NR34, gb.NR44})


@dataclass
class GbTick:
    index: int
    sample: int
    writes: list[tuple[int, int]] = field(default_factory=list)


@dataclass
class MappingStats:
    transposed_ticks: dict[str, int] = field(default_factory=dict)
    max_octaves_up: dict[str, int] = field(default_factory=dict)
    retriggers: dict[str, int] = field(default_factory=dict)
    note_ons: dict[str, int] = field(default_factory=dict)
    active_ticks: dict[str, int] = field(default_factory=dict)
    noise_error_cents: dict[int, float] = field(default_factory=dict)
    register_writes: int = 0
    dpcm_voices: int = 0
    dpcm_restores: int = 0

    def __post_init__(self) -> None:
        for name in CHANNEL_NAMES:
            self.transposed_ticks.setdefault(name, 0)
            self.max_octaves_up.setdefault(name, 0)
            self.retriggers.setdefault(name, 0)
            self.note_ons.setdefault(name, 0)
            self.active_ticks.setdefault(name, 0)


@dataclass
class GbTimeline:
    tick_hz: float
    ticks: list[GbTick]
    loop_tick: int | None
    total_samples: int
    stats: MappingStats


class _RegisterFile:
    """Tracks last-written values so only changed registers are emitted."""

    def __init__(self) -> None:
        self.values: dict[int, int] = {}
        self.pending: list[tuple[int, int]] = []

    def set(self, register: int, value: int) -> bool:
        value &= 0xFF
        if self.values.get(register) == value:
            return False
        self.values[register] = value
        self.pending.append((register, value))
        return True

    def force(self, register: int, value: int, remember: int | None = None) -> None:
        """Always emit the write, but remember ``remember`` as the visible state.

        Trigger bits are self-clearing on the hardware, so remembering the value
        without them keeps the delta compression from re-emitting the register on
        the next tick just to drop a bit that is no longer set.
        """
        value &= 0xFF
        self.values[register] = value if remember is None else remember & 0xFF
        self.pending.append((register, value))

    def peek(self, register: int) -> int | None:
        return self.values.get(register)

    def drain(self) -> list[tuple[int, int]]:
        writes = self.pending
        self.pending = []
        return writes


def _gb_envelope_period(nes_period: int) -> int:
    """Closest Game Boy envelope step (1/64 s units) to a NES step ((V+1)/240 s)."""
    return max(1, min(7, int(round(64.0 * (nes_period + 1) / 240.0))))


class _SquareChannel:
    def __init__(self, name: str, nrx1: int, nrx2: int, nrx3: int, nrx4: int) -> None:
        self.name = name
        self.nrx1 = nrx1
        self.nrx2 = nrx2
        self.nrx3 = nrx3
        self.nrx4 = nrx4
        self.active = False

    def update(
        self,
        state: PulseState,
        registers: _RegisterFile,
        stats: MappingStats,
        volume_mode: str,
        allow_transpose: bool,
    ) -> None:
        if not state.playing:
            if self.active:
                registers.set(self.nrx2, 0x00)  # DAC off silences the channel
                self.active = False
            return

        period, octaves = gb.square_period(state.frequency_hz, allow_transpose)
        if octaves:
            stats.transposed_ticks[self.name] += 1
            stats.max_octaves_up[self.name] = max(
                stats.max_octaves_up[self.name], octaves
            )
        stats.active_ticks[self.name] += 1

        note_on = not self.active or state.retriggered
        registers.set(self.nrx1, state.duty << 6)

        if volume_mode == VOLUME_MODE_ENVELOPE:
            if note_on:
                registers.set(self.nrx2, _envelope_byte(state))
            trigger = note_on
        else:
            volume_changed = registers.set(self.nrx2, state.volume << 4)
            trigger = note_on or volume_changed

        registers.set(self.nrx3, period & 0xFF)
        high = (period >> 8) & 0x07
        if trigger:
            registers.force(self.nrx4, high | 0x80, remember=high)
            stats.retriggers[self.name] += 1
            if note_on:
                stats.note_ons[self.name] += 1
        else:
            registers.set(self.nrx4, high)
        self.active = True


def _envelope_byte(state: PulseState | NoiseState) -> int:
    """NRx2 for a note-on, mapping the NES envelope onto the GB envelope unit."""
    if state.constant_volume:
        return state.volume << 4
    return (15 << 4) | _gb_envelope_period(state.envelope_period)


class _WaveChannel:
    name = "triangle"

    def __init__(self) -> None:
        self.active = False

    def update(
        self,
        state: TriangleState,
        registers: _RegisterFile,
        stats: MappingStats,
        allow_transpose: bool,
    ) -> None:
        if not state.playing:
            if self.active:
                registers.set(gb.NR30, 0x00)
                self.active = False
            return

        period, octaves = gb.wave_period(state.frequency_hz, allow_transpose)
        if octaves:
            stats.transposed_ticks[self.name] += 1
            stats.max_octaves_up[self.name] = max(
                stats.max_octaves_up[self.name], octaves
            )
        stats.active_ticks[self.name] += 1

        note_on = not self.active or state.retriggered
        registers.set(gb.NR30, 0x80)
        registers.set(gb.NR32, 0x20)  # 100% output level
        registers.set(gb.NR33, period & 0xFF)
        high = (period >> 8) & 0x07
        if note_on:
            registers.force(gb.NR34, high | 0x80, remember=high)
            stats.retriggers[self.name] += 1
            stats.note_ons[self.name] += 1
        else:
            registers.set(gb.NR34, high)
        self.active = True


class _NoiseChannel:
    name = "noise"

    def __init__(self) -> None:
        self.active = False

    def update(
        self,
        state: NoiseState,
        registers: _RegisterFile,
        stats: MappingStats,
        volume_mode: str,
    ) -> None:
        if not state.playing:
            if self.active:
                registers.set(gb.NR42, 0x00)
                self.active = False
            return

        shift, divisor, error_cents = gb.nearest_noise(state.shift_rate_hz)
        stats.noise_error_cents[state.period_index] = error_cents
        stats.active_ticks[self.name] += 1

        note_on = not self.active or state.retriggered
        if volume_mode == VOLUME_MODE_ENVELOPE:
            if note_on:
                registers.set(gb.NR42, _envelope_byte(state))
            trigger = note_on
        else:
            volume_changed = registers.set(gb.NR42, state.volume << 4)
            trigger = note_on or volume_changed

        registers.set(gb.NR43, (shift << 4) | (0x08 if state.mode_short else 0) | divisor)
        if trigger:
            registers.force(gb.NR44, 0x80, remember=0x00)
            stats.retriggers[self.name] += 1
            if note_on:
                stats.note_ons[self.name] += 1
        self.active = True


def convert(
    timeline: ApuTimeline,
    *,
    volume_mode: str = VOLUME_MODE_RETRIGGER,
    allow_transpose: bool = True,
    wave_nibbles=gb.TRIANGLE_WAVE_NIBBLES,
    dpcm_plan=None,
) -> GbTimeline:
    """Turn an :class:`ApuTimeline` into Game Boy register writes per tick.

    When ``dpcm_plan`` is given, the channels it borrows are left alone for the
    span of each borrowed voice and restored the tick after, so the NES part
    resumes exactly where it was interrupted.
    """
    if volume_mode not in VOLUME_MODES:
        raise ValueError(f"unknown volume mode {volume_mode!r}")

    registers = _RegisterFile()
    stats = MappingStats()
    pulse1 = _SquareChannel("pulse1", gb.NR11, gb.NR12, gb.NR13, gb.NR14)
    pulse2 = _SquareChannel("pulse2", gb.NR21, gb.NR22, gb.NR23, gb.NR24)
    wave = _WaveChannel()
    noise = _NoiseChannel()
    channels = {"ch1": pulse1, "ch2": pulse2, "ch3": wave, "ch4": noise}

    ticks: list[GbTick] = []
    for index, apu_tick in enumerate(timeline.ticks):
        if index == 0:
            _emit_init(registers, wave_nibbles)

        borrowed: set[str] = set()
        if dpcm_plan is not None:
            for host in dpcm_plan.released_at(index):
                _release_host(host.value, channels, registers, wave_nibbles, stats)
            borrowed = dpcm_plan.borrowed_at(index)

        if "ch1" not in borrowed:
            pulse1.update(apu_tick.pulse1, registers, stats, volume_mode, allow_transpose)
        if "ch2" not in borrowed:
            pulse2.update(apu_tick.pulse2, registers, stats, volume_mode, allow_transpose)
        if "ch3" not in borrowed:
            wave.update(apu_tick.triangle, registers, stats, allow_transpose)
        if "ch4" not in borrowed:
            noise.update(apu_tick.noise, registers, stats, volume_mode)

        if dpcm_plan is not None:
            for voice in dpcm_plan.starting_at(index):
                channels[voice.host.value].active = False
                for register, value in voice.recipe.writes.get(0, ()):
                    # Trigger bits self-clear on the hardware; remembering them
                    # would make the next tick re-emit the register just to
                    # drop a bit that is no longer set.
                    remember = value & ~0x80 if register in TRIGGER_REGISTERS else None
                    registers.force(register, value, remember=remember)
                stats.dpcm_voices += 1

        writes = registers.drain()
        stats.register_writes += len(writes)
        ticks.append(GbTick(index=index, sample=apu_tick.sample, writes=writes))

    return GbTimeline(
        tick_hz=timeline.tick_hz,
        ticks=ticks,
        loop_tick=timeline.loop_tick,
        total_samples=timeline.total_samples,
        stats=stats,
    )


def _release_host(
    name: str,
    channels: dict,
    registers: _RegisterFile,
    wave_nibbles,
    stats: MappingStats,
) -> None:
    """Hand a borrowed channel back in a state the NES part can resume from.

    Clearing ``active`` is what forces the next tick to re-emit the whole note
    rather than trusting delta compression, but two pieces of state outlive a
    trigger and have to be undone by hand: CH1's sweep would detune the next
    note it plays, and CH3's wave RAM would leave the triangle with a drum's
    timbre.
    """
    channel = channels[name]
    channel.active = False
    stats.dpcm_restores += 1
    if name == "ch1":
        registers.set(gb.NR10, 0x00)
        registers.set(gb.NR12, 0x00)
    elif name == "ch2":
        registers.set(gb.NR22, 0x00)
    elif name == "ch3":
        registers.force(gb.NR30, 0x00)
        for offset, byte in enumerate(gb.pack_wave(wave_nibbles)):
            registers.set(gb.WAVE_RAM_BASE + offset, byte)
    elif name == "ch4":
        registers.set(gb.NR42, 0x00)


def _emit_init(registers: _RegisterFile, wave_nibbles) -> None:
    """Power the APU up and load wave RAM before any channel is triggered."""
    registers.force(gb.NR52, 0x80)
    registers.force(gb.NR50, 0x77)
    registers.force(gb.NR51, 0xFF)
    registers.force(gb.NR10, 0x00)  # NES sweep is already baked into the pitch
    registers.force(gb.NR30, 0x00)  # wave RAM is only safe to load with CH3 off
    for offset, byte in enumerate(gb.pack_wave(wave_nibbles)):
        registers.force(gb.WAVE_RAM_BASE + offset, byte)
    registers.force(gb.NR12, 0x00)
    registers.force(gb.NR22, 0x00)
    registers.force(gb.NR42, 0x00)
