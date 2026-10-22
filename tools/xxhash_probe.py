"""Two questions about the HSR text pack, answered empirically.

1. Does the pack store the TextMap key (the vendor JSON's numeric key) anywhere?
   -> search the whole file for the 8-byte little/big endian encodings.
2. The vendor README says TextMap keys use xxHash. If we can reproduce them from
   the plain text alone, the pack needs no key decoding at all: parse the strings
   and recompute the ids. Verify against known vendor pairs.

Usage:
    python tools/xxhash_probe.py <cn.bytes> <TextMapCHS.json>
"""

import mmap
import re
import sys

MASK = (1 << 64) - 1
P1 = 11400714785074694791
P2 = 14029467366897019727
P3 = 1609587929392839161
P4 = 9650029242287828579
P5 = 2870177450012600261


def rotl(x, r):
    return ((x << r) | (x >> (64 - r))) & MASK


def round64(acc, val):
    acc = (acc + val * P2) & MASK
    acc = rotl(acc, 31)
    return (acc * P1) & MASK


def merge(acc, val):
    acc ^= round64(0, val)
    return (acc * P1 + P4) & MASK


def xxh64(data, seed=0):
    n = len(data)
    i = 0
    if n >= 32:
        v1 = (seed + P1 + P2) & MASK
        v2 = (seed + P2) & MASK
        v3 = seed & MASK
        v4 = (seed - P1) & MASK
        limit = n - 32
        while i <= limit:
            v1 = round64(v1, int.from_bytes(data[i:i + 8], "little")); i += 8
            v2 = round64(v2, int.from_bytes(data[i:i + 8], "little")); i += 8
            v3 = round64(v3, int.from_bytes(data[i:i + 8], "little")); i += 8
            v4 = round64(v4, int.from_bytes(data[i:i + 8], "little")); i += 8
        h = (rotl(v1, 1) + rotl(v2, 7) + rotl(v3, 12) + rotl(v4, 18)) & MASK
        h = merge(h, v1); h = merge(h, v2); h = merge(h, v3); h = merge(h, v4)
    else:
        h = (seed + P5) & MASK

    h = (h + n) & MASK
    while i + 8 <= n:
        k1 = round64(0, int.from_bytes(data[i:i + 8], "little"))
        h ^= k1
        h = (rotl(h, 27) * P1 + P4) & MASK
        i += 8
    if i + 4 <= n:
        h ^= (int.from_bytes(data[i:i + 4], "little") * P1) & MASK
        h = (rotl(h, 23) * P2 + P3) & MASK
        i += 4
    while i < n:
        h ^= (data[i] * P5) & MASK
        h = (rotl(h, 11) * P1) & MASK
        i += 1

    h ^= h >> 33
    h = (h * P2) & MASK
    h ^= h >> 29
    h = (h * P3) & MASK
    h ^= h >> 32
    return h


def load_pairs(path, n=40):
    pattern = re.compile(r'"(\d{10,20})"\s*:\s*"((?:[^"\\]|\\.)*)"')
    with open(path, "rb") as f:
        raw = f.read(2_000_000).decode("utf-8", "ignore")
    return pattern.findall(raw)[:n]


def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    game_path, json_path = sys.argv[1], sys.argv[2]

    f = open(game_path, "rb")
    mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
    pairs = load_pairs(json_path)
    print(f"vendor pairs loaded: {len(pairs)}  |  pack bytes: {len(mm):,}\n")

    print("== 1. is the key stored in the pack? (search LE/BE 8-byte encodings) ==")
    found = 0
    for key, text in pairs[:20]:
        k = int(key)
        le = k.to_bytes(8, "little")
        be = k.to_bytes(8, "big")
        off_le = mm.find(le)
        off_be = mm.find(be)
        if off_le >= 0 or off_be >= 0:
            found += 1
            print(f"  HIT  {key}  le@{off_le}  be@{off_be}")
    print(f"  stored keys found: {found}/20\n")

    print("== 2. is the key = xxh64(utf8 text, seed=0)? ==")
    ok = 0
    for key, text in pairs[:20]:
        h = xxh64(text.encode("utf-8"))
        match = "OK " if h == int(key) else "NO "
        if h == int(key):
            ok += 1
        print(f"  {match} key={key:<21} xxh64={h:<21} {text[:24]}")
    print(f"  xxh64 matches: {ok}/20\n")

    print("== 2b. same check with a few seeds/normalisations ==")
    variants = {
        "seed0": lambda t: xxh64(t.encode("utf-8"), 0),
        "seed1": lambda t: xxh64(t.encode("utf-8"), 1),
        "utf16le": lambda t: xxh64(t.encode("utf-16-le"), 0),
        "stripped": lambda t: xxh64(t.strip().encode("utf-8"), 0),
    }
    for name, fn in variants.items():
        score = sum(1 for key, text in pairs[:20] if fn(text) == int(key))
        print(f"  {name:<10} {score}/20")


if __name__ == "__main__":
    main()
