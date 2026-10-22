namespace AnimeStudio.Application.TextMap;

/// <summary>
/// CLI-facing entry point for text extraction. Kept free of <c>Console</c> so the same
/// code path can be driven by the WebView2 host and by tests.
/// </summary>
public static class TextMapCommand
{
    /// <summary>Returns a process exit code: 0 on success.</summary>
    public static int Run(
        string inputPath,
        string outputDirectory,
        TextMapExtractOptions options,
        Action<string>? log = null)
    {
        TextMapExtractResult result = TextMapExtractor.Extract(inputPath, options, log);

        Directory.CreateDirectory(outputDirectory);

        foreach (TextMapTable table in result.Tables)
        {
            string stem = table.GameVersion is null
                ? $"textmap.{table.Locale}"
                : $"textmap.{table.Locale}.{table.GameVersion}";

            string jsonPath = Path.Combine(outputDirectory, stem + ".json");
            string csvPath = Path.Combine(outputDirectory, stem + ".csv");

            table.WriteJson(jsonPath);
            table.WriteCsv(csvPath);

            log?.Invoke(table.Describe());
            log?.Invoke($"wrote      : {jsonPath}");
            log?.Invoke($"wrote      : {csvPath}");
        }

        return 0;
    }
}
