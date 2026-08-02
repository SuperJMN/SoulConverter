"""Decide which Game Boy channel each DPCM voice borrows, and give it back.

The Game Boy has four generators and the NES was already using all four, so a
fifth voice can only exist by taking someone's turn. This module prices that
theft. The price is perceptual: silencing a noise channel already playing a
16 ms hat under a drum hit costs almost nothing, because the hit masks it;
silencing a sustained bass note costs a great deal, because its absence is a
hole in the music.

Two invariants hold throughout:

* **Every borrowed channel is handed back.** A channel is never left muted or
  left playing the drum's settings, which would detune or silence the next NES
  note. CH1's sweep in particular is persistent and is always cleared.
* **The DMC was monophonic**, so a voice is truncated when the next sample
  starts. Overlapping drums would be a sound the original never made.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field

from .dpcm import Role, SampleAnalysis, analyze_all
from .nes_apu import ApuTimeline, SampleRef
from .percussion import Host, Recipe, candidates, role_benefit
from .vgm_reader import DpcmRom

HOST_TO_NES = {
    Host.CH1: "pulse1",
    Host.CH2: "pulse2",
    Host.CH3: "triangle",
    Host.CH4: "noise",
}

# What it costs, per frame of full-volume playing, to silence each channel.
# The triangle is dear because it usually carries the bass line and because
# restoring CH3 means reloading wave RAM, which clicks. Noise is *not* cheap by
# default: a sustained noise run at full volume is a deliberate voice. What
# makes the NES noise channel affordable is that it nearly always plays
# decoration, and that is priced separately by DECORATIVE_NOISE_DISCOUNT -
# discounting it twice would leave real noise parts unprotected.
CHANNEL_WEIGHT = {
    Host.CH1: 1.0,
    Host.CH2: 1.0,
    Host.CH3: 1.4,
    Host.CH4: 1.0,
}

# Cutting a note that starts inside the borrowed span loses the whole note, not
# just its tail, so an attack is priced above a steady-state frame.
ATTACK_PENALTY = 1.6

# A noise burst this short is decoration - a hat or a tick - and on the NES it
# almost always sits on top of a DPCM hit rather than replacing it. The hit
# masks it, so taking the channel for that span costs next to nothing. This is
# the single measurement that makes drums fit at all: on real material the NES
# noise channel is busy for only a few percent of the song, but what little it
# does play lands exactly on the beats the drums need.
DECORATIVE_NOISE_FRAMES = 2
DECORATIVE_NOISE_DISCOUNT = 0.1
# A noise run that ends this close to its own peak was held on purpose; one that
# ends quieter was struck and left to fade, which is percussion.
_SUSTAINED_NOISE_RATIO = 0.8

# How much dearer than the cheapest option the best-sounding host may be before
# cost overrules timbre. Roughly the price of a frame of a quiet note: enough to
# keep preference in charge when the difference is marginal.
PREFERENCE_TOLERANCE = 0.15

# How close two voices must be, in semitones, before the new one is heard as
# fighting the old rather than accompanying it. A fifth is the classical line:
# inside it two square-ish tones beat and mask each other, outside it they read
# as separate registers. A kick rebuilt at 183 Hz against a bass at 196 Hz is
# 1.2 semitones away - the same note, out of tune.
CLASH_SEMITONES = 12.0

# What a full clash costs per frame. It has to exceed PREFERENCE_TOLERANCE on
# its own, or a preferred host would still win the tie-break while stepping on
# the bass; and it has to stay under the price of interrupting a real note, so
# that avoiding a clash never justifies silencing someone.
CLASH_WEIGHT = 0.6

# How far either side of a hit the register still counts as occupied. Hearing
# groups events that are close in time and in pitch into one stream, so a low
# square struck in the gap between two bass notes is not heard as a drum that
# got away with it - it is heard as a wrong note in the bass line. Four frames
# is about 66 ms, the span over which that grouping happens.
CLASH_CONTEXT_FRAMES = 4


@dataclass(frozen=True)
class Preset:
    """A named trade-off between keeping the NES voices and adding the fifth.

    ``boldness`` scales the benefit before it is compared with the cost, so it
    is the single knob that decides how readily a channel gets borrowed.
    """

    name: str
    enabled: bool
    allow_tonal_hosts: bool
    allow_layers: bool
    allow_melodic_dpcm: bool
    boldness: float
    description: str


PRESETS: dict[str, Preset] = {
    "off": Preset(
        name="off",
        enabled=False,
        allow_tonal_hosts=False,
        allow_layers=False,
        allow_melodic_dpcm=False,
        boldness=0.0,
        description="drop the DPCM channel entirely",
    ),
    "safe": Preset(
        name="safe",
        enabled=True,
        allow_tonal_hosts=False,
        allow_layers=False,
        allow_melodic_dpcm=False,
        boldness=1.0,
        description="percussion on the noise channel only, melody never touched",
    ),
    "balanced": Preset(
        name="balanced",
        enabled=True,
        allow_tonal_hosts=True,
        allow_layers=False,
        allow_melodic_dpcm=True,
        boldness=1.0,
        description="drums and sampled bass on a tonal channel where one is free enough",
    ),
    "full": Preset(
        name="full",
        enabled=True,
        allow_tonal_hosts=True,
        allow_layers=True,
        allow_melodic_dpcm=True,
        boldness=1.8,
        description="everything: layered drums and melodic DPCM, borrowing freely",
    ),
}

DEFAULT_PRESET = "off"


@dataclass(frozen=True)
class ScheduledVoice:
    """One DPCM event placed on a host channel for a span of ticks."""

    tick: int
    frames: int
    host: Host
    recipe: Recipe
    analysis: SampleAnalysis
    cost: float
    benefit: float
    layer_of: int | None = None

    @property
    def end(self) -> int:
        return self.tick + self.frames


@dataclass
class AllocationStats:
    hits: int = 0
    placed: int = 0
    dropped_artifact: int = 0
    dropped_too_costly: int = 0
    dropped_no_host: int = 0
    layers: int = 0
    per_role: dict[str, int] = field(default_factory=dict)
    per_host: dict[str, int] = field(default_factory=dict)
    preempted_frames: dict[str, int] = field(default_factory=dict)

    @property
    def realised_fraction(self) -> float:
        playable = self.hits - self.dropped_artifact
        return self.placed / playable if playable else 0.0


@dataclass
class AllocationPlan:
    """Everything the converter needs to weave the fifth voice in."""

    preset: Preset
    voices: list[ScheduledVoice] = field(default_factory=list)
    analyses: dict[SampleRef, SampleAnalysis] = field(default_factory=dict)
    hit_counts: dict[SampleRef, int] = field(default_factory=dict)
    stats: AllocationStats = field(default_factory=AllocationStats)
    _busy: dict[Host, set[int]] = field(default_factory=dict)
    _starts: dict[int, list[ScheduledVoice]] = field(default_factory=dict)
    _releases: dict[int, set[Host]] = field(default_factory=dict)

    def is_borrowed(self, host: Host, tick: int) -> bool:
        return tick in self._busy.get(host, ())

    def borrowed_at(self, tick: int) -> set[str]:
        """Channel names unavailable to the NES part on this tick."""
        return {
            host.value for host, ticks in self._busy.items() if tick in ticks
        }

    def starting_at(self, tick: int) -> list[ScheduledVoice]:
        return self._starts.get(tick, [])

    def released_at(self, tick: int) -> set[Host]:
        return self._releases.get(tick, set())

    def index(self) -> None:
        """Rebuild the tick-indexed views after the voice list is final."""
        self._busy = {host: set() for host in Host}
        self._starts = {}
        self._releases = {}
        for voice in self.voices:
            self._busy[voice.host].update(range(voice.tick, voice.end))
            self._starts.setdefault(voice.tick, []).append(voice)
        for host, ticks in self._busy.items():
            for tick in ticks:
                if tick + 1 not in ticks:
                    self._releases.setdefault(tick + 1, set()).add(host)


# ---------------------------------------------------------------------------
# occupancy of the NES side
# ---------------------------------------------------------------------------
@dataclass
class _Occupancy:
    """Per-tick activity of the four NES voices, as the cost model sees it."""

    volume: dict[Host, list[float]]
    attack: dict[Host, list[bool]]
    burst: dict[Host, list[int]]
    decorative: list[bool] = field(default_factory=list)
    pitches: list[dict[Host, float]] = field(default_factory=list)

    def __post_init__(self) -> None:
        if not self.decorative:
            self.decorative = self._decorative_noise(
                self.volume[Host.CH4], self.burst[Host.CH4]
            )

    @classmethod
    def measure(cls, timeline: ApuTimeline) -> "_Occupancy":
        volume: dict[Host, list[float]] = {host: [] for host in Host}
        attack: dict[Host, list[bool]] = {host: [] for host in Host}
        pitches: list[dict[Host, float]] = []
        for tick in timeline.ticks:
            states = {
                Host.CH1: tick.pulse1,
                Host.CH2: tick.pulse2,
                Host.CH3: tick.triangle,
                Host.CH4: tick.noise,
            }
            sounding: dict[Host, float] = {}
            for host, state in states.items():
                playing = bool(getattr(state, "playing", False))
                level = getattr(state, "volume", None)
                if level is None:
                    # The triangle has no volume control: it is on or it is off.
                    loudness = 1.0 if playing else 0.0
                else:
                    loudness = (level / 15.0) if playing else 0.0
                volume[host].append(loudness)
                attack[host].append(bool(getattr(state, "retriggered", False)) and playing)
                if playing and host is not Host.CH4:
                    hz = float(getattr(state, "frequency_hz", 0.0) or 0.0)
                    if hz > 0.0:
                        sounding[host] = hz
            pitches.append(sounding)
        burst = {host: cls._burst_lengths(volume[host]) for host in Host}
        decorative = cls._decorative_noise(volume[Host.CH4], burst[Host.CH4])
        return cls(
            volume=volume,
            attack=attack,
            burst=burst,
            decorative=decorative,
            pitches=pitches,
        )
    @staticmethod
    def _burst_lengths(levels: list[float]) -> list[int]:
        """For each tick, how many consecutive ticks its sounding run lasts."""
        out = [0] * len(levels)
        run_end = len(levels)
        for tick in range(len(levels) - 1, -1, -1):
            if levels[tick] <= 0.0:
                run_end = tick
                continue
            out[tick] = run_end - tick
        start = 0
        while start < len(levels):
            if out[start] == 0:
                start += 1
                continue
            length = out[start]
            for offset in range(length):
                out[start + offset] = length
            start += length
        return out

    @staticmethod
    def _decorative_noise(levels: list[float], bursts: list[int]) -> list[bool]:
        """Which noise runs are percussive decoration rather than a real part.

        The distinction is audible and the NES writes it down: a run that fades
        from its attack is a hit, and a hit is masked by the DPCM stroke landing
        on top of it. A run held at a steady level is somebody playing the noise
        channel, and taking it away would be heard as a missing part. Runs of a
        frame or two are decoration whatever their shape, because nothing
        sustained fits in 33 ms.
        """
        out = [False] * len(levels)
        start = 0
        while start < len(levels):
            length = bursts[start]
            if length <= 0:
                start += 1
                continue
            run = levels[start : start + length]
            faded = run[-1] < _SUSTAINED_NOISE_RATIO * max(run)
            if length <= DECORATIVE_NOISE_FRAMES or faded:
                for offset in range(length):
                    out[start + offset] = True
            start += length
        return out

    def cost(self, host: Host, start: int, frames: int) -> float:
        weight = CHANNEL_WEIGHT[host]
        levels = self.volume[host]
        attacks = self.attack[host]
        total = 0.0
        for tick in range(start, min(start + frames, len(levels))):
            level = levels[tick]
            if level <= 0.0:
                continue
            frame_cost = level * weight
            if attacks[tick]:
                frame_cost += level * weight * ATTACK_PENALTY
            if host is Host.CH4 and self.decorative[tick]:
                frame_cost *= DECORATIVE_NOISE_DISCOUNT
            total += frame_cost
        return total

    def rate(self, host: Host, start: int, frames: int) -> float:
        """Cost per frame held, which is what a benefit per hit compares against.

        ``cost`` integrates over the frames a voice occupies, so a long note is
        expensive simply for being long. Weighed against a benefit that does not
        grow with duration, no sustained note could ever be afforded - which is
        wrong, because a bass note that plays for a second is also *giving* for
        that second. Comparing rates puts both sides in the same units.
        """
        return self.cost(host, start, frames) / max(1, frames)

    def clash(self, pitch_hz: float, host: Host, start: int, frames: int) -> float:
        """What it costs to put a note into a register another part is using.

        An idle channel is not the same as an idle register. Masking and beating
        happen between frequencies, not between channels, so a kick rebuilt as a
        swept square lands on top of a bass line playing the same notes even
        though nothing was interrupted to make room for it - and two square-ish
        voices a semitone apart do not sound like a drum and a bass, they sound
        like a mistake.

        The host's own part is excluded: it is being replaced, not competed
        with. Noise has no pitch and therefore cannot clash, which is the whole
        reason it stays the safe host for percussion.

        The worst moment sets the price, not the average one. A beat between two
        close tones is heard the instant it happens; spreading it over the
        frames that were clean would price a clearly audible collision as if it
        were half of one.
        """
        if pitch_hz <= 0.0 or not self.pitches:
            return 0.0
        first = max(0, start - CLASH_CONTEXT_FRAMES)
        last = min(start + frames + CLASH_CONTEXT_FRAMES, len(self.pitches))
        worst = 0.0
        for tick in range(first, last):
            for other_host, other in self.pitches[tick].items():
                if other_host is host:
                    continue
                distance = abs(12.0 * math.log2(pitch_hz / other))
                if distance >= CLASH_SEMITONES:
                    continue
                worst = max(worst, 1.0 - distance / CLASH_SEMITONES)
        return CLASH_WEIGHT * worst


# ---------------------------------------------------------------------------
# planning
# ---------------------------------------------------------------------------
def _collect_hits(timeline: ApuTimeline) -> list[tuple[int, SampleRef]]:
    return [
        (index, tick.dmc.sample)
        for index, tick in enumerate(timeline.ticks)
        if tick.dmc.started and tick.dmc.sample is not None
    ]


def plan(
    timeline: ApuTimeline,
    rom: DpcmRom,
    *,
    preset: str | Preset = DEFAULT_PRESET,
    overrides: dict[int, Role] | None = None,
) -> AllocationPlan:
    """Work out what the fifth voice plays, and at whose expense.

    Voices are placed in the order they occur rather than by global optimisation:
    the DMC was monophonic and each hit is independent, so a greedy pass over
    time gives the same answer as a search would, and stays explainable.
    """
    chosen = preset if isinstance(preset, Preset) else PRESETS[preset]
    result = AllocationPlan(preset=chosen)

    hits = _collect_hits(timeline)
    refs = [ref for _, ref in hits]
    for ref in refs:
        result.hit_counts[ref] = result.hit_counts.get(ref, 0) + 1
    result.stats.hits = len(hits)

    # Even when the fifth voice is switched off the report still says what is
    # being thrown away and what it was, because that is exactly the
    # information someone needs to decide whether to switch it on.
    if not chosen.enabled or not hits:
        if refs:
            result.analyses = analyze_all(rom, refs, overrides=overrides)
        result.index()
        return result

    result.analyses = analyze_all(rom, refs, overrides=overrides)

    occupancy = _Occupancy.measure(timeline)
    total_ticks = len(timeline.ticks)

    for position, (tick, ref) in enumerate(hits):
        analysis = result.analyses[ref]
        if not analysis.role.is_playable:
            result.stats.dropped_artifact += 1
            continue
        if analysis.role is Role.TONAL and not chosen.allow_melodic_dpcm:
            result.stats.dropped_too_costly += 1
            continue

        # The DMC could only play one sample at a time: the next hit cuts this
        # one short, and so must its stand-in. A sample that runs out before the
        # next hit stops there too, unless it was set to loop - holding a note
        # through a gap the original left silent invents a sustain.
        next_tick = hits[position + 1][0] if position + 1 < len(hits) else total_ticks
        room = max(1, min(next_tick, total_ticks) - tick)
        if not timeline.ticks[tick].dmc.looping:
            sample_frames = analysis.decoded.duration_seconds * timeline.tick_hz
            room = max(1, min(room, int(round(sample_frames))))

        options = candidates(
            analysis,
            tick_hz=timeline.tick_hz,
            held_seconds=room / timeline.tick_hz,
            allow_tonal_hosts=chosen.allow_tonal_hosts,
            allow_layers=chosen.allow_layers,
        )
        if not options:
            result.stats.dropped_no_host += 1
            continue

        benefit = role_benefit(analysis) * chosen.boldness
        placed = _place(result, options, tick, room, occupancy, benefit, analysis)
        if placed is None:
            result.stats.dropped_too_costly += 1
            continue

        result.stats.placed += 1
        role_name = analysis.role.value
        result.stats.per_role[role_name] = result.stats.per_role.get(role_name, 0) + 1

        if chosen.allow_layers:
            _add_layer(result, options, placed, tick, room, occupancy, benefit, analysis)

    result.index()
    for voice in result.voices:
        name = voice.host.value
        result.stats.per_host[name] = result.stats.per_host.get(name, 0) + 1
        stolen = int(round(occupancy.cost(voice.host, voice.tick, voice.frames)))
        if stolen:
            result.stats.preempted_frames[name] = (
                result.stats.preempted_frames.get(name, 0) + stolen
            )
    return result


def _place(
    result: AllocationPlan,
    options: list[Recipe],
    tick: int,
    room: int,
    occupancy: _Occupancy,
    benefit: float,
    analysis: SampleAnalysis,
) -> ScheduledVoice | None:
    """Take the best-sounding host the voice can afford, unless another is much cheaper.

    The candidate list is in musical preference order, but taking the first
    affordable entry means never looking at a channel sitting idle right next to
    the one being interrupted. Percussion never noticed - its hosts nearly all
    cost nothing, so the preferred one won anyway - but a sustained bass note
    would evict the triangle for the whole song while a pulse channel stayed
    silent. Preference still decides between hosts that cost about the same;
    a clearly cheaper host overrules it.
    """
    priced: list[tuple[Recipe, int, float]] = []
    for recipe in options:
        frames = max(1, min(recipe.frames, room))
        if _conflicts(result, recipe.host, tick, frames):
            continue
        priced.append(
            (
                recipe,
                frames,
                occupancy.rate(recipe.host, tick, frames)
                + occupancy.clash(recipe.pitch_hz, recipe.host, tick, frames),
            )
        )
    if not priced:
        return None

    cheapest = min(cost for _, _, cost in priced)
    affordable = [
        item for item in priced if item[2] < benefit and item[2] <= cheapest + PREFERENCE_TOLERANCE
    ]
    if not affordable:
        return None
    recipe, frames, cost = affordable[0]
    voice = ScheduledVoice(
        tick=tick,
        frames=frames,
        host=recipe.host,
        recipe=recipe,
        analysis=analysis,
        cost=cost,
        benefit=benefit,
    )
    result.voices.append(voice)
    return voice


def _add_layer(
    result: AllocationPlan,
    options: list[Recipe],
    primary: ScheduledVoice,
    tick: int,
    room: int,
    occupancy: _Occupancy,
    benefit: float,
    analysis: SampleAnalysis,
) -> None:
    """Reinforce a hit on a second channel when one happens to be free.

    A layer is a luxury, so it is only taken when it costs nothing at all: it
    must never be the reason a melodic note goes missing, and never the reason
    a bass note has a drum sitting on its register.
    """
    for recipe in options:
        if recipe.host is primary.host:
            continue
        frames = max(1, min(recipe.frames, room))
        if _conflicts(result, recipe.host, tick, frames):
            continue
        if occupancy.cost(recipe.host, tick, frames) > 0.0:
            continue
        if occupancy.clash(recipe.pitch_hz, recipe.host, tick, frames) > 0.0:
            continue
        result.voices.append(
            ScheduledVoice(
                tick=tick,
                frames=frames,
                host=recipe.host,
                recipe=recipe,
                analysis=analysis,
                cost=0.0,
                benefit=benefit * 0.3,
                layer_of=tick,
            )
        )
        result.stats.layers += 1
        return


def _conflicts(result: AllocationPlan, host: Host, tick: int, frames: int) -> bool:
    """Has an earlier voice already booked this channel over this span?"""
    for voice in reversed(result.voices):
        if voice.end <= tick:
            break
        if voice.host is host and voice.tick < tick + frames and tick < voice.end:
            return True
    return False
