"""Streaming inspector for AnimeStudio `.map` (AssetMap) files.

The CLI writes an uncompressed MessagePack `AssetMap`:

    [ GameType:int, [ AssetEntry x N ] ]
    AssetEntry = [ Name:str, Container:str, Source:str, PathID:int,
                   Type:int, Hash:str|nil, Offset:int ]

7.3M entries / ~2 GB is normal for a full HSR dump, so this never materialises
the entries: it mmaps the file and walks the MessagePack header bytes, skipping
string payloads by length. Only the counters you ask for are kept.

Usage:
    python tools/mapscan.py <file.map> [--limit N] [--samples K] [--top K]

Outputs: GameType, entry count, Type histogram, Source path-style check,
name/container prefix samples, and whether the stream ends cleanly.
"""

import argparse
import mmap
import os
import struct
import time
from collections import Counter

# Subset of AnimeStudio\ClassIDType.cs (Unity class ids) for readable output.
CLASS_NAMES = {
    1: "GameObject", 4: "Transform", 21: "Material", 23: "MeshRenderer",
    25: "Renderer", 28: "Texture2D", 33: "MeshFilter", 43: "Mesh",
    48: "Shader", 49: "TextAsset", 74: "AnimationClip", 83: "AudioClip",
    90: "Avatar", 91: "AnimatorController", 95: "Animator",
    114: "MonoBehaviour", 115: "MonoScript", 128: "Font",
    137: "SkinnedMeshRenderer", 142: "AssetBundle", 213: "Sprite",
    222: "CanvasRenderer", 223: "Canvas", 224: "RectTransform",
    1208: "MiHoYoBinData",
}


