using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimeStudio
{
    /// <summary>
    /// Streaming matcher for AssetMap files.
    ///
    /// The CLI writes an uncompressed MessagePack <c>AssetMap</c>:
    /// <code>
    /// [ GameType, [ AssetEntry x N ] ]
    /// AssetEntry = [ Name, Container, Source, PathID, Type, Hash, Offset ]
    /// </code>
    /// A full Honkai: Star Rail map has ~7.3M entries / ~2GB, and deserializing it into
    /// <see cref="AssetEntry"/> objects costs roughly 4GB of RAM. This scanner never
    /// materialises the entries: it walks the MessagePack headers, skips string payloads
    /// and only converts the few fields that actually match, so peak memory stays flat.
    /// </summary>
    public static class AssetMapScanner
    {
        /// <summary>Tag byte of a 2-element MessagePack array (raw, uncompressed map).</summary>
        private const byte FixArray2 = 0x92;

        private const int MaxStringLength = 16 * 1024 * 1024;

        public sealed class ScanResult
        {
            public long TotalEntries { get; internal set; }

            public long MatchedEntries { get; internal set; }

            public long BytesConsumed { get; internal set; }

            public TimeSpan Elapsed { get; internal set; }

            /// <summary>Distinct source container files (bundles/blocks) that hold a match.</summary>
            public HashSet<string> Sources { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Whole-map statistics gathered in one streaming pass.</summary>
        public sealed class MapStats
        {
            public long TotalEntries { get; init; }

            public long SourceFiles { get; init; }

            public required Dictionary<ClassIDType, long> TypeCounts { get; init; }

            public long BytesConsumed { get; init; }

            public TimeSpan Elapsed { get; init; }

            public long CountOf(ClassIDType type) => TypeCounts.TryGetValue(type, out long value) ? value : 0;
        }

        /// <summary>One matched map entry, materialised on demand for the Asset Browser.</summary>
        public sealed record EntryInfo(
            string Name,
            string Container,
            string Source,
            long PathId,
            ClassIDType Type,
            string Hash);

        public sealed class QueryResult
        {
            public long TotalEntries { get; set; }

            public long MatchedEntries { get; set; }

            public bool Truncated { get; set; }

            public TimeSpan Elapsed { get; set; }

            public List<EntryInfo> Items { get; } = [];

            public HashSet<string> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Asset Browser query: filters by name/container/source/type while streaming the map,
        /// returning at most <paramref name="limit"/> entries plus the real match count.
        /// </summary>
        public static QueryResult Query(
            string path,
            ClassIDType[] typeFilter,
            Regex[] nameFilter,
            Regex[] containerFilter,
            Regex[] sourceFilter,
            int limit)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new QueryResult();
            var items = result.Items;
            var sources = result.Sources;
            long matched = 0;
            var utf8 = Encoding.UTF8;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            ForEachEntry(stream, out _, out long totalEntries, (type, pathId, nameBuffer, nameLength, containerBuffer, containerLength, sourceBuffer, sourceLength, hashBuffer, hashLength) =>
            {
                if (!MatchesType((long)type, typeFilter))
                {
                    return;
                }

                if (!MatchesAnyRegex(nameFilter, nameBuffer, nameLength) ||
                    !MatchesAnyRegex(containerFilter, containerBuffer, containerLength) ||
                    !MatchesAnyRegex(sourceFilter, sourceBuffer, sourceLength))
                {
                    return;
                }

                matched++;
                if (sourceLength > 0)
                {
                    sources.Add(utf8.GetString(sourceBuffer, 0, sourceLength));
                }

                if (items.Count < limit)
                {
                    items.Add(new EntryInfo(
                        nameLength <= 0 ? string.Empty : utf8.GetString(nameBuffer, 0, nameLength),
                        containerLength <= 0 ? string.Empty : utf8.GetString(containerBuffer, 0, containerLength),
                        sourceLength <= 0 ? string.Empty : utf8.GetString(sourceBuffer, 0, sourceLength),
                        pathId,
                        type,
                        hashLength <= 0 ? string.Empty : utf8.GetString(hashBuffer, 0, hashLength)));
                }
            });

            result.TotalEntries = totalEntries;
            result.MatchedEntries = matched;
            result.Truncated = matched > items.Count;
            result.Elapsed = stopwatch.Elapsed;
            return result;
        }

        public static bool IsRawAssetMap(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            return IsRawAssetMap(stream);
        }

        public static bool IsRawAssetMap(Stream stream)
        {
            long originalPosition = stream.CanSeek ? stream.Position : -1;
            int first = stream.ReadByte();
            if (originalPosition >= 0)
            {
                stream.Position = originalPosition;
            }

            return first == FixArray2;
        }

        public static ScanResult SelectSources(string path, ClassIDType[] typeFilter, Regex[] nameFilter, Regex[] containerFilter, bool dedupeNames = false)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            return SelectSources(stream, typeFilter, nameFilter, containerFilter, dedupeNames);
        }

        public static ScanResult SelectSources(Stream stream, ClassIDType[] typeFilter, Regex[] nameFilter, Regex[] containerFilter, bool dedupeNames = false)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new ScanResult();
            var seenNames = dedupeNames ? new HashSet<string>(StringComparer.Ordinal) : null;

            ForEachEntry(stream, out long bytesConsumed, out _, (type, _pathId, nameBuffer, nameLength, containerBuffer, containerLength, sourceBuffer, sourceLength, _hashBuffer, _hashLength) =>
            {
                result.TotalEntries++;

                if (!MatchesType((long)type, typeFilter))
                {
                    return;
                }

                if (!MatchesAnyRegex(nameFilter, nameBuffer, nameLength))
                {
                    return;
                }

                if (!MatchesAnyRegex(containerFilter, containerBuffer, containerLength))
                {
                    return;
                }

                // Same name can exist in many blocks (SDK UI, per-scene atlases). Loading all of
                // them is slow and the exports collide, so --dedupe_names keeps the first only.
                if (seenNames is not null &&
                    !seenNames.Add($"{(long)type}\u0000{Encoding.UTF8.GetString(nameBuffer, 0, nameLength)}"))
                {
                    return;
                }

                result.MatchedEntries++;

                if (sourceLength > 0)
                {
                    result.Sources.Add(Encoding.UTF8.GetString(sourceBuffer, 0, sourceLength));
                }
            });

            result.BytesConsumed = bytesConsumed;
            result.Elapsed = stopwatch.Elapsed;
            return result;
        }

        /// <summary>
        /// Counts entries per class and how many source files the map spans in the same
        /// single streaming pass. Lets the host show real numbers instead of estimates.
        /// </summary>
        public static MapStats ScanStats(string path)
        {
            var stopwatch = Stopwatch.StartNew();
            var sources = new HashSet<ulong>();
            var counts = new Dictionary<ClassIDType, long>();
            long total = 0;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            ForEachEntry(stream, out long bytesConsumed, out _, (type, _pathId, _nameBuffer, _nameLength, _containerBuffer, _containerLength, sourceBuffer, sourceLength, _hashBuffer, _hashLength) =>
            {
                total++;
                counts[type] = counts.GetValueOrDefault(type) + 1;

                if (sourceLength > 0)
                {
                    // Hash instead of materialising 7M identical paths: we only need the count.
                    sources.Add(Hash64(new ReadOnlySpan<byte>(sourceBuffer, 0, sourceLength)));
                }
            });

            return new MapStats
            {
                TotalEntries = total,
                SourceFiles = sources.Count,
                TypeCounts = counts,
                BytesConsumed = bytesConsumed,
                Elapsed = stopwatch.Elapsed,
            };
        }

        private static void ForEachEntry(Stream stream, out long consumedBytes, out long totalEntries, EntryVisitor visitor)
        {
            var reader = new BufferedStreamReader(stream);

            byte rootTag = reader.ReadByte();
            if (rootTag != FixArray2)
            {
                throw new InvalidDataException(
                    $"Not a raw (uncompressed) AssetMap: first byte is 0x{rootTag:x2}, expected 0x{FixArray2:x2}.");
            }

            _ = BufferReadInteger(reader);          // GameType
            long entryCount = BufferReadArrayHeader(reader);

            byte[] nameBuffer = new byte[256];
            byte[] containerBuffer = new byte[256];
            byte[] sourceBuffer = new byte[256];
            byte[] hashBuffer = new byte[16];

            for (long index = 0; index < entryCount; index++)
            {
                byte entryTag = reader.ReadByte();
                if (entryTag != 0x97)
                {
                    throw new InvalidDataException(
                        $"Corrupt AssetMap: entry {index:N0} starts with 0x{entryTag:x2}, expected 0x97.");
                }

                int nameLength = BufferReadString(reader, ref nameBuffer);
                int containerLength = BufferReadString(reader, ref containerBuffer);
                int sourceLength = BufferReadString(reader, ref sourceBuffer);
                long pathId = BufferReadInteger(reader);          // PathID
                long type = BufferReadInteger(reader);
                int hashLength = BufferReadString(reader, ref hashBuffer);   // Hash (string or nil)
                _ = BufferReadInteger(reader);      // Offset

                visitor((ClassIDType)type, pathId, nameBuffer, nameLength, containerBuffer, containerLength, sourceBuffer, sourceLength, hashBuffer, hashLength);
            }

            consumedBytes = reader.ConsumedBytes;
            totalEntries = entryCount;
        }

        private delegate void EntryVisitor(
            ClassIDType type,
            long pathId,
            byte[] name,
            int nameLength,
            byte[] container,
            int containerLength,
            byte[] source,
            int sourceLength,
            byte[] hash,
            int hashLength);

        private static ulong Hash64(ReadOnlySpan<byte> data)
        {
            ulong hash = 1469598103934665603UL;
            foreach (byte value in data)
            {
                hash ^= value;
                hash *= 1099511628211UL;
            }

            return hash;
        }

        private static bool MatchesType(long type, ClassIDType[] typeFilter)
        {
            if (typeFilter is null || typeFilter.Length == 0)
            {
                return true;
            }

            foreach (ClassIDType candidate in typeFilter)
            {
                if ((long)candidate == type)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool MatchesAnyRegex(Regex[] filters, byte[] buffer, int length)
        {
            if (filters is null || filters.Length == 0)
            {
                return true;
            }

            string value = length <= 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, length);
            foreach (Regex filter in filters)
            {
                if (filter.IsMatch(value))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Reads one MessagePack string into a reusable buffer. Returns its length, or -1 for nil.</summary>
        private static int BufferReadString(BufferedStreamReader reader, ref byte[] buffer)
        {
            byte tag = reader.ReadByte();

            int length;
            if (tag == 0xC0)
            {
                return -1;
            }
            else if (tag <= 0xBF && tag >= 0xA0)
            {
                length = tag & 0x1F;
            }
            else
            {
                switch (tag)
                {
                    case 0xD9:
                        length = reader.ReadByte();
                        break;
                    case 0xDA:
                        length = (reader.ReadByte() << 8) | reader.ReadByte();
                        break;
                    case 0xDB:
                        length = (reader.ReadByte() << 24) | (reader.ReadByte() << 16) | (reader.ReadByte() << 8) | reader.ReadByte();
                        break;
                    default:
                        throw new InvalidDataException($"Unexpected MessagePack string tag 0x{tag:x2}.");
                }
            }

            if (length < 0 || length > MaxStringLength)
            {
                throw new InvalidDataException($"Unreasonable AssetMap string length {length}.");
            }

            if (buffer.Length < length)
            {
                buffer = new byte[Math.Max(length, buffer.Length * 2)];
            }

            reader.ReadBytes(buffer.AsSpan(0, length));
            return length;
        }

        private static long BufferReadArrayHeader(BufferedStreamReader reader)
        {
            byte tag = reader.ReadByte();

            if (tag >= 0x90 && tag <= 0x9F)
            {
                return tag & 0x0F;
            }

            switch (tag)
            {
                case 0xDC:
                    return (reader.ReadByte() << 8) | reader.ReadByte();
                case 0xDD:
                    return ((long)reader.ReadByte() << 24) | ((long)reader.ReadByte() << 16) |
                           ((long)reader.ReadByte() << 8) | reader.ReadByte();
                default:
                    throw new InvalidDataException($"Unexpected MessagePack array tag 0x{tag:x2}.");
            }
        }

        private static long BufferReadInteger(BufferedStreamReader reader)
        {
            byte tag = reader.ReadByte();

            if (tag <= 0x7F)
            {
                return tag;
            }

            if (tag >= 0xE0)
            {
                return (sbyte)tag;
            }

            switch (tag)
            {
                case 0xC0: return 0;                                            // nil => 0
                case 0xC2: return 0;                                            // false
                case 0xC3: return 1;                                            // true
                case 0xCC: return reader.ReadByte();
                case 0xCD: return (reader.ReadByte() << 8) | reader.ReadByte();
                case 0xCE: return ((long)reader.ReadByte() << 24) | ((long)reader.ReadByte() << 16) |
                                  ((long)reader.ReadByte() << 8) | reader.ReadByte();
                case 0xCF:
                    {
                        long value = 0;
                        for (int i = 0; i < 8; i++)
                        {
                            value = (value << 8) | reader.ReadByte();
                        }

                        return value;
                    }
                case 0xD0: return (sbyte)reader.ReadByte();
                case 0xD1: return (short)((reader.ReadByte() << 8) | reader.ReadByte());
                case 0xD2:
                    return (int)(((uint)reader.ReadByte() << 24) | ((uint)reader.ReadByte() << 16) |
                                 ((uint)reader.ReadByte() << 8) | reader.ReadByte());
                case 0xD3:
                    {
                        long value = 0;
                        for (int i = 0; i < 8; i++)
                        {
                            value = (value << 8) | reader.ReadByte();
                        }

                        return value;
                    }
                default:
                    throw new InvalidDataException($"Unexpected MessagePack integer tag 0x{tag:x2}.");
            }
        }

        private sealed class BufferedStreamReader
        {
            private readonly Stream _stream;
            private readonly byte[] _buffer;
            private int _position;
            private int _length;

            public BufferedStreamReader(Stream stream, int bufferSize = 1 << 20)
            {
                _stream = stream;
                _buffer = new byte[bufferSize];
            }

            public long ConsumedBytes { get; private set; }

            public byte ReadByte()
            {
                if (_position >= _length)
                {
                    Refill();
                }

                ConsumedBytes++;
                return _buffer[_position++];
            }

            public void ReadBytes(Span<byte> destination)
            {
                int written = 0;
                while (written < destination.Length)
                {
                    if (_position >= _length)
                    {
                        Refill();
                    }

                    int take = Math.Min(destination.Length - written, _length - _position);
                    _buffer.AsSpan(_position, take).CopyTo(destination.Slice(written));
                    _position += take;
                    written += take;
                    ConsumedBytes += take;
                }
            }

            private void Refill()
            {
                _length = _stream.Read(_buffer, 0, _buffer.Length);
                _position = 0;
                if (_length <= 0)
                {
                    throw new EndOfStreamException("Unexpected end of AssetMap file.");
                }
            }
        }
    }
}
