using System.Text.Json;

namespace AnimeStudio.Host;

/// <summary>
/// Stable identity for an output folder. The host writes a tiny marker file
/// (<c>.anime-studio.json</c>) into the folder; if the folder is later renamed in Explorer the
/// saved path stops existing, but the marker id lets the host find the new path automatically.
/// </summary>
internal static class OutputLocator
{
    public const string MarkerFileName = ".anime-studio.json";

    private const int MaxDirectoryCandidates = 12000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    internal sealed class Marker
    {
        public string? App { get; set; }

        public int Version { get; set; }

        public string? Game { get; set; }

        public string? Id { get; set; }

        public string? Path { get; set; }
    }

    public static string MarkerPath(string root) => Path.Combine(root, MarkerFileName);

    public static Marker? ReadMarker(string root)
    {
        try
        {
            string path = MarkerPath(root);
            if (!File.Exists(path))
            {
                return null;
            }

            Marker? marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(path), JsonOptions);
            return string.IsNullOrWhiteSpace(marker?.Id) ? null : marker;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Makes sure <paramref name="root"/> carries a marker; adopts an existing marker id when
    /// the folder already has one. Returns true when <paramref name="id"/> changed (caller
    /// should persist settings).
    /// </summary>
    public static bool TryEnsureMarker(string root, string game, ref string? id)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return false;
        }

        Marker? existing = ReadMarker(root);
        if (existing is not null)
        {
            if (string.Equals(existing.Id, id, StringComparison.Ordinal))
            {
                return false;
            }

            id = existing.Id;
            return true;
        }

        bool assigned = string.IsNullOrWhiteSpace(id);
        id = assigned ? Guid.NewGuid().ToString("N") : id;
        WriteMarker(root, game, id!);
        return assigned;
    }

    public static void WriteMarker(string root, string game, string id)
    {
        try
        {
            var marker = new Marker
            {
                App = "AnimeStudio",
                Version = 1,
                Game = game,
                Id = id,
                Path = root,
            };

            string path = MarkerPath(root);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(marker, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A read-only output folder still works; auto-follow just won't be available.
        }
    }

    /// <summary>
    /// Looks for a renamed/moved output folder: checks the missing path's parent and grandparent
    /// (two levels deep) for a marker with the expected id. Ambiguous matches are ignored.
    /// </summary>
    public static bool TryFindRenamed(string missingPath, string? expectedId, string game, out string found)
    {
        found = string.Empty;
        if (string.IsNullOrWhiteSpace(expectedId) || string.IsNullOrWhiteSpace(missingPath))
        {
            return false;
        }

        var roots = new List<string>();
        string? parent = Path.GetDirectoryName(missingPath);
        string leafName = Path.GetFileName(missingPath);
        string parentName = string.IsNullOrEmpty(parent) ? string.Empty : Path.GetFileName(parent);
        if (!string.IsNullOrEmpty(parent))
        {
            if (Directory.Exists(parent))
            {
                roots.Add(parent);
            }

            string? grand = Path.GetDirectoryName(parent);
            if (!string.IsNullOrEmpty(grand) && Directory.Exists(grand))
            {
                roots.Add(grand);
            }
        }

        var matches = new List<(string Path, string? Game)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int remaining = MaxDirectoryCandidates;

        foreach (string root in roots)
        {
            List<string> children = ListDirectories(root, remaining);
            remaining -= children.Count;

            // Try names resembling the old parent first (a renamed parent folder is the
            // common case); the rest is still checked, just later.
            children.Sort((left, right) =>
                Similarity(right, parentName).CompareTo(Similarity(left, parentName)));

            foreach (string child in children)
            {
                Check(child);
                if (leafName.Length > 0)
                {
                    Check(Path.Combine(child, leafName));
                }
            }
        }

        void Check(string candidate)
        {
            if (!seen.Add(candidate))
            {
                return;
            }

            Marker? marker = ReadMarker(candidate);
            if (marker is not null && string.Equals(marker.Id, expectedId, StringComparison.Ordinal))
            {
                matches.Add((candidate, marker.Game));
            }
        }

        if (matches.Count == 0 || matches.Count > 4)
        {
            return false;
        }

        if (matches.Count == 1)
        {
            found = matches[0].Path;
            return true;
        }

        // The same id can legitimately appear once; more than one match is ambiguous unless
        // exactly one of them belongs to this game.
        List<(string Path, string? Game)> sameGame = matches
            .Where(match => string.Equals(match.Game, game, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (sameGame.Count == 1)
        {
            found = sameGame[0].Path;
            return true;
        }

        return false;
    }

    /// <summary>Higher is a better match for "this folder was renamed" heuristics.</summary>
    private static int Similarity(string name, string expected)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(name))
        {
            return 0;
        }

        int common = 0;
        int limit = Math.Min(name.Length, expected.Length);
        while (common < limit && char.ToLowerInvariant(name[common]) == char.ToLowerInvariant(expected[common]))
        {
            common++;
        }

        bool contains = name.Contains(expected, StringComparison.OrdinalIgnoreCase) ||
                        expected.Contains(name, StringComparison.OrdinalIgnoreCase);
        return (contains ? 100 : 0) + common;
    }

    private static List<string> ListDirectories(string root, int maxCount)
    {
        var result = new List<string>();
        if (maxCount <= 0)
        {
            return result;
        }

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        try
        {
            foreach (string child in children)
            {
                if (result.Count >= maxCount)
                {
                    break;
                }

                result.Add(child);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A flaky entry should not abort the search.
        }

        return result;
    }
}
