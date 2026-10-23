using AnimeStudio.Application.ConfigTables;
using AnimeStudio.Application.DesignData;
using AnimeStudio.Application.Diff;
using AnimeStudio.Application.Install;
using AnimeStudio.Application.ZzzText;

namespace AnimeStudio.Tests;

/// <summary>
/// Zero-dependency test runner (the repo has no NuGet test framework available offline).
/// Covers the pure, game-data-independent logic: varint decode, ZZZ language detection,
/// install detection and the version diff.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static string _current = string.Empty;

    private static int Main()
    {
        Run("varint", VarintRoundTrip);
        Run("zzz-language", ZzzLanguageDetection);
        Run("install-detection", InstallDetection);
        Run("config-tables", ConfigTableExport);
        Run("snapshot-diff", SnapshotDiffTests);

        Console.WriteLine();
        Console.WriteLine(_failed == 0
            ? $"[PASS] {_passed} checks passed"
            : $"[FAIL] {_failed} failed / {_passed} passed");

        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        _current = name;
        try
        {
            test();
            Console.WriteLine($"  ok  {name}");
        }
        catch (TestFailureException exception)
        {
            Console.WriteLine($"  FAIL {name}: {exception.Message}");
        }
        catch (Exception exception)
        {
            _failed++;
            Console.WriteLine($"  FAIL {name}: unexpected {exception.GetType().Name}: {exception.Message}");
        }
    }

    private sealed class TestFailureException(string message) : Exception(message);

    private static void Check(bool condition, string message)
    {
        if (condition)
        {
            _passed++;
            return;
        }

        throw new TestFailureException(message);
    }

    private static void Equal<T>(T expected, T actual, string label)
    {
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{label}: expected {expected}, got {actual}");
    }

    /* ---------- varint ---------- */

    private static void VarintRoundTrip()
    {
        ulong[] values = [0, 1, 127, 128, 255, 300, 16_384, 960_878, ulong.MaxValue / 2];
        foreach (ulong value in values)
        {
            byte[] encoded = EncodeUnsigned(value);
            int position = 0;
            ulong decoded = Varint.Read(encoded, ref position);
            Equal(value, decoded, $"roundtrip {value}");
            Equal(encoded.Length, position, $"consumed length {value}");
        }

        bool threw = false;
        try
        {
            int position = 0;
            Varint.Read([0x80], ref position);
        }
        catch (InvalidDataException)
        {
            threw = true;
        }

        Check(threw, "truncated varint must throw");
    }

    private static byte[] EncodeUnsigned(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            byte current = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                current |= 0x80;
            }

            bytes.Add(current);
        }
        while (value != 0);

        return bytes.ToArray();
    }

    /* ---------- ZZZ language ---------- */

    private static void ZzzLanguageDetection()
    {
        Equal(ZzzTextLanguage.Japanese, ZzzTextLanguageDetector.ClassifyString("こんにちは"), "kana");
        Equal(ZzzTextLanguage.SimplifiedChinese, ZzzTextLanguageDetector.ClassifyString("绝区零测试"), "simplified");
        Equal(ZzzTextLanguage.TraditionalChinese, ZzzTextLanguageDetector.ClassifyString("絕區零測試"), "traditional");
        Check(
            ZzzTextLanguageDetector.ClassifyString("3234") is not (ZzzTextLanguage.SimplifiedChinese
                or ZzzTextLanguage.TraditionalChinese
                or ZzzTextLanguage.Japanese),
            "digits should be neutral");
        Check(ZzzTextLanguageDetector.ContainsKana("テスト"), "ContainsKana");
        Check(ZzzTextLanguageDetector.ContainsCjk("测试"), "ContainsCjk");
    }

    /* ---------- install detection ---------- */

    private static void InstallDetection()
    {
        string root = Path.Combine(Path.GetTempPath(), "anime-tests-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            string srRoot = Path.Combine(root, "Star Rail Game");
            string srData = Path.Combine(srRoot, "StarRail_Data");
            Directory.CreateDirectory(Path.Combine(srData, "StreamingAssets", "Asb"));

            string zzzRoot = Path.Combine(root, "ZenlessZoneZero Game");
            string zzzData = Path.Combine(zzzRoot, "ZenlessZoneZero_Data");
            Directory.CreateDirectory(Path.Combine(zzzData, "StreamingAssets", "Blocks"));

            string junk = Path.Combine(root, "NotAGame");
            Directory.CreateDirectory(Path.Combine(junk, "Fake_Data", "StreamingAssets"));

            Equal(GameInstallScanner.HonkaiStarRail, GameInstallScanner.DetectGame(srData), "SR by structure");
            Equal(GameInstallScanner.ZenlessZoneZero, GameInstallScanner.DetectGame(zzzData), "ZZZ by structure");
            Check(GameInstallScanner.DetectGame(Path.Combine(junk, "Fake_Data")) is null, "junk must not match");
            Equal(srData, GameInstallScanner.ResolveDataDirectory(srRoot), "resolve from game root");

            IReadOnlyList<DetectedInstall> found = GameInstallScanner.Scan([root], includeDefaultRoots: false);
            Equal(2, found.Count, "scan count");
            Check(found.Any(install => install.Game == GameInstallScanner.HonkaiStarRail), "scan finds SR");
            Check(found.Any(install => install.Game == GameInstallScanner.ZenlessZoneZero), "scan finds ZZZ");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // temp cleanup only
            }
        }
    }

    /* ---------- config tables ---------- */

    private static void ConfigTableExport()
    {
        string root = Path.Combine(Path.GetTempPath(), "anime-tests-config-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            string repository = Path.Combine(root, "repo");
            string source = Path.Combine(repository, "ExcelOutput");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "AvatarConfig.json"), "[{\"AvatarID\":1001},{\"AvatarID\":1002}]");
            File.WriteAllText(Path.Combine(source, "ItemConfig.json"), "[]");
            File.WriteAllText(Path.Combine(source, "notes.txt"), "ignored");

            string output = Path.Combine(root, "out");
            ConfigTableExtractResult result = ConfigTableExporter.Extract(repository, output);

            Equal(2, result.Tables.Count, "table count");
            Equal(2L, result.Tables.First(table => table.Name == "AvatarConfig").Rows, "array rows");
            Equal(0L, result.Tables.First(table => table.Name == "ItemConfig").Rows, "empty rows");
            Check(File.Exists(Path.Combine(output, "config", "AvatarConfig.json")), "table copied");
            Check(File.Exists(Path.Combine(output, "config", "config.index.json")), "index written");

            ConfigTableExtractResult filtered = ConfigTableExporter.Extract(
                source,
                Path.Combine(root, "out-filtered"),
                new ConfigTableExtractOptions { Filters = ["^Item"] });
            Equal(1, filtered.Tables.Count, "filtered count");
            Equal("ItemConfig", filtered.Tables[0].Name, "filtered name");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // temp cleanup only
            }
        }
    }

    /* ---------- diff ---------- */

    private static void SnapshotDiffTests()
    {
        var oldSnapshot = new Snapshot
        {
            Game = "SR",
            Version = "1.0",
            Text = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
            {
                ["zh-CN"] = new() { ["1"] = "旧文本", ["2"] = "不变", ["3"] = "将被删除" },
            },
            Images = new Dictionary<string, ImageSnapshotEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["ui/a.png"] = new() { Width = 10, Height = 10, Bytes = 100 },
                ["ui/b.png"] = new() { Width = 20, Height = 20, Bytes = 200 },
                ["ui/c.png"] = new() { Width = 30, Height = 30, Bytes = 300 },
            },
        };

        var newSnapshot = new Snapshot
        {
            Game = "SR",
            Version = "2.0",
            Text = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
            {
                ["zh-CN"] = new() { ["1"] = "新文本", ["2"] = "不变", ["4"] = "新增" },
            },
            Images = new Dictionary<string, ImageSnapshotEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["ui/a.png"] = new() { Width = 10, Height = 10, Bytes = 100 },
                ["ui/b.png"] = new() { Width = 22, Height = 20, Bytes = 200 },
                ["ui/d.png"] = new() { Width = 40, Height = 40, Bytes = 400 },
            },
        };

        DiffResult result = SnapshotDiff.Compare(oldSnapshot, newSnapshot);
        Check(result.HasPrevious, "hasPrevious");
        Equal("1.0", result.PreviousVersion, "previous version");
        Equal("2.0", result.Version, "version");
        Equal(1, result.TextAdded, "text added");
        Equal(1, result.TextRemoved, "text removed");
        Equal(1, result.TextChanged, "text changed");
        Equal(1, result.ImagesAdded, "images added");
        Equal(1, result.ImagesRemoved, "images removed");
        Equal(1, result.ImagesChanged, "images changed");
        Equal(3, result.TextSamples.Count, "text samples");
        Equal(3, result.ImageSamples.Count, "image samples");
        Equal(1, result.ByLocale["zh-CN"].Added, "locale added count");

        DiffResult baseline = SnapshotDiff.Compare(null, newSnapshot);
        Check(!baseline.HasPrevious, "baseline has no previous");
        Equal(0, baseline.TotalTextChanges, "baseline text changes");
    }
}
