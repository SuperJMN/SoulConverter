"""A transcription-oriented NES 2A03 APU model.

The Game Boy has no equivalent of the NES envelope, length counter, linear
counter or sweep units, so a register-for-register translation loses volume
shapes and note-offs. This module instead runs those units and samples the
resulting *musical* state (pitch, volume, duty, on/off) on a fixed tick grid.
The Game Boy side is then re-synthesised from that state.

The DMC is modelled too, but only as far as *which* sample is playing and when.
Its waveform is not a musical state the Game Boy can reproduce; turning it into
something the DMG can play is the job of :mod:`vgm_nes2gb.dpcm`.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field, replace

from .vgm_reader import VGM_SAMPLE_RATE, NesCommandStream

LENGTH_TABLE = (
    10, 254, 20, 2, 40, 4, 80, 6, 160, 8, 60, 10, 14, 12, 26, 14,
    12, 16, 24, 18, 48, 20, 96, 22, 192, 24, 72, 26, 16, 28, 32, 30,
)

# NTSC noise timer periods, in CPU cycles, indexed by the low nibble of $400E.
NOISE_PERIOD_TABLE = (
    4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068,
)

# NTSC DMC output rates, in CPU cycles per output bit, indexed by $4010 low nibble.
DMC_RATE_TABLE = (
    428, 380, 340, 320, 286, 254, 226, 214, 190, 160, 142, 128, 106, 84, 72, 54,
)

# Frame sequencer step boundaries in CPU cycles, and what each step clocks.
# 'q' clocks envelopes and the linear counter, 'h' clocks lengths and sweeps.
SEQUENCE_4STEP = ((7457, "q"), (14913, "qh"), (22371, "q"), (29829, "qh"))
SEQUENCE_4STEP_LENGTH = 29830
SEQUENCE_5STEP = (
    (7457, "qh"), (14913, "q"), (22371, "qh"), (29829, "q"), (37281, ""),
)
SEQUENCE_5STEP_LENGTH = 37282

DEFAULT_TICK_HZ = 60.0

# The DMC rate table is NTSC-specific, so its output rates are too. A PAL rip
# would need its own table; this converter targets NTSC.
NTSC_CPU_HZ = 1789773.0


@dataclass(frozen=True)
class PulseState:
    playing: bool = False
    frequency_hz: float = 0.0
    volume: int = 0
    duty: int = 0
    retriggered: bool = False
    constant_volume: bool = True
    envelope_period: int = 0
    envelope_loop: bool = False


@dataclass(frozen=True)
class TriangleState:
    playing: bool = False
    frequency_hz: float = 0.0
    retriggered: bool = False


@dataclass(frozen=True)
class NoiseState:
    playing: bool = False
    period_index: int = 0
    shift_rate_hz: float = 0.0
    volume: int = 0
    mode_short: bool = False
    retriggered: bool = False
    constant_volume: bool = True
    envelope_period: int = 0
    envelope_loop: bool = False


@dataclass(frozen=True)
class SampleRef:
    """Identifies one DPCM sample: where it lives and how fast it plays."""

    address: int
    length: int
    rate_index: int

    @property
    def rate_hz(self) -> float:
        return NTSC_CPU_HZ / DMC_RATE_TABLE[self.rate_index]

    @property
    def duration_seconds(self) -> float:
        return self.length * 8 / self.rate_hz

    @property
    def label(self) -> str:
        return f"{self.address:04X}:{self.length}@{self.rate_index}"


@dataclass(frozen=True)
class DmcState:
    """What the sample channel is doing on one tick.

    The DMC is monophonic, so there is at most one of these in flight, which is
    what makes re-synthesis tractable: only one Game Boy channel has to be found.
    """

    playing: bool = False
    sample: SampleRef | None = None
    started: bool = False
    elapsed_seconds: float = 0.0
    looping: bool = False
    direct_level: int = 0


@dataclass(frozen=True)
class ApuTick:
    index: int
    sample: int
    pulse1: PulseState
    pulse2: PulseState
    triangle: TriangleState
    noise: NoiseState
    dmc: DmcState = field(default_factory=DmcState)


@dataclass
class ApuTimeline:
    tick_hz: float
    cpu_clock_hz: int
    ticks: list[ApuTick]
    loop_tick: int | None
    total_samples: int


class _Envelope:
    """Shared envelope generator used by both pulses and the noise channel."""

    def __init__(self) -> None:
        self.constant = False
        self.loop = False
        self.period = 0
        self.divider = 0
        self.decay = 0
        self.start = False

    def clock(self) -> None:
        if self.start:
            self.start = False
            self.decay = 15
            self.divider = self.period
            return
        if self.divider == 0:
            self.divider = self.period
            if self.decay > 0:
                self.decay -= 1
            elif self.loop:
                self.decay = 15
        else:
            self.divider -= 1

    @property
    def output(self) -> int:
        return self.period if self.constant else self.decay


class _Pulse:
    def __init__(self, is_pulse1: bool) -> None:
        self.is_pulse1 = is_pulse1
        self.enabled = False
        self.duty = 0
        self.timer_period = 0
        self.length = 0
        self.envelope = _Envelope()
        self.sweep_enabled = False
        self.sweep_period = 0
        self.sweep_negate = False
        self.sweep_shift = 0
        self.sweep_divider = 0
        self.sweep_reload = False
        self.retriggered = False

    @property
    def halted(self) -> bool:
        return self.envelope.loop

    def write(self, index: int, value: int) -> None:
        if index == 0:
            self.duty = (value >> 6) & 0x03
            self.envelope.loop = bool(value & 0x20)
            self.envelope.constant = bool(value & 0x10)
            self.envelope.period = value & 0x0F
        elif index == 1:
            self.sweep_enabled = bool(value & 0x80)
            self.sweep_period = (value >> 4) & 0x07
            self.sweep_negate = bool(value & 0x08)
            self.sweep_shift = value & 0x07
            self.sweep_reload = True
        elif index == 2:
            self.timer_period = (self.timer_period & 0x0700) | value
        else:
            self.timer_period = (self.timer_period & 0x00FF) | ((value & 0x07) << 8)
            if self.enabled:
                self.length = LENGTH_TABLE[(value >> 3) & 0x1F]
            self.envelope.start = True
            self.retriggered = True

    def set_enabled(self, enabled: bool) -> None:
        self.enabled = enabled
        if not enabled:
            self.length = 0

    def clock_length(self) -> None:
        if self.length > 0 and not self.halted:
            self.length -= 1

    def sweep_target(self) -> int:
        change = self.timer_period >> self.sweep_shift
        if self.sweep_negate:
            # Pulse 1 negates using one's complement, pulse 2 using two's.
            return self.timer_period - change - (1 if self.is_pulse1 else 0)
        return self.timer_period + change

    @property
    def sweep_muted(self) -> bool:
        return self.timer_period < 8 or self.sweep_target() > 0x07FF

    def clock_sweep(self) -> None:
        if self.sweep_divider == 0 and self.sweep_enabled and self.sweep_shift > 0:
            if not self.sweep_muted:
                self.timer_period = max(0, self.sweep_target())
        if self.sweep_divider == 0 or self.sweep_reload:
            self.sweep_divider = self.sweep_period
            self.sweep_reload = False
        else:
            self.sweep_divider -= 1


class _Triangle:
    def __init__(self) -> None:
        self.enabled = False
        self.timer_period = 0
        self.length = 0
        self.linear = 0
        self.linear_reload_value = 0
        self.linear_reload = False
        self.control = False
        self.retriggered = False

    def write(self, index: int, value: int) -> None:
        if index == 0:
            self.control = bool(value & 0x80)
            self.linear_reload_value = value & 0x7F
        elif index == 2:
            self.timer_period = (self.timer_period & 0x0700) | value
        elif index == 3:
            self.timer_period = (self.timer_period & 0x00FF) | ((value & 0x07) << 8)
            if self.enabled:
                self.length = LENGTH_TABLE[(value >> 3) & 0x1F]
            self.linear_reload = True
            self.retriggered = True

    def set_enabled(self, enabled: bool) -> None:
        self.enabled = enabled
        if not enabled:
            self.length = 0

    def clock_length(self) -> None:
        if self.length > 0 and not self.control:
            self.length -= 1

    def clock_linear(self) -> None:
        if self.linear_reload:
            self.linear = self.linear_reload_value
        elif self.linear > 0:
            self.linear -= 1
        if not self.control:
            self.linear_reload = False


class _Noise:
    def __init__(self) -> None:
        self.enabled = False
        self.period_index = 0
        self.mode_short = False
        self.length = 0
        self.envelope = _Envelope()
        self.retriggered = False

    def write(self, index: int, value: int) -> None:
        if index == 0:
            self.envelope.loop = bool(value & 0x20)
            self.envelope.constant = bool(value & 0x10)
            self.envelope.period = value & 0x0F
        elif index == 2:
            self.mode_short = bool(value & 0x80)
            self.period_index = value & 0x0F
        elif index == 3:
            if self.enabled:
                self.length = LENGTH_TABLE[(value >> 3) & 0x1F]
            self.envelope.start = True
            self.retriggered = True

    def set_enabled(self, enabled: bool) -> None:
        self.enabled = enabled
        if not enabled:
            self.length = 0

    def clock_length(self) -> None:
        if self.length > 0 and not self.envelope.loop:
            self.length -= 1


class _Dmc:
    """The sample channel, tracked as note-on/note-off rather than as audio.

    Which sample plays and when is all the Game Boy side can act on: the actual
    delta-modulated waveform has no DMG equivalent. The unit therefore keeps the
    sample identity and a cycle-accurate end time, and nothing else.
    """

    def __init__(self) -> None:
        self.enabled = False
        self.rate_index = 0
        self.looping = False
        self.direct_level = 0
        self.address_byte = 0
        self.length_byte = 0
        self.playing = False
        self.started = False
        self.start_cycle = 0
        self.end_cycle = 0
        self.current: SampleRef | None = None

    def write(self, index: int, value: int) -> None:
        if index == 0:
            self.looping = bool(value & 0x40)
            self.rate_index = value & 0x0F
        elif index == 1:
            self.direct_level = value & 0x7F
        elif index == 2:
            self.address_byte = value
        elif index == 3:
            self.length_byte = value

    def set_enabled(self, enabled: bool, cycle: int) -> None:
        """`$4015` bit 4: clearing it silences the channel, setting it restarts.

        The hardware only restarts a sample when the bit goes from clear to set,
        or when a running sample has already finished; writing the bit again
        mid-sample does not retrigger.
        """
        self.enabled = enabled
        if not enabled:
            self.playing = False
            self.current = None
            return
        if self.playing:
            return
        self._restart(cycle)

    def _restart(self, cycle: int) -> None:
        length = self.length_byte * 16 + 1
        sample = SampleRef(
            address=0xC000 + self.address_byte * 64,
            length=length,
            rate_index=self.rate_index,
        )
        self.current = sample
        self.playing = True
        self.started = True
        self.start_cycle = cycle
        self.end_cycle = cycle + int(length * 8 * DMC_RATE_TABLE[self.rate_index])

    def run_to_cycle(self, cycle: int) -> None:
        while self.playing and cycle >= self.end_cycle:
            if self.looping and self.enabled:
                self._restart(self.end_cycle)
                continue
            self.playing = False
            self.current = None

    def state(self, cycle: int) -> DmcState:
        elapsed = 0.0
        if self.playing and self.current is not None:
            elapsed = max(0, cycle - self.start_cycle) / NTSC_CPU_HZ
        return DmcState(
            playing=self.playing,
            sample=self.current,
            started=self.started,
            elapsed_seconds=elapsed,
            looping=self.looping,
            direct_level=self.direct_level,
        )


class NesApu:
    """Register-write driven model of the 2A03 sound units."""

    def __init__(self, cpu_clock_hz: int) -> None:
        self.cpu_clock_hz = cpu_clock_hz
        self.pulse1 = _Pulse(is_pulse1=True)
        self.pulse2 = _Pulse(is_pulse1=False)
        self.triangle = _Triangle()
        self.noise = _Noise()
        self.dmc = _Dmc()
        self.cycle = 0
        self._sequence_cycle = 0
        self._step = 0
        self._five_step = False

    # -- register interface -------------------------------------------------
    def write(self, register: int, value: int) -> None:
        if register <= 0x03:
            self.pulse1.write(register, value)
        elif register <= 0x07:
            self.pulse2.write(register - 0x04, value)
        elif register <= 0x0B:
            self.triangle.write(register - 0x08, value)
        elif register <= 0x0F:
            self.noise.write(register - 0x0C, value)
        elif register <= 0x13:
            self.dmc.write(register - 0x10, value)
        elif register == 0x15:
            self.pulse1.set_enabled(bool(value & 0x01))
            self.pulse2.set_enabled(bool(value & 0x02))
            self.triangle.set_enabled(bool(value & 0x04))
            self.noise.set_enabled(bool(value & 0x08))
            self.dmc.set_enabled(bool(value & 0x10), self.cycle)
        elif register == 0x17:
            self._five_step = bool(value & 0x80)
            self._sequence_cycle = 0
            self._step = 0
            if self._five_step:
                self._clock_quarter()
                self._clock_half()

    # -- timing -------------------------------------------------------------
    def run_to_cycle(self, cycle: int) -> None:
        self.dmc.run_to_cycle(cycle)
        while self.cycle < cycle:
            steps, total = (
                (SEQUENCE_5STEP, SEQUENCE_5STEP_LENGTH)
                if self._five_step
                else (SEQUENCE_4STEP, SEQUENCE_4STEP_LENGTH)
            )
            if self._step >= len(steps):
                self._step = 0
                self._sequence_cycle -= total

            boundary, actions = steps[self._step]
            remaining = boundary - self._sequence_cycle
            advance = min(remaining, cycle - self.cycle)
            self.cycle += advance
            self._sequence_cycle += advance
            if advance < remaining:
                return

            if "q" in actions:
                self._clock_quarter()
            if "h" in actions:
                self._clock_half()
            self._step += 1

    def _clock_quarter(self) -> None:
        self.pulse1.envelope.clock()
        self.pulse2.envelope.clock()
        self.noise.envelope.clock()
        self.triangle.clock_linear()

    def _clock_half(self) -> None:
        self.pulse1.clock_length()
        self.pulse1.clock_sweep()
        self.pulse2.clock_length()
        self.pulse2.clock_sweep()
        self.triangle.clock_length()
        self.noise.clock_length()

    # -- observation --------------------------------------------------------
    def snapshot(self, index: int, sample: int) -> ApuTick:
        tick = ApuTick(
            index=index,
            sample=sample,
            pulse1=self._pulse_state(self.pulse1),
            pulse2=self._pulse_state(self.pulse2),
            triangle=self._triangle_state(),
            noise=self._noise_state(),
            dmc=self.dmc.state(self.cycle),
        )
        self.pulse1.retriggered = False
        self.pulse2.retriggered = False
        self.triangle.retriggered = False
        self.noise.retriggered = False
        self.dmc.started = False
        return tick

    def _pulse_state(self, pulse: _Pulse) -> PulseState:
        volume = pulse.envelope.output
        audible = pulse.length > 0 and not pulse.sweep_muted and volume > 0
        frequency = (
            self.cpu_clock_hz / (16.0 * (pulse.timer_period + 1))
            if pulse.timer_period >= 8
            else 0.0
        )
        return PulseState(
            playing=audible,
            frequency_hz=frequency,
            volume=volume,
            duty=pulse.duty,
            retriggered=pulse.retriggered,
            constant_volume=pulse.envelope.constant,
            envelope_period=pulse.envelope.period,
            envelope_loop=pulse.envelope.loop,
        )

    def _triangle_state(self) -> TriangleState:
        tri = self.triangle
        audible = tri.length > 0 and tri.linear > 0 and tri.timer_period >= 2
        frequency = (
            self.cpu_clock_hz / (32.0 * (tri.timer_period + 1))
            if tri.timer_period >= 2
            else 0.0
        )
        return TriangleState(
            playing=audible,
            frequency_hz=frequency,
            retriggered=tri.retriggered,
        )

    def _noise_state(self) -> NoiseState:
        noise = self.noise
        volume = noise.envelope.output
        period = NOISE_PERIOD_TABLE[noise.period_index]
        return NoiseState(
            playing=noise.length > 0 and volume > 0,
            period_index=noise.period_index,
            shift_rate_hz=self.cpu_clock_hz / period,
            volume=volume,
            mode_short=noise.mode_short,
            retriggered=noise.retriggered,
            constant_volume=noise.envelope.constant,
            envelope_period=noise.envelope.period,
            envelope_loop=noise.envelope.loop,
        )


def simulate(stream: NesCommandStream, tick_hz: float = DEFAULT_TICK_HZ) -> ApuTimeline:
    """Run ``stream`` through the APU model and sample it on a tick grid."""
    if tick_hz <= 0:
        raise ValueError("tick_hz must be positive")

    apu = NesApu(stream.cpu_clock_hz)
    cycles_per_sample = stream.cpu_clock_hz / VGM_SAMPLE_RATE
    samples_per_tick = VGM_SAMPLE_RATE / tick_hz

    tick_count = max(1, math.ceil(stream.total_samples / samples_per_tick))
    ticks: list[ApuTick] = []
    write_index = 0
    writes = stream.writes

    for index in range(tick_count):
        sample = int(round(index * samples_per_tick))
        while write_index < len(writes) and writes[write_index].sample <= sample:
            write = writes[write_index]
            apu.run_to_cycle(int(round(write.sample * cycles_per_sample)))
            apu.write(write.register, write.value)
            write_index += 1
        apu.run_to_cycle(int(round(sample * cycles_per_sample)))
        ticks.append(apu.snapshot(index, sample))

    loop_tick = None
    if stream.loop_sample is not None:
        loop_tick = min(tick_count - 1, int(round(stream.loop_sample / samples_per_tick)))

    return ApuTimeline(
        tick_hz=tick_hz,
        cpu_clock_hz=stream.cpu_clock_hz,
        ticks=ticks,
        loop_tick=loop_tick,
        total_samples=stream.total_samples,
    )


def transpose_octaves(state: PulseState | TriangleState, octaves: int):
    """Return ``state`` with its frequency shifted by ``octaves``."""
    return replace(state, frequency_hz=state.frequency_hz * (2.0**octaves))
