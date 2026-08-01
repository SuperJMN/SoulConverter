"""Encode Game Boy register writes as a VGM 1.61 file.

The output is deliberately restricted to the command subset accepted by
RetroSharp's ``VgmImporter``: ``0xB3`` (DMG write), ``0x61``/``0x62`` (wait) and
``0x66`` (end of data). The GD3 tag sits after ``0x66``, where no VGM consumer
reads it as a command.
"""

from __future__ import annotations

import struct

from . import gameboy as gb
from .nes_to_gb import GbTimeline
from .vgm_reader import VGM_SAMPLE_RATE

HEADER_SIZE = 0x100
VGM_VERSION = 0x00000161
MAX_WAIT = 0xFFFF
FRAME_WAIT_60HZ = 735

GD3_FIELD_COUNT = 11
GD3_SYSTEM_EN = 4
GD3_SYSTEM_JP = 5
GD3_NOTES = 10

ALLOWED_COMMANDS = frozenset({0x61, 0x62, 0x66, 0xB3})


class GbVgmWriteError(Exception):
    """The requested stream cannot be encoded as a Game Boy VGM."""


def encode(timeline: GbTimeline, gd3_source: bytes | None = None) -> bytes:
    """Return the bytes of a Game Boy VGM for ``timeline``."""
    data = bytearray()
    loop_data_offset: int | None = None
    sample = 0

    for tick in timeline.ticks:
        if tick.sample < sample:
            raise GbVgmWriteError(
                f"tick {tick.index} goes back in time ({tick.sample} < {sample})"
            )
        _emit_wait(data, tick.sample - sample)
        sample = tick.sample
        if timeline.loop_tick is not None and tick.index == timeline.loop_tick:
            loop_data_offset = len(data)
        for register, value in tick.writes:
            _emit_register(data, register, value)

    total_samples = max(timeline.total_samples, sample)
    _emit_wait(data, total_samples - sample)
    data.append(0x66)

    gd3 = _build_gd3(gd3_source)

    gd3_position = HEADER_SIZE + len(data)
    eof_position = gd3_position + len(gd3)

    header = bytearray(HEADER_SIZE)
    header[0:4] = b"Vgm "
    _put(header, 0x04, eof_position - 0x04)
    _put(header, 0x08, VGM_VERSION)
    _put(header, 0x14, gd3_position - 0x14 if gd3 else 0)
    _put(header, 0x18, total_samples)
    if loop_data_offset is not None:
        loop_sample = timeline.ticks[timeline.loop_tick].sample
        _put(header, 0x1C, HEADER_SIZE + loop_data_offset - 0x1C)
        _put(header, 0x20, total_samples - loop_sample)
    _put(header, 0x24, 60)
    _put(header, 0x34, HEADER_SIZE - 0x34)
    _put(header, 0x80, gb.GB_CLOCK_HZ)

    return bytes(header) + bytes(data) + gd3


def write(path: str, timeline: GbTimeline, gd3_source: bytes | None = None) -> int:
    payload = encode(timeline, gd3_source)
    with open(path, "wb") as handle:
        handle.write(payload)
    return len(payload)


def _emit_register(data: bytearray, register: int, value: int) -> None:
    if not (register <= 0x16 or 0x20 <= register <= 0x2F):
        raise GbVgmWriteError(
            f"register 0x{register:02X} is outside the importable DMG range "
            "(0x00-0x16, 0x20-0x2F)"
        )
    data.extend((0xB3, register & 0xFF, value & 0xFF))


def _emit_wait(data: bytearray, samples: int) -> None:
    if samples < 0:
        raise GbVgmWriteError("negative wait")
    while samples > 0:
        if samples == FRAME_WAIT_60HZ:
            data.append(0x62)
            return
        chunk = min(samples, MAX_WAIT)
        data.extend((0x61, chunk & 0xFF, (chunk >> 8) & 0xFF))
        samples -= chunk


def _put(header: bytearray, offset: int, value: int) -> None:
    header[offset : offset + 4] = struct.pack("<I", value & 0xFFFFFFFF)


def _split_gd3(body: bytes | None) -> list[str]:
    fields = [""] * GD3_FIELD_COUNT
    if not body:
        return fields
    try:
        text = body.decode("utf-16-le", errors="replace")
    except UnicodeError:  # pragma: no cover - decode never raises with replace
        return fields
    parts = text.split("\x00")
    for index in range(min(GD3_FIELD_COUNT, len(parts))):
        fields[index] = parts[index]
    return fields


def _build_gd3(source: bytes | None) -> bytes:
    fields = _split_gd3(source)
    fields[GD3_SYSTEM_EN] = "Game Boy"
    fields[GD3_SYSTEM_JP] = ""
    note = "Converted from NES 2A03 by vgm-nes2gb (DPCM channel dropped)"
    fields[GD3_NOTES] = f"{fields[GD3_NOTES]}\n{note}".strip()

    body = b"".join(field.encode("utf-16-le") + b"\x00\x00" for field in fields)
    return b"Gd3 " + struct.pack("<II", 0x100, len(body)) + body


def scan_commands(payload: bytes) -> set[int]:
    """Return the set of VGM command bytes used by ``payload``.

    Used by the tests to prove the output stays inside the importable subset.
    """
    offset = struct.unpack_from("<I", payload, 0x34)[0] + 0x34
    commands: set[int] = set()
    while offset < len(payload):
        command = payload[offset]
        commands.add(command)
        if command == 0x66:
            break
        if command == 0xB3:
            offset += 3
        elif command == 0x61:
            offset += 3
        elif command in (0x62, 0x63):
            offset += 1
        elif 0x70 <= command <= 0x7F:
            offset += 1
        else:
            raise GbVgmWriteError(
                f"unexpected command 0x{command:02X} at 0x{offset:X}"
            )
    return commands


def duration_seconds(payload: bytes) -> float:
    return struct.unpack_from("<I", payload, 0x18)[0] / VGM_SAMPLE_RATE
