"""Compare the Z3 index paths with the folder names shown in the asset repo screenshot."""

import collections
import json
import sys

path = sys.argv[1]
with open(path, encoding="utf-8") as fh:
    index = json.load(fh)

print(f"entries: {len(index):,}")

segments = collections.Counter()
depth2 = collections.Counter()
depth3 = collections.Counter()

for value in index.values():
    parts = value.split("/")
    if parts:
        segments[parts[0]] += 1
    if len(parts) > 1:
        depth2[parts[1]] += 1
    if len(parts) > 2:
        depth3[parts[2]] += 1

print("top level:", segments.most_common(8))
print("depth 2:", depth2.most_common(10))
print("depth 3:", depth3.most_common(10))

names = [
    "101LimitTapeBundle", "10LimitTapeBundleBig", "10LimitTapeBundleSmall", "3D",
    "Abyss", "Access", "Achievement", "Active", "Activity", "Activityvictoria",
    "Affected", "Agent", "AI", "Aim", "Alert", "Alight", "Allied", "Amillion",
    "Anbi", "Anchor", "Anton", "Aokaku", "Apps", "April", "Arcade", "Arcana", "Area",
]

by_name = collections.defaultdict(collections.Counter)
for value in index.values():
    parts = value.split("/")
    for i, part in enumerate(parts[:-1]):
        if part in names:
            by_name[part][i] += 1

print("\nname                      entries  depths (segment index -> count)")
for name in names:
    counters = by_name.get(name)
    total = sum(counters.values()) if counters else 0
    print(f"{name:<25} {total:>7,}  {dict(counters) if counters else '{}'}")

# Where do the names sit? Print one example path per name.
print("\nexamples:")
seen = set()
for value in index.values():
    parts = value.split("/")
    for i, part in enumerate(parts[:-1]):
        if part in names and part not in seen:
            seen.add(part)
            print(f"  {part:<22} {'/'.join(parts[:i + 2])}")
            break
