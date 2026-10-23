"""v1 (MessagePack) + v2 (compact) AssetMap reader and cross-format comparator."""
import struct, sys

MAGIC = b"ASM2"

def read_varint(data, pos):
    value = 0
    shift = 0
    while True:
        b = data[pos]; pos += 1
        value |= (b & 0x7F) << shift
        if not (b & 0x80):
            return value, pos
        shift += 7

def zigzag(u):
    return (u >> 1) ^ -(u & 1)

def read_string(data, pos):
    length, pos = read_varint(data, pos)
    return data[pos:pos + length].decode("utf-8", "replace"), pos + length

def read_v1_str(data, pos):
    tag = data[pos]; pos += 1
    if tag == 0xC0:
        return "", pos
    if 0xA0 <= tag <= 0xBF:
        length = tag & 0x1F
    elif tag == 0xD9:
        length = data[pos]; pos += 1
    elif tag == 0xDA:
        length = struct.unpack_from(">H", data, pos)[0]; pos += 2
    elif tag == 0xDB:
        length = struct.unpack_from(">I", data, pos)[0]; pos += 4
    else:
        raise SystemExit(f"bad str tag {tag:#x}")
    value = data[pos:pos + length].decode("utf-8", "replace")
    return value, pos + length

def read_v1_int(data, pos):
    tag = data[pos]; pos += 1
    if tag <= 0x7F:
        return tag, pos
    if tag >= 0xE0:
        return tag - 0x100, pos
    if tag == 0xCC:
        return data[pos], pos + 1
    if tag == 0xCD:
        return struct.unpack_from(">H", data, pos)[0], pos + 2
    if tag == 0xCE:
        return struct.unpack_from(">I", data, pos)[0], pos + 4
    if tag == 0xCF:
        return struct.unpack_from(">Q", data, pos)[0], pos + 8
    if tag == 0xD0:
        return struct.unpack_from(">b", data, pos)[0], pos + 1
    if tag == 0xD1:
        return struct.unpack_from(">h", data, pos)[0], pos + 2
    if tag == 0xD2:
        return struct.unpack_from(">i", data, pos)[0], pos + 4
    if tag == 0xD3:
        return struct.unpack_from(">q", data, pos)[0], pos + 8
    raise SystemExit(f"bad int tag {tag:#x}")

def as_signed(value):
    return value - (1 << 64) if value >= (1 << 63) else value

def read_v1_array(data, pos):
    tag = data[pos]; pos += 1
    if 0x90 <= tag <= 0x9F:
        return tag & 0x0F, pos
    if tag == 0xDC:
        return struct.unpack_from(">H", data, pos)[0], pos + 2
    if tag == 0xDD:
        return struct.unpack_from(">I", data, pos)[0], pos + 4
    raise SystemExit(f"bad array tag {tag:#x}")

def parse_v1(path):
    data = open(path, "rb").read()
    pos = 0
    _, pos = read_v1_array(data, pos)
    _, pos = read_v1_int(data, pos)
    count, pos = read_v1_array(data, pos)
    out = []
    for _ in range(count):
        tag = data[pos]; pos += 1
        if tag != 0x97:
            raise SystemExit(f"bad entry tag {tag:#x}")
        name, pos = read_v1_str(data, pos)
        container, pos = read_v1_str(data, pos)
        source, pos = read_v1_str(data, pos)
        path_id, pos = read_v1_int(data, pos)
        type_id, pos = read_v1_int(data, pos)
        hash_value, pos = read_v1_str(data, pos)
        offset, pos = read_v1_int(data, pos)
        out.append((name, container, source, type_id, path_id, offset, hash_value))
    return {"entries": count, "data": out}

def parse_v2(path):
    with open(path, "rb") as handle:
        data = handle.read()
    if data[:4] != MAGIC:
        raise SystemExit(f"{path}: not a compact map")
    pos = 4
    version = data[pos]; pos += 1
    flags = data[pos]; pos += 1
    has_hash = (flags & 1) != 0
    game, = struct.unpack_from("<i", data, pos); pos += 4
    entries, = struct.unpack_from("<q", data, pos); pos += 8
    files, = struct.unpack_from("<i", data, pos); pos += 4
    containers_count, = struct.unpack_from("<i", data, pos); pos += 4
    sources = []
    for _ in range(files):
        value, pos = read_string(data, pos)
        sources.append(value)
    containers = [""] * containers_count
    for i in range(containers_count):
        containers[i], pos = read_string(data, pos)
    out = []
    for file_index in range(files):
        count, pos = read_varint(data, pos)
        for _ in range(count):
            name, pos = read_string(data, pos)
            container_id, pos = read_varint(data, pos)
            type_id, pos = read_varint(data, pos)
            path_id, pos = read_varint(data, pos)
            path_id = as_signed(path_id)
            u, pos = read_varint(data, pos)
            offset = zigzag(u)
            hash_value = ""
            if has_hash:
                hash_value = struct.unpack_from("<Q", data, pos)[0]; pos += 8
            out.append((name, containers[container_id - 1] if container_id > 0 else "", sources[file_index], type_id, path_id, offset, hash_value))
    if pos != len(data):
        raise SystemExit(f"{path}: trailing bytes {len(data) - pos}")
    return {"game": game, "entries": entries, "files": files, "data": out, "has_hash": has_hash}

def canonical(parsed):
    """Entry identity without the hash column (v1 stores hex text, v2 stores the raw number)."""
    return sorted((e[0], e[1], e[2], e[3], e[4], e[5]) for e in parsed["data"])

def normalize_hash(value):
    if isinstance(value, int):
        return value
    if not value or value.startswith("size:"):
        return 0
    try:
        return int(value, 16)
    except ValueError:
        return 0

def hash_matches(a, b):
    left_hashes = sorted((e[0], e[2], e[3], e[4], normalize_hash(e[6])) for e in a["data"])
    right_hashes = sorted((e[0], e[2], e[3], e[4], normalize_hash(e[6])) for e in b["data"])
    return left_hashes == right_hashes

if __name__ == "__main__":
    mode = sys.argv[1]
    if mode == "v2":
        print(parse_v2(sys.argv[2])["entries"])
    elif mode == "v1v2":
        a = parse_v1(sys.argv[2])
        b = parse_v2(sys.argv[3])
        ca, cb = canonical(a), canonical(b)
        print({"v1_entries": a["entries"], "v2_entries": b["entries"], "same": ca == cb,
               "hash_same": hash_matches(a, b),
               "first_mismatch": next(((x, y) for x, y in zip(ca, cb) if x != y), None)})
    elif mode == "v2v2":
        a = parse_v2(sys.argv[2])
        b = parse_v2(sys.argv[3])
        print({"a": a["entries"], "b": b["entries"], "same": canonical(a) == canonical(b)})
