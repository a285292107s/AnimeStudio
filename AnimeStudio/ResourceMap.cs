using MessagePack;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace AnimeStudio
{
    public static class ResourceMap
    {
        private static AssetMap Instance = new() { GameType = GameType.Normal, AssetEntries = new List<AssetEntry>() };
        public static List<AssetEntry> GetEntries() => Instance.AssetEntries;
        public static GameType GetGameType() => Instance.GameType;

        public static List<String> GetTypes()
        {
            var types = new List<String>();
            foreach (var entry in Instance.AssetEntries)
            {
                if (!types.Contains(entry.Type.ToString()))
                {
                    types.Add(entry.Type.ToString());
                }
            }
            return types;
        }
        public static int FromFile(string path)
        {
            if (!string.IsNullOrEmpty(path))
            {
                Logger.Info(string.Format("Parsing...."));
                try
                {
                    var extension = Path.GetExtension(path).ToLower();
                    using var stream = File.OpenRead(path);
                    
                    if (extension == ".map")
                    {
                        // The Host/CLI default writer produces a compact v2 map that this legacy
                        // Asset Browser cannot materialise; report it instead of a parse error.
                        Span<byte> magic = stackalloc byte[4];
                        int read = stream.Read(magic);
                        stream.Position = 0;
                        if (read == 4 && magic.SequenceEqual(AssetMapV2.MagicBytes))
                        {
                            throw new NotSupportedException(
                                "这是紧凑 v2 AssetMap（Host/CLI 默认格式），老 Asset Browser 读不了；" +
                                "要用这里的界面看，请先加 --map_format MessagePack 重新建图。");
                        }

                        // Deserialize map
                        Instance = MessagePackSerializer.Deserialize<AssetMap>(stream, MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray));
                    }
                    else if (extension == ".json")
                    {
                        // Deserialize json
                        using var reader = new StreamReader(stream);
                        var jsonContent = reader.ReadToEnd();
                        var parsed = JsonConvert.DeserializeObject<AssetMap>(jsonContent);

                        Instance = new AssetMap
                        {
                            GameType = parsed.GameType,
                            AssetEntries = parsed.AssetEntries
                        };
                    }   
                }
                catch (Exception e)
                {
                    Logger.Error("AssetMap was not loaded");
                    Console.WriteLine(e.ToString());
                    return -1;
                }
                Logger.Info("Loaded !!");
                return 1;
            } else
            {
                Logger.Error("AssetMap was not loaded");
                return -1;
            }
        }

        public static void Clear()
        {
            Instance.GameType = GameType.Normal;
            Instance.AssetEntries = new List<AssetEntry>();
        }
    }
}
