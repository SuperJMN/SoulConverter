"""Turn a classified sample into Game Boy register writes.

Every recipe leans on the DMG's own length counter and envelope generator rather
than on per-frame writes. That is not an optimisation detail: RetroSharp
quantises playback to a 60 Hz grid, so an envelope drawn frame by frame would be
coarse and would flood the stream with writes, while the hardware envelope runs
at 64 Hz internally and the length counter at 256 Hz. One burst of writes at the
attack buys a decay shaped more finely than the format could otherwise express.

Recipes are parameterised by what was measured in the sample, never by fixed
constants: the kick's sweep starts from its own pitch, the snare's noise divisor
is chosen so the Game Boy's spectral centroid lands on the measured one.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from enum import Enum

from . import gameboy as gb
from .dpcm import Role, SampleAnalysis, wave_table

LENGTH_TICK_HZ = 256.0
ENVELOPE_TICK_HZ = 64.0
SWEEP_TICK_HZ = 128.0

# White noise band-limited to half the LFSR clock has its spectral centroid at
# roughly a quarter of that clock, so a target centroid asks for a four times
# faster LFSR.
NOISE_CENTROID_RATIO = 4.0


class Host(str, Enum):
    """Which DMG generator a recipe needs. Each has a fixed timbre."""

    CH1 = "ch1"  # square with hardware frequency sweep
    CH2 = "ch2"  # square, no sweep
    CH3 = "ch3"  # wave, arbitrary 32-step timbre
    CH4 = "ch4"  # noise

    @property
    def is_square(self) -> bool:
        return self in (Host.CH1, Host.CH2)


_BASE = {
    Host.CH1: gb.NR10,
    Host.CH2: gb.NR21 - 1,  # NR20 does not exist; kept for uniform arithmetic
    Host.CH3: gb.NR30,
    Host.CH4: gb.NR41 - 1,  # NR40 does not exist
}


@dataclass(frozen=True)
class Recipe:
    """A self-contained percussive or tonal event on one DMG channel.

    ``frames`` is how long the host channel is unavailable, which is what the
    allocator prices. ``writes`` is keyed by frame offset from the attack so a
    recipe can add a follow-up without the allocator knowing the details.
    """

    host: Host
    role: Role
    frames: int
    writes: dict[int, tuple[tuple[int, int], ...]]
    wave: tuple[int, ...] | None = None
    amplitude: float = 1.0
    description: str = ""
    pitch_hz: float = 0.0
    """The note this recipe sounds, or 0 when it has no pitch at all.

    Noise has none, and that is exactly what makes it safe to drop anywhere: it
    cannot be out of tune with the part that is already playing.
    """

    @property
    def needs_wave_ram(self) -> bool:
        return self.wave is not None


def _length_load(seconds: float, bits: int) -> int:
    """Pick the length counter value that silences the channel after ``seconds``.

    The counter counts up to its maximum at 256 Hz, so the value loaded is the
    complement of the duration. Durations longer than the counter can express
    saturate at the longest available, which for a drum is always long enough.
    """
    span = 1 << bits
    steps = int(round(seconds * LENGTH_TICK_HZ))
    return max(0, min(span - 1, span - steps))


def _envelope_pace(seconds: float, start_volume: int) -> int:
    """Pick the envelope pace whose fade lands closest to ``seconds``.

    Pace 0 disables the envelope entirely, so a decay too fast to express is
    clamped to the fastest real fade rather than to silence.
    """
    if start_volume <= 0 or seconds <= 0:
        return 1
    steps = start_volume
    ideal = seconds * ENVELOPE_TICK_HZ / steps
    return max(1, min(7, int(round(ideal))))


def _sweep_register(octaves_down: float, seconds: float) -> int:
    """Build NR10 for a downward pitch sweep of roughly ``octaves_down``.

    Each sweep tick subtracts ``period >> step`` from the period, and because
    frequency is ``131072 / (2048 - period)`` a shrinking period means a falling
    pitch. Halving the period takes about ``0.69 * 2^step`` ticks, which fixes
    the pace once the wanted duration is known.
    """
    if octaves_down <= 0 or seconds <= 0:
        return 0
    best = (0, math.inf)
    for step in range(1, 8):
        ticks_per_octave = math.log(2.0) * (1 << step)
        for pace in range(1, 8):
            achieved = seconds * SWEEP_TICK_HZ / (pace * ticks_per_octave)
            error = abs(achieved - octaves_down)
            if error < best[1]:
                best = ((pace << 4) | 0x08 | step, error)
    return best[0]


def _square_writes(
    host: Host,
    *,
    period: int,
    duty: int,
    volume: int,
    envelope_pace: int,
    length_seconds: float,
    sweep: int = 0,
) -> dict[int, tuple[tuple[int, int], ...]]:
    base = _BASE[host]
    length = _length_load(length_seconds, bits=6)
    writes: list[tuple[int, int]] = []
    if host is Host.CH1:
        writes.append((gb.NR10, sweep))
    writes.extend(
        [
            (base + 1, ((duty & 3) << 6) | length),
            (base + 2, ((volume & 0x0F) << 4) | (envelope_pace & 7)),
            (base + 3, period & 0xFF),
            (base + 4, 0xC0 | ((period >> 8) & 7)),
        ]
    )
    return {0: tuple(writes)}


def _noise_writes(
    *,
    shift: int,
    divisor: int,
    short_mode: bool,
    volume: int,
    envelope_pace: int,
    length_seconds: float,
) -> dict[int, tuple[tuple[int, int], ...]]:
    length = _length_load(length_seconds, bits=6)
    control = ((shift & 0x0F) << 4) | (0x08 if short_mode else 0) | (divisor & 7)
    return {
        0: (
            (gb.NR41, length),
            (gb.NR42, ((volume & 0x0F) << 4) | (envelope_pace & 7)),
            (gb.NR43, control),
            (gb.NR44, 0xC0),
        )
    }


def _wave_writes(
    *, period: int, level: int, length_seconds: float, wave: tuple[int, ...]
) -> dict[int, tuple[tuple[int, int], ...]]:
    """Load a timbre and trigger CH3.

    Wave RAM can only be rewritten with the channel's DAC off, which clicks. The
    click is placed on the attack transient, where it is masked by the very
    event it introduces.
    """
    length = _length_load(length_seconds, bits=8)
    packed = gb.pack_wave(wave)
    writes: list[tuple[int, int]] = [(gb.NR30, 0x00)]
    writes.extend((gb.WAVE_RAM_BASE + i, byte) for i, byte in enumerate(packed))
    writes.extend(
        [
            (gb.NR30, 0x80),
            (gb.NR31, length & 0xFF),
            (gb.NR32, (level & 3) << 5),
            (gb.NR33, period & 0xFF),
            (gb.NR34, 0xC0 | ((period >> 8) & 7)),
        ]
    )
    return {0: tuple(writes)}


def _frames(seconds: float, tick_hz: float) -> int:
    return max(1, int(math.ceil(seconds * tick_hz)))


# ---------------------------------------------------------------------------
# per-role recipes
# ---------------------------------------------------------------------------
def _kick_square(analysis: SampleAnalysis, host: Host, tick_hz: float) -> Recipe:
    """A kick as a swept square: the standard chiptune substitute for a sample.

    The sweep is what makes it read as a drum rather than as a short bass note,
    and CH1 is the only channel that can do it in hardware.
    """
    f = analysis.features
    pitch = f.pitch_hz if 30.0 <= f.pitch_hz <= 400.0 else 160.0
    # Start above the measured pitch and fall onto it: a kick's perceived pitch
    # is its body, not its transient.
    start_hz = max(gb.SQUARE_MIN_HZ, pitch * 2.2)
    period, _ = gb.square_period(start_hz)
    body = max(0.03, min(f.effective_duration, 0.20))
    sweep = _sweep_register(1.2, body * 0.8) if host is Host.CH1 else 0
    return Recipe(
        host=host,
        role=Role.KICK,
        frames=_frames(body, tick_hz),
        writes=_square_writes(
            host,
            period=period,
            duty=2,
            volume=15,
            envelope_pace=_envelope_pace(body, 15),
            length_seconds=body,
            sweep=sweep,
        ),
        amplitude=f.amplitude,
        description=(
            f"{'swept' if sweep else 'plain'} square from {start_hz:.0f} Hz "
            f"over {body * 1000:.0f} ms"
        ),
        pitch_hz=pitch,
    )


def _kick_noise(analysis: SampleAnalysis, tick_hz: float) -> Recipe:
    """A kick as low noise: worse, but it never steals a melodic channel.

    At the bottom of the noise table the LFSR runs slowly enough that individual
    transitions are audible as a rumble, which reads as a thump.
    """
    f = analysis.features
    body = max(0.03, min(f.effective_duration, 0.15))
    shift, divisor, _ = gb.nearest_noise(max(120.0, f.pitch_hz * NOISE_CENTROID_RATIO))
    return Recipe(
        host=Host.CH4,
        role=Role.KICK,
        frames=_frames(body, tick_hz),
        writes=_noise_writes(
            shift=shift,
            divisor=divisor,
            short_mode=False,
            volume=15,
            envelope_pace=_envelope_pace(body, 15),
            length_seconds=body,
        ),
        amplitude=f.amplitude,
        description=f"low noise thump over {body * 1000:.0f} ms",
    )


def _snare_noise(analysis: SampleAnalysis, tick_hz: float) -> Recipe:
    f = analysis.features
    body = max(0.02, min(f.effective_duration, 0.15))
    target = max(200.0, f.centroid_hz * NOISE_CENTROID_RATIO)
    shift, divisor, _ = gb.nearest_noise(target)
    return Recipe(
        host=Host.CH4,
        role=Role.SNARE,
        frames=_frames(body, tick_hz),
        writes=_noise_writes(
            shift=shift,
            divisor=divisor,
            short_mode=False,
            volume=15,
            envelope_pace=_envelope_pace(body, 15),
            length_seconds=body,
        ),
        amplitude=f.amplitude,
        description=(
            f"noise at {gb.noise_frequency(shift, divisor):.0f} Hz "
            f"for centroid {f.centroid_hz:.0f} Hz over {body * 1000:.0f} ms"
        ),
    )


def _snare_body(analysis: SampleAnalysis, host: Host, tick_hz: float) -> Recipe:
    """The tonal half of a snare, layered under the noise when a square is free.

    A real snare is a crack plus a shell resonance. The noise alone sounds thin;
    one or two frames of a mid square underneath restores the body.
    """
    f = analysis.features
    body = max(0.02, min(f.effective_duration * 0.5, 0.05))
    period, _ = gb.square_period(max(gb.SQUARE_MIN_HZ, min(f.centroid_hz * 0.5, 1200.0)))
    return Recipe(
        host=host,
        role=Role.SNARE,
        frames=_frames(body, tick_hz),
        writes=_square_writes(
            host,
            period=period,
            duty=0,
            volume=9,
            envelope_pace=_envelope_pace(body, 9),
            length_seconds=body,
        ),
        amplitude=f.amplitude * 0.5,
        description=f"square body layer over {body * 1000:.0f} ms",
        pitch_hz=max(gb.SQUARE_MIN_HZ, min(f.centroid_hz * 0.5, 1200.0)),
    )


def _hat_noise(analysis: SampleAnalysis, tick_hz: float) -> Recipe:
    f = analysis.features
    body = max(0.012, min(f.effective_duration, 0.06))
    target = max(2000.0, f.centroid_hz * NOISE_CENTROID_RATIO)
    shift, divisor, _ = gb.nearest_noise(target)
    # The 7-bit LFSR repeats every 127 steps, giving a metallic ring that suits
    # a hat and separates it from the snare sharing the same channel.
    short_mode = f.flatness < 0.35
    return Recipe(
        host=Host.CH4,
        role=Role.HAT,
        frames=_frames(body, tick_hz),
        writes=_noise_writes(
            shift=shift,
            divisor=divisor,
            short_mode=short_mode,
            volume=11,
            envelope_pace=_envelope_pace(body, 11),
            length_seconds=body,
        ),
        amplitude=f.amplitude * 0.7,
        description=f"{'metallic' if short_mode else 'white'} tick over {body * 1000:.0f} ms",
    )


def _tom_wave(analysis: SampleAnalysis, tick_hz: float) -> Recipe:
    """A tom as a pitched wave note that rings and decays.

    Two things separate a tom from the kick recipe, and the sample only sounds
    like a tom when both are present. Its pitch has to be audible, so there is
    no sweep - a sweep is what makes a square read as a kick in the first place.
    And it has to ring: the sample this is built from is still at a sixth of its
    peak after a quarter of a second, and a drum truncated to its transient is
    just a click.

    CH3 has no envelope generator, only four output levels, so the decay is
    stepped down by hand across the tail.
    """
    f = analysis.features
    # The whole recording, not just its loud head: the tail is the resonance,
    # and the resonance is the instrument.
    body = max(0.04, min(analysis.decoded.duration_seconds, 0.30))
    pitch = f.pitch_hz if f.pitch_hz > 0 else 220.0
    period, _ = gb.wave_period(pitch)
    table = tuple(wave_table(analysis))
    frames = _frames(body, tick_hz)
    writes = dict(
        _wave_writes(period=period, level=1, length_seconds=body, wave=table)
    )
    for step, level in ((0.35, 2), (0.65, 3)):
        frame = int(frames * step)
        if frame > 0:
            writes[frame] = ((gb.NR32, (level & 3) << 5),)
    return Recipe(
        host=Host.CH3,
        role=Role.TOM,
        frames=frames,
        writes=writes,
        wave=table,
        amplitude=f.amplitude,
        description=f"wave tom at {pitch:.0f} Hz ringing over {body * 1000:.0f} ms",
        pitch_hz=pitch,
    )


def _tonal_wave(analysis: SampleAnalysis, tick_hz: float, held_seconds: float) -> Recipe:
    """A melodic sample as a real note, using a timbre lifted from the sample.

    CH3 is the right host because its waveform is programmable: a bass line
    sampled from a real instrument keeps some of its character instead of
    becoming a generic square.
    """
    f = analysis.features
    pitch = f.pitch_hz if 30.0 <= f.pitch_hz <= 2000.0 else 110.0
    period, _ = gb.wave_period(pitch)
    table = tuple(wave_table(analysis))
    return Recipe(
        host=Host.CH3,
        role=Role.TONAL,
        frames=_frames(held_seconds, tick_hz),
        writes=_wave_writes(
            period=period,
            level=1,
            length_seconds=held_seconds,
            wave=table,
        ),
        wave=table,
        amplitude=f.amplitude,
        description=f"wave note at {pitch:.0f} Hz with a timbre from the sample",
        pitch_hz=pitch,
    )


def _tonal_square(analysis: SampleAnalysis, host: Host, tick_hz: float, held_seconds: float) -> Recipe:
    f = analysis.features
    pitch = f.pitch_hz if 30.0 <= f.pitch_hz <= 2000.0 else 110.0
    period, _ = gb.square_period(pitch)
    return Recipe(
        host=host,
        role=Role.TONAL,
        frames=_frames(held_seconds, tick_hz),
        writes=_square_writes(
            host,
            period=period,
            duty=1,
            volume=12,
            envelope_pace=0,
            length_seconds=held_seconds,
        ),
        amplitude=f.amplitude,
        description=f"square note at {pitch:.0f} Hz",
        pitch_hz=pitch,
    )


# ---------------------------------------------------------------------------
# public entry point
# ---------------------------------------------------------------------------
_ROLE_BENEFIT = {
    Role.KICK: 1.0,
    Role.SNARE: 0.95,
    Role.TONAL: 0.9,
    Role.TOM: 0.7,
    Role.HAT: 0.45,
    Role.NOISE_SFX: 0.3,
    Role.ARTIFACT: 0.0,
}


def role_benefit(analysis: SampleAnalysis) -> float:
    """How much is lost by dropping this voice, in the allocator's units.

    Kick and snare carry the groove and are missed immediately; a hat is
    decoration. Amplitude enters as a square root because loudness is perceived
    roughly that way: a sample at a third of full scale is not a third as
    important. Classification confidence scales it too, so a doubtful verdict is
    a worse bet than a certain one.
    """
    base = _ROLE_BENEFIT.get(analysis.role, 0.3)
    loudness = min(1.0, math.sqrt(max(0.0, analysis.features.amplitude)))
    certainty = 0.5 + 0.5 * analysis.confidence
    return base * loudness * certainty


def candidates(
    analysis: SampleAnalysis,
    *,
    tick_hz: float = 60.0,
    held_seconds: float | None = None,
    allow_tonal_hosts: bool = True,
    allow_layers: bool = True,
) -> list[Recipe]:
    """Every way this sample could be played, best first.

    The allocator walks this list and takes the first host it can afford, so the
    order encodes musical preference: the recipe that sounds most like the
    original comes first, cheaper fallbacks after it. ``allow_tonal_hosts``
    exists so a preset can forbid touching a melodic channel at all.
    """
    if not analysis.role.is_playable:
        return []

    held = held_seconds if held_seconds is not None else analysis.features.duration
    out: list[Recipe] = []

    if analysis.role is Role.KICK:
        if allow_tonal_hosts:
            out.append(_kick_square(analysis, Host.CH1, tick_hz))
            out.append(_kick_square(analysis, Host.CH2, tick_hz))
        out.append(_kick_noise(analysis, tick_hz))
    elif analysis.role is Role.SNARE:
        out.append(_snare_noise(analysis, tick_hz))
        if allow_tonal_hosts and allow_layers:
            out.append(_snare_body(analysis, Host.CH2, tick_hz))
            out.append(_snare_body(analysis, Host.CH1, tick_hz))
    elif analysis.role is Role.HAT:
        out.append(_hat_noise(analysis, tick_hz))
    elif analysis.role is Role.TOM:
        if allow_tonal_hosts:
            out.append(_tom_wave(analysis, tick_hz))
        # Not the kick recipe as a fallback: its sweep erases the pitch that
        # made this a tom rather than a kick. Noise keeps the attack instead.
        out.append(_snare_noise(analysis, tick_hz))
    elif analysis.role is Role.TONAL:
        if allow_tonal_hosts:
            out.append(_tonal_wave(analysis, tick_hz, held))
            out.append(_tonal_square(analysis, Host.CH2, tick_hz, held))
            out.append(_tonal_square(analysis, Host.CH1, tick_hz, held))
    elif analysis.role is Role.NOISE_SFX:
        out.append(_snare_noise(analysis, tick_hz))

    return out


@dataclass
class LayerPlan:
    """A primary recipe plus optional reinforcement on another channel."""

    primary: Recipe
    layers: list[Recipe] = field(default_factory=list)
