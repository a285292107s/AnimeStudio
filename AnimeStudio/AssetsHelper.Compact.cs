using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AnimeStudio
{
    /// <summary>
    /// Compact (v2) map builder: per-file entry chunks + container table + optional incremental
    /// cache, with file-level parallelism. The legacy MessagePack writer stays in AssetsHelper.cs
    /// for the original GUI and for <c>--map_format messagepack</c>.
    /// </summary>
    public static partial class AssetsHelper
    {
        private const string CompactCacheMagic = "ASM2CACHE";
        private const int CompactCacheVersion = 1;

        private sealed class CompactCache
        {
            public GameType GameType;
            public bool HasHash;
            public long TotalEntries;
            public readonly Dictionary<string, CompactCacheFile> Files = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class CompactCacheFile
        {
            public long Length;
            public long MtimeUtcTicks;
            public long EntryCount;
            public string PartFile;
        }

        /// <summary>Builds the compact v2 AssetMap (incremental when no filters are set).</summary>
        private static void BuildAssetMapCompact(
            string[] input,
            string mapName,
            Game game,
            string savePath,
            ClassIDType[] typeFilters,
            Regex[] nameFilters,
            Regex[] containerFilters)
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
            Directory.CreateDirectory(savePath);

            var totalStopwatch = Stopwatch.StartNew();
            string mapPath = Path.Combine(savePath, $"{mapName}.map");
            bool hasFilters = !typeFilters.IsNullOrEmpty() || !nameFilters.IsNullOrEmpty() || !containerFilters.IsNullOrEmpty();
            bool useCache = !hasFilters;
            bool hasHash = !SkipHash;

            List<string> filesList = PrepareInputFiles(input);
            int fileCount = filesList.Count;
            if (fileCount == 0)
            {
                Logger.Warning("No input files to index.");
                return;
            }

            string cacheRoot = Path.Combine(savePath, ".cache", SanitizeFileComponent(mapName) + ".mapcache");
            string partsDir = useCache
                ? Path.Combine(cacheRoot, "parts")
                : Path.Combine(Path.GetTempPath(), $"animestudio-map-{Guid.NewGuid():N}");
            Directory.CreateDirectory(partsDir);

            if (Profile)
            {
                ResetProfileCounters();
            }

            lastGcAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);

            CompactCache cache = null;
            var containers = new ContainerTable();
            if (useCache && !ForceMapRebuild)
            {
                cache = LoadCompactCache(cacheRoot, game.Type, hasHash, filesList.Count, out List<string> containerValues, out string cacheProblem);
                if (cache != null)
                {
                    containers.Load(containerValues);
                    Logger.Info($"Incremental map cache: {cache.Files.Count:N0} file(s) known" +
                                (cacheProblem.Length > 0 ? $"，{cacheProblem}" : string.Empty));
                }
            }

            var counts = new long[fileCount];
            var partPaths = new string[fileCount];
            var pending = new List<int>(fileCount);
            long reusedEntries = 0;
            int reusedFiles = 0;
            long newestInputTicks = 0;
            bool cacheUsable = useCache && cache != null && File.Exists(mapPath) && IsCompactMapFile(mapPath);
            bool allCached = cacheUsable;

            for (int i = 0; i < fileCount; i++)
            {
                string file = filesList[i];
                (long length, long mtimeTicks) = FileFingerprint(file);
                if (mtimeTicks > newestInputTicks)
                {
                    newestInputTicks = mtimeTicks;
                }

                string partName = PartNameFor(file);
                partPaths[i] = Path.Combine(partsDir, partName);

                if (cache != null
                    && cache.Files.TryGetValue(file, out CompactCacheFile known)
                    && known.Length == length
                    && known.MtimeUtcTicks == mtimeTicks
                    && known.EntryCount >= 0
                    && string.Equals(known.PartFile, partName, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(partPaths[i]))
                {
                    counts[i] = known.EntryCount;
                    reusedEntries += known.EntryCount;
                    reusedFiles++;
                    continue;
                }

                allCached = false;
                pending.Add(i);
            }

            if (allCached && File.GetLastWriteTimeUtc(mapPath).Ticks >= newestInputTicks)
            {
                Logger.Info($"AssetMap is up to date: {reusedEntries:N0} assets from {reusedFiles:N0} unchanged file(s), " +
                            $"nothing to rebuild ({totalStopwatch.Elapsed.TotalSeconds:F1} s).");
                return;
            }

            Logger.Info($"Building AssetMap ({MapFormat.Compact}): {fileCount:N0} files, " +
                        $"{reusedFiles:N0} cached, {pending.Count:N0} to scan, {Math.Max(1, ThreadCount)} worker(s)...");

            long totalEntries = reusedEntries;
            Exception firstError = null;
            var abort = false;

            if (pending.Count > 0)
            {
                int workerCount = ThreadCount <= 1 ? 1 : Math.Min(ThreadCount, pending.Count);
                int nextSlot = -1;
                long completed = 0;
                long scannedEntries = 0;
                int lastLoggedPercent = -1;
                bool stringCacheWasEnabled = StringCache.Enabled;
                if (workerCount > 1)
                {
                    // The shared interning cache is not thread-safe and entries only live for one
                    // bundle, so workers skip it instead of paying for a lock on every entry.
                    StringCache.Enabled = false;
                }

                try
                {
                    void WorkerLoop()
                    {
                        AssetsManager manager = CreateBuildManager(game);
                        var batch = new List<AssetEntry>(256);
                        try
                        {
                            while (true)
                            {
                                if (tokenSource.IsCancellationRequested)
                                {
                                    throw new OperationCanceledException("AssetMap build cancelled.");
                                }

                                int slot = Interlocked.Increment(ref nextSlot);
                                if (slot >= pending.Count)
                                {
                                    break;
                                }

                                int index = pending[slot];
                                string file = filesList[index];
                                bool processed = ProcessCompactFile(manager, file, partPaths[index], batch, containers, hasHash,
                                    typeFilters, nameFilters, containerFilters, out long entryCount);
                                if (processed)
                                {
                                    counts[index] = entryCount;
                                    Interlocked.Add(ref scannedEntries, entryCount);
                                }

                                // One line per percent: the Host turns it into the progress bar and
                                // the log stays short even for 11k+ input files.
                                long done = Interlocked.Increment(ref completed);
                                int percentNow = (int)(done * 100 / pending.Count);
                                int previousPercent = Volatile.Read(ref lastLoggedPercent);
                                if (percentNow > previousPercent &&
                                    Interlocked.CompareExchange(ref lastLoggedPercent, percentNow, previousPercent) == previousPercent)
                                {
                                    Logger.Info($"[{done}/{pending.Count}] {Path.GetFileName(file)}");
                                }
                                else
                                {
                                    Logger.Verbose($"[{done}/{pending.Count}] Processed {Path.GetFileName(file)}");
                                }
                            }
                        }
                        finally
                        {
                            manager.Clear();
                        }
                    }

                    if (workerCount == 1)
                    {
                        WorkerLoop();
                    }
                    else
                    {
                        var threads = new Thread[workerCount];
                        for (int i = 0; i < workerCount; i++)
                        {
                            threads[i] = new Thread(WorkerLoop) { IsBackground = true, Name = $"map-worker-{i}" };
                            threads[i].Start();
                        }

                        foreach (Thread thread in threads)
                        {
                            thread.Join();
                        }
                    }
                }
                catch (Exception exception)
                {
                    firstError = exception;
                    abort = true;
                }
                finally
                {
                    StringCache.Enabled = stringCacheWasEnabled;
                }

                if (firstError != null)
                {
                    CleanupParts(partsDir, partPaths, useCache);
                    throw new InvalidOperationException($"AssetMap build failed: {firstError.Message}", firstError);
                }

                totalEntries += Interlocked.Read(ref scannedEntries);
            }

            if (abort)
            {
                return;
            }

            // Assemble the map: fixed header, source list, per-file counts and entries.
            Logger.Info($"Assembling AssetMap: {fileCount:N0} files, {totalEntries:N0} entries...");
            string partialPath = mapPath + ".partial";
            try
            {
                using (var output = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan))
                {
                    List<string> containerSnapshot = containers.Snapshot();
                    AssetMapV2.WriteHeader(output, game.Type, hasHash, totalEntries, fileCount, containerSnapshot.Count);
                    foreach (string source in filesList)
                    {
                        AssetMapV2.WriteString(output, source);
                    }

                    foreach (string container in containerSnapshot)
                    {
                        AssetMapV2.WriteString(output, container);
                    }

                    var buffer = new byte[1 << 20];
                    Span<byte> countBytes = stackalloc byte[8];
                    for (int i = 0; i < fileCount; i++)
                    {
                        using var part = new FileStream(partPaths[i], FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
                        if (part.Read(countBytes) != 8)
                        {
                            throw new InvalidDataException($"Map part {partPaths[i]} is truncated.");
                        }

                        long partCount = BitConverter.ToInt64(countBytes);
                        if (partCount != counts[i])
                        {
                            throw new InvalidDataException($"Map part {partPaths[i]} reports {partCount} entries, expected {counts[i]}.");
                        }

                        AssetMapV2.WriteVarUInt(output, (ulong)counts[i]);
                        int read;
                        while ((read = part.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            output.Write(buffer, 0, read);
                        }
                    }
                }

                File.Move(partialPath, mapPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(partialPath))
                {
                    try { File.Delete(partialPath); } catch { /* best-effort */ }
                }
            }

            long mapBytes = new FileInfo(mapPath).Length;
            Logger.Info($"Finished building AssetMap with {totalEntries:N0} assets " +
                        $"({mapBytes / (1024.0 * 1024.0):N1} MB, {totalStopwatch.Elapsed.TotalSeconds:F1} s).");
            LogProfileBreakdown(totalEntries);

            if (useCache)
            {
                try
                {
                    containers.Save(Path.Combine(cacheRoot, "table.bin"));
                    SaveCompactCache(cacheRoot, cache, game.Type, hasHash, totalEntries, filesList, counts, partPaths);
                    Logger.Info($"Incremental cache updated: {reusedFiles:N0} reused, {pending.Count:N0} scanned, " +
                                $"cache at {cacheRoot}");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Logger.Warning($"Could not update the map cache: {exception.Message}");
                }
            }
            else
            {
                try { Directory.Delete(partsDir, recursive: true); } catch { /* best-effort */ }
                Logger.Info("Type/name/container filters active: incremental cache is disabled for this build.");
            }
        }

        private static bool ProcessCompactFile(
            AssetsManager manager,
            string file,
            string partPath,
            List<AssetEntry> batch,
            ContainerTable containers,
            bool hasHash,
            ClassIDType[] typeFilters,
            Regex[] nameFilters,
            Regex[] containerFilters,
            out long entryCount)
        {
            entryCount = 0;
            long localEntryCount = 0;
            FileStream part = null;
            try
            {
                part = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan);
                part.Write(new byte[8]); // entry count placeholder

                ProcessFile(manager, file, _ =>
                {
                    batch.Clear();
                    BuildAssetMap(manager, file, batch, typeFilters, nameFilters, containerFilters);
                    foreach (AssetEntry entry in batch)
                    {
                        long serializationStart = Profile ? Stopwatch.GetTimestamp() : 0;
                        AssetMapV2.WriteEntry(part, entry, containers.GetId(entry.Container), hasHash);
                        if (Profile)
                        {
                            Interlocked.Add(ref profileSerializeTicks, Stopwatch.GetTimestamp() - serializationStart);
                        }

                        localEntryCount++;
                    }
                });

                entryCount = localEntryCount;
                part.Position = 0;
                Span<byte> countBytes = stackalloc byte[8];
                BitConverter.TryWriteBytes(countBytes, entryCount);
                part.Write(countBytes);
                part.Flush();
                part.Dispose();
                part = null;
                return true;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Logger.Warning($"Skipping {Path.GetFileName(file)}: {exception.Message}");

                // Keep the file in the map with zero entries so assembly can still stream it.
                try
                {
                    part?.Dispose();
                    part = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 16);
                    part.Write(new byte[8]);
                    part.Dispose();
                    part = null;
                }
                catch (Exception writeException) when (writeException is IOException or UnauthorizedAccessException)
                {
                    // Assembly reports the missing part; nothing else to do here.
                }

                return false;
            }
            finally
            {
                if (part != null)
                {
                    part.Dispose();
                    try { File.Delete(partPath); } catch { /* best-effort */ }
                }
            }
        }

        private static AssetsManager CreateBuildManager(Game game) => new()
        {
            Silent = true,
            SkipProcess = true,
            ResolveDependencies = false,
            Game = game,
            SpecifyUnityVersion = assetsManager.SpecifyUnityVersion,
        };

        private static (long Length, long MtimeUtcTicks) FileFingerprint(string file)
        {
            try
            {
                var info = new FileInfo(file);
                return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : (-1, 0);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return (-1, 0);
            }
        }

        private static string PartNameFor(string file)
        {
            byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(file.ToLowerInvariant()));
            return Convert.ToHexString(hash)[..20].ToLowerInvariant() + ".bin";
        }

        private static string SanitizeFileComponent(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }

            return string.IsNullOrWhiteSpace(value) ? "map" : value;
        }

        private static bool IsCompactMapFile(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16);
                Span<byte> magic = stackalloc byte[4];
                return stream.Read(magic) == 4 && magic.SequenceEqual(AssetMapV2.MagicBytes);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /* ---------- incremental cache ---------- */

        private static CompactCache LoadCompactCache(
            string cacheRoot,
            GameType gameType,
            bool hasHash,
            int expectedFiles,
            out List<string> containerValues,
            out string problem)
        {
            containerValues = new List<string>();
            problem = string.Empty;
            string metaPath = Path.Combine(cacheRoot, "meta.bin");
            string tablePath = Path.Combine(cacheRoot, "table.bin");
            if (!File.Exists(metaPath) || !File.Exists(tablePath))
            {
                return null;
            }

            try
            {
                using var reader = new BinaryReader(File.OpenRead(metaPath), Encoding.UTF8, leaveOpen: false);
                string magic = reader.ReadString();
                int version = reader.ReadInt32();
                if (magic != CompactCacheMagic || version != CompactCacheVersion)
                {
                    return null;
                }

                var cache = new CompactCache
                {
                    GameType = (GameType)reader.ReadInt32(),
                    HasHash = reader.ReadBoolean(),
                    TotalEntries = reader.ReadInt64(),
                };
                int fileCount = reader.ReadInt32();
                for (int i = 0; i < fileCount; i++)
                {
                    var known = new CompactCacheFile
                    {
                        Length = reader.ReadInt64(),
                        MtimeUtcTicks = reader.ReadInt64(),
                        EntryCount = reader.ReadInt64(),
                    };
                    string path = reader.ReadString();
                    known.PartFile = reader.ReadString();
                    cache.Files[path] = known;
                }

                if (cache.GameType != gameType || cache.HasHash != hasHash)
                {
                    return null;
                }

                containerValues = ContainerTable.LoadFile(tablePath);
                return cache;
            }
            catch (Exception exception) when (exception is IOException or EndOfStreamException or UnauthorizedAccessException)
            {
                problem = "cache ignored: " + exception.Message;
                return null;
            }
        }

        private static void SaveCompactCache(
            string cacheRoot,
            CompactCache previous,
            GameType gameType,
            bool hasHash,
            long totalEntries,
            List<string> filesList,
            long[] counts,
            string[] partPaths)
        {
            Directory.CreateDirectory(cacheRoot);
            string metaPath = Path.Combine(cacheRoot, "meta.bin");
            string tempPath = metaPath + ".tmp";
            using (var writer = new BinaryWriter(File.Create(tempPath), Encoding.UTF8))
            {
                writer.Write(CompactCacheMagic);
                writer.Write(CompactCacheVersion);
                writer.Write((int)gameType);
                writer.Write(hasHash);
                writer.Write(totalEntries);
                writer.Write(filesList.Count);
                for (int i = 0; i < filesList.Count; i++)
                {
                    (long length, long mtimeTicks) = FileFingerprint(filesList[i]);
                    writer.Write(length);
                    writer.Write(mtimeTicks);
                    writer.Write(counts[i]);
                    writer.Write(filesList[i]);
                    writer.Write(Path.GetFileName(partPaths[i]));
                }
            }

            File.Move(tempPath, metaPath, overwrite: true);

            // Drop parts that no longer belong to any input file (patch removed a bundle).
            var live = new HashSet<string>(partPaths.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            string partsDir = Path.Combine(cacheRoot, "parts");
            if (Directory.Exists(partsDir))
            {
                foreach (string stale in Directory.EnumerateFiles(partsDir, "*.bin"))
                {
                    if (!live.Contains(Path.GetFileName(stale)))
                    {
                        try { File.Delete(stale); } catch { /* best-effort */ }
                    }
                }
            }
        }

        private static void CleanupParts(string partsDir, string[] partPaths, bool useCache)
        {
            if (useCache)
            {
                return; // keep valid cached parts; the next build validates them
            }

            try { Directory.Delete(partsDir, recursive: true); } catch { /* best-effort */ }
        }

        private static void ResetProfileCounters()
        {
            profileLoadTicks = 0;
            profileProcessTicks = 0;
            profileHashTicks = 0;
            profileSerializeTicks = 0;
            profileBundleCallbacks = 0;
            profileStartAllocated = GC.GetTotalAllocatedBytes(precise: false);
            profileStartGen2 = GC.CollectionCount(2);
        }
    }
}
