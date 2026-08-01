"""Command line interface for vgm-nes2gb."""

from __future__ import annotations

import argparse
import json
import os
import sys

from . import converter, testing
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
    result = converter.convert_file(
        source,
        tick_hz=args.tick_hz,
        volume_mode=args.volume_mode,
        allow_transpose=not args.no_transpose,
        render_audio=True,
    )

    os.makedirs(os.path.dirname(os.path.abspath(destination)) or ".", exist_ok=True)
    with open(destination, "wb") as handle:
        handle.write(result.payload)
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
