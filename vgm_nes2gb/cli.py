"""Command line interface for vgm-nes2gb."""

from __future__ import annotations

import argparse
import gzip
import json
import os
import sys

from . import converter, testing
from .allocator import DEFAULT_PRESET, PRESETS
from .dpcm import Role
from .nes_apu import DEFAULT_TICK_HZ
from .nes_to_gb import VOLUME_MODES, VOLUME_MODE_RETRIGGER
from .render import wav
from .vgm_reader import VgmFormatError

SOURCE_EXTENSIONS = (".vgm", ".vgz")
OUTPUT_SUFFIX = ".gb.vgm"


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="vgm-nes2gb",
        description="Convert NES (2A03) VGM tunes into Game Boy (DMG) VGM files.",
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    convert = subparsers.add_parser("convert", help="convert a file or a directory")
    convert.add_argument("input", help="NES .vgm/.vgz file, or a directory of them")
    convert.add_argument(
        "-o", "--out", help="output file, or output directory in batch mode"
    )
    convert.add_argument(
        "--tick-hz",
        type=float,
        default=DEFAULT_TICK_HZ,
        help="resolution of the conversion grid (default: %(default)s)",
    )
    convert.add_argument(
        "--volume-mode",
        choices=VOLUME_MODES,
        default=VOLUME_MODE_RETRIGGER,
        help=(
            "retrigger: track the NES volume exactly, re-triggering the Game Boy "
            "channel whenever it changes. envelope: map the NES envelope onto the "
            "Game Boy envelope unit once per note (smaller and click-free)."
        ),
    )
    convert.add_argument(
        "--no-transpose",
        action="store_true",
        help="clamp notes below the Game Boy range instead of raising them an octave",
    )
    convert.add_argument(
        "--dpcm",
        choices=list(PRESETS),
        default=DEFAULT_PRESET,
        help=(
            "how to handle the NES sample channel, which the Game Boy lacks. "
            + "; ".join(f"{name}: {preset.description}" for name, preset in PRESETS.items())
            + " (default: %(default)s)"
        ),
    )
    convert.add_argument(
        "--variants",
        action="store_true",
        help=(
            "write one file per DPCM strategy plus a comparison table, because no "
            "single strategy wins on every song and no metric can tell which sounds best"
        ),
    )
    convert.add_argument(
        "--dpcm-role",
        action="append",
        metavar="ADDR=ROLE",
        default=[],
        help=(
            "override the classifier for one sample, e.g. 0xEA80=kick. Roles: "
            + ", ".join(role.value for role in Role)
        ),
    )
    convert.add_argument(
        "--dpcm-dump",
        metavar="DIR",
        help="write one WAV per distinct DPCM sample, to audit the classification",
    )
    convert.add_argument(
        "--report", metavar="DIR", help="write a JSON report per converted file"
    )
    convert.add_argument(
        "--wav", action="store_true", help="also write NES and Game Boy reference WAVs"
    )
    convert.add_argument(
        "--spectrogram", action="store_true", help="also write a comparison PNG"
    )
    convert.add_argument("--quiet", action="store_true", help="only print errors")

    demo = subparsers.add_parser(
        "demo", help="write the synthetic four-channel NES reference tune"
    )
    demo.add_argument("-o", "--out", default="demo.vgm", help="output NES VGM path")

    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if args.command == "demo":
        return _run_demo(args)
    return _run_convert(args)


def _run_demo(args) -> int:
    os.makedirs(os.path.dirname(os.path.abspath(args.out)) or ".", exist_ok=True)
    with open(args.out, "wb") as handle:
        handle.write(testing.demo_vgm())
    print(f"wrote {args.out}")
    return 0


def _run_convert(args) -> int:
    sources = _collect_sources(args.input)
    if not sources:
        print(f"error: no VGM files found at {args.input}", file=sys.stderr)
        return 1

    batch = os.path.isdir(args.input)
    if batch and args.out and not os.path.isdir(args.out):
        os.makedirs(args.out, exist_ok=True)
    if args.report:
        os.makedirs(args.report, exist_ok=True)

    failures = 0
    for source in sources:
        destination = _output_path(source, args.out, batch)
        try:
            _convert_one(source, destination, args)
        except (VgmFormatError, OSError, ValueError) as error:
            failures += 1
            print(f"error: {source}: {error}", file=sys.stderr)
    return 1 if failures == len(sources) else (2 if failures else 0)


def _convert_one(source: str, destination: str, args) -> None:
    roles = _parse_roles(args.dpcm_role)
    common = dict(
        tick_hz=args.tick_hz,
        volume_mode=args.volume_mode,
        allow_transpose=not args.no_transpose,
        render_audio=True,
        dpcm_roles=roles,
    )

    if args.variants:
        results = converter.convert_variants(source, **common)
    else:
        results = [converter.convert_file(source, dpcm=args.dpcm, **common)]

    for result in results:
        path = _variant_path(destination, result.preset) if args.variants else destination
        _emit(result, path, args)

    if args.dpcm_dump:
        _dump_samples(source, args.dpcm_dump, roles, quiet=args.quiet)

    if args.variants and not args.quiet:
        print(converter.render_variant_table(results))
        print()


