"""Extract ZZZ's key -> localized-text pairs from exported TextAssets.

ZZZ stores localisation as FlatBuffers tables, not as a hash->string TextMap like
HSR. Strings are stored the FlatBuffers way (u32 length, UTF-8 bytes, NUL), and
each entry appears as an adjacent pair:

    <localized text> <key>            e.g. 抱歉，当前权限设置…   XboxAlert_UGC

The language is not written anywhere; the same feature table exists once per
language with different asset ids, so we classify each string by script:

    has kana            -> ja
    simplified-only set  -> zh-CN
    traditional-only set -> zh-TW
    otherwise           -> skip (English and other latin locales)

Usage:
    python tools/extract_zzz_text.py <export-root> --out <dir>
"""

import argparse
import json
import os
import re
import struct
import sys
from collections import Counter

STRING_RE = re.compile(rb"(?:[\x20-\x7e]|[\xc2-\xf4][\x80-\xbf]+){2,300}")
KEY_RE = re.compile(r"^[A-Za-z_][A-Za-z0-9_.\[\]#/:\- ]{0,120}$")

KANA_RE = re.compile(r"[\u3040-\u30ff]")
CJK_RE = re.compile(r"[\u4e00-\u9fff]")
SIMPLIFIED_ONLY = set("权该设观说时换来对经过还为个门问题真实发现认识说话应当问题")
TRADITIONAL_ONLY = set("權該設觀說時換來對經過還為個門問題真實發現認識說話應當問題")


def classify(text):
    if KANA_RE.search(text):
        return "ja"
    if not CJK_RE.search(text):
        return None
    simplified = sum(1 for ch in text if ch in SIMPLIFIED_ONLY)
    traditional = sum(1 for ch in text if ch in TRADITIONAL_ONLY)
    if simplified > traditional:
        return "zh-CN"
    if traditional > simplified:
        return "zh-TW"
    return None


def flatbuffer_strings(data):
    """Yield (offset, text) for plausible FlatBuffers strings."""
    for match in STRING_RE.finditer(data):
        start = match.start()
        if start < 4:
            continue
        length_bytes = data[start - 4:start]
        length = struct.unpack("<I", length_bytes)[0]
        if length != match.end() - start:
            continue
        if match.end() >= len(data) or data[match.end()] != 0:
            continue
        try:
            text = match.group().decode("utf-8")
        except UnicodeDecodeError:
            continue
        if any(ord(ch) < 0x20 for ch in text):
            continue
        yield start, text


def process(path, buckets, stats):
    try:
        with open(path, "rb") as handle:
            data = handle.read()
    except OSError:
        return

    strings = list(flatbuffer_strings(data))
    if not strings:
        return

    for index in range(len(strings) - 1):
        offset, text = strings[index]
        _, following = strings[index + 1]
        if not KEY_RE.match(following) or len(following) < 3:
            continue
        language = classify(text)
        if language is None:
            continue

        # Only keep the pair when key and text are close (same FlatBuffers table row).
        next_offset = strings[index + 1][0]
        if next_offset - offset > len(text.encode("utf-8")) + 256:
            continue

        buckets[language].setdefault(following, text)
        stats[language] += 1


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    parser = argparse.ArgumentParser()
    parser.add_argument("root")
    parser.add_argument("--out", default="")
    args = parser.parse_args()

    buckets = {language: {} for language in ("zh-CN", "zh-TW", "ja")}
    stats = Counter()
    files = 0

    for directory, _subdirs, names in os.walk(args.root):
        for name in names:
            files += 1
            process(os.path.join(directory, name), buckets, stats)
        if files % 20000 == 0:
            print(f"  scanned {files:,} files ...")

    print(f"\nscanned {files:,} files")
    for language, table in buckets.items():
        print(f"  {language}: {len(table):,} entries ({stats[language]:,} pairs seen)")

    for language, table in buckets.items():
        sample = list(table.items())[:6]
        print(f"\n{language} sample:")
        for key, value in sample:
            print(f"  {key}  ->  {value[:60]!r}")

    if args.out:
        os.makedirs(args.out, exist_ok=True)
        for language, table in buckets.items():
            if not table:
                continue
            path = os.path.join(args.out, f"zzz-text.{language}.json")
            with open(path, "w", encoding="utf-8") as handle:
                json.dump(table, handle, ensure_ascii=False, indent=2)
            print(f"\nwrote {path}  ({len(table):,} entries)")


if __name__ == "__main__":
    main()
