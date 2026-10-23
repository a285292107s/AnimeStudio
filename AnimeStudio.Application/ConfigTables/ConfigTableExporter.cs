using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnimeStudio.Application.ConfigTables;

/// <summary>One exported config table (StarRailData-style <c>ExcelOutput/&lt;Name&gt;.json</c>).</summary>
public sealed record ConfigTableEntry(string Name, string File, long Bytes, long Rows);

public sealed class ConfigTableExtractOptions
{
    /// <summary>Optional regex filters matched against the table name (without extension).</summary>
    public IReadOnlyList<string>? Filters { get; init; }
}

public sealed class ConfigTableExtractResult
{
    public required string SourceDirectory { get; init; }

    public required string OutputDirectory { get; init; }

    public required IReadOnlyList<ConfigTableEntry> Tables { get; init; }

    public long TotalRows { get; init; }

    public long TotalBytes { get; init; }
}

/// <summary>
/// Exports client config tables (skills, avatars, items, stages …) as individual JSON files
/// plus a <c>config.index.json</c> manifest.
///
/// HSR ships these tables as engine-side design data rather than Unity assets, so the source is
/// a StarRailData-style dump directory (an <c>ExcelOutput</c> folder or its parent). The exporter
/// itself is format-agnostic: any directory of <c>&lt;TableName&gt;.json</c> files works, which keeps
/// the pipeline usable for ZZZ / future games once their own readers are added.
/// </summary>
public static class ConfigTableExporter
{
    public static ConfigTableExtractResult Extract(
        string sourcePath,
        string outputRoot,
        ConfigTableExtractOptions? options = null,
        Action<string>? log = null)
    {
        options ??= new ConfigTableExtractOptions();

        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            throw new ArgumentException("Output directory is required.", nameof(outputRoot));
        }

        string sourceDirectory = ResolveSourceDirectory(sourcePath);
        string outputDirectory = Path.Combine(outputRoot, "config");
        Directory.CreateDirectory(outputDirectory);

        Regex[] filters = (options.Filters ?? [])
            .Where(static pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(static pattern => new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled))
            .ToArray();

        List<string> files = Directory
            .GetFiles(sourceDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Where(path => filters.Length == 0 ||
                           filters.Any(regex => regex.IsMatch(Path.GetFileNameWithoutExtension(path))))
            .ToList();

        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                $"No table JSON matched in '{sourceDirectory}'" +
                (filters.Length > 0 ? $" (filters: {string.Join(", ", filters.Select(static f => f.ToString()))})." : "."));
        }

        var entries = new List<ConfigTableEntry>(files.Count);
        long totalBytes = 0;
        long totalRows = 0;

        for (int i = 0; i < files.Count; i++)
        {
            string source = files[i];
            string name = Path.GetFileNameWithoutExtension(source);
            string destination = Path.Combine(outputDirectory, name + ".json");

            byte[] bytes = File.ReadAllBytes(source);
            long rows = CountRows(bytes, name);

            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllBytes(destination, bytes);
            }

            entries.Add(new ConfigTableEntry(name, name + ".json", bytes.LongLength, rows));
            totalBytes += bytes.LongLength;
            totalRows += rows;

            if ((i + 1) % 100 == 0 || i + 1 == files.Count)
            {
                log?.Invoke($"[{i + 1}/{files.Count}] {name} ({rows:N0} rows)");
            }
        }

        WriteIndex(outputDirectory, sourceDirectory, entries, totalRows, totalBytes);

        log?.Invoke($"config tables: {entries.Count:N0} table(s), {totalRows:N0} rows, " +
                    $"{totalBytes / (1024.0 * 1024.0):F1} MB -> {outputDirectory}");

        return new ConfigTableExtractResult
        {
            SourceDirectory = sourceDirectory,
            OutputDirectory = outputDirectory,
            Tables = entries,
            TotalRows = totalRows,
            TotalBytes = totalBytes,
        };
    }

    /// <summary>Accepts the repo root (contains <c>ExcelOutput</c>) or the <c>ExcelOutput</c> folder itself.</summary>
    public static string ResolveSourceDirectory(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("Config table source directory is required.", nameof(sourcePath));
        }

        string full = Path.GetFullPath(sourcePath);
        if (File.Exists(full))
        {
            full = Path.GetDirectoryName(full) ?? full;
        }

        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"Config table source not found: '{full}'.");
        }

        string excelOutput = Path.Combine(full, "ExcelOutput");
        return Directory.Exists(excelOutput) ? excelOutput : full;
    }

    private static long CountRows(byte[] json, string tableName)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => document.RootElement.GetArrayLength(),
                JsonValueKind.Object => 1,
                _ => 0,
            };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Table '{tableName}' is not valid JSON: {exception.Message}", exception);
        }
    }

    private static void WriteIndex(
        string outputDirectory,
        string sourceDirectory,
        IReadOnlyList<ConfigTableEntry> entries,
        long totalRows,
        long totalBytes)
    {
        var index = new
        {
            generatedAtUtc = DateTime.UtcNow.ToString("O"),
            sourceDirectory,
            tableCount = entries.Count,
            totalRows,
            totalBytes,
            tables = entries.Select(static entry => new
            {
                name = entry.Name,
                file = entry.File,
                bytes = entry.Bytes,
                rows = entry.Rows,
            }),
        };

        string path = Path.Combine(outputDirectory, "config.index.json");
        File.WriteAllText(path, JsonSerializer.Serialize(index, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
    }
}
