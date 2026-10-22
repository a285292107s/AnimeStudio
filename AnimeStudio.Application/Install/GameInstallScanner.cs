namespace AnimeStudio.Application.Install;

/// <summary>A supported game installation found on this machine.</summary>
public sealed record DetectedInstall(string Game, string GameDataPath, string GameRoot, string Source);

/// <summary>
/// Finds installed games without asking the user to browse:
/// <list type="bullet">
/// <item>extra roots supplied by the host (miHoYo launcher registry values);</item>
/// <item>well-known launcher directories under Program Files / ProgramData;</item>
/// <item>common custom locations on every fixed drive (<c>X:\Games</c>, <c>X:\miHoYo Launcher\games</c>, …).</item>
/// </list>
/// Every candidate is validated by structure (<c>StreamingAssets/Asb</c> for Star Rail,
/// <c>StreamingAssets/Blocks</c> for Zenless Zone Zero), so a wrong folder is never picked.
/// Manual selection stays available as the fallback.
/// </summary>
public static class GameInstallScanner
{
    public const string HonkaiStarRail = "SR";
    public const string ZenlessZoneZero = "ZZZ";

    public static IReadOnlyList<DetectedInstall> Scan(
        IEnumerable<string>? extraRoots = null,
        Action<string>? log = null,
        bool includeDefaultRoots = true)
    {
        var results = new List<DetectedInstall>();
        var seenDataDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in BuildRoots(extraRoots, includeDefaultRoots))
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            log?.Invoke($"查找：{root}");

            foreach (string candidate in EnumerateCandidates(root))
            {
                string? dataDirectory = ResolveDataDirectory(candidate);
                if (dataDirectory is null || !seenDataDirectories.Add(dataDirectory))
                {
                    continue;
                }

                string? game = DetectGame(dataDirectory);
                if (game is null)
                {
                    continue;
                }

                results.Add(new DetectedInstall(
                    game,
                    dataDirectory,
                    Path.GetDirectoryName(dataDirectory.TrimEnd(Path.DirectorySeparatorChar)) ?? dataDirectory,
                    root));
            }
        }

        return results;
    }

    /// <summary>Identifies which supported game a <c>&lt;Game&gt;_Data</c> directory belongs to.</summary>
    public static string? DetectGame(string dataDirectory)
    {
        if (!Directory.Exists(dataDirectory))
        {
            return null;
        }

        string name = Path.GetFileName(dataDirectory.TrimEnd(Path.DirectorySeparatorChar));
        string streaming = Path.Combine(dataDirectory, "StreamingAssets");
        bool hasStreaming = Directory.Exists(streaming);

        if (name.Contains("StarRail", StringComparison.OrdinalIgnoreCase) ||
            (hasStreaming && Directory.Exists(Path.Combine(streaming, "Asb"))))
        {
            return HonkaiStarRail;
        }

        if (name.Contains("ZenlessZoneZero", StringComparison.OrdinalIgnoreCase) ||
            (hasStreaming && Directory.Exists(Path.Combine(streaming, "Blocks"))))
        {
            return ZenlessZoneZero;
        }

        return null;
    }

    /// <summary>Accepts a game root, a <c>*_Data</c> directory or a launcher root, returns the data directory.</summary>
    public static string? ResolveDataDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return null;
        }

        if (Path.GetFileName(path).EndsWith("_Data", StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(Path.Combine(path, "StreamingAssets")))
        {
            return path;
        }

        try
        {
            foreach (string directory in Directory.EnumerateDirectories(path))
            {
                if (Path.GetFileName(directory).EndsWith("_Data", StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(Path.Combine(directory, "StreamingAssets")))
                {
                    return directory;
                }
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            // Unreadable directory: skip it.
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidates(string root)
    {
        yield return root;

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(root);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            yield break;
        }

        foreach (string child in children)
        {
            yield return child;

            // Launcher layouts: games\<name>, so one more level is enough.
            string name = Path.GetFileName(child);
            if (name.Equals("games", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Launcher", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("HoYo", StringComparison.OrdinalIgnoreCase))
            {
                IEnumerable<string> grandChildren;
                try
                {
                    grandChildren = Directory.EnumerateDirectories(child);
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                foreach (string grandChild in grandChildren)
                {
                    yield return grandChild;
                }
            }
        }
    }

    private static IEnumerable<string> BuildRoots(IEnumerable<string>? extraRoots, bool includeDefaultRoots)
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
            {
                roots.Add(path);
            }
        }

        if (extraRoots is not null)
        {
            foreach (string extra in extraRoots)
            {
                Add(extra);
            }
        }

        if (!includeDefaultRoots)
        {
            return roots;
        }

        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "miHoYo Launcher", "games"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "miHoYo Launcher", "games"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HoYoPlay", "games"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "HoYoPlay", "games"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "miHoYo Launcher", "games"));

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
            {
                continue;
            }

            string root = drive.RootDirectory.FullName;
            Add(Path.Combine(root, "Games"));
            Add(Path.Combine(root, "miHoYo Launcher", "games"));
            Add(Path.Combine(root, "HoYoPlay", "games"));
        }

        return roots;
    }
}
