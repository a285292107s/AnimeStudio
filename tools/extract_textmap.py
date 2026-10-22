"""Self-extract the HSR Chinese TextMap from the game's own DesignData pack.

Bypasses every third-party dump: the pack at
    <Game>_Data/Persistent/DesignData/Windows/cn/<md5>.bytes
is a flat table of localized strings and it stores the TextMapHash explicitly.

Format (reverse-engineered, validated against a 960k-record walk):

    file   := varint(record_count) record*
    record := 0x03
              varint  id_a          (32-bit-ish; not the textmap hash)
              varint  textmap_hash  (the key used by config tables)
              varint  utf8_len
              utf8    text[utf8_len]

Usage:
    python tools/extract_textmap.py <cn.bytes> [--out TextMapCHS.json] [--compare vendor.json]
"""

import json
import mmap
import sys
import time


def varint(buf, pos):
    shift = 0
    value = 0
    while True:
        b = buf[pos]
        value |= (b & 0x7F) << shift
        pos += 1
        if not (b & 0x80):
            return value, pos
        shift += 7


def parse_all(mm, want_out=None):
    size = len(mm)
    total, pos = varint(mm, 0)

    entries = [] if want_out else None
    count = 0
    t0 = time.time()
    while pos < size:
        if mm[pos] != 0x03:
            return count, pos, entries, f"unexpected tag 0x{mm[pos]:02x} at {pos}"
        pos += 1
        _id_a, pos = varint(mm, pos)
        tmap_hash, pos = varint(mm, pos)
        length, pos = varint(mm, pos)
        if length < 0 or pos + length > size:
            return count, pos, entries, f"bad length {length} at {pos}"
        text = mm[pos:pos + length].decode("utf-8")
        pos += length
        count += 1
        if entries is not None:
            entries.append((tmap_hash, text))
        if count % 200000 == 0:
            dt = time.time() - t0
            print(f"  {count:,}/{total:,}  ({dt:.1f}s, {count / dt:,.0f}/s)")

    return count, pos, entries, (None if count == total else f"count {count} != header {total}")


def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    args = sys.argv[1:]
    if not args:
        raise SystemExit(__doc__)
    pack = args[0]
    out_path = None
    compare = None
    for i, a in enumerate(args):
        if a == "--out":
            out_path = args[i + 1]
        elif a == "--compare":
            compare = args[i + 1]

    f = open(pack, "rb")
    mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
    print(f"pack: {pack}")
    print(f"size: {len(mm):,} bytes")

    count, end, entries, err = parse_all(mm, want_out=bool(out_path or compare))
    print(f"\n== parse ==")
    print(f"  records     : {count:,}")
    print(f"  bytes used  : {end:,} / {len(mm):,}  ({end / len(mm) * 100:.3f}%)")
    print(f"  structure   : {'OK' if err is None else err}")

    if entries:
        uniq_ids = len({e[0] for e in entries})
        print(f"  distinct ids: {uniq_ids:,}")
        print(f"  first 3     : {[(e[0], e[1][:20]) for e in entries[:3]]}")
        total_chars = sum(len(e[1]) for e in entries)
        print(f"  total chars : {total_chars:,}")

    if out_path and entries:
        t0 = time.time()
        # keep file order so diffs between game versions stay readable
        with open(out_path, "w", encoding="utf-8") as out:
            out.write("{\n")
            for i, (h, t) in enumerate(entries):
                out.write(f'  "{h}": {json.dumps(t, ensure_ascii=False)}')
                out.write(",\n" if i + 1 < len(entries) else "\n")
            out.write("}\n")
        print(f"\n  wrote {out_path} in {time.time() - t0:.1f}s")

    if compare and entries:
        print(f"\n== compare against {compare} ==")
        t0 = time.time()
        with open(compare, "r", encoding="utf-8") as fh:
            vendor = json.load(fh)
        print(f"  vendor entries : {len(vendor):,}  (loaded in {time.time() - t0:.1f}s)")

        ours = {h: t for h, t in entries}
        missing = [h for h in vendor if h not in ours]
        extra = [h for h in ours if h not in vendor]
        same = sum(1 for h, t in vendor.items() if ours.get(h) == t)
        differ = [h for h, t in vendor.items() if h in ours and ours[h] != t]

        print(f"  common ids     : {len(vendor) - len(missing):,}")
        print(f"  identical text : {same:,}")
        print(f"  text differs   : {len(differ):,}")
        print(f"  only in vendor : {len(missing):,}")
        print(f"  only in game   : {len(extra):,}")
        for h in (differ[:5] if differ else []):
            print(f"    DIFF {h}")
            print(f"      vendor: {vendor[h][:60]!r}")
            print(f"      game  : {ours[h][:60]!r}")


if __name__ == "__main__":
    main()
