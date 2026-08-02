"""Decode DPCM samples and work out what they were meant to sound like.

The Game Boy cannot replay a sample, so the only way to keep the fifth voice is
to recognise it. This module decodes the delta-modulated bytes back to a
waveform, measures physical properties of that waveform, and applies named rules
to name a percussive or tonal role. The classifier is deliberately rule-based
rather than statistical: every verdict has to be explainable in the conversion
report and overridable by hand when it gets one wrong.

The measurements the rules are calibrated against come from real material, not
from theory. See ``README.md`` for the numbers.
"""

from __future__ import annotations

from dataclasses import dataclass, replace
from enum import Enum
import math
from collections import Counter

import numpy as np

from .nes_apu import SampleRef
from .vgm_reader import DpcmRom

DELTA_STEP = 2
LEVEL_MAX = 127
DEFAULT_INITIAL_LEVEL = 64

_BAND_LOW_HZ = 250.0
_BAND_MID_HZ = 2000.0
_ENVELOPE_WINDOW_SECONDS = 0.003
_DECAY_THRESHOLD = 0.25
_PITCH_MIN_HZ = 40.0
_PITCH_MAX_HZ = 1500.0
# One delta step out of full scale is the quantisation floor of the format, so
# nothing quieter than a hair above it can carry musical intent - it is either
# encoded silence or DAC dither.
_SILENCE_FLOOR = 1.5 * DELTA_STEP / DEFAULT_INITIAL_LEVEL
# Measured drums reverse direction constantly and land near 0.55; only DC
# motion runs one way from end to end.
_ONE_WAY_RATIO = 0.95


class Role(str, Enum):
    """What a sample is musically, not what it is acoustically.

    ``ARTIFACT`` is not an instrument: it marks samples that carry no musical
    intent, such as the DAC-levelling blip many drivers emit at startup. Those
    must be dropped rather than re-synthesised, or the conversion gains a thump
    that the original never had.
    """

    KICK = "kick"
    SNARE = "snare"
    HAT = "hat"
    TOM = "tom"
    TONAL = "tonal"
    NOISE_SFX = "noise_sfx"
    ARTIFACT = "artifact"

    @property
    def is_percussive(self) -> bool:
        return self in (Role.KICK, Role.SNARE, Role.HAT, Role.TOM)

    @property
    def is_playable(self) -> bool:
        return self is not Role.ARTIFACT


@dataclass(frozen=True)
class DecodedSample:
    """A DPCM sample decoded to a normalised waveform."""

    ref: SampleRef
    waveform: np.ndarray
    rate_hz: float

    @property
    def duration_seconds(self) -> float:
        return self.waveform.size / self.rate_hz


@dataclass(frozen=True)
class SampleFeatures:
    """Physical measurements a rule can be written against.

    Everything here is either a duration in seconds, a normalised ratio in
    ``0..1`` or a frequency in Hz, so thresholds in the classifier read as
    statements about sound rather than as magic numbers.
    """

    duration: float
    effective_duration: float
    peak_position: float
    decay_ratio: float
    low: float
    mid: float
    high: float
    centroid_hz: float
    flatness: float
    pitch_hz: float
    pitch_confidence: float
    pitch_slope: float
    monotonic_ratio: float
    amplitude: float

    @property
    def percussive_decay(self) -> bool:
        """True when the sample dies away like a struck instrument.

        This is the single most discriminating property in practice. Delta
        modulation gives even a snare a strongly periodic look, so pitch
        confidence alone will happily call a drum a held note; a struck sound
        collapsing to near-silence inside a tenth of a second will not.
        """
        return self.effective_duration <= 0.12 and self.decay_ratio < 0.20


@dataclass(frozen=True)
class SampleAnalysis:
    """A decoded sample plus the verdict on what to play instead of it."""

    ref: SampleRef
    decoded: DecodedSample
    features: SampleFeatures
    role: Role
    confidence: float
    reasons: tuple[str, ...]
    overridden: bool = False

    @property
    def label(self) -> str:
        return self.ref.label


