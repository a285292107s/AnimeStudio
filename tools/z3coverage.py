"""How much of a compact ZZZ map's container column resolves through the Z3 index."""

import json
import sys

map_path = sys.argv[1]
index_path = sys.argv[2]

with open(index_path, encoding="utf-8") as fh:
    index = json.load(fh)
print(f"index entries: {len(index):,}")

IMAGE_TYPES = {28: "Texture2D", 213: "Sprite"}

with open(map_path, "rb") as fh:
    data = fh.read()

pos = 0
assert data[0:4] == b"ASM2"
version = data[4]
flags = data[5]
pos = 6
game = int.from_bytes(data[pos:pos + 4], "little", signed=True)
pos += 4
entries = int.from_bytes(data[pos:pos + 8], "little", signed=True)
pos += 8
files = int.from_bytes(data[pos:pos + 4], "little", signed=True)
pos += 4
containers = int.from_bytes(data[pos:pos + 4], "little", signed=True)
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


sources = [string() for _ in range(files)]
container_table = [string() for _ in range(containers)]

total = 0
with_container = 0
resolved = 0
image_total = 0
image_with = 0
image_resolved = 0
for _ in range(files):
    count = varint()
    for _ in range(count):
        _name = string()
        cid = varint()
        typ = varint()
        _pathid = varint()
        _offset = varint()
        if flags & 1:
            pos += 8
        total += 1
        if cid:
            with_container += 1
            if container_table[cid - 1] in index:
                resolved += 1
        if typ in IMAGE_TYPES:
            image_total += 1
            if cid:
                image_with += 1
                if container_table[cid - 1] in index:
                    image_resolved += 1

print(f"entries={entries:,} parsed={total:,}")
print(f"with container: {with_container:,} ({with_container / total:.1%})")
print(f"  resolved by index: {resolved:,} ({resolved / max(1, with_container):.1%} of those)")
print(f"images (Sprite+Texture2D): {image_total:,}")
print(f"  with container: {image_with:,}")
print(f"  resolved: {image_resolved:,} ({image_resolved / max(1, image_with):.1%} of those)")
