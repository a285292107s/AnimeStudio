"""Rank files by how much Chinese text they contain.

Used to locate ZZZ's localization table among tens of thousands of exported
TextAssets: the real text table is the one with tens of thousands of distinct
Chinese strings, everything else is config with a handful of editor comments.

Usage:
    python tools/scan_zzz_text.py <dir> [--top 20] [--min-chars 200]
"""

import os
import re
import sys

CJK = re.compile(r"[\u4e00-\u9fff]")


def scan_file(path):
    try:
        with open(path, "rb") as handle:
            data = handle.read()
    except OSError:
        return None

    # Text tables are UTF-8; decoding with replace is enough to count CJK shape.
    text = data.decode("utf-8", "ignore")
    characters = CJK.findall(text)
    if not characters:
        return None

    return len(characters), text


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    root = sys.argv[1]
    top = 20
    minimum = 200
    for index, argument in enumerate(sys.argv):
        if argument == "--top":
            top = int(sys.argv[index + 1])
        if argument == "--min-chars":
            minimum = int(sys.argv[index + 1])

    results = []
    files = 0
    for directory, _subdirs, names in os.walk(root):
        for name in names:
            files += 1
            path = os.path.join(directory, name)
            entry = scan_file(path)
            if entry is None:
                continue
            count, text = entry
            if count < minimum:
                continue
            results.append((count, path, text))

    results.sort(reverse=True)
    print(f"scanned {files:,} files, {len(results):,} with >= {minimum} CJK chars")
    print()

    for count, path, text in results[:top]:
        relative = os.path.relpath(path, root)
        print(f"{count:>10,} CJK  {os.path.getsize(path)/1024:>10,.0f} KB  {relative}")

        # Show a few CJK fragments so we can recognise the table.
        fragments = []
        for match in re.finditer(r"[\u4e00-\u9fff][^\x00]{1,40}", text):
            fragment = match.group()
            if any('\u4e00' <= ch <= '\u9fff' for ch in fragment) and fragment not in fragments:
                fragments.append(fragment)
            if len(fragments) >= 3:
                break
        for fragment in fragments:
            print(f"            {fragment[:60]!r}")
        print()


if __name__ == "__main__":
    main()
