"""For each folder name in the repo screenshot, show where it sits inside the Z3 index paths."""

import collections
import json
import sys

path = sys.argv[1]
with open(path, encoding="utf-8") as fh:
    index = json.load(fh)

names = [
    "101LimitTapeBundle", "10LimitTapeBundleBig", "10LimitTapeBundleSmall", "3D",
    "Abyss", "Access", "Achievement", "Active", "Activity", "Activityvictoria",
    "Affected", "Agent", "AI", "Aim", "Alert", "Alight", "Allied", "Amillion",
    "Anbi", "Anchor", "Anton", "Aokaku", "Apps", "April", "Arcade", "Arcana", "Area",
]

# For every path, record the segment right before and after each known name.
contexts = collections.defaultdict(collections.Counter)
after = collections.defaultdict(collections.Counter)
examples = collections.defaultdict(list)

for value in index.values():
    parts = value.split("/")
    for i, part in enumerate(parts[:-1]):
        if part in names:
            prefix = "/".join(parts[:i])
            contexts[part][prefix] += 1
            if i + 1 < len(parts) and len(examples[part]) < 2:
                examples[part].append(value)

print(f"{'name':<24} {'entries':>8}  most common prefixes")
for name in names:
    total = sum(contexts[name].values()) if contexts[name] else 0
    top = contexts[name].most_common(3)
    joined = "; ".join(f"{prefix or '(root)'} ×{count:,}" for prefix, count in top)
    print(f"{name:<24} {total:>8,}  {joined}")

print("\nexamples:")
for name in names:
    if examples[name]:
        print(f"  {name}: {examples[name][0]}")