class Reader:
    __slots__ = ("mm", "pos", "size")

    def __init__(self, mm):
        self.mm = mm
        self.pos = 0
        self.size = len(mm)

    def u8(self):
        v = self.mm[self.pos]
        self.pos += 1
        return v

    def int(self):
        mm, pos = self.mm, self.pos
        b = mm[pos]
        if b <= 0x7F:                      # positive fixint
            self.pos = pos + 1
            return b
        if b >= 0xE0:                      # negative fixint
            self.pos = pos + 1
            return b - 0x100
        if b == 0xCC:
            self.pos = pos + 2
            return mm[pos + 1]
        if b == 0xCD:
            self.pos = pos + 3
            return struct.unpack_from(">H", mm, pos + 1)[0]
        if b == 0xCE:
            self.pos = pos + 5
            return struct.unpack_from(">I", mm, pos + 1)[0]
        if b == 0xCF:
            self.pos = pos + 9
            return struct.unpack_from(">Q", mm, pos + 1)[0]
        if b == 0xD0:
            self.pos = pos + 2
            return struct.unpack_from(">b", mm, pos + 1)[0]
        if b == 0xD1:
            self.pos = pos + 3
            return struct.unpack_from(">h", mm, pos + 1)[0]
        if b == 0xD2:
            self.pos = pos + 5
            return struct.unpack_from(">i", mm, pos + 1)[0]
        if b == 0xD3:
            self.pos = pos + 9
            return struct.unpack_from(">q", mm, pos + 1)[0]
        raise ValueError(f"int tag 0x{b:02x} at {pos}")

    def str_len(self):
        """Advance past the string header, return (length, nil, payload_pos)."""
        mm, pos = self.mm, self.pos
        b = mm[pos]
        if b == 0xC0:                      # nil
            self.pos = pos + 1
            return 0, True, pos + 1
        if 0xA0 <= b <= 0xBF:              # fixstr
            self.pos = pos + 1
            return b & 0x1F, False, pos + 1
        if b == 0xD9:                      # str8
            self.pos = pos + 2
            return mm[pos + 1], False, pos + 2
        if b == 0xDA:                      # str16
            self.pos = pos + 3
            return struct.unpack_from(">H", mm, pos + 1)[0], False, pos + 3
        if b == 0xDB:                      # str32
            self.pos = pos + 5
            return struct.unpack_from(">I", mm, pos + 1)[0], False, pos + 5
        raise ValueError(f"str tag 0x{b:02x} at {pos}")

    def read_str(self):
        n, nil, p = self.str_len()
        self.pos = p + n
        return None if nil else self.mm[p:p + n]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("map")
    ap.add_argument("--limit", type=int, default=0, help="stop after N entries")
    ap.add_argument("--samples", type=int, default=5)
    ap.add_argument("--top", type=int, default=20)
    ap.add_argument("--type-samples", type=int, default=6, help="names kept per type")
    ap.add_argument("--substr", default="textmap,ui_,icon_,atlas,spriteoutp",
                    help="comma separated name substrings to count")
    ap.add_argument("--dump-type", type=int, default=0, help="class id whose names to dump")
    ap.add_argument("--dump-max", type=int, default=400)
    ap.add_argument("--dump-out", default="", help="write dump to this utf-8 file")
    ap.add_argument("--dump-containers", default="", help="write distinct containers here")
    ap.add_argument("--type-filter", type=int, default=-1, help="only count entries of this ClassID")
    ap.add_argument("--cont-substr", default="", help="only count entries whose container contains this substring")
    ap.add_argument("--uniq-names", action="store_true", help="count unique names among filtered entries")
    args = ap.parse_args()

    f = open(args.map, "rb")
    mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
    r = Reader(mm)

    if r.mm[0] != 0x92:
        raise SystemExit(f"not an uncompressed AssetMap (first byte 0x{mm[0]:02x})")

    r.pos = 1
    game_type = r.int()
    tag = r.u8()
    if tag == 0xDD:
        total = struct.unpack_from(">I", mm, r.pos)[0]
        r.pos += 4
    elif tag == 0xDC:
        total = struct.unpack_from(">H", mm, r.pos)[0]
        r.pos += 2
    elif 0x90 <= tag <= 0x9F:
        total = tag & 0x0F
    else:
        raise SystemExit(f"bad entry-array tag 0x{tag:02x} at {r.pos - 1}")

    print(f"file bytes   : {len(mm):,}")
    print(f"game type    : {game_type}")
    print(f"entries      : {total:,}")

    types = Counter()
    src_abs = 0
    src_rel = 0
    src_other = 0
    name_samples = []
    src_samples = []
    cont_prefix = Counter()
    type_names = {}
    sources = set()
    hash_n = 0
    substrs = [s.strip().lower() for s in args.substr.split(",") if s.strip()]
    sub_counts = Counter()
    sub_samples = {s: [] for s in substrs}
    ui_by_type = Counter()
    ui_entries = 0
    dump_list = []
    containers = set()
    match_count = 0
    match_sources = set()
    match_names = Counter()
    cont_needle = args.cont_substr.lower().encode()
    declared = total if not args.limit else min(total, args.limit)

    t0 = time.time()
    seen = 0
    while seen < declared and r.pos < len(mm):
        if r.mm[r.pos] != 0x97:
            print(f"!! entry {seen}: expected 0x97, got 0x{r.mm[r.pos]:02x} at {r.pos}")
            break
        r.pos += 1

        name = r.read_str()
        cont = r.read_str()
        src = r.read_str()
        _path_id = r.int()
        etype = r.int()
        h = r.read_str()
        _offset = r.int()

        types[etype] += 1
        if (args.type_filter < 0 or etype == args.type_filter) and cont is not None:
            if not cont_needle or cont_needle in cont.lower():
                match_count += 1
                if name is not None and args.uniq_names:
                    match_names[name] += 1
                if src is not None:
                    match_sources.add(src)
        if cont is not None and len(cont) > 1:
            containers.add(cont)
        if cont is not None and b"/ui/" in cont.lower():
            ui_entries += 1
            ui_by_type[etype] += 1
        if etype == args.dump_type and len(dump_list) < args.dump_max:
            dump_list.append((
                name.decode("utf-8", "replace") if name else "",
                cont.decode("utf-8", "replace") if cont else "",
            ))
        if h:
            hash_n += 1
        if src is not None:
            sources.add(src)
        if name is not None:
            low = name.decode("utf-8", "replace").lower()
            for s in substrs:
                if s in low:
                    sub_counts[s] += 1
                    if len(sub_samples[s]) < 4:
                        sub_samples[s].append(name.decode("utf-8", "replace"))
            lst = type_names.get(etype)
            if lst is None:
                type_names[etype] = [name.decode("utf-8", "replace")]
            elif len(lst) < args.type_samples:
                lst.append(name.decode("utf-8", "replace"))
        if seen < args.samples:
            name_samples.append(name.decode("utf-8", "replace") if name else None)
            src_samples.append(src.decode("utf-8", "replace") if src else None)
        if seen < 200000 and cont:
            try:
                cont_prefix[cont.decode("utf-8", "replace")[:24]] += 1
            except Exception:
                pass
        if src:
            if src[:3].lower() == b"c:\\" or src[:2] == b"\\\\":
                src_abs += 1
            elif src[:1] in (b"/", b"\\") or (len(src) > 2 and src[1:2] == b":"):
                src_other += 1
            else:
                src_rel += 1

        seen += 1
        if seen % 1000000 == 0:
            dt = time.time() - t0
            print(f"  {seen:,} / {declared:,}  ({dt:.0f}s, {seen / dt:,.0f}/s)")

    dt = time.time() - t0
    print(f"\nscanned      : {seen:,} entries in {dt:.1f}s ({seen / max(dt, 1e-9):,.0f}/s)")
    print(f"eof reached  : {r.pos >= len(mm)}  (pos={r.pos:,} / {len(mm):,})")

    print(f"\ntype histogram (ClassIDType):")
    for t, c in types.most_common(args.top):
        nm = CLASS_NAMES.get(t, "Type")
        print(f"  {t:>6} {nm:<18} {c:>10,}  {c / max(seen, 1) * 100:5.2f}%")

    print(f"\nsource files  : {len(sources):,} distinct")
    print(f"entries w/hash: {hash_n:,}")

    print(f"\nname substring counts:")
    for s in substrs:
        print(f"  {s!r:<14} {sub_counts[s]:>10,}")
        for smp in sub_samples[s]:
            print(f"                 e.g. {smp}")

    print(f"\nsource path style:")
    print(f"  absolute  : {src_abs:,}")
    print(f"  relative  : {src_rel:,}")
    print(f"  other     : {src_other:,}")

    print(f"\nname samples:")
    for s in name_samples:
        print(f"  {s}")

    print(f"\nsource samples:")
    for s in src_samples:
        print(f"  {s}")

    print(f"\ncontainer prefixes (top {args.top} of first 200k):")
    for p, c in cont_prefix.most_common(args.top):
        print(f"  {c:>8,}  {p!r}")

    print(f"\nname samples per type:")
    for t, _c in types.most_common(8):
        print(f"  {t} {CLASS_NAMES.get(t, 'Type')}:")
        for nm in type_names.get(t, [])[:args.type_samples]:
            print(f"    {nm}")

    print(f"\nentries under a '/ui/' container: {ui_entries:,}")
    for t, c in ui_by_type.most_common(12):
        print(f"  {t:>6} {CLASS_NAMES.get(t, 'Type'):<18} {c:>10,}")

    if args.type_filter >= 0 or args.cont_substr:
        print(f"\nfiltered (type={args.type_filter}, container~{args.cont_substr!r}):")
        print(f"  entries : {match_count:,}")
        print(f"  sources : {len(match_sources):,}")
        if args.uniq_names:
            uniq = len(match_names)
            dups = [(n, c) for n, c in match_names.most_common(10) if c > 1]
            print(f"  names   : {uniq:,} unique  -> collision loss {match_count - uniq:,}")
            for name, count in dups:
                print(f"    x{count:<4} {name.decode('utf-8', 'replace')}")

    if args.dump_containers:
        containers.discard(b"")
        out_dir = os.path.dirname(os.path.abspath(args.dump_containers))
        os.makedirs(out_dir, exist_ok=True)
        with open(args.dump_containers, "w", encoding="utf-8", newline="\n") as out:
            for ct in sorted(containers):
                out.write(ct.decode("utf-8", "replace") + "\n")
        print(f"\ndistinct containers: {len(containers):,} -> {args.dump_containers}")

    if args.dump_type:
        print(f"\ndump of type {args.dump_type} ({CLASS_NAMES.get(args.dump_type, '?')}): "
              f"{len(dump_list)} shown")
        if args.dump_out:
            os.makedirs(os.path.dirname(os.path.abspath(args.dump_out)), exist_ok=True)
            with open(args.dump_out, "w", encoding="utf-8", newline="\n") as out:
                for nm, ct in dump_list:
                    out.write(f"{nm}\t{ct}\n")
            print(f"  written to {args.dump_out}")
        else:
            for nm, ct in dump_list[:60]:
                print(f"  {nm}\n      {ct}")


if __name__ == "__main__":
    main()
