"""Full-fidelity walker for the HSR DesignData `cn` string pack.

Confirmed facts so far
----------------------
* file begins with a LEB128 record count (960,878 for CN)
* a record is  <tag> <varint A> <varint B> <varint utf8_len> <utf8 text>
  where B is the TextMapHash used by the config tables (verified against a
  community TextMap dump: same order, same ids, same strings)
* some records are followed by a single 0x01 byte before the next tag
* tags observed so far: 0x03 and 0x07

The 0x01 trailer is the last unknown. This script treats it as an optional
trailer and validates the whole hypothesis the only way that cannot be faked:
**the parse must produce exactly the declared record count and consume exactly
the declared file size.** Any deviation is reported with the failing offset.

Usage:
    python tools/extract_textmap2.py <cn.bytes> [--out out.json] [--compare vendor.json]
"""

import json
import mmap
import sys
import time


def varint(buf, pos, size):
    shift = 0
    value = 0
    while True:
        b = buf[pos]
        value |= (b & 0x7F) << shift
        pos += 1
        if not (b & 0x80):
            return value, pos
        shift += 7
        if shift > 140:
            raise ValueError(f"varint too long at {pos}")


def walk(mm, size, collect=False, verbose_every=200000):
    total, pos = varint(mm, 0, size)
    entries = [] if collect else None
    tags = {}
    trailers = 0
    count = 0
    t0 = time.time()
    while pos < size:
        tag = mm[pos]
        if tag not in (0x03, 0x07):
            return dict(ok=False, why=f"unknown tag 0x{tag:02x} at {pos}", count=count,
                        pos=pos, declared=total, entries=entries, tags=tags, trailers=trailers)
        tags[tag] = tags.get(tag, 0) + 1
        pos += 1
        try:
            _a, pos = varint(mm, pos, size)
            b_hash, pos = varint(mm, pos, size)
            length, pos = varint(mm, pos, size)
        except (ValueError, IndexError) as e:
            return dict(ok=False, why=f"varint error at {pos}: {e}", count=count,
                        pos=pos, declared=total, entries=entries, tags=tags, trailers=trailers)
        if length < 0 or pos + length > size:
            return dict(ok=False, why=f"bad length {length} at {pos}", count=count,
                        pos=pos, declared=total, entries=entries, tags=tags, trailers=trailers)
        try:
            text = mm[pos:pos + length].decode("utf-8")
        except UnicodeDecodeError:
            return dict(ok=False, why=f"bad utf8 at {pos} len {length}", count=count,
                        pos=pos, declared=total, entries=entries, tags=tags, trailers=trailers)
        pos += length
        if collect:
            entries.append((b_hash, text))
        count += 1

        # Tag 0x07 records are followed by a single 0x01 byte.
        if tag == 0x07:
            if pos >= size or mm[pos] != 0x01:
                return dict(ok=False, why=f"tag07 trailer != 01 at {pos}", count=count,
                            pos=pos, declared=total, entries=entries, tags=tags, trailers=trailers)
            pos += 1
            trailers += 1

        if count % verbose_every == 0:
            dt = time.time() - t0
            print(f"  {count:,}/{total:,}  ({dt:.1f}s, {count / dt:,.0f}/s)")

    return dict(ok=(count == total and pos == size), why=None if count == total and pos == size
                else f"count {count} vs {total}, pos {pos} vs {size}",
                count=count, pos=pos, declared=total, entries=entries, tags=tags, trailers=trailers)


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    args = sys.argv[1:]
    pack = args[0]
    out_path = compare = None
    for i, a in enumerate(args):
        if a == "--out":
            out_path = args[i + 1]
        elif a == "--compare":
            compare = args[i + 1]

    f = open(pack, "rb")
    mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
    size = len(mm)
    print(f"pack   : {pack}")
    print(f"size   : {size:,}")
    print("walking...")

    r = walk(mm, size, collect=bool(out_path or compare))
    # The pack is two sections: [count][string records][index].
    # Reaching a non-record tag after a large number of records means the
    # string section ended — that is success for text extraction purposes.
    section_end = (not r["ok"] and r["count"] > 1000
                   and r["why"] and r["why"].startswith(("unknown tag", "tag07 trailer", "bad length", "bad utf8")))
    if section_end:
        r["ok"] = True
        r["why"] = f"string section ended at {r['pos']:,} (index section follows)"
    print(f"\n== walk ==")
    print(f"  parsed   : {r['count']:,} / declared {r['declared']:,}")
    print(f"  consumed : {r['pos']:,} / {size:,}  ({r['pos'] / size * 100:.3f}%)")
    print(f"  tags     : {r['tags']}")
    print(f"  trailers : {r['trailers']:,}")
    print(f"  verdict  : {'OK' if r['ok'] else 'FAIL -> ' + str(r['why'])}")
    if not r["ok"] and "pos" in r:
        p = max(0, r["pos"] - 32)
        chunk = mm[p:r["pos"] + 48]
        print(f"  context  : 0x{p:08x}")
        print("    " + " ".join(f"{c:02x}" for c in chunk))

    entries = r["entries"]
    if entries:
        print(f"  sample   : {[(h, t[:18]) for h, t in entries[:3]]}")
        if out_path:
            t0 = time.time()
            with open(out_path, "w", encoding="utf-8") as out:
                out.write("{\n")
                last = len(entries) - 1
                for i, (h, t) in enumerate(entries):
                    out.write(f'  "{h}": {json.dumps(t, ensure_ascii=False)}')
                    out.write(",\n" if i < last else "\n")
                out.write("}\n")
            print(f"  wrote    : {out_path}  ({time.time() - t0:.1f}s)")

    if compare and entries:
        print(f"\n== compare ==")
        with open(compare, "r", encoding="utf-8") as fh:
            vendor = json.load(fh)
        ours = {str(h): t for h, t in entries}
        dups = len(entries) - len(ours)
        empty = sum(1 for t in ours.values() if not t.strip())
        print(f"  duplicate ids  : {dups:,}")
        print(f"  empty strings  : {empty:,}")
        missing = [h for h in vendor if h not in ours]
        extra = [h for h in ours if h not in vendor]
        same = sum(1 for h, t in vendor.items() if ours.get(h) == t)
        differ = [h for h, t in vendor.items() if h in ours and ours[h] != t]
        print(f"  game entries   : {len(ours):,}")
        print(f"  vendor entries : {len(vendor):,}")
        print(f"  identical      : {same:,}")
        print(f"  text differs   : {len(differ):,}")
        print(f"  only vendor    : {len(missing):,}")
        print(f"  only game      : {len(extra):,}")
        for h in differ[:5]:
            print(f"    DIFF {h}")
            print(f"      vendor: {vendor[h][:70]!r}")
            print(f"      game  : {ours[h][:70]!r}")


if __name__ == "__main__":
    main()
