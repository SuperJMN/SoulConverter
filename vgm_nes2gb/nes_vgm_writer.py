"""Encode translated 2A03 writes as a NES VGM file."""

from __future__ import annotations

import struct
from dataclasses import dataclass, field

from . import vgm_reader

HEADER_SIZE = 0x100
VGM_VERSION = 0x00000161
NES_CLOCK_HZ = 1789773
MAX_WAIT = 0xFFFF
FRAME_WAIT_60HZ = 735


@dataclass(frozen=True)
class RegisterWrite:
    sample: int
    register: int
    value: int


@dataclass
class NesTimeline:
    writes: list[RegisterWrite] = field(default_factory=list)
    total_samples: int = 0
    loop_sample: int | None = None


class NesVgmWriteError(Exception):
    """The translated stream cannot be encoded as a NES VGM."""


def encode(timeline: NesTimeline, gd3_source: bytes | None = None) -> bytes:
    data = bytearray()
    loop_data_offset: int | None = None
    sample = 0
    for write in timeline.writes:
        if write.sample < sample:
            raise NesVgmWriteError(
                f"write goes back in time ({write.sample} < {sample})"
            )
        if (
            timeline.loop_sample is not None
            and loop_data_offset is None
            and sample <= timeline.loop_sample <= write.sample
        ):
            _emit_wait(data, timeline.loop_sample - sample)
            sample = timeline.loop_sample
            loop_data_offset = len(data)
        _emit_wait(data, write.sample - sample)
        sample = write.sample
        _emit_register(data, write.register, write.value)

    total_samples = max(timeline.total_samples, sample)
    if (
        timeline.loop_sample is not None
        and loop_data_offset is None
        and sample <= timeline.loop_sample <= total_samples
    ):
        _emit_wait(data, timeline.loop_sample - sample)
        sample = timeline.loop_sample
        loop_data_offset = len(data)
    _emit_wait(data, total_samples - sample)
    data.append(0x66)

    gd3 = _build_gd3(gd3_source)
    gd3_position = HEADER_SIZE + len(data)
    eof_position = gd3_position + len(gd3)
    header = bytearray(HEADER_SIZE)
    header[0:4] = b"Vgm "
    _put(header, 0x04, eof_position - 0x04)
    _put(header, 0x08, VGM_VERSION)
    _put(header, 0x14, gd3_position - 0x14)
    _put(header, 0x18, total_samples)
    if loop_data_offset is not None and timeline.loop_sample is not None:
        _put(header, 0x1C, HEADER_SIZE + loop_data_offset - 0x1C)
        _put(header, 0x20, total_samples - timeline.loop_sample)
    _put(header, 0x24, 60)
    _put(header, 0x34, HEADER_SIZE - 0x34)
    _put(header, 0x84, NES_CLOCK_HZ)
    return bytes(header) + bytes(data) + gd3


def write(path: str, timeline: NesTimeline, gd3_source: bytes | None = None) -> int:
    payload = encode(timeline, gd3_source)
    with open(path, "wb") as handle:
        handle.write(payload)
    return len(payload)


def _emit_register(data: bytearray, register: int, value: int) -> None:
    if not 0 <= register <= 0x1F:
        raise NesVgmWriteError(
            f"register 0x{register:02X} is outside the base NES 2A03 range"
        )
    data.extend((0xB4, register & 0xFF, value & 0xFF))


def _emit_wait(data: bytearray, samples: int) -> None:
    if samples < 0:
        raise NesVgmWriteError("negative wait")
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
    fields = [""] * 11
    if not body:
        return fields
    text = body.decode("utf-16-le", errors="replace")
    parts = text.split("\x00")
    for index in range(min(len(fields), len(parts))):
        fields[index] = parts[index]
    return fields


def _build_gd3(source: bytes | None) -> bytes:
    fields = _split_gd3(source)
    fields[4] = "NES"
    fields[5] = ""
    note = "Converted from Game Boy DMG by vgm-nes2gb (wave channel approximated as NES triangle)"
    fields[10] = f"{fields[10]}\n{note}".strip()
    body = b"".join(field.encode("utf-16-le") + b"\x00\x00" for field in fields)
    return b"Gd3 " + struct.pack("<II", 0x100, len(body)) + body


def scan_commands(payload: bytes) -> set[int]:
    offset = struct.unpack_from("<I", payload, 0x34)[0] + 0x34
    commands: set[int] = set()
    while offset < len(payload):
        command = payload[offset]
        commands.add(command)
        if command == 0x66:
            break
        if command in (0xB4, 0x61):
            offset += 3
        elif command in (0x62, 0x63) or 0x70 <= command <= 0x7F:
            offset += 1
        else:
            raise NesVgmWriteError(f"unexpected command 0x{command:02X} at 0x{offset:X}")
    return commands


def duration_seconds(payload: bytes) -> float:
    return struct.unpack_from("<I", payload, 0x18)[0] / vgm_reader.VGM_SAMPLE_RATE


__all__ = [
    "NES_CLOCK_HZ",
    "NesTimeline",
    "RegisterWrite",
    "duration_seconds",
    "encode",
    "scan_commands",
    "write",
]
