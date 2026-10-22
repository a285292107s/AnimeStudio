using System.Text.Json;

namespace AnimeStudio.Host;

internal sealed record TextTableInfo(string Locale, string FileName, int Count);

/// <summary>
/// Loads the text tables the CLI produced (<c>textmap.&lt;locale&gt;.json</c> for HSR,
/// <c>zzz-text.&lt;locale&gt;.json</c> for ZZZ) and answers paged queries for the UI.
/// Tables are kept in memory; a full ZZZ table is ~35 MB of JSON.
/// </summary>
internal sealed class TextTableStore
{
    private readonly Dictionary<string, List<KeyValuePair<string, string>>> _tables =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<TextTableInfo> Tables { get; private set; } = [];

    public bool IsLoaded => _tables.Count > 0;

    /// <summary>Drops the loaded tables (used when switching games).</summary>
    public void Clear()
    {
        _tables.Clear();
        Tables = [];
    }

    public void Load(string textDirectory)
    {
        _tables.Clear();

        if (!Directory.Exists(textDirectory))
        {
            Tables = [];
            return;
        }

        var infos = new List<TextTableInfo>();

        foreach (string file in Directory.GetFiles(textDirectory, "*.json").OrderBy(static f => f, StringComparer.OrdinalIgnoreCase))
        {
            string locale = ParseLocale(file);
            if (locale.Length == 0)
            {
                continue;
            }

            try
            {
                List<KeyValuePair<string, string>> rows = LoadTable(file);
                _tables[locale] = rows;
                infos.Add(new TextTableInfo(locale, Path.GetFileName(file), rows.Count));
            }
            catch (JsonException)
            {
                // Partial or corrupt output: skip it rather than failing the whole load.
            }
        }

        Tables = infos;
    }

    public string[] Locales => _tables.Keys.OrderBy(static l => l, StringComparer.Ordinal).ToArray();

    public int Total(string locale) => _tables.TryGetValue(locale, out var rows) ? rows.Count : 0;

    public (int Total, List<KeyValuePair<string, string>> Rows) Query(
        string locale,
        string query,
        int offset,
        int limit)
    {
        if (!_tables.TryGetValue(locale, out var rows))
        {
            return (0, []);
        }

        IEnumerable<KeyValuePair<string, string>> source = rows;

        if (!string.IsNullOrWhiteSpace(query))
        {
            string needle = query.Trim();
            source = rows.Where(row =>
                row.Key.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                row.Value.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        List<KeyValuePair<string, string>> filtered = query.Length > 0 ? source.ToList() : source as List<KeyValuePair<string, string>> ?? source.ToList();
        int total = filtered.Count;
        offset = Math.Clamp(offset, 0, Math.Max(0, total - 1));

        return (total, filtered.Skip(offset).Take(limit).ToList());
    }

    private static string ParseLocale(string file)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        string[] parts = name.Split('.');

        // textmap.zh-CN.0.0.16566615 / zzz-text.zh-CN
        if (parts.Length >= 2 && (parts[0].Equals("textmap", StringComparison.OrdinalIgnoreCase) ||
                                  parts[0].Equals("zzz-text", StringComparison.OrdinalIgnoreCase)))
        {
            return parts[1];
        }

        return string.Empty;
    }

    private static List<KeyValuePair<string, string>> LoadTable(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using JsonDocument document = JsonDocument.Parse(stream);

        var rows = new List<KeyValuePair<string, string>>(capacity: 4096);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            rows.Add(new KeyValuePair<string, string>(property.Name, property.Value.GetString() ?? string.Empty));
        }

        return rows;
    }
}
