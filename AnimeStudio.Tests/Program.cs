using AnimeStudio.Application.Diff;
using AnimeStudio.Application.Install;

namespace AnimeStudio.Tests;

/// <summary>
/// Zero-dependency test runner (the repo has no NuGet test framework available offline).
/// Covers the pure, game-data-independent logic: install detection and the image snapshot diff.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static string _current = string.Empty;

    private static int Main()
    {
        Run("install-detection", InstallDetection);
        Run("snapshot-diff", SnapshotDiffTests);
        Run("container-index", ContainerIndexTests);

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

    /* ---------- diff ---------- */

    private static void SnapshotDiffTests()
    {
        var oldSnapshot = new Snapshot
        {
            Game = "SR",
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
            Images = new Dictionary<string, ImageSnapshotEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["ui/a.png"] = new() { Width = 10, Height = 10, Bytes = 100 },
                ["ui/b.png"] = new() { Width = 22, Height = 20, Bytes = 200 },
                ["ui/d.png"] = new() { Width = 40, Height = 40, Bytes = 400 },
            },
        };

        DiffResult result = SnapshotDiff.Compare(oldSnapshot, newSnapshot);
        Check(result.HasPrevious, "hasPrevious");
        Equal(1, result.ImagesAdded, "images added");
        Equal(1, result.ImagesRemoved, "images removed");
        Equal(1, result.ImagesChanged, "images changed");
        Equal(3, result.ImageSamples.Count, "image samples");
        Equal(3, result.TotalImageChanges, "total image changes");

        DiffResult baseline = SnapshotDiff.Compare(null, newSnapshot);
        Check(!baseline.HasPrevious, "baseline has no previous");
        Equal(0, baseline.TotalImageChanges, "baseline image changes");
    }

    /* ---------- ZZZ container index ---------- */

    private static void ContainerIndexTests()
    {
        string root = Path.Combine(Path.GetTempPath(), "anime-tests-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(root);
            string json = Path.Combine(root, "Z3-AssetIndex-Eleiyas.json");
            File.WriteAllText(json, """
                {
                  "7444794748978933277": "Assets/NapResources/UI/Menus/Dialog/Friend/BusinessCardDialog.prefab",
                  "6706176343850719170": "Assets/Code/Logic/VoxelArrayForOverlay.compute"
                }
                """);

            Check(ContainerIndex.TryLoad(json, out string error), $"load json: {error}");
            Equal(2, ContainerIndex.Count, "index count");
            Equal(
                "Assets/NapResources/UI/Menus/Dialog/Friend/BusinessCardDialog.prefab",
                ContainerIndex.Resolve("7444794748978933277") ?? string.Empty,
                "resolve hash");
            Check(ContainerIndex.Resolve("12345") is null, "unknown hash stays unresolved");
            Check(ContainerIndex.Resolve("Assets/plain") is null, "non-numeric container is not a hash");

            // 第二次从二进制缓存读，内容必须一致。
            ContainerIndex.Unload();
            Check(ContainerIndex.TryLoad(json, out string cachedError), $"load cache: {cachedError}");
            Equal(2, ContainerIndex.Count, "cached count");
            Equal(
                "Assets/Code/Logic/VoxelArrayForOverlay.compute",
                ContainerIndex.Resolve("6706176343850719170") ?? string.Empty,
                "resolve from cache");
        }
        finally
        {
            ContainerIndex.Unload();
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
}
