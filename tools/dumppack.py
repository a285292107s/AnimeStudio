"""Hexdump helper for the HSR DesignData text pack (reverse-engineering aid)."""

import mmap
import sys
from collections import Counter


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    path = sys.argv[1]
    f = open(path, "rb")
    mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)

    def dump(a, b):
        for i in range(a, b, 16):
            chunk = mm[i:i + 16]
            hx = " ".join(f"{c:02x}" for c in chunk)
            print(f"  {i:08x}  {hx:<47}  {chunk.decode('utf-8', 'replace')}")

    print("== 0x0000..0x0060 ==")
    dump(0, 0x60)
    print("\n== 0x01a0..0x0260 (around the failing tag) ==")
    dump(0x1a0, 0x260)

    for probe in ("攻击力", "网络连接中断"):
        blob = probe.encode()
        off = mm.find(blob)
        print(f"\n== first {probe!r} at 0x{off:x} ==")
        if off > 0:
            dump(max(0, off - 48), off + 48)

    print("\n== tag candidates: bytes that immediately follow a CJK run ==")
    # crude: find all 0x03 / 0x07 occurrences and show the byte histogram of
    # the 512 bytes before each of the first few
    tags = Counter()
    pos = mm.find(b"\x03")
    n = 0
    while pos >= 0 and n < 5000:
        tags[mm[pos]] += 1
        n += 1
        pos = mm.find(b"\x03", pos + 1)
    print(f"  0x03 occurrences scanned: {n}")


if __name__ == "__main__":
    main()
