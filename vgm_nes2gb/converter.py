"""Top level orchestration: NES VGM in, Game Boy VGM plus a report out."""

from __future__ import annotations

import os
from dataclasses import dataclass, field

import numpy as np

from . import analyze, gb_vgm_writer, nes_apu, nes_to_gb, vgm_reader
from .nes_apu import DEFAULT_TICK_HZ
from .nes_to_gb import CHANNEL_NAMES, VOLUME_MODE_RETRIGGER
from .render import gb_synth, nes_synth
from .vgm_reader import VGM_SAMPLE_RATE

NOISE_WARN_CENTS = 200.0
ENVELOPE_WARN_CORRELATION = 0.8
ACTIVITY_WARN_AGREEMENT = 0.9


@dataclass
class ConversionResult:
    payload: bytes
    report: dict
    nes_audio: np.ndarray
    gb_audio: np.ndarray
    warnings: list[str] = field(default_factory=list)


def convert_file(
    path: str,
    *,
    tick_hz: float = DEFAULT_TICK_HZ,
    volume_mode: str = VOLUME_MODE_RETRIGGER,
    allow_transpose: bool = True,
    render_audio: bool = True,
) -> ConversionResult:
    """Convert one NES VGM file and measure the result."""
    stream = vgm_reader.parse(path)
    timeline = nes_apu.simulate(stream, tick_hz=tick_hz)
    gb_timeline = nes_to_gb.convert(
        timeline, volume_mode=volume_mode, allow_transpose=allow_transpose
    )
    payload = gb_vgm_writer.encode(gb_timeline, stream.gd3)

    nes_audio = np.zeros(0)
    gb_audio = np.zeros(0)
    metrics: dict = {}
    if render_audio:
        nes_audio = nes_synth.render(timeline)
        gb_audio = gb_synth.render(payload)
        metrics = analyze.compare(
            nes_audio,
            gb_audio,
            nes_synth.channel_frequencies(timeline),
            gb_synth.channel_frequencies(payload, tick_hz),
        )

    report = _build_report(
        path, stream, timeline, gb_timeline, payload, metrics,
        tick_hz, volume_mode, allow_transpose,
    )
    return ConversionResult(
        payload=payload,
        report=report,
        nes_audio=nes_audio,
        gb_audio=gb_audio,
        warnings=report["warnings"],
    )


def _build_report(
    path: str,
    stream: vgm_reader.NesCommandStream,
    timeline: nes_apu.ApuTimeline,
    gb_timeline: nes_to_gb.GbTimeline,
    payload: bytes,
    metrics: dict,
    tick_hz: float,
    volume_mode: str,
    allow_transpose: bool,
) -> dict:
    stats = gb_timeline.stats
    report = {
        "source": {
            "path": os.path.abspath(path),
            "cpu_clock_hz": stream.cpu_clock_hz,
            "vgm_version": f"{stream.version >> 8:x}.{stream.version & 0xFF:02x}",
            "total_samples": stream.total_samples,
            "duration_seconds": round(stream.total_samples / VGM_SAMPLE_RATE, 3),
            "loop_sample": stream.loop_sample,
            "register_writes": len(stream.writes),
        },
        "output": {
            "bytes": len(payload),
            "duration_seconds": round(gb_vgm_writer.duration_seconds(payload), 3),
            "register_writes": stats.register_writes,
            "commands": sorted(f"0x{c:02X}" for c in gb_vgm_writer.scan_commands(payload)),
            "ticks": len(gb_timeline.ticks),
            "loop_tick": gb_timeline.loop_tick,
        },
        "settings": {
            "tick_hz": tick_hz,
            "volume_mode": volume_mode,
            "transpose_out_of_range": allow_transpose,
        },
        "dropped": {
            "dpcm_register_writes": stream.dropped.dpcm_register_writes,
            "dpcm_block_bytes": stream.dropped.dpcm_block_bytes,
            "dpcm_block_count": stream.dropped.dpcm_block_count,
            "pcm_stream_commands": stream.dropped.pcm_stream_commands,
        },
        "channels": {
            name: {
                "active_ticks": stats.active_ticks[name],
                "note_ons": stats.note_ons[name],
                "retriggers": stats.retriggers[name],
                "transposed_ticks": stats.transposed_ticks[name],
                "max_octaves_up": stats.max_octaves_up[name],
            }
            for name in CHANNEL_NAMES
        },
        "noise_mapping_cents": {
            str(index): round(cents, 1)
            for index, cents in sorted(stats.noise_error_cents.items())
        },
        "metrics": metrics,
    }
    report["warnings"] = _warnings(report)
    return report


