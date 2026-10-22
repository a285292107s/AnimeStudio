using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AnimeStudio.Application.TextMap;
using AnimeStudio.Application.ZzzText;

namespace AnimeStudio.CLI
{
    /// <summary>
    /// 绝区零文本提取：直接遍历设计数据块里的 TextAsset 载荷，在内存里解析 FlatBuffers，
    /// 不落盘、不需要先导出 1 GB 中间文件。
    /// </summary>
    internal static class ZzzTextCommand
    {
        public static int Run(string inputPath, string outputDirectory, string[] languages, Action<string>? log)
        {
            string dataDirectory = GameDataLocator.ResolveDataDirectory(inputPath, GameDataLocator.HasBlockData);
            List<string> sources = CollectSources(dataDirectory);

            log?.Invoke($"game data  : {dataDirectory}");
            log?.Invoke($"text source: {sources.Count} file(s)");

            if (sources.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No ZZZ design-data block found under '{dataDirectory}'.");
            }

            var extractor = new ZzzTextExtractor(new ZzzTextExtractOptions { Languages = languages });
            var manager = new AssetsManager
            {
                Game = Studio.Game,
                Silent = true,
                ResolveDependencies = false,
            };

            int done = 0;
            foreach (string file in sources)
            {
                done++;
                try
                {
                    manager.LoadFiles(new[] { file }, mergeSplitAssets: false);
                    foreach (SerializedFile assetsFile in manager.assetsFileList)
                    {
                        foreach (ObjectInfo objectInfo in assetsFile.m_Objects)
                        {
                            if (objectInfo.classID != (int)ClassIDType.TextAsset)
                            {
                                continue;
                            }

                            var reader = new ObjectReader(assetsFile.reader, assetsFile, objectInfo, manager.Game);
                            var textAsset = new TextAsset(reader);
                            extractor.AddPayload(file, textAsset.m_Script);
                        }
                    }
                }
                catch (Exception exception)
                {
                    log?.Invoke($"[skip] {Path.GetFileName(file)}: {exception.Message}");
                }
                finally
                {
                    manager.ClearLoadedAssets();
                }

                if (done % 5 == 0 || done == sources.Count)
                {
                    log?.Invoke($"[{done}/{sources.Count}] {Path.GetFileName(file)}  {extractor.Describe()}");
                }
            }

            IReadOnlyList<ZzzTextTable> tables = extractor.Build();
            if (tables.Count == 0)
            {
                log?.Invoke("no localization table found in this installation");
                return 4;
            }

            Directory.CreateDirectory(outputDirectory);
            foreach (ZzzTextTable table in tables)
            {
                string stem = $"zzz-text.{table.Locale}";
                string jsonPath = Path.Combine(outputDirectory, stem + ".json");
                string csvPath = Path.Combine(outputDirectory, stem + ".csv");

                ZzzTextWriter.WriteJson(jsonPath, table);
                ZzzTextWriter.WriteCsv(csvPath, table);

                log?.Invoke($"{table.Locale}: {table.EntryCount:N0} entries " +
                            $"(dropped {table.Duplicates:N0} duplicate key(s), {table.Payloads:N0} payload(s))");
                log?.Invoke($"wrote      : {jsonPath}");
                log?.Invoke($"wrote      : {csvPath}");
            }

            return 0;
        }

        private static List<string> CollectSources(string dataDirectory)
        {
            var sources = new List<string>();

            foreach (string root in new[]
                     {
                         Path.Combine(dataDirectory, "Persistent", "Blocks", "Data"),
                         Path.Combine(dataDirectory, "StreamingAssets", "Blocks", "Data"),
                     })
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                sources.AddRange(
                    Directory.GetFiles(root, "*.blk", SearchOption.TopDirectoryOnly)
                        .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase));
            }

            // The main serialized files also carry TextAssets (resources.assets …).
            if (Directory.Exists(dataDirectory))
            {
                sources.AddRange(
                    Directory.GetFiles(dataDirectory, "*.assets", SearchOption.TopDirectoryOnly)
                        .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase));
            }

            return sources
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
