using AnimeStudio.Application.DesignData;

namespace AnimeStudio.Application.TextMap;

public sealed class TextMapExtractOptions
{
    /// <summary>
    /// Locales to extract (<c>zh-CN</c>, <c>en</c>, <c>ja</c>, …). Null or empty means
    /// "everything installed" — the tool never hardcodes a single language.
    /// </summary>
    public IReadOnlyList<string>? Languages { get; init; }

    /// <summary>
    /// Keep empty strings. Community dumps drop them, and so does this tool by default.
    /// </summary>
    public bool IncludeEmptyStrings { get; init; }
}

public sealed class TextMapExtractResult
{
    public required GameDataLayout Layout { get; init; }

    public required IReadOnlyList<TextMapTable> Tables { get; init; }
}

/// <summary>
/// Turns the game's own DesignData packs into <c>TextMapHash -&gt; string</c> tables.
/// No Unity bundles, no typetree, no native DLLs, no third-party data.
/// </summary>
public static class TextMapExtractor
{
    public static TextMapExtractResult Extract(
        string inputPath,
        TextMapExtractOptions options,
        Action<string>? log = null)
    {
        GameDataLayout layout = GameDataLocator.Locate(inputPath);

        log?.Invoke($"game data  : {layout.DataDirectory}");
        log?.Invoke($"design ver : {(layout.Version is null ? "unknown" : "v" + layout.Version)}");

        if (layout.Packs.Count == 0)
        {
            throw new InvalidOperationException(
                $"No DesignData language pack found under '{layout.DataDirectory}'.");
        }

        HashSet<string>? wanted = options.Languages is { Count: > 0 }
            ? new HashSet<string>(options.Languages, StringComparer.OrdinalIgnoreCase)
            : null;

        var tables = new List<TextMapTable>(layout.Packs.Count);

        foreach (LanguagePack pack in layout.Packs)
        {
            if (wanted is not null
                && !wanted.Contains(pack.Locale)
                && !wanted.Contains(pack.LanguageFolder))
            {
                continue;
            }

            log?.Invoke($"pack       : {pack.Locale} ({pack.LanguageFolder}) {pack.Size:N0} bytes  {pack.Path}");

            DesignDataReadResult read = DesignDataPackReader.Read(pack.Path);
            log?.Invoke($"parsed     : {read.ParsedCount:N0} strings, " +
                        $"{read.BytesConsumed:N0}/{read.FileSize:N0} bytes" +
                        (read.ReachedIndexSection ? " (index section follows)" : " (whole file)"));

            var seenHashes = new HashSet<ulong>(read.Strings.Count);
            var entries = new List<TextMapEntry>(read.Strings.Count);
            int emptyCount = 0;
            int duplicateCount = 0;

            foreach (DesignDataString item in read.Strings)
            {
                if (!seenHashes.Add(item.TextMapHash))
                {
                    duplicateCount++;
                    continue;
                }

                if (string.IsNullOrEmpty(item.Text))
                {
                    emptyCount++;
                    if (!options.IncludeEmptyStrings)
                    {
                        continue;
                    }
                }

                entries.Add(new TextMapEntry(item.TextMapHash, item.Text));
            }

            tables.Add(new TextMapTable
            {
                LanguageFolder = pack.LanguageFolder,
                Locale = pack.Locale,
                SourcePath = pack.Path,
                GameVersion = layout.Version,
                Entries = entries,
                DeclaredCount = read.DeclaredCount,
                ParsedCount = read.ParsedCount,
                EmptyCount = emptyCount,
                DuplicateCount = duplicateCount,
            });
        }

        if (tables.Count == 0)
        {
            throw new InvalidOperationException(
                $"None of the requested languages is installed (requested: " +
                $"{(options.Languages is { Count: > 0 } ? string.Join(", ", options.Languages) : "all")}; " +
                $"installed: {string.Join(", ", layout.Packs.Select(static p => p.Locale))}).");
        }

        return new TextMapExtractResult { Layout = layout, Tables = tables };
    }
}
