"""Top level orchestration: NES VGM in, Game Boy VGM plus a report out."""

from __future__ import annotations

import os
from dataclasses import dataclass, field

import numpy as np

from . import allocator, analyze, gb_vgm_writer, nes_apu, nes_to_gb, vgm_reader
from .allocator import DEFAULT_PRESET, PRESETS
from .dpcm import Role
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
    preset: str = DEFAULT_PRESET


def convert_file(
    path: str,
    *,
    tick_hz: float = DEFAULT_TICK_HZ,
    volume_mode: str = VOLUME_MODE_RETRIGGER,
    allow_transpose: bool = True,
    render_audio: bool = True,
    dpcm: str = DEFAULT_PRESET,
    dpcm_roles: dict[int, Role] | None = None,
) -> ConversionResult:
    """Convert one NES VGM file and measure the result."""
    stream = vgm_reader.parse(path)
    timeline = nes_apu.simulate(stream, tick_hz=tick_hz)
    return _convert_timeline(
        path,
        stream,
        timeline,
        tick_hz=tick_hz,
        volume_mode=volume_mode,
        allow_transpose=allow_transpose,
        render_audio=render_audio,
        dpcm=dpcm,
        dpcm_roles=dpcm_roles,
    )


def convert_variants(
    path: str,
    presets: list[str] | None = None,
    *,
    tick_hz: float = DEFAULT_TICK_HZ,
    volume_mode: str = VOLUME_MODE_RETRIGGER,
    allow_transpose: bool = True,
    render_audio: bool = True,
    dpcm_roles: dict[int, Role] | None = None,
) -> list[ConversionResult]:
    """Convert the same song once per DPCM strategy.

    No single strategy wins on every song - a dense melody wants its channels
    left alone, a sparse one has room to spare - and no metric can tell which
    one sounds better. Producing them all and letting the ear decide is the
    honest answer.

    The source is parsed and simulated once; only the allocation and the
    Game Boy side are redone per preset.
    """
    stream = vgm_reader.parse(path)
    timeline = nes_apu.simulate(stream, tick_hz=tick_hz)
    return [
        _convert_timeline(
            path,
            stream,
            timeline,
            tick_hz=tick_hz,
            volume_mode=volume_mode,
            allow_transpose=allow_transpose,
            render_audio=render_audio,
            dpcm=name,
            dpcm_roles=dpcm_roles,
        )
        for name in (presets or list(PRESETS))
    ]


