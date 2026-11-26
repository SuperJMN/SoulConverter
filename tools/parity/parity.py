"""Check the .NET port against this Python converter on the same inputs.

Run from the repository root, with numpy available and the solution built:

    dotnet build SoulConverter.sln -c Release
    python3 tools/parity/parity.py [--work DIR] [--fuzz N]

The corpus is synthetic: the three demo songs and seeded random 2A03 logs, and for
the Game Boy to NES direction every Game Boy log those conversions produce plus a
hand-written one. For each input and option set it converts with the Python and
with the .NET command line and compares

* the register streams: sample position, register and value of every write, the
  total length and the loop point. Writes to different registers at the same
  sample may come in a different order, because the Python orders some of them by
  iterating a set;
* the DPCM analysis in the report;
* the metrics of the two renders.

The .NET side runs with SOULCONVERTER_PARITY=python, which switches the Game Boy
reader and the 2A03 writer to what the Python did (no length counters, no sweep,
sweep register 0, no re-keyed triangle).
"""

from __future__ import annotations

import argparse
import itertools
import json
import os
import random
import struct
import subprocess
import sys
import tempfile
from concurrent.futures import ThreadPoolExecutor

sys.path.insert(0, os.getcwd())

from vgm_nes2gb import converter, gameboy, gb_to_nes, testing  # noqa: E402

CLI = os.path.join("src", "SoulConverter.Cli", "bin", "Release", "net10.0", "SoulConverter.Cli.dll")
TOLERANCE = 1e-6


def parse(payload: bytes):
    """Return (writes, total_samples, loop_sample) of a NES or Game Boy VGM."""
    offset = struct.unpack_from("<I", payload, 0x34)[0] + 0x34
    total = struct.unpack_from("<I", payload, 0x18)[0]
    loop_offset = struct.unpack_from("<I", payload, 0x1C)[0]
    loop_samples = struct.unpack_from("<I", payload, 0x20)[0]
    writes = []
    sample = 0
    while offset < len(payload):
        command = payload[offset]
        offset += 1
        if command == 0x66:
            break
        if command in (0xB3, 0xB4):
            writes.append((sample, payload[offset], payload[offset + 1]))
            offset += 2
        elif command == 0x61:
            sample += struct.unpack_from("<H", payload, offset)[0]
            offset += 2
        elif command == 0x62:
            sample += 735
        elif command == 0x67:
            offset += 6 + struct.unpack_from("<I", payload, offset + 2)[0]
        else:
            raise ValueError(f"unexpected command 0x{command:02X}")
    return writes, total, (total - loop_samples if loop_offset else None)


def per_register(writes):
    """Group writes by sample, then by register, keeping each register's order."""
    grouped: dict = {}
    for sample, register, value in writes:
        grouped.setdefault(sample, {}).setdefault(register, []).append(value)
    return grouped