# ---------------------------------------------------------------------------
# decoding
# ---------------------------------------------------------------------------
def decode(
    rom: DpcmRom,
    ref: SampleRef,
    initial_level: int = DEFAULT_INITIAL_LEVEL,
) -> DecodedSample:
    """Run the 2A03 delta decoder over ``ref`` and normalise the result.

    Each bit nudges a 7-bit level by ±2 with saturation, least significant bit
    first. The output is centred on the initial level so that the DC offset the
    driver happened to leave in the DAC does not colour the spectrum.
    """

    data = np.frombuffer(
        bytes(rom.read(ref.address + i) for i in range(ref.length)), dtype=np.uint8
    )
    level = int(np.clip(initial_level, 0, LEVEL_MAX))
    out = np.empty(data.size * 8, dtype=np.float32)
    cursor = 0
    for byte in data.tolist():
        for bit in range(8):
            if byte & (1 << bit):
                if level <= LEVEL_MAX - DELTA_STEP:
                    level += DELTA_STEP
            elif level >= DELTA_STEP:
                level -= DELTA_STEP
            out[cursor] = level
            cursor += 1
    waveform = (out - initial_level) / float(DEFAULT_INITIAL_LEVEL)
    return DecodedSample(ref=ref, waveform=waveform, rate_hz=ref.rate_hz)


# ---------------------------------------------------------------------------
# feature extraction
# ---------------------------------------------------------------------------
def _envelope(waveform: np.ndarray, rate_hz: float) -> np.ndarray:
    window = max(1, int(rate_hz * _ENVELOPE_WINDOW_SECONDS))
    kernel = np.ones(window, dtype=np.float32) / window
    return np.convolve(np.abs(waveform), kernel, mode="same")


def _band_energies(waveform: np.ndarray, rate_hz: float) -> tuple[float, float, float, float, float]:
    size = 1 << int(np.floor(np.log2(max(waveform.size, 2))))
    if size < 8:
        return 0.0, 0.0, 0.0, 0.0, 0.0
    block = waveform[:size] - waveform[:size].mean()
    spectrum = np.abs(np.fft.rfft(block * np.hanning(size))) ** 2
    freqs = np.fft.rfftfreq(size, 1.0 / rate_hz)
    total = float(spectrum.sum()) + 1e-12
    low = float(spectrum[freqs < _BAND_LOW_HZ].sum() / total)
    mid = float(spectrum[(freqs >= _BAND_LOW_HZ) & (freqs < _BAND_MID_HZ)].sum() / total)
    high = float(spectrum[freqs >= _BAND_MID_HZ].sum() / total)
    centroid = float((spectrum * freqs).sum() / total)
    positive = spectrum[1:] + 1e-12
    flatness = float(np.exp(np.log(positive).mean()) / positive.mean())
    return low, mid, high, centroid, flatness


def _autocorrelation_pitch(segment: np.ndarray, rate_hz: float) -> tuple[float, float]:
    """Dominant pitch and how convincingly periodic the segment actually is.

    The peak is taken after the autocorrelation first crosses zero, not as the
    global maximum. A bass tone sampled at 33 kHz is so oversampled that it
    correlates strongly with itself at every short lag, and a plain argmax would
    always answer with the shortest lag allowed - reporting a kick as a 1.5 kHz
    sound. Waiting for the zero crossing skips that shoulder and finds the real
    period.

    Confidence matters more than the pitch value: a snare will always yield
    *some* peak, and only the confidence tells the classifier to ignore it.
    """

    if segment.size < 64:
        return 0.0, 0.0
    block = segment - segment.mean()
    energy = float(np.dot(block, block))
    if energy <= 1e-9:
        return 0.0, 0.0
    corr = np.correlate(block, block, mode="full")[block.size - 1 :]
    lo = max(1, int(rate_hz / _PITCH_MAX_HZ))
    hi = min(int(rate_hz / _PITCH_MIN_HZ), corr.size - 1)
    if hi <= lo:
        return 0.0, 0.0

    negative = np.nonzero(corr[:hi] < 0)[0]
    search_start = max(lo, int(negative[0])) if negative.size else lo
    if search_start >= hi:
        search_start = lo
    window = corr[search_start:hi]
    if window.size == 0:
        return 0.0, 0.0
    index = int(np.argmax(window))
    confidence = float(window[index] / (corr[0] + 1e-12))
    return rate_hz / (index + search_start), max(0.0, min(1.0, confidence))


