using System.Text.Json;

namespace AnimeStudio.Application.TextMap;

/// <summary>A localized DesignData pack found inside a game installation.</summary>
public sealed record LanguagePack(string LanguageFolder, string Locale, string Path, long Size);

public sealed class GameDataLayout
{
    /// <summary>The game's <c>&lt;Game&gt;_Data</c> directory.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>Design-data version from <c>M_Design_ArchiveV.bytes</c>, when present.</summary>
    public string? Version { get; init; }

    /// <summary>Every installed language, hotfix roots first.</summary>
    public required IReadOnlyList<LanguagePack> Packs { get; init; }
}

/// <summary>
/// Finds the DesignData directories inside a game installation.
///
/// Nothing here assumes a single language: whatever folders exist under
/// <c>DesignData/Windows/&lt;lang&gt;/</c> are picked up automatically, so installing
/// another language pack makes it appear without code changes.
/// </summary>
public static class GameDataLocator
{
    private static readonly Dictionary<string, string> LocaleByFolder = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cn"] = "zh-CN",
        ["chs"] = "zh-CN",
        ["cht"] = "zh-TW",
        ["tc"] = "zh-TW",
        ["en"] = "en",
        ["jp"] = "ja",
        ["kr"] = "ko",
        ["de"] = "de",
        ["es"] = "es",
        ["fr"] = "fr",
        ["id"] = "id",
        ["pt"] = "pt",
        ["ru"] = "ru",
        ["th"] = "th",
        ["vi"] = "vi",
    };

    /// <summary>
    /// Accepts a game root, a <c>*_Data</c> directory, a <c>Persistent</c> directory or even a
    /// language folder, and returns the owning <c>*_Data</c> directory.
    /// </summary>
    public static string ResolveDataDirectory(string input)
    {
        string? current = Path.GetFullPath(input);
        if (current is not null && File.Exists(current))
        {
            current = Path.GetDirectoryName(current);
        }

        for (int depth = 0; depth < 6 && current is not null; depth++)
        {
            if (HasDesignData(current))
            {
                return current;
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null)
            {
                break;
            }

            // A game install root usually holds exactly one "<Game>_Data" child.
            if (HasDesignData(parent))
            {
                return parent;
            }

            current = parent;
        }

        // Last try: the input is the install root that contains a single *_Data directory.
        var root = Path.GetFullPath(input);
        if (Directory.Exists(root))
        {
            var candidates = Directory.GetDirectories(root, "*_Data", SearchOption.TopDirectoryOnly);
            if (candidates.Length == 1 && HasDesignData(candidates[0]))
            {
                return candidates[0];
            }
        }

        throw new DirectoryNotFoundException(
            $"No DesignData directory found under '{input}'. Point this at the game's \"<Game>_Data\" folder.");
    }

    public static GameDataLayout Locate(string input)
    {
        string dataDirectory = ResolveDataDirectory(input);

        var packs = new List<LanguagePack>();
        var seenLocales = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Hotfix/patched data first: if both roots ship the same language, the patch wins.
        var roots = new[]
        {
            Path.Combine(dataDirectory, "Persistent", "DesignData", "Windows"),
            Path.Combine(dataDirectory, "StreamingAssets", "DesignData", "Windows"),
        };

        foreach (var root in roots)
        {
            CollectPacks(root, packs, seenLocales);
        }

        return new GameDataLayout
        {
            DataDirectory = dataDirectory,
            Version = ReadDesignVersion(dataDirectory),
            Packs = packs,
        };
    }

    public static bool HasDesignData(string directory) =>
        Directory.Exists(Path.Combine(directory, "Persistent", "DesignData", "Windows")) ||
        Directory.Exists(Path.Combine(directory, "StreamingAssets", "DesignData", "Windows"));

    private static void CollectPacks(string root, List<LanguagePack> packs, HashSet<string> seenLocales)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var languageDirectory in Directory.GetDirectories(root))
        {
            string folder = Path.GetFileName(languageDirectory);
            if (!LocaleByFolder.TryGetValue(folder, out string? locale))
            {
                continue;
            }

            if (!seenLocales.Add(locale))
            {
                continue;
            }

            FileInfo? pack = Directory
                .GetFiles(languageDirectory, "*.bytes", SearchOption.TopDirectoryOnly)
                .Select(static path => new FileInfo(path))
                .OrderByDescending(static info => info.Length)
                .FirstOrDefault();

            if (pack is null || pack.Length == 0)
            {
                seenLocales.Remove(locale);
                continue;
            }

            packs.Add(new LanguagePack(folder, locale, pack.FullName, pack.Length));
        }
    }

    private static string? ReadDesignVersion(string dataDirectory)
    {
        foreach (string root in new[]
                 {
                     Path.Combine(dataDirectory, "Persistent", "DesignData", "Windows"),
                     Path.Combine(dataDirectory, "StreamingAssets", "DesignData", "Windows"),
                 })
        {
            string manifest = Path.Combine(root, "M_Design_ArchiveV.bytes");
            if (!File.Exists(manifest))
            {
                continue;
            }

            try
            {
                // The manifest is NDJSON: one JSON object per line, several manifests per file.
                foreach (string line in File.ReadLines(manifest))
                {
                    if (!line.Contains("MajorVersion", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    using JsonDocument document = JsonDocument.Parse(line);
                    JsonElement rootElement = document.RootElement;

                    string major = GetNumber(rootElement, "MajorVersion");
                    string minor = GetNumber(rootElement, "MinorVersion");
                    string patch = GetNumber(rootElement, "PatchVersion");
                    return $"{major}.{minor}.{patch}";
                }
            }
            catch (JsonException)
            {
                // Manifest unreadable: version is only cosmetic, keep going.
            }
        }

        return null;
    }

    private static string GetNumber(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.TryGetInt64(out long number)
            ? number.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "0";
}