def _convert_timeline(
    path: str,
    stream: vgm_reader.NesCommandStream,
    timeline: nes_apu.ApuTimeline,
    *,
    tick_hz: float,
    volume_mode: str,
    allow_transpose: bool,
    render_audio: bool,
    dpcm: str,
    dpcm_roles: dict[int, Role] | None,
) -> ConversionResult:
    if dpcm not in PRESETS:
        raise ValueError(f"unknown DPCM preset {dpcm!r}")

    plan = allocator.plan(
        timeline, stream.dpcm_rom, preset=dpcm, overrides=dpcm_roles
    )
    gb_timeline = nes_to_gb.convert(
        timeline,
        volume_mode=volume_mode,
        allow_transpose=allow_transpose,
        dpcm_plan=plan if plan.preset.enabled else None,
    )
    payload = gb_vgm_writer.encode(gb_timeline, stream.gd3)

    nes_audio = np.zeros(0)
    gb_audio = np.zeros(0)
    metrics: dict = {}
    if render_audio:
        nes_audio = nes_synth.render(timeline, stream.dpcm_rom)
        gb_audio = gb_synth.render(payload)
        metrics = analyze.compare(
            nes_audio,
            gb_audio,
            nes_synth.channel_frequencies(timeline),
            gb_synth.channel_frequencies(payload, tick_hz),
        )

    report = _build_report(
        path, stream, timeline, gb_timeline, payload, metrics,
        tick_hz, volume_mode, allow_transpose, plan,
    )
    return ConversionResult(
        payload=payload,
        report=report,
        nes_audio=nes_audio,
        gb_audio=gb_audio,
        warnings=report["warnings"],
        preset=dpcm,
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
    plan,
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
            "dpcm": plan.preset.name,
        },
        "dpcm": _dpcm_section(stream, plan),
        "dropped": {
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


def _dpcm_section(stream, plan) -> dict:
    """What the sample channel contained and what became of it."""
    stats = plan.stats
    samples = []
    for ref, analysis in plan.analyses.items():
        features = analysis.features
        placed = sum(1 for voice in plan.voices if voice.analysis.ref == ref and voice.layer_of is None)
        hosts = sorted({voice.host.value for voice in plan.voices if voice.analysis.ref == ref})
        samples.append(
            {
                "address": f"0x{ref.address:04X}",
                "length": ref.length,
                "duration_ms": round(ref.duration_seconds * 1000, 1),
                "rate_hz": round(ref.rate_hz, 1),
                "hits": plan.hit_counts.get(ref, 0),
                "role": analysis.role.value,
                "confidence": analysis.confidence,
                "overridden": analysis.overridden,
                "why": list(analysis.reasons),
                "pitch_hz": round(features.pitch_hz, 1),
                "centroid_hz": round(features.centroid_hz, 1),
                "bands": {
                    "low": round(features.low, 3),
                    "mid": round(features.mid, 3),
                    "high": round(features.high, 3),
                },
                "placed": placed,
                "hosts": hosts,
                "recipe": next(
                    (v.recipe.description for v in plan.voices if v.analysis.ref == ref),
                    None,
                ),
            }
        )
    samples.sort(key=lambda entry: -entry["hits"])
    return {
        "preset": plan.preset.name,
        "strategy": plan.preset.description,
        "rom_bytes": len(stream.dpcm_rom),
        "rom_blocks": stream.dpcm_rom.block_count,
        "register_writes": stream.dropped.dpcm_register_writes,
        "direct_dac_writes": stream.direct_dac_writes,
        "hits": stats.hits,
        "placed": stats.placed,
        "layers": stats.layers,
        "realised_fraction": round(stats.realised_fraction, 3),
        "dropped": {
            "artifact": stats.dropped_artifact,
            "too_costly": stats.dropped_too_costly,
            "no_host": stats.dropped_no_host,
        },
        "per_role": stats.per_role,
        "per_host": stats.per_host,
        "samples": samples,
    }


def _warnings(report: dict) -> list[str]:
    warnings: list[str] = []
    dpcm = report["dpcm"]
    if dpcm["rom_bytes"] and dpcm["preset"] == "off":
        warnings.append(
            f"DPCM dropped: {dpcm['register_writes']} register writes and "
            f"{dpcm['rom_bytes']} bytes of sample data over {dpcm['hits']} hits. The "
            "Game Boy has no sample channel; re-synthesise them with --dpcm safe, "
            "balanced or full."
        )
    elif dpcm["hits"]:
        missed = dpcm["dropped"]["too_costly"] + dpcm["dropped"]["no_host"]
        if missed:
            warnings.append(
                f"{missed} of {dpcm['hits']} DPCM hits were dropped because no Game Boy "
                "channel was free enough to carry them."
            )
        for sample in dpcm["samples"]:
            if not sample["overridden"] and sample["confidence"] < 0.35 and sample["hits"] > 1:
                warnings.append(
                    f"sample {sample['address']} was classified as {sample['role']} with "
                    f"low confidence ({sample['confidence']:.2f}); check it with "
                    f"--dpcm-dump and override with --dpcm-role {sample['address']}=ROLE."
                )
    if dpcm["direct_dac_writes"] > 50 and not dpcm["rom_bytes"]:
        warnings.append(
            f"{dpcm['direct_dac_writes']} direct writes to $4011 with no sample data: "
            "this song streams PCM in software (usually speech), which cannot be "
            "re-synthesised and is not converted."
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
        onsets = metrics.get("onsets") or {}
        lines += [
            "",
            f"envelope correlation : {metrics['envelope_correlation']:.3f}",
            f"spectral correlation : {metrics['spectral_correlation']:.3f}",
            f"rms ratio (gb/nes)   : {metrics['rms_ratio']:.3f}",
        ]
        if onsets.get("reference"):
            lines.append(
                f"percussive attacks   : {onsets['matched']}/{onsets['reference']} "
                f"kept ({onsets['recall']:.2f})"
            )

    dpcm = report["dpcm"]
    if dpcm["rom_bytes"] or dpcm["hits"]:
        lines += ["", f"DPCM ({dpcm['preset']}): {dpcm['strategy']}"]
        if dpcm["hits"]:
            layered = f" (+{dpcm['layers']} layered)" if dpcm["layers"] else ""
            lines.append(
                f"  {dpcm['placed']}/{dpcm['hits']} hits re-synthesised{layered}"
                f"; hosts {dpcm['per_host'] or '-'}"
            )
        for sample in dpcm["samples"]:
            flag = " (forced)" if sample["overridden"] else ""
            lines.append(
                f"  {sample['address']} {sample['length']:>4}B "
                f"{sample['duration_ms']:>6.1f}ms x{sample['hits']:<3} -> "
                f"{sample['role']:<8}{flag} conf {sample['confidence']:.2f}"
            )
            lines.append(f"      why: {', '.join(sample['why']) or 'no rule matched'}")
            if sample["recipe"]:
                lines.append(f"      plays: {sample['recipe']} on {', '.join(sample['hosts'])}")

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


VARIANT_COLUMNS = (
    ("preset", 9),
    ("bytes", 8),
    ("hits", 9),
    ("attacks", 9),
    ("envelope", 9),
    ("spectral", 9),
)


def render_variant_table(results: list[ConversionResult]) -> str:
    """Compare the strategies side by side.

    Deliberately not ranked. The numbers say how closely each variant resembles
    a chip the Game Boy is not; they cannot say which one is the better piece of
    music. That judgement is the listener's.
    """
    header = " ".join(f"{name:>{width}}" for name, width in VARIANT_COLUMNS)
    lines = ["DPCM strategies (listen before choosing; the numbers only measure",
             "resemblance to a different chip, not whether it sounds good)", "", header]
    for result in results:
        metrics = result.report.get("metrics") or {}
        onsets = metrics.get("onsets") or {}
        dpcm = result.report["dpcm"]
        hits = f"{dpcm['placed']}/{dpcm['hits']}" if dpcm["hits"] else "-"
        attacks = f"{onsets['recall']:.3f}" if onsets.get("reference") else "-"
        lines.append(
            " ".join(
                [
                    f"{result.preset:>9}",
                    f"{len(result.payload):>8}",
                    f"{hits:>9}",
                    f"{attacks:>9}",
                    f"{metrics.get('envelope_correlation', 0.0):>9.3f}",
                    f"{metrics.get('spectral_correlation', 0.0):>9.3f}",
                ]
            )
        )
        lines.append(f"{'':>9} {dpcm['strategy']}")
    return "\n".join(lines)