def _trend(waveform: np.ndarray, rate_hz: float) -> np.ndarray:
    """The signed shape of the sample with the format's own dither removed.

    Delta modulation cannot hold still: tracking anything slower than two DAC
    steps per sample makes it alternate up and down, so sample-to-sample
    direction says nothing about the sound. Smoothing to the envelope window
    and decimating leaves the movement a listener would actually follow.
    """

    window = max(1, int(rate_hz * _ENVELOPE_WINDOW_SECONDS))
    if waveform.size <= window * 2:
        return waveform
    kernel = np.ones(window, dtype=np.float32) / window
    return np.convolve(waveform, kernel, mode="valid")[::window]


def extract_features(decoded: DecodedSample) -> SampleFeatures:
    """Measure the properties the classification rules are written against."""

    waveform = decoded.waveform
    rate = decoded.rate_hz
    duration = decoded.duration_seconds
    if waveform.size < 8:
        return SampleFeatures(
            duration=duration,
            effective_duration=duration,
            peak_position=0.0,
            decay_ratio=1.0,
            low=0.0,
            mid=0.0,
            high=0.0,
            centroid_hz=0.0,
            flatness=0.0,
            pitch_hz=0.0,
            pitch_confidence=0.0,
            pitch_slope=0.0,
            monotonic_ratio=0.0,
            amplitude=float(np.abs(waveform).max()) if waveform.size else 0.0,
        )

    env = _envelope(waveform, rate)
    peak_index = int(np.argmax(env))
    peak = float(env[peak_index]) + 1e-9
    tail = env[peak_index:]
    below = np.nonzero(tail < peak * _DECAY_THRESHOLD)[0]
    effective = (
        float(below[0] + peak_index) / rate if below.size else duration
    )
    decay_ratio = float(env[-1] / peak)

    low, mid, high, centroid, flatness = _band_energies(waveform, rate)

    body = waveform[peak_index:] if waveform.size - peak_index >= 64 else waveform
    pitch, confidence = _autocorrelation_pitch(body, rate)
    halves = np.array_split(body, 2)
    first, _ = _autocorrelation_pitch(halves[0], rate)
    second, _ = _autocorrelation_pitch(halves[1], rate)
    slope = (second - first) / first if first > 0 else 0.0

    deltas = np.diff(_trend(waveform, rate))
    moving = deltas[deltas != 0]
    rising = float(np.count_nonzero(moving > 0))
    monotonic = max(rising, moving.size - rising) / max(moving.size, 1)

    return SampleFeatures(
        duration=duration,
        effective_duration=effective,
        peak_position=peak_index / max(waveform.size - 1, 1),
        decay_ratio=decay_ratio,
        low=low,
        mid=mid,
        high=high,
        centroid_hz=centroid,
        flatness=flatness,
        pitch_hz=pitch,
        pitch_confidence=confidence,
        pitch_slope=slope,
        monotonic_ratio=monotonic,
        amplitude=float(np.abs(waveform).max()),
    )


# ---------------------------------------------------------------------------
# classification
# ---------------------------------------------------------------------------
@dataclass(frozen=True)
class _Rule:
    """One piece of named evidence for or against a role.

    A negative weight is a veto: it argues against the role without needing a
    separate mechanism, and it still shows up in the report so a wrong verdict
    can be traced to the rule that caused it.
    """

    role: Role
    name: str
    weight: float
    test: object

    def evaluate(self, f: SampleFeatures) -> bool:
        return bool(self.test(f))  # type: ignore[operator]


