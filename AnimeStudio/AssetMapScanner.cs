using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimeStudio
{
    /// <summary>
    /// Streaming matcher for the compact v2 AssetMap (<see cref="AssetMapV2"/>) written under the
    /// game's output folder. v1 (raw MessagePack) maps are no longer read.
    ///
    /// A full map holds millions of entries; deserializing them into <see cref="AssetEntry"/>
    /// objects costs gigabytes of RAM, so this scanner never materialises the entries: it walks
    /// the format, skips string payloads and only converts the few fields that actually match.
    /// </summary>
    public static class AssetMapScanner
    {
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
            ulong PathId,
            ClassIDType Type,
            string Hash);

        public sealed class QueryResult
        {
            public long TotalEntries { get; set; }

            public long MatchedEntries { get; set; }

            /// <summary>Matches that sit in a bundle manifest, i.e. that have a container path.</summary>
            public long MatchedWithContainer { get; set; }

            public bool Truncated { get; set; }

            public TimeSpan Elapsed { get; set; }

            public List<EntryInfo> Items { get; } = [];

            public HashSet<string> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Asset Browser query: filters by name/container/source/type while streaming the map,
        /// returning at most <paramref name="limit"/> entries plus the real match count.
        /// <paramref name="requireContainer"/> drops entries with no container path (ZZZ's
        /// <c>resources.assets</c> textures and other built-ins), which otherwise fill the first
        /// page with rows that cannot be grouped or filtered by container.
        /// </summary>
        public static QueryResult Query(
            string path,
            ClassIDType[] typeFilter,
            Regex[] nameFilter,
            Regex[] containerFilter,
            Regex[] sourceFilter,
            int limit,
            bool requireContainer = false,
            bool requireResolved = false)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new QueryResult();
            var items = result.Items;
            var sources = result.Sources;
            long matched = 0;
            long withContainer = 0;
            var utf8 = Encoding.UTF8;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            ForEachEntry(stream, out _, out long totalEntries, (type, pathId, nameBuffer, nameLength, containerBuffer, containerLength, sourceBuffer, sourceLength, hashBuffer, hashLength) =>
            {
                if (!MatchesType((long)type, typeFilter))
                {
                    return;
                }

                if (containerLength > 0)
                {
                    if (requireResolved && !IsDisplayableContainer(utf8.GetString(containerBuffer, 0, containerLength)))
                    {
                        return;
                    }

                    if (!MatchesAnyContainer(containerFilter, containerBuffer, containerLength))
                    {
                        return;
                    }
                }
                else if (requireContainer || requireResolved || (containerFilter is { Length: > 0 }))
                {
                    // A container filter can never match "no container", and the UI can hide
                    // containerless rows entirely (ZZZ maps are a third built-in assets).
                    return;
                }

                if (!MatchesAnyRegex(nameFilter, nameBuffer, nameLength) ||
                    !MatchesAnyRegex(sourceFilter, sourceBuffer, sourceLength))
                {
                    return;
                }

                matched++;
                if (containerLength > 0)
                {
                    withContainer++;
                }

                if (sourceLength > 0)
                {
                    sources.Add(utf8.GetString(sourceBuffer, 0, sourceLength));
                }

                if (items.Count < limit)
                {
                    items.Add(new EntryInfo(
                        nameLength <= 0 ? string.Empty : utf8.GetString(nameBuffer, 0, nameLength),
                        containerLength <= 0 ? string.Empty : ResolveContainer(utf8.GetString(containerBuffer, 0, containerLength)),
                        sourceLength <= 0 ? string.Empty : utf8.GetString(sourceBuffer, 0, sourceLength),
                        unchecked((ulong)pathId),
                        type,
                        hashLength <= 0 ? string.Empty : utf8.GetString(hashBuffer, 0, hashLength)));
                }
            });

            result.TotalEntries = totalEntries;
            result.MatchedEntries = matched;
            result.MatchedWithContainer = withContainer;
            result.Truncated = matched > items.Count;
            result.Elapsed = stopwatch.Elapsed;
            return result;
        }

        /// <summary>True when the file is a compact (v2) map this scanner can read.</summary>
        public static bool IsRawAssetMap(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            return IsRawAssetMap(stream);
        }

        public static bool IsRawAssetMap(Stream stream)
        {
            long originalPosition = stream.CanSeek ? stream.Position : -1;
            Span<byte> magic = stackalloc byte[4];
            int read = stream.Read(magic);
            if (originalPosition >= 0)
            {
                stream.Position = originalPosition;
            }

            return read == 4 && magic.SequenceEqual(AssetMapV2.MagicBytes);
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

                if (!MatchesAnyContainer(containerFilter, containerBuffer, containerLength))
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
            if (!stream.CanSeek)
            {
                throw new InvalidOperationException("AssetMap streams must be seekable.");
            }

            stream.Position = 0;
            Span<byte> magic = stackalloc byte[4];
            int read = stream.Read(magic);
            stream.Position = 0;

            if (read != 4 || !magic.SequenceEqual(AssetMapV2.MagicBytes))
            {
                // v1 (MessagePack) maps are no longer supported: the tool always writes and
                // reads the compact v2 format under the output folder now.
                throw new InvalidDataException("只支持 v2 紧凑地图（ASM2），请重新建索引。");
            }

            ForEachEntryCompact(stream, out consumedBytes, out totalEntries, visitor);
        }

        /// <summary>Reads the compact v2 map (<see cref="AssetMapV2"/>), grouped by source file.</summary>
        private static void ForEachEntryCompact(Stream stream, out long consumedBytes, out long totalEntries, EntryVisitor visitor)
        {
            var reader = new BufferedStreamReader(stream);

            Span<byte> magic = stackalloc byte[4];
            reader.ReadBytes(magic);
            if (!magic.SequenceEqual(AssetMapV2.MagicBytes))
            {
                throw new InvalidDataException("Not a compact AssetMap.");
            }

            byte version = reader.ReadByte();
            if (version != AssetMapV2.Version)
            {
                throw new InvalidDataException($"Unsupported compact AssetMap version {version}.");
            }

            byte flags = reader.ReadByte();
            bool hasHash = (flags & AssetMapV2.FlagHasHash) != 0;
            _ = reader.ReadInt32();                  // gameType
            long entryCount = reader.ReadInt64();
            int fileCount = reader.ReadInt32();
            int containerCount = reader.ReadInt32();
            if (fileCount < 0 || containerCount < 0 || entryCount < 0)
            {
                throw new InvalidDataException("Corrupt compact AssetMap header.");
            }

            byte[][] sources = new byte[fileCount][];
            for (int i = 0; i < fileCount; i++)
            {
                sources[i] = reader.ReadStringBlock();
            }

            byte[][] containers = new byte[containerCount][];
            for (int i = 0; i < containerCount; i++)
            {
                containers[i] = reader.ReadStringBlock();
            }

            byte[] nameBuffer = new byte[256];
            byte[] hashBuffer = new byte[16];

            for (int fileIndex = 0; fileIndex < fileCount; fileIndex++)
            {
                long fileEntries = (long)reader.ReadVarUInt();
                byte[] source = sources[fileIndex];
                for (long i = 0; i < fileEntries; i++)
                {
                    int nameLength = reader.ReadStringInto(ref nameBuffer);
                    int containerId = (int)reader.ReadVarUInt();
                    int type = (int)reader.ReadVarUInt();
                    long pathId = (long)reader.ReadVarUInt();
                    _ = reader.ReadZigZag();                 // offset
                    int hashLength = -1;
                    if (hasHash)
                    {
                        FormatHash(reader.ReadUInt64(), hashBuffer);
                        hashLength = 16;
                    }

                    byte[] container = containerId > 0 && containerId <= containers.Length
                        ? containers[containerId - 1]
                        : Array.Empty<byte>();

                    visitor((ClassIDType)type, pathId, nameBuffer, nameLength, container, container.Length, source, source.Length, hashBuffer, hashLength);
                }
            }

            consumedBytes = reader.ConsumedBytes;
            totalEntries = entryCount;
        }

        private static void FormatHash(ulong value, byte[] destination)
        {
            const string digits = "0123456789abcdef";
            for (int i = 15; i >= 0; i--)
            {
                destination[i] = (byte)digits[(int)(value & 0xF)];
                value >>= 4;
            }
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

        /// <summary>Container filter that also matches the readable ZZZ path behind a hash id.</summary>
        private static bool MatchesAnyContainer(Regex[] filters, byte[] buffer, int length)
        {
            if (filters is null || filters.Length == 0)
            {
                return true;
            }

            string value = ResolveContainer(length <= 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, length));
            foreach (Regex filter in filters)
            {
                if (filter.IsMatch(value))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>ZZZ container ids are xxhash64 values; show the path when the index knows it.</summary>
        private static string ResolveContainer(string container)
        {
            if (container.Length == 0)
            {
                return container;
            }

            return ContainerIndex.Resolve(container) ?? container;
        }

        /// <summary>
        /// True when the container column already carries something displayable: a map built with
        /// the ZZZ hash index bakes in the resolved path, one built without it keeps the bare id,
        /// which resolves through the index at query time. About 29% of ZZZ image containers are
        /// hashes the index cannot resolve, and those are what "只看有路径的" hides.
        /// </summary>
        private static bool IsDisplayableContainer(string container)
        {
            if (container.Length == 0)
            {
                return false;
            }

            if (ContainerIndex.Resolve(container) is not null)
            {
                return true;
            }

            return !IsContainerHash(container);
        }

        /// <summary>A container id that never resolved: all digits, long enough to be a hash.</summary>
        private static bool IsContainerHash(string value)
        {
            if (value.Length < 10)
            {
                return false;
            }

            foreach (char character in value)
            {
                if (character is < '0' or > '9')
                {
                    return false;
                }
            }

            return true;
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

            public int ReadInt32()
            {
                Span<byte> buffer = stackalloc byte[4];
                ReadBytes(buffer);
                return BitConverter.ToInt32(buffer);
            }

            public long ReadInt64()
            {
                Span<byte> buffer = stackalloc byte[8];
                ReadBytes(buffer);
                return BitConverter.ToInt64(buffer);
            }

            public ulong ReadUInt64()
            {
                Span<byte> buffer = stackalloc byte[8];
                ReadBytes(buffer);
                return BitConverter.ToUInt64(buffer);
            }

            public ulong ReadVarUInt()
            {
                ulong value = 0;
                int shift = 0;
                while (true)
                {
                    byte current = ReadByte();
                    value |= (ulong)(current & 0x7F) << shift;
                    if ((current & 0x80) == 0)
                    {
                        return value;
                    }

                    shift += 7;
                    if (shift > 63)
                    {
                        throw new InvalidDataException("Corrupt compact AssetMap varint.");
                    }
                }
            }

            public long ReadZigZag()
            {
                ulong value = ReadVarUInt();
                return (long)(value >> 1) ^ -(long)(value & 1);
            }

            /// <summary>Reads a varint-length-prefixed UTF-8 block into a freshly allocated array.</summary>
            public byte[] ReadStringBlock()
            {
                int length = (int)ReadVarUInt();
                if (length <= 0)
                {
                    return Array.Empty<byte>();
                }

                if (length > MaxStringLength)
                {
                    throw new InvalidDataException($"Compact AssetMap string is too long ({length} bytes).");
                }

                byte[] buffer = new byte[length];
                ReadBytes(buffer);
                return buffer;
            }

            /// <summary>Reads a varint-length-prefixed UTF-8 block into a reusable buffer.</summary>
            public int ReadStringInto(ref byte[] buffer)
            {
                int length = (int)ReadVarUInt();
                if (length <= 0)
                {
                    return -1;
                }

                if (length > MaxStringLength)
                {
                    throw new InvalidDataException($"Compact AssetMap string is too long ({length} bytes).");
                }

                if (buffer.Length < length)
                {
                    buffer = new byte[Math.Max(length, buffer.Length * 2)];
                }

                ReadBytes(buffer.AsSpan(0, length));
                return length;
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
