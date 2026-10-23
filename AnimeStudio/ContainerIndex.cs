using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace AnimeStudio
{
    /// <summary>
    /// ZZZ stores container paths as xxhash64 values: the bundle manifest key is the hash, so a
    /// map built from a ZZZ install only knows numbers like <c>7444794748978933277</c>.
    /// <para>
    /// Eleiyas' Z3 asset index (https://github.com/Eleiyas/Z3-Asset-Index) rebuilds the
    /// hash → path table by scraping strings out of the game and brute-forcing combinations, so
    /// containers can be shown and exported by path like Star Rail's. It is a plain JSON object
    /// of <c>"&lt;decimal hash&gt;": "&lt;path&gt;"</c> and is ~80 MB, so the first load writes a
    /// compact binary cache next to it and later runs mmap that instead.
    /// </para>
    /// </summary>
    public static class ContainerIndex
    {
        public const string DefaultFileName = "Z3-AssetIndex-Eleiyas.json";

        private const string CacheMagic = "Z3IX";
        private const byte CacheVersion = 1;

        private static Dictionary<ulong, string>? _map;

        /// <summary>File the current table was loaded from (JSON or its cache), if any.</summary>
        public static string? LoadedFrom { get; private set; }

        public static int Count => _map?.Count ?? 0;

        public static bool IsLoaded => _map is not null;

        /// <summary>The loaded hash → path table (null when nothing is loaded).</summary>
        public static Dictionary<ulong, string>? Table => _map;

        /// <summary>Drops the loaded table (used when the user points the host at another file).</summary>
        public static void Unload()
        {
            _map = null;
            LoadedFrom = null;
        }

        /// <summary>
        /// Returns the container path for a numeric container id, or null when the hash is not in
        /// the index (about a third of ZZZ entries are still missing upstream).
        /// </summary>
        public static string? Resolve(string? container)
        {
            if (_map is null || string.IsNullOrEmpty(container))
            {
                return null;
            }

            // Container ids in a map are decimal strings of the raw ulong hash.
            if (!ulong.TryParse(container, NumberStyles.None, CultureInfo.InvariantCulture, out ulong hash))
            {
                return null;
            }

            return _map.TryGetValue(hash, out string? path) ? path : null;
        }

        /// <summary>Where the index is looked for when the user did not pick a file.</summary>
        public static IEnumerable<string> DefaultCandidates()
        {
            string local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AnimeStudio", "Maps", DefaultFileName);
            yield return local;

            string baseDirectory = AppContext.BaseDirectory;
            yield return Path.Combine(baseDirectory, "Maps", DefaultFileName);
            yield return Path.Combine(baseDirectory, "..", "Maps", DefaultFileName);
            yield return Path.Combine(Environment.CurrentDirectory, "Maps", DefaultFileName);
        }

        /// <summary>First existing candidate, or null.</summary>
        public static string? FindDefault()
        {
            foreach (string candidate in DefaultCandidates())
            {
                try
                {
                    string full = Path.GetFullPath(candidate);
                    if (File.Exists(full))
                    {
                        return full;
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // Ignore malformed candidate paths.
                }
            }

            return null;
        }

        /// <summary>Loads a JSON index (or its binary cache). Returns false and sets <paramref name="error"/> on failure.</summary>
        public static bool TryLoad(string jsonPath, out string error)
        {
            error = string.Empty;

            try
            {
                string full = Path.GetFullPath(jsonPath);
                if (!File.Exists(full))
                {
                    error = $"找不到容器索引：{full}";
                    return false;
                }

                string cachePath = full + ".idx";
                if (TryLoadCache(cachePath, full, out Dictionary<ulong, string> cached))
                {
                    _map = cached;
                    LoadedFrom = full;
                    return true;
                }

                Dictionary<ulong, string> map = ParseJson(full);
                _map = map;
                LoadedFrom = full;
                TryWriteCache(cachePath, map);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static Dictionary<ulong, string> ParseJson(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            var map = new Dictionary<ulong, string>(capacity: 800_000);
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw new InvalidDataException("容器索引不是 JSON 对象。");
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (!ulong.TryParse(reader.ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out ulong hash))
                {
                    reader.Read();      // skip the value
                    continue;
                }

                if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                {
                    continue;
                }

                map[hash] = reader.GetString() ?? string.Empty;
            }

            return map;
        }

        private static bool TryLoadCache(string cachePath, string jsonPath, out Dictionary<ulong, string> map)
        {
            map = null!;

            try
            {
                if (!File.Exists(cachePath) ||
                    File.GetLastWriteTimeUtc(cachePath) < File.GetLastWriteTimeUtc(jsonPath))
                {
                    return false;
                }

                using var stream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                Span<byte> magic = stackalloc byte[4];
                if (stream.Read(magic) != 4 || !magic.SequenceEqual(Encoding.ASCII.GetBytes(CacheMagic)))
                {
                    return false;
                }

                int version = stream.ReadByte();
                if (version != CacheVersion)
                {
                    return false;
                }

                Span<byte> int32 = stackalloc byte[4];
                stream.ReadExactly(int32);
                int count = BitConverter.ToInt32(int32);

                var loaded = new Dictionary<ulong, string>(capacity: Math.Max(16, count));
                var buffer = new byte[256];
                Span<byte> hashBytes = stackalloc byte[8];
                for (int i = 0; i < count; i++)
                {
                    stream.ReadExactly(hashBytes);
                    ulong hash = BitConverter.ToUInt64(hashBytes);

                    int length = ReadVarUInt(stream);
                    if (length <= 0)
                    {
                        loaded[hash] = string.Empty;
                        continue;
                    }

                    if (buffer.Length < length)
                    {
                        buffer = new byte[Math.Max(length, buffer.Length * 2)];
                    }

                    stream.ReadExactly(buffer.AsSpan(0, length));
                    loaded[hash] = Encoding.UTF8.GetString(buffer, 0, length);
                }

                map = loaded;
                return true;
            }
            catch (Exception exception) when (exception is IOException or EndOfStreamException or InvalidDataException)
            {
                return false;
            }
        }

        private static void TryWriteCache(string cachePath, Dictionary<ulong, string> map)
        {
            try
            {
                string temporary = cachePath + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    stream.Write(Encoding.ASCII.GetBytes(CacheMagic));
                    stream.WriteByte(CacheVersion);
                    Span<byte> int32 = stackalloc byte[4];
                    BitConverter.TryWriteBytes(int32, map.Count);
                    stream.Write(int32);

                    Span<byte> hashBytes = stackalloc byte[8];
                    foreach ((ulong hash, string path) in map)
                    {
                        BitConverter.TryWriteBytes(hashBytes, hash);
                        stream.Write(hashBytes);

                        byte[] utf8 = Encoding.UTF8.GetBytes(path);
                        WriteVarUInt(stream, (ulong)utf8.Length);
                        stream.Write(utf8);
                    }
                }

                File.Move(temporary, cachePath, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A read-only folder is fine: parsing 80 MB takes a few seconds, the cache is a bonus.
            }
        }

        private static int ReadVarUInt(Stream stream)
        {
            int value = 0;
            int shift = 0;
            while (true)
            {
                int current = stream.ReadByte();
                if (current < 0)
                {
                    throw new EndOfStreamException();
                }

                value |= (current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                {
                    return value;
                }

                shift += 7;
                if (shift > 28)
                {
                    throw new InvalidDataException("Corrupt container index cache.");
                }
            }
        }

        private static void WriteVarUInt(Stream stream, ulong value)
        {
            while (value >= 0x80)
            {
                stream.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }

            stream.WriteByte((byte)value);
        }
    }
}