_RULES: tuple[_Rule, ...] = (
    # A DAC-levelling blip or driver start-up glitch attacks late and carries
    # nothing the DMG could reproduce anyway: its energy sits below the range
    # its speaker and its period grid can reach.
    _Rule(Role.ARTIFACT, "peak at the tail", 2.5, lambda f: f.peak_position > 0.6),
    _Rule(Role.ARTIFACT, "below the DMG's range", 2.0, lambda f: f.low > 0.9 and f.centroid_hz < 200.0),
    _Rule(Role.ARTIFACT, "not a hit", -4.0, lambda f: f.percussive_decay and f.peak_position < 0.4),
    # A kick is low-heavy, pitched under the bass range and gone quickly.
    _Rule(Role.KICK, "low-band dominant", 2.0, lambda f: f.low >= 0.30),
    _Rule(Role.KICK, "bass pitch", 1.5, lambda f: 30.0 <= f.pitch_hz <= 260.0 and f.pitch_confidence > 0.2),
    _Rule(Role.KICK, "short body", 1.0, lambda f: f.effective_duration <= 0.12),
    _Rule(Role.KICK, "downward sweep", 1.0, lambda f: f.pitch_slope < -0.01),
    _Rule(Role.KICK, "not bright", 1.0, lambda f: f.centroid_hz < 2500.0),
    _Rule(Role.KICK, "no audible pitch", -2.5, lambda f: f.centroid_hz < 200.0 or f.pitch_hz > 400.0),
    _Rule(Role.KICK, "attacks late", -3.0, lambda f: f.peak_position > 0.5),
    # A snare is a mid-heavy broadband crack. Its centroid lands lower than a
    # real snare's because delta modulation rolls off the top end hard.
    _Rule(Role.SNARE, "mid-band dominant", 2.5, lambda f: f.mid >= 0.45),
    _Rule(Role.SNARE, "crack centroid", 1.5, lambda f: 700.0 <= f.centroid_hz <= 8000.0),
    _Rule(Role.SNARE, "crack length", 1.5, lambda f: 0.015 <= f.effective_duration <= 0.12),
    _Rule(Role.SNARE, "little bass", 1.5, lambda f: f.low < 0.35),
    _Rule(Role.SNARE, "dies away", 1.0, lambda f: f.percussive_decay),
    _Rule(Role.SNARE, "attacks late", -3.0, lambda f: f.peak_position > 0.5),
    # A hat is almost all treble and over before anything else starts.
    _Rule(Role.HAT, "treble dominant", 2.0, lambda f: f.high >= 0.45),
    _Rule(Role.HAT, "very short", 2.0, lambda f: f.effective_duration <= 0.045),
    _Rule(Role.HAT, "very bright", 1.5, lambda f: f.centroid_hz >= 6000.0),
    _Rule(Role.HAT, "no bass", 1.0, lambda f: f.low < 0.20),
    _Rule(Role.HAT, "attacks late", -3.0, lambda f: f.peak_position > 0.5),
    # Without this veto a low sample whose pitch fell outside the detector's
    # range scores on "very short" alone and wins the role by default. A cymbal
    # made of bass is not a thing.
    _Rule(Role.HAT, "all bass", -4.0, lambda f: f.low >= 0.6),
    # A tom is a pitched drum: clearer pitch than a snare, higher than a kick.
    _Rule(Role.TOM, "drum pitch", 2.0, lambda f: 120.0 <= f.pitch_hz <= 500.0 and f.pitch_confidence > 0.35),
    _Rule(Role.TOM, "mid body", 1.5, lambda f: f.mid >= 0.30 and f.low < 0.45),
    _Rule(Role.TOM, "decays", 1.0, lambda f: 0.04 <= f.effective_duration <= 0.30),
    _Rule(Role.TOM, "downward sweep", 1.5, lambda f: f.pitch_slope < -0.05),
    _Rule(Role.TOM, "attacks late", -3.0, lambda f: f.peak_position > 0.5),
    # A tonal sample is a held note. It must actually be held: a struck sound
    # that collapses inside a tenth of a second is a drum however periodic it
    # looks, which is the trap delta modulation sets for the pitch detector.
    _Rule(Role.TONAL, "strongly periodic", 2.5, lambda f: f.pitch_confidence >= 0.55),
    _Rule(Role.TONAL, "sustained", 2.0, lambda f: f.effective_duration >= 0.15),
    _Rule(Role.TONAL, "stable pitch", 1.5, lambda f: abs(f.pitch_slope) < 0.08),
    _Rule(Role.TONAL, "musical pitch", 1.0, lambda f: 55.0 <= f.pitch_hz <= 1200.0),
    _Rule(Role.TONAL, "tonal spectrum", 1.0, lambda f: f.flatness < 0.15),
    _Rule(Role.TONAL, "struck, not held", -5.0, lambda f: f.percussive_decay),
    _Rule(Role.TONAL, "no audible pitch", -3.0, lambda f: f.centroid_hz < 200.0),
    # Anything long and unpitched is a sound effect, not an instrument.
    _Rule(Role.NOISE_SFX, "long", 1.5, lambda f: f.duration >= 0.25),
    _Rule(Role.NOISE_SFX, "unpitched", 1.5, lambda f: f.pitch_confidence < 0.30),
    _Rule(Role.NOISE_SFX, "flat spectrum", 1.0, lambda f: f.flatness >= 0.15),
    _Rule(Role.NOISE_SFX, "sustained energy", 1.0, lambda f: f.decay_ratio > 0.35),
    _Rule(Role.NOISE_SFX, "struck, not sustained", -3.0, lambda f: f.percussive_decay),
)


