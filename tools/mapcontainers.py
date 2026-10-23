"""Dump container statistics from a compact (ASM2) AssetMap."""

import collections
import sys

path = sys.argv[1]
with open(path, "rb") as fh:
    data = fh.read()

pos = 0
assert data[0:4] == b"ASM2", data[0:4]
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
print(f"version={version} flags={flags} game={game} entries={entries:,} files={files:,} containers={containers:,}")


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
containers_list = [string() for _ in range(containers)]
print("source sample:", sources[:3])
print("container sample:", containers_list[:20])
print("empty container strings:", sum(1 for c in containers_list if not c))

empty_entries = 0
counter = collections.Counter()
type_counter = collections.Counter()
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
        type_counter[typ] += 1
        if not cid:
            empty_entries += 1
        else:
            counter[containers_list[cid - 1]] += 1

print(f"entries without container: {empty_entries:,} / {entries:,}")
print("top containers:")
for value, number in counter.most_common(15):
    print(f"  {number:>10,}  {value}")
print("top types:", type_counter.most_common(8))
print("parsed bytes:", f"{pos:,}", "of", f"{len(data):,}")
