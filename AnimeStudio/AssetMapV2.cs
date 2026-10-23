using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AnimeStudio
{
    /// <summary>
    /// Compact AssetMap v2.
    ///
    /// Layout (little-endian fixed integers, LEB128 varints):
    /// <code>
    /// magic "ASM2" | version:byte | flags:byte | gameType:int32 | entryCount:int64 |
    /// fileCount:int32 | containerCount:int32
    /// sources   : fileCount     x (varint length + UTF-8 path)
    /// containers: containerCount x (varint length + UTF-8 path)
    /// files     : fileCount     x { varint entryCount, entryCount x entry }
    /// entry     : name(varint len + UTF-8), containerId(varint, 0 = none),
    ///             type(varint), pathId(varint), offset(zigzag varint),
    ///             hash(ulong, only when flags has <see cref="FlagHasHash"/>)
    /// </code>
    /// Grouping entries by source file removes the repeated absolute source path and lets the
    /// incremental builder reuse one file's entries without touching the others.
    /// </summary>
    public static class AssetMapV2
    {
        public static ReadOnlySpan<byte> MagicBytes => new byte[] { 0x41, 0x53, 0x4D, 0x32 };

        public const byte Version = 1;
        public const byte FlagHasHash = 1;

        /// <summary>Fixed header size: magic(4) + version(1) + flags(1) + game(4) + count(8) + files(4) + containers(4).</summary>
        public const int HeaderSize = 26;

        public static void WriteVarUInt(Stream stream, ulong value)
        {
            Span<byte> buffer = stackalloc byte[10];
            int length = 0;
            while (value >= 0x80)
            {
                buffer[length++] = (byte)(value | 0x80);
                value >>= 7;
            }

            buffer[length++] = (byte)value;
            stream.Write(buffer[..length]);
        }

        public static void WriteVarInt(Stream stream, long value) => WriteVarUInt(stream, ZigZag(value));

        public static void WriteString(Stream stream, string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                WriteVarUInt(stream, 0);
                return;
            }

            int byteCount = Encoding.UTF8.GetByteCount(value);
            WriteVarUInt(stream, (ulong)byteCount);
            if (byteCount == 0)
            {
                return;
            }

            byte[] buffer = Encoding.UTF8.GetBytes(value);
            stream.Write(buffer, 0, buffer.Length);
        }

        public static ulong ZigZag(long value) => (ulong)((value << 1) ^ (value >> 63));

        public static void WriteHeader(
            Stream stream,
            GameType gameType,
            bool hasHash,
            long entryCount,
            int fileCount,
            int containerCount)
        {
            stream.Write(MagicBytes);
            stream.WriteByte(Version);
            stream.WriteByte(hasHash ? FlagHasHash : (byte)0);
            WriteInt32(stream, (int)gameType);
            WriteInt64(stream, entryCount);
            WriteInt32(stream, fileCount);
            WriteInt32(stream, containerCount);
        }

        /// <summary>Encodes one entry. <paramref name="containerId"/> is 1-based (0 = empty).</summary>
        public static void WriteEntry(Stream stream, AssetEntry entry, int containerId, bool hasHash)
        {
            WriteString(stream, entry.Name);
            WriteVarUInt(stream, (ulong)Math.Max(0, containerId));
            WriteVarUInt(stream, (ulong)(int)entry.Type);
            WriteVarUInt(stream, (ulong)entry.PathID);
            WriteVarInt(stream, entry.Offset);
            if (hasHash)
            {
                WriteUInt64(stream, entry.Hash64);
            }
        }

        public static void WriteInt32(Stream stream, int value)
        {
            Span<byte> buffer = stackalloc byte[4];
            BitConverter.TryWriteBytes(buffer, value);
            stream.Write(buffer);
        }

        public static void WriteInt64(Stream stream, long value)
        {
            Span<byte> buffer = stackalloc byte[8];
            BitConverter.TryWriteBytes(buffer, value);
            stream.Write(buffer);
        }

        public static void WriteUInt64(Stream stream, ulong value)
        {
            Span<byte> buffer = stackalloc byte[8];
            BitConverter.TryWriteBytes(buffer, value);
            stream.Write(buffer);
        }

        /// <summary>Number of bytes a string would occupy on disk (varint length + UTF-8).</summary>
        public static int MeasureString(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return 1;
            }

            int length = Encoding.UTF8.GetByteCount(value);
            return MeasureVarUInt((ulong)length) + length;
        }

        public static int MeasureVarUInt(ulong value)
        {
            int length = 1;
            while (value >= 0x80)
            {
                value >>= 7;
                length++;
            }

            return length;
        }
    }

    /// <summary>
    /// Container interning table shared by the map builder. Ids are 1-based so 0 stays "no
    /// container"; appending ids never invalidates an entry chunk that was cached earlier.
    /// </summary>
    public sealed class ContainerTable
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
        private readonly List<string> _values = new();

        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _values.Count;
                }
            }
        }

        public int GetId(string? container)
        {
            if (string.IsNullOrEmpty(container))
            {
                return 0;
            }

            lock (_lock)
            {
                if (_ids.TryGetValue(container, out int existing))
                {
                    return existing;
                }

                _values.Add(container);
                int id = _values.Count;
                _ids[container] = id;
                return id;
            }
        }

        public List<string> Snapshot()
        {
            lock (_lock)
            {
                return new List<string>(_values);
            }
        }

        public void Load(IEnumerable<string> values)
        {
            lock (_lock)
            {
                _ids.Clear();
                _values.Clear();
                foreach (string value in values)
                {
                    if (string.IsNullOrEmpty(value) || _ids.ContainsKey(value))
                    {
                        continue;
                    }

                    _values.Add(value);
                    _ids[value] = _values.Count;
                }
            }
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            List<string> values = Snapshot();
            AssetMapV2.WriteInt32(stream, values.Count);
            foreach (string value in values)
            {
                AssetMapV2.WriteString(stream, value);
            }
        }

        public static List<string> LoadFile(string path)
        {
            var values = new List<string>();
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            Span<byte> buffer = stackalloc byte[4];
            if (stream.Read(buffer) != 4)
            {
                return values;
            }

            int count = BitConverter.ToInt32(buffer);
            for (int i = 0; i < count; i++)
            {
                int length = (int)ReadVarUInt(stream);
                if (length <= 0)
                {
                    values.Add(string.Empty);
                    continue;
                }

                byte[] bytes = new byte[length];
                ReadExactly(stream, bytes);
                values.Add(Encoding.UTF8.GetString(bytes));
            }

            return values;
        }

        public static ulong ReadVarUInt(Stream stream)
        {
            ulong value = 0;
            int shift = 0;
            while (true)
            {
                int current = stream.ReadByte();
                if (current < 0)
                {
                    throw new EndOfStreamException("Unexpected end of AssetMap data.");
                }

                value |= (ulong)(current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                {
                    return value;
                }

                shift += 7;
            }
        }

        public static void ReadExactly(Stream stream, byte[] buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer, total, buffer.Length - total);
                if (read <= 0)
                {
                    throw new EndOfStreamException("Unexpected end of AssetMap data.");
                }

                total += read;
            }
        }
    }
}
