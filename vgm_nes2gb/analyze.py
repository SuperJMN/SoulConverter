"""Compare the NES source with the Game Boy conversion.

Every number here is diagnostic. A poor score never fails a conversion; it is
reported so a human can decide whether the tune survived the trip.
"""

from __future__ import annotations

import math
import struct
import zlib

import numpy as np

from .render.oscillators import SAMPLE_RATE

CHANNELS = ("pulse1", "pulse2", "triangle", "noise")
ENVELOPE_WINDOW_HZ = 60.0


def envelope(samples: np.ndarray, window_hz: float = ENVELOPE_WINDOW_HZ) -> np.ndarray:
    """RMS envelope of ``samples`` on a fixed window grid."""
    window = max(1, int(round(SAMPLE_RATE / window_hz)))
    usable = (samples.size // window) * window
    if usable == 0:
        return np.zeros(0)
    blocks = samples[:usable].reshape(-1, window)
    return np.sqrt(np.mean(blocks**2, axis=1))


def correlation(left: np.ndarray, right: np.ndarray) -> float:
    size = min(left.size, right.size)
    if size < 2:
        return 0.0
    a = left[:size] - left[:size].mean()
    b = right[:size] - right[:size].mean()
    denominator = math.sqrt(float(np.sum(a**2)) * float(np.sum(b**2)))
    if denominator == 0.0:
        return 0.0
    return float(np.sum(a * b) / denominator)


def _pitch_stats(nes: np.ndarray, gb: np.ndarray) -> dict:
    size = min(nes.size, gb.size)
    if size == 0:
        return _empty_pitch()
    nes, gb = nes[:size], gb[:size]

    nes_on, gb_on = nes > 0, gb > 0
    both = nes_on & gb_on
    agreement = float(np.mean(nes_on == gb_on)) if size else 1.0

    if not both.any():
        stats = _empty_pitch()
        stats["activity_agreement"] = agreement
        stats["nes_active_ticks"] = int(nes_on.sum())
        stats["gb_active_ticks"] = int(gb_on.sum())
        return stats

    cents = 1200.0 * np.log2(gb[both] / nes[both])
    # Octave transposition is an intended, reported outcome, so pitch-class error
    # is measured separately from raw error.
    residual = cents - 1200.0 * np.round(cents / 1200.0)
    return {
        "compared_ticks": int(both.sum()),
        "nes_active_ticks": int(nes_on.sum()),
        "gb_active_ticks": int(gb_on.sum()),
        "activity_agreement": agreement,
        "mean_abs_cents": float(np.mean(np.abs(cents))),
        "max_abs_cents": float(np.max(np.abs(cents))),
        "p95_abs_cents": float(np.percentile(np.abs(cents), 95)),
        "mean_abs_cents_mod_octave": float(np.mean(np.abs(residual))),
        "max_abs_cents_mod_octave": float(np.max(np.abs(residual))),
        "octaves_up_ticks": int(np.sum(np.round(cents / 1200.0) > 0)),
    }


def _empty_pitch() -> dict:
    return {
        "compared_ticks": 0,
        "nes_active_ticks": 0,
        "gb_active_ticks": 0,
        "activity_agreement": 1.0,
        "mean_abs_cents": 0.0,
        "max_abs_cents": 0.0,
        "p95_abs_cents": 0.0,
        "mean_abs_cents_mod_octave": 0.0,
        "max_abs_cents_mod_octave": 0.0,
        "octaves_up_ticks": 0,
    }


ONSET_TOLERANCE_SECONDS = 0.035
ONSET_HOP = 512


def onsets(samples: np.ndarray, sample_rate: float = 44100.0) -> np.ndarray:
    """Times where the spectrum jumps, i.e. where a listener hears a hit.

    Spectral flux rather than an amplitude jump, because a drum landing on top
    of a sustained note barely changes the level but changes the spectrum a lot.
    """
    if samples.size < ONSET_HOP * 4:
        return np.zeros(0)
    frames = samples.size // ONSET_HOP
    block = samples[: frames * ONSET_HOP].reshape(frames, ONSET_HOP)
    spectrum = np.abs(np.fft.rfft(block * np.hanning(ONSET_HOP), axis=1))
    flux = np.diff(spectrum, axis=0)
    strength = np.clip(flux, 0, None).sum(axis=1)
    if not strength.size or strength.max() <= 0:
        return np.zeros(0)
    threshold = strength.mean() + strength.std()
    peaks = [
        index
        for index in range(1, strength.size - 1)
        if strength[index] > threshold
        and strength[index] >= strength[index - 1]
        and strength[index] > strength[index + 1]
    ]
    return np.array(peaks, dtype=float) * ONSET_HOP / sample_rate


def onset_match(
    nes_audio: np.ndarray, gb_audio: np.ndarray, sample_rate: float = 44100.0
) -> dict:
    """How many of the source's audible hits survive, and how punctually.

    Diagnostic only. Two different chips cannot produce the same attack, so a
    low score is a prompt to listen, never a reason to fail a conversion.
    """
    reference = onsets(nes_audio, sample_rate)
    produced = onsets(gb_audio, sample_rate)
    if reference.size == 0:
        return {"reference": 0, "produced": int(produced.size), "matched": 0, "recall": 0.0}

    remaining = list(produced)
    matched = 0
    for time in reference:
        for index, candidate in enumerate(remaining):
            if abs(candidate - time) <= ONSET_TOLERANCE_SECONDS:
                matched += 1
                remaining.pop(index)
                break
    return {
        "reference": int(reference.size),
        "produced": int(produced.size),
        "matched": matched,
        "recall": round(matched / reference.size, 3),
    }


def compare(
    nes_audio: np.ndarray,
    gb_audio: np.ndarray,
    nes_frequencies: dict[str, np.ndarray],
    gb_frequencies: dict[str, np.ndarray],
) -> dict:
    """Build the diagnostic metric set for one conversion."""
    nes_env = envelope(nes_audio)
    gb_env = envelope(gb_audio)

    nes_rms = float(np.sqrt(np.mean(nes_audio**2))) if nes_audio.size else 0.0
    gb_rms = float(np.sqrt(np.mean(gb_audio**2))) if gb_audio.size else 0.0

    return {
        "envelope_correlation": correlation(nes_env, gb_env),
        "spectral_correlation": _spectral_correlation(nes_audio, gb_audio),
        "nes_rms": nes_rms,
        "gb_rms": gb_rms,
        "rms_ratio": (gb_rms / nes_rms) if nes_rms > 0 else 0.0,
        "onsets": onset_match(nes_audio, gb_audio),
        "channels": {
            name: _pitch_stats(
                nes_frequencies.get(name, np.zeros(0)),
                gb_frequencies.get(name, np.zeros(0)),
            )
            for name in CHANNELS
        },
    }


def _spectrogram(samples: np.ndarray, bins: int = 512):
    if samples.size < bins * 2:
        return np.zeros((bins // 2, 1))
    frames = samples.size // bins
    blocks = samples[: frames * bins].reshape(frames, bins)
    window = np.hanning(bins)
    spectrum = np.abs(np.fft.rfft(blocks * window, axis=1))[:, : bins // 2]
    return spectrum.T


def _spectral_correlation(nes_audio: np.ndarray, gb_audio: np.ndarray) -> float:
    """Correlation of the two long-term average spectra, in dB."""
    size = min(nes_audio.size, gb_audio.size)
    if size < 1024:
        return 0.0
    nes_spectrum = _spectrogram(nes_audio[:size]).mean(axis=1)
    gb_spectrum = _spectrogram(gb_audio[:size]).mean(axis=1)
    nes_db = 20.0 * np.log10(nes_spectrum + 1e-9)
    gb_db = 20.0 * np.log10(gb_spectrum + 1e-9)
    return correlation(nes_db, gb_db)


def write_spectrogram_png(
    path: str, nes_audio: np.ndarray, gb_audio: np.ndarray, width: int = 900
) -> None:
    """Write a stacked NES-over-GB spectrogram PNG (no plotting dependency)."""
    top = _spectrogram_image(nes_audio, width)
    bottom = _spectrogram_image(gb_audio, width)
    separator = np.zeros((4, width, 3), dtype=np.uint8)
    separator[:, :] = (60, 60, 60)
    image = np.concatenate([top, separator, bottom], axis=0)
    _write_png(path, image)


def _spectrogram_image(samples: np.ndarray, width: int, height: int = 160) -> np.ndarray:
    spectrum = _spectrogram(samples)
    if spectrum.shape[1] < 2:
        return np.zeros((height, width, 3), dtype=np.uint8)
    magnitude = 20.0 * np.log10(spectrum + 1e-9)
    magnitude = np.clip((magnitude + 80.0) / 80.0, 0.0, 1.0)

    rows = np.linspace(0, magnitude.shape[0] - 1, height).astype(int)
    columns = np.linspace(0, magnitude.shape[1] - 1, width).astype(int)
    resampled = magnitude[np.ix_(rows, columns)][::-1]

    image = np.zeros((height, width, 3), dtype=np.uint8)
    image[:, :, 0] = (resampled**1.4 * 255).astype(np.uint8)
    image[:, :, 1] = (resampled * 255).astype(np.uint8)
    image[:, :, 2] = (np.sqrt(resampled) * 255).astype(np.uint8)
    return image


def _write_png(path: str, image: np.ndarray) -> None:
    height, width, _ = image.shape
    raw = b"".join(
        b"\x00" + image[row].tobytes() for row in range(height)
    )

    def chunk(tag: bytes, payload: bytes) -> bytes:
        return (
            struct.pack(">I", len(payload))
            + tag
            + payload
            + struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF)
        )

    header = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    with open(path, "wb") as handle:
        handle.write(b"\x89PNG\r\n\x1a\n")
        handle.write(chunk(b"IHDR", header))
        handle.write(chunk(b"IDAT", zlib.compress(raw, 6)))
        handle.write(chunk(b"IEND", b""))