def _emit(result, destination: str, args) -> None:
    os.makedirs(os.path.dirname(os.path.abspath(destination)) or ".", exist_ok=True)
    payload = result.payload
    # ``.vgz`` is not a different format, it is a gzipped VGM. Writing the plain
    # bytes under that name produces a file some players refuse to open.
    if destination.lower().endswith(".vgz"):
        payload = gzip.compress(payload)
    with open(destination, "wb") as handle:
        handle.write(payload)
    result.report["output"]["path"] = os.path.abspath(destination)

    stem = _sidecar_stem(destination)
    if args.wav:
        wav.write_wav(_sibling(destination, f"{stem}.nes.wav"), result.nes_audio)
        wav.write_wav(_sibling(destination, f"{stem}.gb.wav"), result.gb_audio)
    if args.spectrogram:
        from . import analyze

        analyze.write_spectrogram_png(
            _sibling(destination, f"{stem}.compare.png"),
            result.nes_audio,
            result.gb_audio,
        )
    if args.report:
        report_path = os.path.join(args.report, f"{stem}.json")
        with open(report_path, "w", encoding="utf-8") as handle:
            json.dump(result.report, handle, indent=2, ensure_ascii=False)

    if not args.quiet:
        print(converter.render_text(result.report))
        print()


def _parse_roles(entries: list[str]) -> dict[int, Role]:
    """Parse ``--dpcm-role 0xEA80=kick`` overrides."""
    roles: dict[int, Role] = {}
    for entry in entries:
        if "=" not in entry:
            raise ValueError(f"--dpcm-role expects ADDR=ROLE, got {entry!r}")
        address, _, name = entry.partition("=")
        try:
            roles[int(address, 0)] = Role(name.strip().lower())
        except ValueError as error:
            raise ValueError(
                f"--dpcm-role {entry!r}: expected an address and one of "
                + ", ".join(role.value for role in Role)
            ) from error
    return roles


def _dump_samples(source: str, directory: str, roles: dict[int, Role], quiet: bool = False) -> None:
    """Write one WAV per distinct sample so the classification can be checked by ear."""
    from . import dpcm as dpcm_module
    from . import nes_apu, vgm_reader

    os.makedirs(directory, exist_ok=True)
    stream = vgm_reader.parse(source)
    timeline = nes_apu.simulate(stream)
    refs = [
        tick.dmc.sample
        for tick in timeline.ticks
        if tick.dmc.started and tick.dmc.sample is not None
    ]
    for ref, analysis in dpcm_module.analyze_all(
        stream.dpcm_rom, refs, overrides=roles
    ).items():
        # The rate is part of the identity: the same bytes played slower are a
        # different sound, and the runner really does play its kick both ways.
        name = f"{ref.address:04X}_{ref.length}_r{ref.rate_index}_{analysis.role.value}.wav"
        wav.write_wav(
            os.path.join(directory, name),
            analysis.decoded.waveform,
            sample_rate=int(analysis.decoded.rate_hz),
        )
        if not quiet:
            print(f"wrote {os.path.join(directory, name)}")


def _variant_path(destination: str, preset: str) -> str:
    stem = _sidecar_stem(destination)
    extension = _output_extension(destination)
    return _sibling(destination, f"{stem}.{preset}{extension}")


def _output_extension(destination: str) -> str:
    """The suffix asked for, so that ``-o theme.vgz`` really produces ``.vgz`` files."""
    name = os.path.basename(destination).lower()
    if name.endswith(".vgz"):
        return ".vgz"
    if name.endswith(OUTPUT_SUFFIX):
        return OUTPUT_SUFFIX
    return os.path.splitext(name)[1] or OUTPUT_SUFFIX


def _sidecar_stem(destination: str) -> str:
    """Base name for WAV/PNG/JSON companions, without the ``.gb.vgm`` suffix."""
    name = os.path.basename(destination)
    if name.lower().endswith(OUTPUT_SUFFIX):
        return name[: -len(OUTPUT_SUFFIX)]
    return os.path.splitext(name)[0]


def _sibling(destination: str, name: str) -> str:
    return os.path.join(os.path.dirname(os.path.abspath(destination)), name)


def _collect_sources(path: str) -> list[str]:
    if os.path.isdir(path):
        return sorted(
            os.path.join(path, entry)
            for entry in os.listdir(path)
            if entry.lower().endswith(SOURCE_EXTENSIONS)
        )
    return [path] if os.path.isfile(path) else []


def _output_path(source: str, out: str | None, batch: bool) -> str:
    default = _default_name(source)
    if not out:
        return os.path.join(os.path.dirname(source), default)
    if batch or os.path.isdir(out):
        return os.path.join(out, default)
    return out


def _default_name(source: str) -> str:
    stem = os.path.basename(source)
    for extension in SOURCE_EXTENSIONS:
        if stem.lower().endswith(extension):
            stem = stem[: -len(extension)]
            break
    return stem + OUTPUT_SUFFIX


if __name__ == "__main__":  # pragma: no cover
    raise SystemExit(main())
