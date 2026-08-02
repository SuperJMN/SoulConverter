"""Parse a VGM/VGZ file into a NES 2A03 register-write stream.

Only the base 2A03 APU is supported. Any expansion chip (VRC6, FDS, MMC5,
Namco 163, Sunsoft 5B, ...) or any non-NES sound chip declared in the header is
rejected with an explicit error naming the chip.
"""

from __future__ import annotations

import gzip
import struct
from dataclasses import dataclass, field

VGM_SAMPLE_RATE = 44100

MIN_HEADER_SIZE = 0x40

# Header offset -> chip name, for every chip clock field defined up to VGM 1.71.
# The NES APU field (0x84) is deliberately absent: it is the one chip we accept.
CHIP_CLOCK_FIELDS = {
    0x0C: "SN76489",
    0x10: "YM2413",
    0x2C: "YM2612",
    0x30: "YM2151",
    0x38: "Sega PCM",
    0x40: "RF5C68",
    0x44: "YM2203",
    0x48: "YM2608",
    0x4C: "YM2610/YM2610B",
    0x50: "YM3812",
    0x54: "YM3526",
    0x58: "Y8950",
    0x5C: "YMF262",
    0x60: "YMF278B",
    0x64: "YMF271",
    0x68: "YMZ280B",
    0x6C: "RF5C164",
    0x70: "PWM",
    0x74: "AY8910",
    0x80: "Game Boy DMG",
    0x88: "MultiPCM",
    0x8C: "uPD7759",
    0x90: "OKIM6258",
    0x98: "OKIM6295",
    0x9C: "K051649/SCC1",
    0xA0: "K054539",
    0xA4: "HuC6280",
    0xA8: "C140",
    0xAC: "K053260",
    0xB0: "Pokey",
    0xB4: "QSound",
    0xB8: "SCSP",
    0xC0: "WonderSwan",
    0xC4: "VSU",
    0xC8: "SAA1099",
    0xCC: "ES5503",
    0xD0: "ES5505/ES5506",
    0xD8: "X1-010",
    0xDC: "C352",
    0xE0: "GA20",
}

# 2A03 register range that this converter understands. VGM routes NES expansion
# audio (FDS and friends) through command 0xB4 with registers >= 0x20.
APU_REGISTER_MAX = 0x1F

# The DMC reads its samples from $C000-$FFFF, addressed in 64-byte units.
DMC_BASE_ADDRESS = 0xC000

NES_EXPANSION_REGISTER_CHIPS = (
    (0x20, 0x3F, "FDS"),
    (0x40, 0x5F, "Namco 163"),
    (0x60, 0x7F, "MMC5"),
    (0x80, 0x9F, "Sunsoft 5B"),
    (0xA0, 0xBF, "VRC6"),
    (0xC0, 0xDF, "VRC7"),
)


class VgmFormatError(Exception):
    """The input file is not a VGM this converter can read."""


class UnsupportedChipError(VgmFormatError):
    """The input declares or drives a chip other than the base NES 2A03."""


@dataclass(frozen=True)
class RegisterWrite:
    """A single 2A03 register write at an absolute VGM sample position."""

    sample: int
    register: int
    value: int

    @property
    def address(self) -> int:
        return 0x4000 + self.register


@dataclass
class DroppedStats:
    """Everything the reader deliberately threw away, for the report."""

    dpcm_block_bytes: int = 0
    dpcm_block_count: int = 0
    dpcm_register_writes: int = 0
    pcm_stream_commands: int = 0


@dataclass
class DpcmRom:
    """The sample memory the DMC channel reads from.

    Sparse on purpose: a rip only carries the pages the song actually plays, and
    the DMC address space ($C000-$FFFF) is mostly program ROM in the real thing.
    """

    data: dict[int, int] = field(default_factory=dict)
    block_count: int = 0

    def __bool__(self) -> bool:
        return bool(self.data)

    def __len__(self) -> int:
        return len(self.data)

    def load(self, start: int, payload: bytes) -> None:
        for index, byte in enumerate(payload):
            self.data[(start + index) & 0xFFFF] = byte

    def read(self, address: int) -> int:
        """Read one byte, wrapping like the DMC does. Missing pages read as 0."""
        return self.data.get(address & 0xFFFF, 0)

    def covers(self, address: int, length: int) -> int:
        """How many of ``length`` bytes from ``address`` are actually present."""
        return sum(
            1 for index in range(length) if ((address + index) & 0xFFFF) in self.data
        )