def _warnings(report: dict) -> list[str]:
    warnings: list[str] = []
    dropped = report["dropped"]
    if dropped["dpcm_register_writes"] or dropped["dpcm_block_bytes"]:
        warnings.append(
            f"DPCM dropped: {dropped['dpcm_register_writes']} register writes and "
            f"{dropped['dpcm_block_bytes']} bytes of sample data. The Game Boy has no "
            "DPCM channel, so sample-based percussion is missing."
        )

    for name, channel in report["channels"].items():
        if channel["transposed_ticks"]:
            warnings.append(
                f"{name}: {channel['transposed_ticks']} ticks transposed up to "
                f"{channel['max_octaves_up']} octave(s); the Game Boy cannot reach "
                "those low notes."
            )

    for index, cents in report["noise_mapping_cents"].items():
        if abs(cents) > NOISE_WARN_CENTS:
            warnings.append(
                f"noise period index {index} maps {cents:+.0f} cents off; the Game Boy "
                "noise divisor grid has no closer match."
            )

    metrics = report.get("metrics") or {}
    if metrics:
        if metrics["envelope_correlation"] < ENVELOPE_WARN_CORRELATION:
            warnings.append(
                f"envelope correlation is {metrics['envelope_correlation']:.2f}; the "
                "loudness shape of the conversion differs noticeably from the source."
            )
        for name, channel in metrics["channels"].items():
            if channel["activity_agreement"] < ACTIVITY_WARN_AGREEMENT:
                warnings.append(
                    f"{name}: channel on/off agreement is "
                    f"{channel['activity_agreement']:.2f}; notes start or stop at "
                    "different times."
                )
    return warnings


def render_text(report: dict) -> str:
    """Human-readable rendering of a conversion report."""
    source, output = report["source"], report["output"]
    lines = [
        f"source : {source['path']}",
        f"         VGM {source['vgm_version']}, 2A03 @ {source['cpu_clock_hz']} Hz, "
        f"{source['duration_seconds']:.2f}s, {source['register_writes']} writes"
        + (f", loop @ sample {source['loop_sample']}" if source["loop_sample"] else ""),
        f"output : {output['bytes']} bytes, {output['duration_seconds']:.2f}s, "
        f"{output['register_writes']} writes, commands {' '.join(output['commands'])}",
        f"settings: tick {report['settings']['tick_hz']} Hz, volume mode "
        f"{report['settings']['volume_mode']}, transpose "
        f"{'on' if report['settings']['transpose_out_of_range'] else 'off'}",
        "",
        f"{'channel':10} {'active':>7} {'notes':>7} {'retrig':>7} {'transp':>7} "
        f"{'cents':>8} {'on/off':>7}",
    ]

    metrics = report.get("metrics") or {}
    for name, channel in report["channels"].items():
        measured = (metrics.get("channels") or {}).get(name, {})
        cents = measured.get("mean_abs_cents_mod_octave")
        agreement = measured.get("activity_agreement")
        lines.append(
            f"{name:10} {channel['active_ticks']:7} {channel['note_ons']:7} "
            f"{channel['retriggers']:7} {channel['transposed_ticks']:7} "
            f"{(f'{cents:8.1f}' if cents is not None else ' ' * 8)} "
            f"{(f'{agreement:7.2f}' if agreement is not None else ' ' * 7)}"
        )

    if metrics:
        lines += [
            "",
            f"envelope correlation : {metrics['envelope_correlation']:.3f}",
            f"spectral correlation : {metrics['spectral_correlation']:.3f}",
            f"rms ratio (gb/nes)   : {metrics['rms_ratio']:.3f}",
        ]

    if report["noise_mapping_cents"]:
        mapping = ", ".join(
            f"{index}:{cents:+.0f}c"
            for index, cents in report["noise_mapping_cents"].items()
        )
        lines += ["", f"noise period mapping : {mapping}"]

    if report["warnings"]:
        lines += ["", "warnings:"]
        lines += [f"  - {warning}" for warning in report["warnings"]]

    return "\n".join(lines)