def classify(features: SampleFeatures) -> tuple[Role, float, tuple[str, ...]]:
    """Score every role and return the winner with the evidence behind it.

    Scoring rather than a decision tree because real samples sit between
    categories: a scored verdict degrades into a low confidence the report can
    flag, where a tree would silently pick a branch.

    Silence is settled before the rules run, because it is an absence of
    evidence rather than evidence for a role. A stretch of digital silence
    encodes as alternating bits, so it decodes to a one-step buzz at half the
    sample rate: weighed as evidence it looks exactly like a bright, short,
    bass-free sound, and the rules would confidently call it a hi-hat.

    A one-way ramp is settled the same way and for the same kind of reason.
    Sound is oscillation; a sample whose smoothed trend never reverses is the
    DAC being walked to a new resting level, not a note or a hit. Measured
    drums sit near 0.55 and a levelling ramp at 1.0, so the two never meet.
    """

    scores: dict[Role, float] = {}
    totals: dict[Role, float] = {}
    evidence: dict[Role, list[str]] = {}
    if features.amplitude <= _SILENCE_FLOOR:
        return Role.ARTIFACT, 1.0, ("no signal above the DAC's own step",)
    if features.monotonic_ratio >= _ONE_WAY_RATIO:
        return Role.ARTIFACT, 1.0, ("one-way ramp, never turns around",)
    for rule in _RULES:
        if rule.weight > 0:
            totals[rule.role] = totals.get(rule.role, 0.0) + rule.weight
        else:
            totals.setdefault(rule.role, 0.0)
        if rule.evaluate(features):
            scores[rule.role] = scores.get(rule.role, 0.0) + rule.weight
            prefix = "" if rule.weight > 0 else "NOT "
            evidence.setdefault(rule.role, []).append(f"{prefix}{rule.name}")

    ratios = {
        role: max(0.0, scores.get(role, 0.0)) / total
        for role, total in totals.items()
        if total > 0
    }
    # An artifact verdict wins outright: playing back a DAC blip as a drum is a
    # far worse outcome than dropping a quiet sample that might have been one.
    if ratios.get(Role.ARTIFACT, 0.0) >= 0.5:
        return Role.ARTIFACT, ratios[Role.ARTIFACT], tuple(evidence.get(Role.ARTIFACT, []))

    ranked = sorted(
        (r for r in ratios.items() if r[0] is not Role.ARTIFACT),
        key=lambda item: item[1],
        reverse=True,
    )
    best, best_ratio = ranked[0]
    runner_up = ranked[1][1] if len(ranked) > 1 else 0.0
    if best_ratio <= 0.0:
        return Role.NOISE_SFX, 0.0, ("no rule matched",)
    # Confidence is the margin over the nearest rival, not the raw score: a
    # sample that fits two roles equally well is genuinely ambiguous.
    confidence = best_ratio * (1.0 - 0.5 * (runner_up / best_ratio))
    return best, round(min(1.0, confidence), 3), tuple(evidence.get(best, []))


def analyze(
    rom: DpcmRom,
    ref: SampleRef,
    *,
    initial_level: int = DEFAULT_INITIAL_LEVEL,
    role_override: Role | None = None,
) -> SampleAnalysis:
    """Decode, measure and classify one sample."""

    decoded = decode(rom, ref, initial_level)
    features = extract_features(decoded)
    role, confidence, reasons = classify(features)
    if role_override is not None:
        return SampleAnalysis(
            ref=ref,
            decoded=decoded,
            features=features,
            role=role_override,
            confidence=1.0,
            reasons=(f"forced by hand (auto verdict was {role.value})",),
            overridden=True,
        )
    return SampleAnalysis(
        ref=ref,
        decoded=decoded,
        features=features,
        role=role,
        confidence=confidence,
        reasons=reasons,
    )