@dataclass
class NesCommandStream:
    """A time-ordered stream of 2A03 register writes plus timing metadata."""

    cpu_clock_hz: int
    total_samples: int
    loop_sample: int | None
    writes: list[RegisterWrite] = field(default_factory=list)
    dropped: DroppedStats = field(default_factory=DroppedStats)
    dpcm_rom: DpcmRom = field(default_factory=DpcmRom)
    gd3: bytes | None = None
    version: int = 0

    @property
    def duration_seconds(self) -> float:
        return self.total_samples / VGM_SAMPLE_RATE

    @property
    def direct_dac_writes(self) -> int:
        """Writes to $4011, which is how software PCM (speech) is played.

        A song that drives $4011 in bulk is streaming a waveform through the DAC
        rather than triggering samples, and no amount of re-synthesis recovers it.
        """
        return sum(1 for write in self.writes if write.register == 0x11)


def read_bytes(path: str) -> bytes:
    """Read a VGM file, transparently decompressing gzip/VGZ payloads."""
    with open(path, "rb") as handle:
        raw = handle.read()
    if raw[:2] == b"\x1f\x8b":
        return gzip.decompress(raw)
    return raw


def parse(path: str) -> NesCommandStream:
    """Parse ``path`` into a :class:`NesCommandStream`."""
    return parse_bytes(read_bytes(path), name=path)


def parse_bytes(data: bytes, name: str = "<memory>") -> NesCommandStream:
    if len(data) < MIN_HEADER_SIZE or data[:4] != b"Vgm ":
        raise VgmFormatError(f"{name}: does not start with the 'Vgm ' signature")

    version = _u32(data, 0x08)
    total_samples = _u32(data, 0x18)
    loop_offset = _u32(data, 0x1C)
    loop_samples = _u32(data, 0x20)

    # Per the VGM spec, header fields that overlap the start of the command data
    # must be treated as zero, so older/short headers do not read garbage.
    header_limit = _data_offset(data, name)

    cpu_clock_hz = _header_u32(data, 0x84, header_limit) & 0x3FFFFFFF
    if cpu_clock_hz == 0:
        raise UnsupportedChipError(
            f"{name}: no NES APU (2A03) clock declared at header offset 0x84; "
            "this converter only accepts NES VGM files"
        )

    _reject_other_chips(data, name, header_limit)

    loop_sample = None
    if loop_offset != 0 and 0 < loop_samples <= total_samples:
        loop_sample = total_samples - loop_samples

    stream = NesCommandStream(
        cpu_clock_hz=cpu_clock_hz,
        total_samples=total_samples,
        loop_sample=loop_sample,
        gd3=_read_gd3(data),
        version=version,
    )

    _decode_commands(data, name, stream)

    if stream.writes:
        last = stream.writes[-1].sample
        if stream.total_samples < last:
            stream.total_samples = last
    return stream


def _reject_other_chips(data: bytes, name: str, header_limit: int) -> None:
    """Fail if the header declares any chip clock other than the NES APU."""
    for offset, chip in CHIP_CLOCK_FIELDS.items():
        if _header_u32(data, offset, header_limit) & 0x3FFFFFFF:
            raise UnsupportedChipError(
                f"{name}: declares a {chip} clock at header offset 0x{offset:02X}; "
                "only the base NES 2A03 is supported"
            )


