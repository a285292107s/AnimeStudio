"""Walk the HSR `DesignData/Windows/cn/*.bytes` string pack end to end.

Layout discovered so far (record boundaries confirmed against a community
TextMap dump, whose entry order matches the pack's record order):

    file   := <varint header fields> record* EOF
    record := <varint metadata, 14..18 bytes> <1-byte UTF-8 length> <utf-8 text>

This script proves or disproves the walk by parsing the whole file, counting
records, and checking the text is valid UTF-8 throughout. It also tries to decode
the per-record metadata as LEB128 varints and reports whether any of them equals
the TextMap key (which would give us hash->text without knowing a hash salt).

Usage:
    python tools/decode_designdata.py <cn.bytes> [TextMapCHS.json]
"""

import mmap
import re
import sys


def varint(buf, pos, limit=12):
    """LEB128 decode. Returns (value, new_pos) or (None, pos)."""
    shift = 0
    value = 0
    start = pos
    while pos < len(buf) and pos - start < limit:
        b = buf[pos]
        value |= (b & 0x7F) << shift
        pos += 1
        if not (b & 0x80):
            return value, pos
        shift += 7
    return None, start


def load_pairs(path, n=200):
    pattern = re.compile(r'"(\d{10,20})"\s*:\s*"((?:[^"\\]|\\.)*)"')
    with open(path, "rb") as f:
        raw = f.read(4_000_000).decode("utf-8", "ignore")
    return pattern.findall(raw)[:n]


def head_decodes(mm):
    print("== header varint decodes ==")
    for pos in range(0, 6):
        v, end = varint(mm, pos)
        if v is not None and end <= pos + 12:
            print(f"  from {pos}: value={v:,}  consumed={end - pos} bytes  next=0x{mm[end]:02x}")
    print()


def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    path = sys.argv[1]
    json_path = sys.argv[2] if len(sys.argv) > 2 else None

    f = open(path, "rb")
    mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
    size = len(mm)
    print(f"pack: {size:,} bytes\n")
    head_decodes(mm)

    # Skip the header: first records are known to start after <header><metadata>.
    # Find the first string start by scanning for a valid record-like boundary.
    pos = 4  # header is short; we search from here
    records = []
    meta_lens = {}
    bad = 0
    while pos < size - 2:
        found = None
        for k in range(0, 40):
            p = pos + k
            if p < 1 or p >= size:
                break
            length = mm[p - 1]
            if length < 1 or length > 0x7F or p + length > size:
                continue
            seg = mm[p:p + length]
            try:
                s = seg.decode("utf-8")
            except UnicodeDecodeError:
                continue
            if not s or s.isspace():
                continue
            if any(ord(c) < 0x20 and c not in "\n\t" for c in s):
                continue
            found = (p, length, s, mm[pos:p])
            break

        if found is None:
            bad += 1
            break
        p, length, s, meta = found
        records.append((p, meta, s))
        meta_lens[len(meta)] = meta_lens.get(len(meta), 0) + 1
        pos = p + length

    print(f"== walk result ==")
    print(f"  records parsed : {len(records):,}")
    print(f"  bytes consumed : {pos:,} / {size:,}  ({pos / size * 100:.2f}%)")
    print(f"  stopped early  : {bad > 0}")
    print(f"  metadata length histogram: {dict(sorted(meta_lens.items()))}")

    if records:
        print(f"\n  first record text  : {records[0][2][:40]!r}")
        print(f"  last  record text  : {records[-1][2][:40]!r}")

    # metadata varint decode vs vendor keys
    if json_path and records:
        pairs = load_pairs(json_path, 60)
        print(f"\n== metadata varint decode vs vendor key (first {min(len(pairs), len(records))} records) ==")
        hits_le = 0
        hits_varint = 0
        for i, (key, text) in enumerate(pairs):
            if i >= len(records):
                break
            p, meta, s = records[i]
            if s != text:
                continue
            # collect all varint values reachable inside meta
            vals = []
            for start in range(len(meta)):
                v, end = varint(meta, start)
                if v is not None and end == len(meta):
                    vals.append(v)
            le64 = int.from_bytes(meta[-8:], "little")
            be64 = int.from_bytes(meta[-8:], "big")
            if le64 == int(key) or be64 == int(key):
                hits_le += 1
            if int(key) in vals:
                hits_varint += 1
            if i < 8:
                print(f"  rec{i}: meta={meta.hex(' ')}")
                print(f"         text={s[:28]!r} key={key}")
                print(f"         meta[-8:] LE={le64}  BE={be64}")
                print(f"         trailing varint values={vals}")
        print(f"\n  key found as meta[-8:] LE/BE : {hits_le}")
        print(f"  key found as trailing varint : {hits_varint}")


if __name__ == "__main__":
    main()