def analyze_all(
    rom: DpcmRom,
    refs: object,
    *,
    overrides: dict[int, Role] | None = None,
) -> dict[SampleRef, SampleAnalysis]:
    """Analyse a collection of sample references, one entry per distinct sample."""

    overrides = overrides or {}
    out: dict[SampleRef, SampleAnalysis] = {}
    plays: Counter[SampleRef] = Counter()
    for ref in refs:  # type: ignore[union-attr]
        plays[ref] += 1
        if ref in out:
            continue
        out[ref] = analyze(rom, ref, role_override=overrides.get(ref.address))
    return _apply_song_context(out, plays)


# The DMC rate is the only knob that transposes a sample, so a driver that plays
# one address back at several rates is playing a scale on it. Drums get at most
# a normal and a low variant, so this only fires well clear of them.
_MELODIC_RATE_COUNT = 3
_MELODIC_PITCH_CONFIDENCE = 0.5
_PERCUSSIVE_ROLES = (Role.KICK, Role.SNARE, Role.HAT, Role.TOM)

# Below the melodic threshold there is still something to hear: a drum retuned
# for a fill is a pitched drum. Two semitones is about where a listener stops
# hearing "the same drum" and starts hearing "a different one".
_TUNED_DRUM_SEMITONES = 2.0
_TUNED_DRUM_PITCH_CONFIDENCE = 0.5