def _decode_commands(data: bytes, name: str, stream: NesCommandStream) -> None:
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
        elif command == 0xB4:
            _require(data, pos, 2, start, name)
            register, value = data[pos], data[pos + 1]
            pos += 2
            _record_write(stream, samples, register, value, start, name)
        elif command == 0x67:
            pos = _read_data_block(data, pos, start, name, stream)
        elif command == 0xE0:
            _require(data, pos, 4, start, name)
            pos += 4
            stream.dropped.pcm_stream_commands += 1
        elif 0x80 <= command <= 0x8F:
            # YM2612 PCM write + wait; cannot appear without a YM2612 clock.
            raise UnsupportedChipError(
                f"{name}: command 0x{command:02X} at 0x{start:X} targets a YM2612, "
                "which this converter does not support"
            )
        else:
            raise UnsupportedChipError(
                f"{name}: unsupported VGM command 0x{command:02X} at 0x{start:X} "
                "for NES 2A03 import"
            )

    raise VgmFormatError(f"{name}: ended before the 0x66 end-of-data command")


def _record_write(
    stream: NesCommandStream,
    samples: int,
    register: int,
    value: int,
    offset: int,
    name: str,
) -> None:
    if register > APU_REGISTER_MAX:
        for low, high, chip in NES_EXPANSION_REGISTER_CHIPS:
            if low <= register <= high:
                raise UnsupportedChipError(
                    f"{name}: register 0x{register:02X} at 0x{offset:X} targets "
                    f"{chip} expansion audio, which this converter does not support"
                )
        raise UnsupportedChipError(
            f"{name}: unknown NES register 0x{register:02X} at 0x{offset:X}"
        )

    if 0x10 <= register <= 0x13:
        # Kept, not dropped: the DMC is re-synthesised on the Game Boy. The
        # counter still records them so an `--dpcm off` run can report the cost.
        stream.dropped.dpcm_register_writes += 1

    stream.writes.append(RegisterWrite(samples, register, value))


def _read_data_block(
    data: bytes, pos: int, start: int, name: str, stream: NesCommandStream
) -> int:
    _require(data, pos, 6, start, name)
    if data[pos] != 0x66:
        raise VgmFormatError(
            f"{name}: malformed data block at 0x{start:X}: missing 0x66 marker"
        )
    block_type = data[pos + 1]
    size = struct.unpack_from("<I", data, pos + 2)[0]
    pos += 6
    if pos + size > len(data):
        raise VgmFormatError(
            f"{name}: data block at 0x{start:X} extends past the end of the file"
        )

    body = data[pos : pos + size]
    # 0x07 = NES DPCM ROM image, 0xC2 = NES APU RAM write. Both carry sample data.
    if block_type == 0xC2 and len(body) >= 2:
        stream.dpcm_rom.load(struct.unpack_from("<H", body, 0)[0], body[2:])
        stream.dpcm_rom.block_count += 1
        stream.dropped.dpcm_block_bytes += size
        stream.dropped.dpcm_block_count += 1
    elif block_type == 0x07 and len(body) >= 8:
        address = struct.unpack_from("<I", body, 4)[0]
        # Some rips give an absolute DMC address, others an offset into the image.
        stream.dpcm_rom.load(
            address if address >= DMC_BASE_ADDRESS else DMC_BASE_ADDRESS + address,
            body[8:],
        )
        stream.dpcm_rom.block_count += 1
        stream.dropped.dpcm_block_bytes += size
        stream.dropped.dpcm_block_count += 1
    return pos + size


def _data_offset(data: bytes, name: str) -> int:
    relative = _u32(data, 0x34)
    offset = MIN_HEADER_SIZE if relative == 0 else 0x34 + relative
    if offset < MIN_HEADER_SIZE or offset >= len(data):
        raise VgmFormatError(f"{name}: invalid VGM data offset 0x{offset:X}")
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
    if end > len(data):
        return None
    return data[offset + 12 : end]


def _header_u32(data: bytes, offset: int, header_limit: int) -> int:
    if offset + 4 > header_limit:
        return 0
    return _u32(data, offset)


def _u32(data: bytes, offset: int) -> int:
    if offset + 4 > len(data):
        return 0
    return struct.unpack_from("<I", data, offset)[0]


def _require(data: bytes, pos: int, count: int, offset: int, name: str) -> None:
    if pos + count > len(data):
        raise VgmFormatError(
            f"{name}: command at 0x{offset:X} extends past the end of the file"
        )