def fuzz_song(seed: int) -> bytes:
    """A seeded random 2A03 log: every unit, both sequencer modes, off-grid writes."""
    rng = random.Random(seed)
    rom, layout = testing.dpcm_kit()
    data = bytearray()
    use_dpcm = seed % 3 != 0
    if use_dpcm:
        for address, payload in rom.items():
            body = struct.pack("<H", address) + payload
            data.extend(b"\x67\x66\xc2" + struct.pack("<I", len(body)) + body)

    total = 0
    loop_offset = None
    loop_sample = 0
    frames = rng.randint(120, 420)
    loop_frame = rng.randint(1, frames // 2) if seed % 2 else None
    data.extend((0xB4, 0x15, 0x0F, 0xB4, 0x17, rng.choice((0x00, 0x40, 0x80, 0xC0))))
    for frame in range(frames):
        if loop_frame == frame:
            loop_offset, loop_sample = len(data), total
        writes = []
        if rng.random() < 0.25:
            base = rng.choice((0x00, 0x04))
            period = rng.randint(0, 0x7FF)
            writes += [
                (base, rng.randint(0, 255)),
                (base + 1, rng.choice((0x00, 0x08, rng.randint(0, 255)))),
                (base + 2, period & 0xFF),
                (base + 3, (rng.randint(0, 31) << 3) | (period >> 8)),
            ]
        if rng.random() < 0.10:
            base = rng.choice((0x00, 0x04))
            writes.append((base + rng.choice((0, 2)), rng.randint(0, 255)))
        if rng.random() < 0.15:
            period = rng.randint(0, 0x7FF)
            writes += [(0x08, rng.choice((0xFF, 0x00, rng.randint(0, 255)))), (0x0A, period & 0xFF),
                       (0x0B, (rng.randint(0, 31) << 3) | (period >> 8))]
        if rng.random() < 0.20:
            writes += [(0x0C, rng.randint(0, 63)), (0x0E, rng.choice((0x00, 0x80)) | rng.randint(0, 15)),
                       (0x0F, rng.randint(0, 31) << 3)]
        if use_dpcm and rng.random() < 0.08:
            address, length = layout[rng.choice(("kick", "snare", "hat"))]
            writes += testing.dpcm_trigger(address, length, rate_index=rng.choice((15, 15, 15, 12, 10)),
                                           loop=rng.random() < 0.05)
        if rng.random() < 0.03:
            writes.append((0x15, rng.choice((0x0F, 0x1F, 0x0B, 0x07, 0x00))))
        if rng.random() < 0.02:
            writes.append((0x17, rng.choice((0x00, 0x80))))
        if rng.random() < 0.02:
            writes.append((0x11, rng.randint(0, 127)))

        split = rng.randint(1, 734) if writes and rng.random() < 0.2 else 0
        for index, (register, value) in enumerate(writes):
            if split and index == len(writes) // 2:
                data.extend((0x61, split & 0xFF, split >> 8))
            data.extend((0xB4, register & 0xFF, value & 0xFF))
        rest = 735 - (split if split and len(writes) // 2 < len(writes) else 0)
        data.extend((0x61, rest & 0xFF, rest >> 8))
        total += 735
    data.append(0x66)

    header = bytearray(0x100)
    header[0:4] = b"Vgm "
    struct.pack_into("<I", header, 0x04, 0x100 + len(data) - 4)
    struct.pack_into("<I", header, 0x08, 0x161)
    struct.pack_into("<I", header, 0x18, total)
    if loop_offset is not None:
        struct.pack_into("<I", header, 0x1C, 0x100 + loop_offset - 0x1C)
        struct.pack_into("<I", header, 0x20, total - loop_sample)
    struct.pack_into("<I", header, 0x34, 0x100 - 0x34)
    struct.pack_into("<I", header, 0x84, testing.NTSC_CPU_HZ)
    return bytes(header) + bytes(data)


def hand_written_gb() -> bytes:
    pulse_period, wave_period = 1000, 700
    return testing.build_gb_vgm(
        [
            [
                (gameboy.NR52, 0x80), (gameboy.NR50, 0x77), (gameboy.NR51, 0xFF),
                (gameboy.WAVE_RAM_BASE, 0xF0),
                (gameboy.NR11, 0x80), (gameboy.NR12, 0xF3),
                (gameboy.NR13, pulse_period & 0xFF), (gameboy.NR14, 0x80 | (pulse_period >> 8)),
                (gameboy.NR21, 0x40), (gameboy.NR22, 0xA0),
                (gameboy.NR23, pulse_period & 0xFF), (gameboy.NR24, 0x80 | (pulse_period >> 8)),
                (gameboy.NR30, 0x80), (gameboy.NR32, 0x60),
                (gameboy.NR33, wave_period & 0xFF), (gameboy.NR34, 0x80 | (wave_period >> 8)),
                (gameboy.NR42, 0xF1), (gameboy.NR43, 0x8D), (gameboy.NR44, 0x80),
            ],
            [
                (gameboy.NR13, (pulse_period + 2) & 0xFF), (gameboy.NR14, (pulse_period + 2) >> 8),
                (gameboy.NR33, (wave_period + 3) & 0xFF), (gameboy.NR34, (wave_period + 3) >> 8),
            ],
            *[[] for _ in range(40)],
            [(gameboy.NR44, 0x80)],
            *[[] for _ in range(20)],
            [(gameboy.NR52, 0x00)],
        ],
        loop_frame=1,
    )


def run_cli(arguments, parity=True):
    environment = dict(os.environ, DOTNET_NOLOGO="1")
    if parity:
        environment["SOULCONVERTER_PARITY"] = "python"
    result = subprocess.run(["dotnet", CLI, *arguments], capture_output=True, text=True, env=environment)
    if result.returncode != 0:
        raise RuntimeError(f"{' '.join(arguments)}: {result.stderr.strip()}")


def close(left, right, tolerance=TOLERANCE):
    return abs(left - right) <= tolerance * max(1.0, abs(left), abs(right))


def compare_streams(label, python_payload, dotnet_payload, problems):
    python_writes, python_total, python_loop = parse(python_payload)
    dotnet_writes, dotnet_total, dotnet_loop = parse(dotnet_payload)
    if (python_total, python_loop) != (dotnet_total, dotnet_loop):
        problems.append(f"{label}: length/loop {python_total}/{python_loop} != {dotnet_total}/{dotnet_loop}")
    if per_register(python_writes) != per_register(dotnet_writes):
        left, right = per_register(python_writes), per_register(dotnet_writes)
        first = min(s for s in set(left) | set(right) if left.get(s) != right.get(s))
        problems.append(f"{label}: register streams differ first at sample {first}: "
                        f"python {left.get(first)} dotnet {right.get(first)}")
    return python_writes == dotnet_writes


def compare_metrics(label, python_metrics, dotnet_metrics, problems):
    renames = {"nes_rms": "source_rms", "gb_rms": "target_rms",
               "nes_active_ticks": "source_active_ticks", "gb_active_ticks": "target_active_ticks"}

    def walk(path, python_value, dotnet_value):
        if isinstance(python_value, dict):
            for key, value in python_value.items():
                walk(f"{path}.{key}", value, dotnet_value.get(renames.get(key, key)))
        elif isinstance(python_value, (int, float)) and not isinstance(python_value, bool):
            if dotnet_value is None or not close(float(python_value), float(dotnet_value)):
                problems.append(f"{label}: metric {path}: python {python_value} dotnet {dotnet_value}")

    walk("metrics", python_metrics, dotnet_metrics or {})


def compare_dpcm(label, python_dpcm, dotnet_dpcm, problems):
    for key in ("preset", "rom_bytes", "rom_blocks", "register_writes", "direct_dac_writes", "hits", "placed",
                "layers", "dropped", "per_role", "per_host", "preempted_frames"):
        if python_dpcm[key] != dotnet_dpcm[key]:
            problems.append(f"{label}: dpcm.{key}: python {python_dpcm[key]} dotnet {dotnet_dpcm[key]}")
    if not close(python_dpcm["realised_fraction"], dotnet_dpcm["realised_fraction"]):
        problems.append(f"{label}: dpcm.realised_fraction")
    python_samples = {(s["address"], s["length"], s["rate_hz"]): s for s in python_dpcm["samples"]}
    dotnet_samples = {(s["address"], s["length"], s["rate_hz"]): s for s in dotnet_dpcm["samples"]}
    if set(python_samples) != set(dotnet_samples):
        problems.append(f"{label}: dpcm samples differ: {sorted(python_samples)} != {sorted(dotnet_samples)}")
        return
    for key, python_sample in python_samples.items():
        dotnet_sample = dotnet_samples[key]
        for field in ("hits", "role", "overridden", "why", "placed", "hosts", "recipe"):
            if python_sample[field] != dotnet_sample[field]:
                problems.append(f"{label}: sample {key} {field}: python {python_sample[field]!r} "
                                f"dotnet {dotnet_sample[field]!r}")
        for field in ("confidence", "pitch_hz", "centroid_hz", "duration_ms"):
            if not close(python_sample[field], dotnet_sample[field], 1e-3):
                problems.append(f"{label}: sample {key} {field}: python {python_sample[field]} "
                                f"dotnet {dotnet_sample[field]}")
        for band in ("low", "mid", "high"):
            if abs(python_sample["bands"][band] - dotnet_sample["bands"][band]) > 1.5e-3:
                problems.append(f"{label}: sample {key} band {band}")


def nes_to_gb_case(work, name, source_path, options):
    volume_mode, tick_hz, transpose, dpcm, roles = options
    label = f"{name} [{volume_mode} tick={tick_hz} transpose={transpose} dpcm={dpcm} roles={roles}]"
    tag = f"{name}.{volume_mode}.{tick_hz}.{int(transpose)}.{dpcm}.{len(roles)}"
    problems: list[str] = []
    python = converter.convert_file(
        source_path, tick_hz=tick_hz, volume_mode=volume_mode, allow_transpose=transpose, dpcm=dpcm,
        dpcm_roles={address: converter.Role(role) for address, role in roles.items()} or None)
    python_path = os.path.join(work, "gb", f"{tag}.py.gb.vgm")
    with open(python_path, "wb") as handle:
        handle.write(python.payload)

    out = os.path.join(work, "gb", f"{tag}.net.gb.vgm")
    report_dir = os.path.join(work, "reports", tag)
    arguments = ["convert", source_path, "-o", out, "--to", "gb", "--tick-hz", repr(tick_hz), "--volume-mode",
                 volume_mode, "--dpcm", dpcm, "--report", report_dir, "--quiet"]
    if not transpose:
        arguments.append("--no-transpose")
    for address, role in roles.items():
        arguments += ["--dpcm-role", f"0x{address:04X}={role}"]
    run_cli(arguments)
    with open(out, "rb") as handle:
        dotnet_payload = handle.read()
    with open(os.path.join(report_dir, os.path.basename(out)[: -len(".gb.vgm")] + ".json"), encoding="utf-8") as handle:
        report = json.load(handle)

    exact = compare_streams(label, python.payload, dotnet_payload, problems)
    compare_dpcm(label, python.report["dpcm"], report["dpcm"], problems)
    compare_metrics(label, python.report["metrics"], report["metrics"], problems)
    for channel, stats in python.report["channels"].items():
        if stats != report["channels"][channel]:
            problems.append(f"{label}: channel stats {channel}: python {stats} dotnet {report['channels'][channel]}")
    return label, problems, exact, python_path


def gb_to_nes_case(work, name, source_path):
    label = f"{name} -> NES"
    problems: list[str] = []
    python = gb_to_nes.convert_file(source_path)
    out = os.path.join(work, "nes", f"{name}.net.nes.vgm")
    report_dir = os.path.join(work, "reports-nes", name)
    run_cli(["convert", source_path, "-o", out, "--to", "nes", "--report", report_dir, "--quiet"])
    with open(out, "rb") as handle:
        dotnet_payload = handle.read()
    with open(os.path.join(report_dir, os.path.basename(out)[: -len(".nes.vgm")] + ".json"), encoding="utf-8") as handle:
        report = json.load(handle)
    exact = compare_streams(label, python.payload, dotnet_payload, problems)
    compare_metrics(label, python.report["metrics"], report["metrics"], problems)
    return label, problems, exact


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--work")
    parser.add_argument("--fuzz", type=int, default=24)
    parser.add_argument("--jobs", type=int, default=os.cpu_count() or 4)
    args = parser.parse_args()
    work = args.work or tempfile.mkdtemp(prefix="soulconverter-parity-")
    for folder in ("in", "gb", "nes"):
        os.makedirs(os.path.join(work, folder), exist_ok=True)

    problems: list[str] = []

    # The .NET demo generator writes the same songs as the Python one.
    for song, python_bytes in (("four-channel", testing.demo_vgm()), ("drum-kit", testing.dpcm_demo_vgm()),
                               ("sampled-bass", testing.sampled_bass_vgm())):
        path = os.path.join(work, "in", f"net-{song}.vgm")
        run_cli(["demo", "-o", path, "--song", song], parity=False)
        with open(path, "rb") as handle:
            dotnet_bytes = handle.read()
        if parse(python_bytes) != parse(dotnet_bytes) or python_bytes[0x100:python_bytes.index(b"Gd3 ")] != \
                dotnet_bytes[0x100:dotnet_bytes.index(b"Gd3 ")]:
            problems.append(f"demo {song}: the .NET generator differs from the Python one")

    songs = {
        "demo": testing.demo_vgm(), "demo-noloop": testing.demo_vgm(loop=False),
        "drums": testing.dpcm_demo_vgm(), "drums-noloop": testing.dpcm_demo_vgm(loop=False),
        "bass": testing.sampled_bass_vgm(), "bass-noloop": testing.sampled_bass_vgm(loop=False),
    }
    for seed in range(args.fuzz):
        songs[f"fuzz{seed:02d}"] = fuzz_song(seed)
    paths = {}
    for name, payload in songs.items():
        paths[name] = os.path.join(work, "in", f"{name}.vgm")
        with open(paths[name], "wb") as handle:
            handle.write(payload)

    full = list(itertools.product(("retrigger", "envelope"), (60.0, 240.0), (True, False),
                                  ("off", "safe", "balanced", "full"), ({},)))
    light = [("retrigger", 60.0, True, "off", {}), ("envelope", 60.0, True, "off", {}),
             ("retrigger", 60.0, True, "full", {}), ("envelope", 60.0, False, "balanced", {}),
             ("retrigger", 240.0, True, "safe", {}), ("retrigger", 50.0, True, "balanced", {})]
    cases = []
    for name in songs:
        for options in (light if name.startswith("fuzz") else full):
            cases.append((name, options))
    cases.append(("drums", ("retrigger", 60.0, True, "full", {0xC000: "snare", 0xC180: "tom"})))
    cases.append(("bass", ("retrigger", 60.0, True, "balanced", {0xC000: "kick"})))

    gb_sources = {}
    exact = total = 0
    with ThreadPoolExecutor(args.jobs) as pool:
        for (name, options), (label, found, was_exact, python_path) in zip(
                cases, pool.map(lambda case: nes_to_gb_case(work, case[0], paths[case[0]], case[1]), cases)):
            problems += found
            exact += was_exact
            total += 1
            gb_sources[os.path.basename(python_path)[: -len(".py.gb.vgm")]] = python_path
    print(f"2A03 -> DMG: {total} conversions, {exact} with the writes in the very same order")

    hand = os.path.join(work, "in", "hand-written.gb.vgm")
    with open(hand, "wb") as handle:
        handle.write(hand_written_gb())
    gb_sources["hand-written"] = hand

    exact = total = 0
    with ThreadPoolExecutor(args.jobs) as pool:
        for label, found, was_exact in pool.map(lambda item: gb_to_nes_case(work, item[0], item[1]), gb_sources.items()):
            problems += found
            exact += was_exact
            total += 1
    print(f"DMG -> 2A03: {total} conversions, {exact} with the writes in the very same order")

    for problem in problems[:60]:
        print("DIFF", problem)
    print(f"{len(problems)} differences" if problems else "no differences")
    print(f"work directory: {work}")
    return 1 if problems else 0


if __name__ == "__main__":
    raise SystemExit(main())