def _apply_song_context(
    analyses: dict[SampleRef, SampleAnalysis],
    plays: Counter[SampleRef] | None = None,
) -> dict[SampleRef, SampleAnalysis]:
    """Re-read percussion verdicts in the light of how the song uses the sample.

    Classification looks at one sample in isolation, and in isolation a short
    sampled bass note really does look like a kick: low, dark, struck rather
    than held. The evidence that separates them is not in the waveform at all,
    it is in the song - a kick is played at one or two pitches, a bass line at
    however many the melody needs. Only the collection can see that, so the
    correction belongs here rather than in the rules.

    The decision is taken per address, because the instrument is the sample and
    the rate is only which note it is playing. Judging each rate separately
    would let one note of a bass line come out as a drum simply because its
    pitch fell outside what the detector can measure.
    """

    by_address: dict[int, list[SampleRef]] = {}
    for ref in analyses:
        by_address.setdefault(ref.address, []).append(ref)

    out: dict[SampleRef, SampleAnalysis] = dict(analyses)
    for address, refs in by_address.items():
        if any(analyses[ref].overridden for ref in refs):
            continue
        if not any(analyses[ref].role in _PERCUSSIVE_ROLES for ref in refs):
            continue
        # The median rather than the best: one lucky reading is not evidence
        # that a whole drum sample is really an instrument.
        confidences = sorted(analyses[ref].features.pitch_confidence for ref in refs)
        median_confidence = confidences[len(confidences) // 2]

        if len(refs) < _MELODIC_RATE_COUNT:
            _retune_drums(out, analyses, refs, median_confidence, plays or Counter())
            continue
        if median_confidence < _MELODIC_PITCH_CONFIDENCE:
            continue

        anchor = max(refs, key=lambda r: analyses[r].features.pitch_confidence)
        reason = (
            f"the song plays this sample at {len(refs)} different rates: "
            f"a melodic instrument, not a drum"
        )
        for ref in refs:
            analysis = analyses[ref]
            if analysis.role not in _PERCUSSIVE_ROLES:
                continue
            out[ref] = SampleAnalysis(
                ref=ref,
                decoded=analysis.decoded,
                features=_borrow_pitch(analysis, analyses[anchor]),
                role=Role.TONAL,
                confidence=analysis.confidence,
                reasons=(reason, f"isolated verdict was {analysis.role.value}"),
            )
    return out


def _retune_drums(
    out: dict[SampleRef, SampleAnalysis],
    analyses: dict[SampleRef, SampleAnalysis],
    refs: list[SampleRef],
    median_confidence: float,
    plays: Counter[SampleRef],
) -> None:
    """Hear a drum that was deliberately retuned as a different drum.

    Short of a full scale there is still a musical decision in the arrangement:
    a driver that plays the kick sample a fourth down for three hits in a bar is
    playing a tom fill, and the composer meant those hits to sound different.
    Isolated classification cannot see it - both variants are the same recording,
    so they measure identically and score identically - and it is audible, since
    a kick substitute sweeps its pitch away while a tom holds it.

    The most-used tuning keeps its verdict: that one is the beat, and rebuilding
    it as a pitched drum would cost the song its punch. Only the departures are
    re-read, which is exactly what the departure was for.
    """
    if len(refs) < 2 or median_confidence < _TUNED_DRUM_PITCH_CONFIDENCE:
        return
    tuned = [ref for ref in refs if analyses[ref].role in (Role.KICK, Role.SNARE, Role.TOM)]
    if len(tuned) < 2:
        return

    home = max(tuned, key=lambda r: (plays[r], analyses[r].features.pitch_confidence))
    home_pitch = analyses[home].features.pitch_hz
    if home_pitch <= 0.0:
        return

    for ref in tuned:
        if ref is home or analyses[ref].role is Role.TOM:
            continue
        pitch = analyses[ref].features.pitch_hz
        if pitch <= 0.0:
            continue
        semitones = abs(12.0 * math.log2(pitch / home_pitch))
        if semitones < _TUNED_DRUM_SEMITONES:
            continue
        analysis = analyses[ref]
        out[ref] = replace(
            analysis,
            role=Role.TOM,
            reasons=(
                f"the song retunes this drum {semitones:.0f} semitones for these "
                f"hits: a pitched drum, not another {analysis.role.value}",
                f"isolated verdict was {analysis.role.value}",
            ),
        )


def _borrow_pitch(analysis: SampleAnalysis, anchor: SampleAnalysis) -> SampleFeatures:
    """Recover a note's pitch from a sibling when the detector could not hear it.

    The DMC rate is a clock divider, so the same bytes played faster come out
    proportionally higher - exactly. A note below the detector's floor is
    therefore not unknowable: it is the sibling's pitch scaled by the ratio of
    the two rates, which is better than either a wrong reading or a default.
    """
    if analysis.features.pitch_confidence >= _MELODIC_PITCH_CONFIDENCE:
        return analysis.features
    if anchor.features.pitch_confidence < _MELODIC_PITCH_CONFIDENCE:
        return analysis.features
    if anchor.decoded.rate_hz <= 0:
        return analysis.features
    scaled = anchor.features.pitch_hz * (
        analysis.decoded.rate_hz / anchor.decoded.rate_hz
    )
    return replace(
        analysis.features,
        pitch_hz=scaled,
        pitch_confidence=anchor.features.pitch_confidence,
    )


# ---------------------------------------------------------------------------
# timbre transfer
# ---------------------------------------------------------------------------
def wave_table(analysis: SampleAnalysis, points: int = 32, levels: int = 16) -> list[int]:
    """Derive a Game Boy wave table from one period of the sample itself.

    Using the sample's own period keeps some of its timbre rather than replacing
    it with a generic waveform. When the sample has no usable pitch the average
    spectrum still shapes something better than a square wave, so this falls back
    to the loudest window rather than refusing.
    """

    waveform = analysis.decoded.waveform
    if waveform.size < points:
        return [levels // 2] * points

    rate = analysis.decoded.rate_hz
    pitch = analysis.features.pitch_hz
    if pitch > 0 and analysis.features.pitch_confidence > 0.15:
        period = int(round(rate / pitch))
    else:
        period = min(waveform.size, int(rate / 220.0))
    period = max(points, min(period, waveform.size))

    env = _envelope(waveform, rate)
    start = int(np.argmax(env))
    if start + period > waveform.size:
        start = max(0, waveform.size - period)
    cycle = waveform[start : start + period]

    positions = np.linspace(0, cycle.size - 1, points)
    resampled = np.interp(positions, np.arange(cycle.size), cycle)
    span = float(np.abs(resampled).max()) + 1e-9
    scaled = (resampled / span) * 0.5 + 0.5
    return [int(np.clip(round(v * (levels - 1)), 0, levels - 1)) for v in scaled]
