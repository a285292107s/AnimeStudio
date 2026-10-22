using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AnimeStudio.Application.TextMap;

/// <summary>One <c>TextMapHash -&gt; localized string</c> pair.</summary>
public sealed record TextMapEntry(ulong Hash, string Text);

/// <summary>
/// A single language's TextMap. Same shape as the community dumps
/// (<c>{"1234567890": "text"}</c>) so existing tooling keeps working.
/// </summary>
public sealed class TextMapTable
{
    public required string LanguageFolder { get; init; }

    /// <summary>BCP-47 tag, e.g. <c>zh-CN</c>. Architecture supports any locale; today only cn/cht/…</summary>
    public required string Locale { get; init; }

    public required string SourcePath { get; init; }

    /// <summary>Game design-data version, when discoverable (e.g. <c>0.0.16566615</c>).</summary>
    public string? GameVersion { get; init; }

    public required IReadOnlyList<TextMapEntry> Entries { get; init; }

    public long DeclaredCount { get; init; }

    public long ParsedCount { get; init; }

    /// <summary>Strings that were empty and therefore dropped.</summary>
    public int EmptyCount { get; init; }

    /// <summary>Entries dropped because their hash had already been seen.</summary>
    public int DuplicateCount { get; init; }

    public int EntryCount => Entries.Count;

    public string Describe()
    {
        var version = GameVersion is null ? "unknown" : GameVersion;
        return $"{Locale} ({LanguageFolder}) v{version}: {EntryCount:N0} entries" +
               $" (dropped {DuplicateCount:N0} duplicate hash(es), {EmptyCount:N0} empty string(s))";
    }

    /// <summary>
    /// Writes the same JSON shape as the community TextMap dumps:
    /// a single object of decimal-hash keys in pack order.
    /// </summary>
    public void WriteJson(string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        writer.WriteStartObject();
        foreach (var entry in Entries)
        {
            writer.WriteString(entry.Hash.ToString(CultureInfo.InvariantCulture), entry.Text);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes a UTF-8 (BOM) CSV so Excel opens Chinese text correctly.
    /// </summary>
    public void WriteCsv(string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))
        {
            NewLine = "\r\n",
        };

        writer.WriteLine("hash,text");
        foreach (var entry in Entries)
        {
            writer.Write(entry.Hash.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.WriteLine(EscapeCsv(entry.Text));
        }
    }

    private static string EscapeCsv(string value)
    {
        bool needsQuotes = value.IndexOfAny([',', '"', '\r', '\n']) >= 0;
        if (!needsQuotes)
        {
            return value;
        }

        return '"' + value.Replace("\"", "\"\"") + '"';
    }
}
