"""Container shapes of a compact map: real paths vs numeric hashes (and how many hashes the index knows)."""

import json
import struct
import sys

map_path = sys.argv[1]
index_path = sys.argv[2]

with open(index_path, encoding="utf-8") as fh:
    index = json.load(fh)

data = open(map_path, "rb").read()
assert data[0:4] == b"ASM2"
flags = data[5]
pos = 6
pos += 4
entries = struct.unpack_from("<q", data, pos)[0]
pos += 8
files = struct.unpack_from("<i", data, pos)[0]
pos += 4
containers = struct.unpack_from("<i", data, pos)[0]
pos += 4


def varint():
    global pos
    value = 0
    shift = 0
    while True:
        byte = data[pos]
        pos += 1
        value |= (byte & 0x7F) << shift
        if not byte & 0x80:
            return value
        shift += 7


def string():
    global pos
    length = varint()
    if length == 0:
        return ""
    value = data[pos:pos + length].decode("utf-8", "replace")
    pos += length
    return value


for _ in range(files):
    string()
table = [string() for _ in range(containers)]

shape = {"path": 0, "hash-known": 0, "hash-unknown": 0}
total = without = 0
for _ in range(files):
    count = varint()
    for _ in range(count):
        string()
        cid = varint()
        varint()
        varint()
        varint()
        if flags & 1:
            pos += 8
        total += 1
        if not cid:
            without += 1
            continue
        container = table[cid - 1]
        if container.isdigit():
            shape["hash-known" if container in index else "hash-unknown"] += 1
        else:
            shape["path"] += 1

print(f"entries={total:,} without container={without:,}")
for key, value in shape.items():
    print(f"  {key}: {value:,} ({value / max(1, total - without):.1%})")
