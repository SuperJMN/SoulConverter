"""Parse a Game Boy DMG VGM/VGZ register-write stream.

The forward converter has a deliberately NES-specific reader.  Keeping the
DMG reader separate makes the two format contracts explicit and prevents a
Game Boy file from being mistaken for a file with no NES clock.
"""

from __future__ import annotations

import struct
from dataclasses import dataclass, field

from . import vgm_reader

GB_REGISTER_RANGES = ((0x00, 0x16), (0x20, 0x2F))
GB_CLOCK_OFFSET = 0x80


@dataclass(frozen=True)
class RegisterWrite:
    """One DMG register write at an absolute VGM sample position."""

    sample: int
    register: int
    value: int


@dataclass
class GbCommandStream:
    """A time-ordered stream of DMG writes plus VGM metadata."""

    cpu_clock_hz: int
    total_samples: int
    loop_sample: int | None
    writes: list[RegisterWrite] = field(default_factory=list)
    gd3: bytes | None = None
    version: int = 0

    @property
    def duration_seconds(self) -> float:
        return self.total_samples / vgm_reader.VGM_SAMPLE_RATE


def parse(path: str) -> GbCommandStream:
    """Parse a Game Boy ``.vgm`` or compressed ``.vgz`` file."""
    return parse_bytes(vgm_reader.read_bytes(path), name=path)


def parse_bytes(data: bytes, name: str = "<memory>") -> GbCommandStream:
    if len(data) < vgm_reader.MIN_HEADER_SIZE or data[:4] != b"Vgm ":
        raise vgm_reader.VgmFormatError(
            f"{name}: does not start with the 'Vgm ' signature"
        )

    header_limit = _data_offset(data, name)
    clock = _header_u32(data, GB_CLOCK_OFFSET, header_limit) & 0x3FFFFFFF
    if clock == 0:
        raise vgm_reader.UnsupportedChipError(
            f"{name}: no Game Boy DMG clock declared at header offset 0x80; "
            "this converter only accepts Game Boy VGM files"
        )

    _reject_other_chips(data, name, header_limit)
    total_samples = _u32(data, 0x18)
    loop_offset = _u32(data, 0x1C)
    loop_samples = _u32(data, 0x20)
    loop_sample = (
        total_samples - loop_samples
        if loop_offset and 0 < loop_samples <= total_samples
        else None
    )

    stream = GbCommandStream(
        cpu_clock_hz=clock,
        total_samples=total_samples,
        loop_sample=loop_sample,
        gd3=_read_gd3(data),
        version=_u32(data, 0x08),
    )
    _decode_commands(data, name, stream)
    if stream.writes:
        stream.total_samples = max(stream.total_samples, stream.writes[-1].sample)
    return stream


def _reject_other_chips(data: bytes, name: str, header_limit: int) -> None:
    if _header_u32(data, 0x84, header_limit) & 0x3FFFFFFF:
        raise vgm_reader.UnsupportedChipError(
            f"{name}: declares a NES 2A03 clock at header offset 0x84; "
            "only the Game Boy DMG is supported"
        )
    for offset, chip in vgm_reader.CHIP_CLOCK_FIELDS.items():
        if offset == GB_CLOCK_OFFSET:
            continue
        if _header_u32(data, offset, header_limit) & 0x3FFFFFFF:
            raise vgm_reader.UnsupportedChipError(
                f"{name}: declares a {chip} clock at header offset "
                f"0x{offset:02X}; only the Game Boy DMG is supported"
            )


def _decode_commands(data: bytes, name: str, stream: GbCommandStream) -> None:
    pos = _data_offset(data, name)
    samples = 0
    while pos < len(data):
        start = pos
        command = data[pos]
        pos += 1
        if command == 0x66:
            return
        if command == 0x61:
            _require(data, pos, 2, start, name)
            samples += struct.unpack_from("<H", data, pos)[0]
            pos += 2
        elif command == 0x62:
            samples += 735
        elif command == 0x63:
            samples += 882
        elif 0x70 <= command <= 0x7F:
            samples += (command & 0x0F) + 1
        elif command == 0xB3:
            _require(data, pos, 2, start, name)
            register, value = data[pos], data[pos + 1]
            pos += 2
            if not _valid_register(register):
                raise vgm_reader.UnsupportedChipError(
                    f"{name}: register 0x{register:02X} at 0x{start:X} is outside "
                    "the Game Boy DMG register ranges"
                )
            stream.writes.append(RegisterWrite(samples, register, value))
        else:
            raise vgm_reader.UnsupportedChipError(
                f"{name}: unsupported VGM command 0x{command:02X} at 0x{start:X} "
                "for Game Boy DMG import"
            )
    raise vgm_reader.VgmFormatError(
        f"{name}: ended before the 0x66 end-of-data command"
    )


def _valid_register(register: int) -> bool:
    return any(low <= register <= high for low, high in GB_REGISTER_RANGES)


def _data_offset(data: bytes, name: str) -> int:
    relative = _u32(data, 0x34)
    offset = vgm_reader.MIN_HEADER_SIZE if relative == 0 else 0x34 + relative
    if offset < vgm_reader.MIN_HEADER_SIZE or offset >= len(data):
        raise vgm_reader.VgmFormatError(f"{name}: invalid VGM data offset 0x{offset:X}")
    return offset


def _read_gd3(data: bytes) -> bytes | None:
    relative = _u32(data, 0x14)
    if relative == 0:
        return None
    offset = 0x14 + relative
    if offset + 12 > len(data) or data[offset : offset + 4] != b"Gd3 ":
        return None
    length = struct.unpack_from("<I", data, offset + 8)[0]
    end = offset + 12 + length
    return data[offset + 12 : end] if end <= len(data) else None


def _header_u32(data: bytes, offset: int, header_limit: int) -> int:
    return _u32(data, offset) if offset + 4 <= header_limit else 0


def _u32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<I", data, offset)[0] if offset + 4 <= len(data) else 0


def _require(data: bytes, pos: int, count: int, offset: int, name: str) -> None:
    if pos + count > len(data):
        raise vgm_reader.VgmFormatError(
            f"{name}: command at 0x{offset:X} extends past the end of the file"
        )
