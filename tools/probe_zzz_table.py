"""Peek at a ZZZ `*Tb` table TextAsset and pull out its strings.

ZZZ design-data tables are **FlatBuffers** binaries despite the .txt extension:
the head is a uoffset to the root table followed by a vtable, and strings are
stored the FlatBuffers way (u32 length + UTF-8 bytes + NUL). That means the text
inside can be recovered without the .fbs schema: scan for plausible
length-prefixed strings and keep the ones that decode as clean UTF-8.

Usage:
    python tools/probe_zzz_table.py <file> [more files...]
"""

import mmap
import struct
import sys


def hexdump(data, offset=0, count=128):
    chunk = data[offset:offset + count]
    for i in range(0, len(chunk), 16):
        piece = chunk[i:i + 16]
        hexpart = " ".join(f"{b:02x}" for b in piece)
        ascii_part = "".join(chr(b) if 32 <= b < 127 else "." for b in piece)
        print(f"  {offset + i:08x}  {hexpart:<47}  |{ascii_part}|")


def read_flatbuffer_strings(mm, limit=2_000_000):
    """Return (total, cjk_samples, all_samples) by scanning u32-len prefixed UTF-8."""
    size = len(mm)
    total = 0
    cjk = []
    samples = []
    pos = 0
    view = mm

    while pos + 5 < size and total < limit:
        length = struct.unpack_from("<I", view, pos)[0]
        if length < 2 or length > 512 or pos + 4 + length >= size:
            pos += 1
            continue

        payload = view[pos + 4:pos + 4 + length]
        terminator = view[pos + 4 + length]
        if terminator != 0:
            pos += 1
            continue

        if any(b < 0x20 and b not in (0x09, 0x0A, 0x0D) for b in payload):
            pos += 1
            continue

        try:
            text = payload.decode("utf-8")
        except UnicodeDecodeError:
            pos += 1
            continue

        if not text.strip():
            pos += 1
            continue

        total += 1
        has_cjk = any('\u4e00' <= ch <= '\u9fff' for ch in text)
        if has_cjk and len(cjk) < 6:
            cjk.append((pos, text))
        if len(samples) < 4:
            samples.append((pos, text))
        pos += 4 + length + 1

    return total, cjk, samples


def probe(path):
    print(f"\n=== {path} ===")
    f = open(path, "rb")
    mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
    print(f"size: {len(mm):,} bytes")

    root = struct.unpack_from("<I", mm, 0)[0]
    print(f"flatbuffer root offset: {root}")
    hexdump(mm, 0, 96)
    if root + 8 < len(mm):
        print("root table:")
        hexdump(mm, root, 64)

    total, cjk, samples = read_flatbuffer_strings(mm)
    print(f"\nlength-prefixed UTF-8 strings found: {total:,}")
    print("samples:")
    for offset, text in samples:
        print(f"  @0x{offset:08x}  {text[:60]!r}")
    print(f"with CJK: {len(cjk)} shown")
    for offset, text in cjk:
        print(f"  @0x{offset:08x}  {text[:80]!r}")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    for argument in sys.argv[1:]:
        probe(argument)
