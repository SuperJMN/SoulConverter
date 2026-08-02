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
# Noise is cheap because on the NES it is decoration; the triangle is dear
# because it usually carries the bass line and because restoring CH3 means
# reloading wave RAM, which clicks.
CHANNEL_WEIGHT = {
    Host.CH1: 1.0,
    Host.CH2: 1.0,
    Host.CH3: 1.4,
    Host.CH4: 0.22,
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
        allow_melodic_dpcm=False,
        boldness=1.0,
        description="percussion on noise, plus a tonal channel when it is idle",
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

    @classmethod
    def measure(cls, timeline: ApuTimeline) -> "_Occupancy":
        volume: dict[Host, list[float]] = {host: [] for host in Host}
        attack: dict[Host, list[bool]] = {host: [] for host in Host}
        for tick in timeline.ticks:
            states = {
                Host.CH1: tick.pulse1,
                Host.CH2: tick.pulse2,
                Host.CH3: tick.triangle,
                Host.CH4: tick.noise,
            }
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
        burst = {host: cls._burst_lengths(volume[host]) for host in Host}
        return cls(volume=volume, attack=attack, burst=burst)

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

    def cost(self, host: Host, start: int, frames: int) -> float:
        weight = CHANNEL_WEIGHT[host]
        levels = self.volume[host]
        attacks = self.attack[host]
        bursts = self.burst[host]
        total = 0.0
        for tick in range(start, min(start + frames, len(levels))):
            level = levels[tick]
            if level <= 0.0:
                continue
            frame_cost = level * weight
            if attacks[tick]:
                frame_cost += level * weight * ATTACK_PENALTY
            if host is Host.CH4 and bursts[tick] <= DECORATIVE_NOISE_FRAMES:
                frame_cost *= DECORATIVE_NOISE_DISCOUNT
            total += frame_cost
        return total


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
        # one short, and so must its stand-in.
        next_tick = hits[position + 1][0] if position + 1 < len(hits) else total_ticks
        room = max(1, min(next_tick, total_ticks) - tick)

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
    """Take the first host the voice can afford, preferring the best-sounding.

    The candidate list is already in musical preference order, so walking it and
    stopping at the first affordable host trades fidelity for availability in
    exactly the order the recipes intended.
    """
    best: tuple[float, Recipe, int] | None = None
    for recipe in options:
        frames = max(1, min(recipe.frames, room))
        cost = occupancy.cost(recipe.host, tick, frames)
        if _conflicts(result, recipe.host, tick, frames):
            continue
        if cost < benefit:
            best = (cost, recipe, frames)
            break
        if best is None or cost < best[0]:
            best = (cost, recipe, frames)
    if best is None:
        return None
    cost, recipe, frames = best
    if cost >= benefit:
        return None
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
    must never be the reason a melodic note goes missing.
    """
    for recipe in options:
        if recipe.host is primary.host:
            continue
        frames = max(1, min(recipe.frames, room))
        if _conflicts(result, recipe.host, tick, frames):
            continue
        if occupancy.cost(recipe.host, tick, frames) > 0.0:
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
