#!/usr/bin/env python3
"""
catalog.py — 把 AnimeStudio CLI 的导出结果，整理成 wiki 能直接消费的结构化数据。

设计前提：
  * 解包/解密由 sidecar（AnimeStudio.CLI.exe）负责，本脚本不碰任何加密逻辑。
  * 输入 = 一个导出目录（--export_type convert/dump 的产物）+ 可选的多语言 TextMap。
  * 输出 = manifest.jsonl（逐条资产）、assets/（内容寻址的图片）、site/（站点用 JSON）。

子命令：
  ingest   扫描导出目录，产出 manifest.jsonl + 内容寻址副本
  textmap  合并多语言 TextMap（TextMapEN.json 之类）到 SQLite
  logstat  解析 CLI 日志，统计失败并落库（补上它退出码恒 0 的坑）
  build    从 manifest + textmap 构建 SQLite 编目
  diff     两个版本的编目比对，输出新增/变更/删除
  join     把资产里的哈希字段关联到 TextMap，产出 refs.jsonl + 覆盖率\n  export   导出站点用的 JSON + 图片

只依赖标准库；装了 Pillow 会自动用上（感知哈希、非 PNG 尺寸）。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import sqlite3
import sys
from datetime import datetime, timezone
from pathlib import Path

SCHEMA = """
CREATE TABLE IF NOT EXISTS meta(
  key TEXT PRIMARY KEY, value TEXT
);
CREATE TABLE IF NOT EXISTS run(
  id INTEGER PRIMARY KEY,
  game TEXT, version TEXT,
  started TEXT, finished TEXT,
  files_seen INTEGER, files_ok INTEGER, files_failed INTEGER,
  log_errors INTEGER, note TEXT
);
CREATE TABLE IF NOT EXISTS asset(
  id INTEGER PRIMARY KEY,
  game TEXT NOT NULL,
  version TEXT NOT NULL,
  kind TEXT NOT NULL,
  name TEXT NOT NULL,
  container TEXT,
  payload_path TEXT NOT NULL,
  sha256 TEXT NOT NULL,
  bytes INTEGER NOT NULL,
  width INTEGER, height INTEGER,
  mime TEXT,
  first_seen TEXT, last_seen TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_asset ON asset(game, version, kind, name);
CREATE INDEX IF NOT EXISTS ix_asset_sha ON asset(sha256);
CREATE INDEX IF NOT EXISTS ix_asset_container ON asset(game, container);
CREATE TABLE IF NOT EXISTS text(
  hash TEXT NOT NULL,
  locale TEXT NOT NULL,
  raw TEXT NOT NULL,
  primary key(hash, locale)
);
CREATE TABLE IF NOT EXISTS failure(
  id INTEGER PRIMARY KEY,
  game TEXT, version TEXT,
  raw TEXT NOT NULL,
  reason TEXT
);
"""

IMAGE_EXT = {".png", ".jpg", ".jpeg", ".webp", ".tga", ".bmp", ".exr", ".tif", ".tiff"}
TEXT_EXT = {".txt", ".json", ".xml", ".csv", ".bytes", ".yaml", ".yml", ".lua"}

# 导出目录里常见的类型前缀（AnimeStudio 的 ByType 分组产物），用来判 kind
KIND_HINTS = {
    "texture2d": "texture", "sprite": "sprite", "textasset": "textasset",
    "monobehaviour": "monobehaviour", "mesh": "mesh", "material": "material",
    "shader": "shader", "audioclip": "audio", "font": "font",
    "animationclip": "animation", "animatorcontroller": "animator",
}


# ---------------------------------------------------------------- helpers

def now() -> str:
    return datetime.now(timezone.utc).replace(microsecond=0).isoformat()


def sha256_file(path: Path, chunk: int = 1 << 20) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        while True:
            b = f.read(chunk)
            if not b:
                break
            h.update(b)
    return h.hexdigest()


def png_size(path: Path):
    """读 IHDR，不依赖 Pillow。"""
    try:
        with path.open("rb") as f:
            head = f.read(33)
        if head[:8] != b"\x89PNG\r\n\x1a\n" or head[12:16] != b"IHDR":
            return None
        w = int.from_bytes(head[16:20], "big")
        h = int.from_bytes(head[20:24], "big")
        return (w, h)
    except OSError:
        return None


def image_info(path: Path):
    size = png_size(path)
    if size:
        return size, "image/png"
    try:
        from PIL import Image  # type: ignore
        with Image.open(path) as im:
            fmt = (im.format or "").lower()
        return im.size, f"image/{fmt or path.suffix.lstrip('.').lower()}"
    except Exception:
        return None, None


def dhash(path: Path, size: int = 8):
    """感知哈希（可选依赖 Pillow）。返回 16 位十六进制。"""
    import warnings
    try:
        from PIL import Image  # type: ignore
    except Exception:
        return None
    try:
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            with Image.open(path) as im:
                im = im.convert("L").resize((size + 1, size))
                px = list(im.tobytes())
        bits = 0
        for y in range(size):
            for x in range(size):
                bits = (bits << 1) | (1 if px[y * (size + 1) + x] > px[y * (size + 1) + x + 1] else 0)
        return f"{bits:016x}"
    except Exception:
        return None


def guess_kind(rel: Path) -> str:
    parts = [p.lower() for p in rel.parts]
    for p in parts:
        base = re.split(r"[\\/._]", p)[0]
        if base in KIND_HINTS:
            return KIND_HINTS[base]
    ext = rel.suffix.lower()
    if ext in IMAGE_EXT:
        return "image"
    if ext in TEXT_EXT:
        return "textasset"
    return "other"


def container_of(rel: Path) -> str:
    """ByContainer 分组时，父目录就是容器路径；否则取上一级。"""
    parent = rel.parent.as_posix()
    return "" if parent in (".", "") else parent


# ---------------------------------------------------------------- ingest

def cmd_ingest(a) -> int:
    src = Path(a.dump).resolve()
    out = Path(a.out).resolve()
    if not src.is_dir():
        print(f"[ingest] not a directory: {src}", file=sys.stderr)
        return 2
    blobs = out / "assets"
    blobs.mkdir(parents=True, exist_ok=True)
    manifest = out / "manifest.jsonl"

    seen = ok = failed = 0
    with manifest.open("w", encoding="utf-8") as mf:
        for path in sorted(src.rglob("*")):
            if not path.is_file():
                continue
            seen += 1
            rel = path.relative_to(src)
            try:
                digest = sha256_file(path)
                size = path.stat().st_size
                kind = guess_kind(rel)
                w = h = None
                mime = None
                if kind in ("texture", "sprite", "image"):
                    (w, h), mime = image_info(path)
                blob_rel = Path("assets") / digest[:2] / digest[2:4] / (digest + path.suffix.lower())
                blob_abs = out / blob_rel
                if not blob_abs.exists():
                    blob_abs.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(path, blob_abs)
                rec = {
                    "game": a.game,
                    "version": a.version,
                    "kind": kind,
                    "name": path.stem,
                    "container": container_of(rel),
                    "source": rel.as_posix(),
                    "sha256": digest,
                    "bytes": size,
                    "width": w,
                    "height": h,
                    "mime": mime,
                    "blob": blob_rel.as_posix(),
                    "dhash": dhash(path) if a.phash and kind in ("texture", "sprite", "image") else None,
                }
                mf.write(json.dumps(rec, ensure_ascii=False) + "\n")
                ok += 1
            except Exception as exc:  # 单文件失败不能拖垮整批
                failed += 1
                mf.write(json.dumps({
                    "game": a.game, "version": a.version, "kind": "error",
                    "name": path.name, "source": rel.as_posix(), "error": repr(exc),
                }, ensure_ascii=False) + "\n")

    print(f"[ingest] files={seen} ok={ok} failed={failed} -> {manifest}")
    return 0 if failed == 0 else 1


# ---------------------------------------------------------------- textmap

def load_textmap(path: Path) -> dict:
    """TextMap 的 key 可能是十进制或十六进制字符串，统一成十进制字符串。"""
    raw = json.loads(path.read_text(encoding="utf-8"))
    out = {}
    for k, v in raw.items():
        kk = str(k)
        if kk.lower().startswith("0x"):
            kk = str(int(kk, 16))
        elif not kk.isdigit():
            kk = str(int(hashlib.md5(kk.encode()).hexdigest()[:8], 16))
        out[kk] = v if isinstance(v, str) else json.dumps(v, ensure_ascii=False)
    return out


def cmd_textmap(a) -> int:
    Path(a.out).mkdir(parents=True, exist_ok=True)
    total = 0
    for spec in a.map:
        if "=" not in spec:
            print(f"[textmap] expected locale=path, got {spec!r}", file=sys.stderr)
            return 2
        locale, p = spec.split("=", 1)
        data = load_textmap(Path(p))
        dst = Path(a.out) / f"text.{locale}.json"
        dst.write_text(json.dumps(data, ensure_ascii=False, indent=0), encoding="utf-8")
        total += len(data)
        print(f"[textmap] {locale}: {len(data)} entries -> {dst}")
    print(f"[textmap] total {total}")
    return 0


# ---------------------------------------------------------------- logstat

ERR_RE = re.compile(r"\b(error|exception|failed|cannot|unsupported)\b", re.I)


def cmd_logstat(a) -> int:
    p = Path(a.log)
    if not p.is_file():
        print(f"[logstat] missing {p}", file=sys.stderr)
        return 2
    errs, total = [], 0
    with p.open("r", encoding="utf-8", errors="replace") as f:
        for line in f:
            total += 1
            if ERR_RE.search(line):
                errs.append(line.rstrip()[:500])
    dst = Path(a.out)
    dst.parent.mkdir(parents=True, exist_ok=True)
    dst.write_text(json.dumps({"lines": total, "errors": len(errs), "sample": errs[:200]},
                              ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"[logstat] lines={total} errors={len(errs)} -> {dst}")
    # 非零退出码：CI 才能真正失败
    return 0 if len(errs) <= a.tolerance else 1


# ---------------------------------------------------------------- build

def cmd_build(a) -> int:
    db = Path(a.db)
    con = sqlite3.connect(db)
    con.executescript(SCHEMA)
    cur = con.cursor()
    cur.execute("INSERT OR REPLACE INTO meta(key,value) VALUES(?,?)", ("built_at", now()))
    for k, v in (("game", a.game), ("version", a.version)):
        cur.execute("INSERT OR REPLACE INTO meta(key,value) VALUES(?,?)", (k, v))

    n = seen_sha = 0
    with Path(a.manifest).open(encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            r = json.loads(line)
            if r.get("kind") == "error":
                cur.execute("INSERT INTO failure(game,version,raw,reason) VALUES(?,?,?,?)",
                            (r.get("game"), r.get("version"), r.get("source", ""), r.get("error")))
                continue
            cur.execute("""INSERT INTO asset(game,version,kind,name,container,payload_path,
                            sha256,bytes,width,height,mime,first_seen,last_seen)
                           VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?)
                           ON CONFLICT(game,version,kind,name) DO UPDATE SET
                             sha256=excluded.sha256, bytes=excluded.bytes,
                             width=excluded.width, height=excluded.height,
                             last_seen=excluded.last_seen""",
                        (r["game"], r["version"], r["kind"], r["name"], r.get("container"),
                         r["blob"], r["sha256"], r["bytes"], r.get("width"), r.get("height"),
                         r.get("mime"), now(), now()))
            n += 1
    # 内容去重统计：同一 sha 出现多次说明导出了重复内容
    cur.execute("SELECT count(*) FROM (SELECT sha256 FROM asset GROUP BY sha256 HAVING count(*)>1)")
    dup = cur.fetchone()[0]
    con.commit()
    print(f"[build] assets={n} duplicate_hashes={dup} -> {db}")
    return 0


# ---------------------------------------------------------------- diff

def cmd_diff(a) -> int:
    def load(p):
        con = sqlite3.connect(p)
        rows = con.execute("SELECT kind,name,container,sha256 FROM asset").fetchall()
        ver = dict(con.execute("SELECT key,value FROM meta").fetchall()).get("version", "?")
        return ver, {(k, n): (c, s) for k, n, c, s in rows}

    va, A = load(a.old)
    vb, B = load(a.new)
    added = sorted(set(B) - set(A))
    removed = sorted(set(A) - set(B))
    changed = sorted(k for k in set(A) & set(B) if A[k][1] != B[k][1])
    report = {
        "from": va, "to": vb,
        "added": [{"kind": k, "name": n} for k, n in added],
        "removed": [{"kind": k, "name": n} for k, n in removed],
        "changed": [{"kind": k, "name": n} for k, n in changed],
        "counts": {"added": len(added), "removed": len(removed), "changed": len(changed)},
    }
    if a.out:
        Path(a.out).parent.mkdir(parents=True, exist_ok=True)
        Path(a.out).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report["counts"], ensure_ascii=False))
    return 0


# ---------------------------------------------------------------- join

def _walk_json(node, path="$"):
    if isinstance(node, dict):
        for k, v in node.items():
            yield from _walk_json(v, f"{path}.{k}")
    elif isinstance(node, list):
        for i, v in enumerate(node):
            yield from _walk_json(v, f"{path}[{i}]")
    else:
        yield path, node


def _as_key(v):
    """TextMap 的 key 在导出里可能表现为整数，也可能是数字字符串。"""
    if isinstance(v, bool):
        return None
    if isinstance(v, int):
        return str(v) if 0 <= v < 2 ** 64 else None
    if isinstance(v, str) and v.isdigit() and 0 < len(v) <= 20:
        return v
    return None


def cmd_join(a) -> int:
    textdir = Path(a.text)
    keys, locales = set(), []
    for f in sorted(textdir.glob("text.*.json")):
        loc = f.name[len("text."):-len(".json")]
        locales.append(loc)
        keys |= set(json.loads(f.read_text(encoding="utf-8")).keys())
    if not keys:
        print(f"[join] no text.*.json under {textdir}", file=sys.stderr)
        return 2

    dump = Path(a.dump).resolve()
    refs, scanned, parsed = [], 0, 0
    for p in sorted(dump.rglob("*.json")):
        scanned += 1
        try:
            node = json.loads(p.read_text(encoding="utf-8", errors="replace"))
        except Exception:
            continue
        parsed += 1
        rel = p.relative_to(dump).as_posix()
        for jp, val in _walk_json(node):
            k = _as_key(val)
            if k is not None and k in keys:
                refs.append({"source": rel, "field": jp, "hash": k})

    out = Path(a.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    with out.open("w", encoding="utf-8") as f:
        for r in refs:
            f.write(json.dumps(r, ensure_ascii=False) + "\n")

    used = {r["hash"] for r in refs}
    stat = {
        "locales": locales,
        "text_keys": len(keys),
        "json_files_seen": scanned,
        "json_files_parsed": parsed,
        "refs": len(refs),
        "distinct_hashes_used": len(used),
        "coverage": round(len(used) / len(keys), 4) if keys else 0,
        "sources": len({r["source"] for r in refs}),
    }
    (out.parent / "join-stats.json").write_text(
        json.dumps(stat, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"[join] refs={stat['refs']} hashes_used={stat['distinct_hashes_used']}"
          f"/{stat['text_keys']} sources={stat['sources']} -> {out}")
    if parsed == 0:
        print("[join] 警告：没有解析到任何 JSON。若导出的是 TypeTree 文本 dump，"
              "需要先转成 JSON（见 README §7）", file=sys.stderr)
    return 0

# ---------------------------------------------------------------- export

def cmd_export(a) -> int:
    con = sqlite3.connect(a.db)
    con.row_factory = sqlite3.Row
    out = Path(a.out)
    (out / "img").mkdir(parents=True, exist_ok=True)

    assets = []
    for r in con.execute("SELECT * FROM asset ORDER BY kind, container, name"):
        if r["kind"] in ("texture", "sprite", "image"):
            dst = out / "img" / f"{r['sha256']}{Path(r['payload_path']).suffix}"
            if not dst.exists():
                src = Path(a.manifest_dir) / r["payload_path"]
                if src.exists():
                    shutil.copy2(src, dst)
            url = f"img/{dst.name}"
        else:
            url = None
        assets.append({
            "id": r["id"], "kind": r["kind"], "name": r["name"],
            "container": r["container"], "sha256": r["sha256"],
            "w": r["width"], "h": r["height"], "url": url,
        })

    (out / "assets.json").write_text(json.dumps(assets, ensure_ascii=False), encoding="utf-8")
    meta = dict(con.execute("SELECT key,value FROM meta").fetchall())
    (out / "versions.json").write_text(json.dumps(meta, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"[export] assets={len(assets)} -> {out}")
    return 0


# ---------------------------------------------------------------- main

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="AnimeStudio 导出结果 → wiki 结构化编目")
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("ingest", help="扫描导出目录，写 manifest.jsonl + 内容寻址副本")
    p.add_argument("--dump", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--game", required=True, choices=["hsr", "zzz", "gi"])
    p.add_argument("--version", required=True)
    p.add_argument("--phash", action="store_true", help="计算感知哈希（需要 Pillow）")
    p.set_defaults(func=cmd_ingest)

    p = sub.add_parser("textmap", help="合并多语言 TextMap")
    p.add_argument("--map", action="append", required=True, help="locale=path，可重复")
    p.add_argument("--out", required=True)
    p.set_defaults(func=cmd_textmap)

    p = sub.add_parser("logstat", help="解析 CLI 日志；有错就返回非零退出码")
    p.add_argument("--log", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--tolerance", type=int, default=0)
    p.set_defaults(func=cmd_logstat)

    p = sub.add_parser("build", help="manifest → SQLite")
    p.add_argument("--manifest", required=True)
    p.add_argument("--db", required=True)
    p.add_argument("--game", required=True)
    p.add_argument("--version", required=True)
    p.set_defaults(func=cmd_build)

    p = sub.add_parser("diff", help="两个版本比对")
    p.add_argument("--old", required=True)
    p.add_argument("--new", required=True)
    p.add_argument("--out")
    p.set_defaults(func=cmd_diff)

    p = sub.add_parser("join", help="把资产里的 hashy 字段关联到 TextMap")
    p.add_argument("--dump", required=True, help="导出目录（含 JSON 形式的 MonoBehaviour/TextAsset）")
    p.add_argument("--text", required=True, help="textmap 子命令的输出目录")
    p.add_argument("--out", required=True, help="产出 refs.jsonl")
    p.set_defaults(func=cmd_join)

    p = sub.add_parser("export", help="导出站点 JSON")
    p.add_argument("--db", required=True)
    p.add_argument("--manifest-dir", required=True)
    p.add_argument("--out", required=True)
    p.set_defaults(func=cmd_export)

    a = ap.parse_args(argv)
    return a.func(a)


if __name__ == "__main__":
    raise SystemExit(main())
