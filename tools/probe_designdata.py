"""Reverse-engineer the HSR `DesignData/Windows/cn/*.bytes` text pack.

The pack is NOT a Unity bundle: it lives under
    <Game>_Data/Persistent/DesignData/Windows/cn/<md5>.bytes
and its payload contains plain UTF-8 Chinese strings, so it can be parsed
without any decryption step (at least for the record bodies).

Strategy: take known hash->text pairs from a community TextMap dump, locate
each text inside the game file, and dump the bytes that precede it. If every
hit is preceded by the same-width value equal to the TextMap key (little
endian / varint), the record layout falls out immediately.

Usage:
    python tools/probe_designdata.py <cn.bytes> <TextMapCHS.json>
"""

import json
import mmap
import re
import struct
import sys

HEX = "0123456789abcdef"


def hexdump(b, base=0):
    out = []
    for i in range(0, len(b), 16):
        chunk = b[i:i + 16]
        hx = " ".join(f"{c:02x}" for c in chunk)
        asc = "".join(chr(c) if 32 <= c < 127 else "." for c in chunk)
        ascii_cn = chunk.decode("utf-8", "replace").replace("\n", " ")
        out.append(f"  {base + i:08x}  {hx:<47}  |{asc}| {ascii_cn}")
    return "\n".join(out)


def load_pairs(json_path, want_first=6, scan_bytes=8_000_000):
    """Pull hash->text pairs out of a TextMap JSON without loading all of it."""
    pattern = re.compile(r'"(\d{10,20})"\s*:\s*"((?:[^"\\]|\\.)*)"')
    with open(json_path, "rb") as f:
        raw = f.read(scan_bytes).decode("utf-8", "ignore")
    pairs = pattern.findall(raw)
    head = pairs[:want_first]
    tail = pairs[-want_first:]
    return head, tail, len(pairs)


def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    game_path, json_path = sys.argv[1], sys.argv[2]

    head, tail, n_first = load_pairs(json_path)
    print(f"vendor pairs found in first chunk: {n_first}")
    print()

    f = open(game_path, "rb")
    mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
    print(f"game file: {len(mm):,} bytes")
    print()

    samples = head + tail
    hits = 0
    for key, text in samples:
        blob = text.encode("utf-8")
        off = mm.find(blob)
        key_int = int(key)
        if off < 0:
            print(f"  MISS  key={key[:12]}.. text={text[:30]}")
            continue
        hits += 1
        prev = mm[max(0, off - 24):off]
        le64 = int.from_bytes(prev[-8:], "little") if len(prev) >= 8 else None
        be64 = int.from_bytes(prev[-8:], "big") if len(prev) >= 8 else None
        match = ""
        if le64 == key_int:
            match = " <== LE64 == key"
        elif be64 == key_int:
            match = " <== BE64 == key"
        print(f"  HIT   key={key}  offset=0x{off:x}  len={len(blob)}")
        print(f"        text={text[:40]!r}{match}")
        print(hexdump(mm[max(0, off - 24):off + len(blob) + 12], max(0, off - 24)))
        print()

    print(f"hits: {hits}/{len(samples)}")

    # Also try to read the very first bytes of the file as a header.
    print("\nfile head (first 64 bytes):")
    print(hexdump(mm[:64]))

    # And see whether a simple sequential parse is plausible: scan for records
    # by looking at the first N strings and the gaps between them.
    print("\nfirst 12 printable UTF-8 runs (>=6 bytes, contains CJK):")
    cjk = re.compile(rb"(?:[\xe4-\xe9][\x80-\xbf]{2}){3,}")
    count = 0
    last = 0
    for m in cjk.finditer(mm):
        if m.start() < last:
            continue
        s = m.group()
        try:
            decoded = s.decode("utf-8")
        except Exception:
            continue
        print(f"  0x{m.start():08x} len={len(s):<4} gap_prev={m.start() - last:<4} {decoded[:30]}")
        last = m.end()
        count += 1
        if count >= 12:
            break


if __name__ == "__main__":
    main()
