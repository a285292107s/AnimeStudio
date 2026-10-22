using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnimeStudio.Application.ZzzText;

public sealed class ZzzTextExtractOptions
{
    /// <summary>Locales to keep (<c>zh-CN</c>, <c>zh-TW</c>, <c>ja</c>). Empty means all detected.</summary>
    public IReadOnlyList<string>? Languages { get; init; }
}

public sealed class ZzzTextTable
{
    private readonly List<string> _order = new();
    private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);

    public required string Locale { get; init; }

    public long Payloads { get; set; }

    public long Duplicates { get; private set; }

    public int EntryCount => _order.Count;

    public IReadOnlyList<string> Keys => _order;

    public string this[string key] => _entries[key];

    public bool TryGetValue(string key, out string value) => _entries.TryGetValue(key, out value!);

    public void Add(string key, string text)
    {
        if (_entries.TryAdd(key, text))
        {
            _order.Add(key);
        }
        else
        {
            Duplicates++;
        }
    }

    public IEnumerable<KeyValuePair<string, string>> Entries()
    {
        foreach (string key in _order)
        {
            yield return new KeyValuePair<string, string>(key, _entries[key]);
        }
    }
}

/// <summary>
/// Turns ZZZ TextAsset payloads into <c>key -&gt; localized text</c> tables.
///
/// Each payload is a FlatBuffers table holding "text, key" pairs; the language is
/// inferred from the script of the whole payload (see <see cref="ZzzTextLanguage"/>).
/// </summary>
public sealed class ZzzTextExtractor
{
    private static readonly Regex KeyPattern = new(
        "^[A-Za-z_][A-Za-z0-9_.\\[\\]#/:\\- ]{0,140}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dictionary<string, ZzzTextTable> _tables = new(StringComparer.Ordinal);
    private readonly HashSet<string>? _wanted;

    private long _payloads;
    private long _strings;
    private long _pairs;

    public ZzzTextExtractor(ZzzTextExtractOptions options)
    {
        _wanted = options.Languages is { Count: > 0 }
            ? new HashSet<string>(options.Languages, StringComparer.OrdinalIgnoreCase)
            : null;
    }

    public long Payloads => _payloads;

    public long Strings => _strings;

    public long Pairs => _pairs;

    public void AddPayload(string sourceName, byte[] payload, Action<string>? log = null)
    {
        if (payload is null || payload.Length < 8)
        {
            return;
        }

        _payloads++;
        List<(int Offset, string Text)> strings = FlatBufferTextScanner.Scan(payload);
        _strings += strings.Count;

        if (strings.Count < 4)
        {
            return;
        }

        // 1) Per-string evidence: only strings with script-specific characters vote.
        var votes = new ZzzTextLanguage[strings.Count];
        var voteCounts = new List<ZzzTextLanguage>(strings.Count);

        for (int index = 0; index < strings.Count; index++)
        {
            ZzzTextLanguage vote = ZzzTextLanguageDetector.ClassifyString(strings[index].Text);
            votes[index] = vote;
            if (vote != ZzzTextLanguage.Unknown)
            {
                voteCounts.Add(vote);
            }
        }

        if (voteCounts.Count < 3)
        {
            return;
        }

        ZzzTextLanguage language = ZzzTextLanguageDetector.ClassifyPayload(voteCounts);
        if (language == ZzzTextLanguage.Unknown)
        {
            return;
        }

        string locale = ZzzTextLanguageDetector.ToLocale(language);
        if (_wanted is not null && !_wanted.Contains(locale))
        {
            return;
        }

        // 2) Pair each localized text with the key that follows it. Neutral strings
        //    ("攻击力") inherit the payload's language; contradicting strings are dropped.
        var accepted = new List<(string Key, string Text)>();

        for (int index = 0; index + 1 < strings.Count; index++)
        {
            (int offset, string text) = strings[index];
            (int nextOffset, string key) = strings[index + 1];

            if (!KeyPattern.IsMatch(key))
            {
                continue;
            }

            if (!ZzzTextLanguageDetector.ContainsCjk(text) && !ZzzTextLanguageDetector.ContainsKana(text))
            {
                continue;
            }

            int textBytes = Encoding.UTF8.GetByteCount(text);
            if (nextOffset - offset > textBytes + 256)
            {
                continue;
            }

            ZzzTextLanguage vote = votes[index];
            if (vote == ZzzTextLanguage.Unknown)
            {
                vote = language;
            }

            if (vote != language)
            {
                continue;
            }

            accepted.Add((key, text));
        }

        // Binary payloads can produce a few accidental pairs; real localisation tables have many.
        if (accepted.Count < 5)
        {
            return;
        }

        if (!_tables.TryGetValue(locale, out ZzzTextTable? table))
        {
            table = new ZzzTextTable { Locale = locale };
            _tables[locale] = table;
        }

        table.Payloads++;
        foreach ((string key, string text) in accepted)
        {
            _pairs++;
            table.Add(key, text);
        }

        log?.Invoke($"  {Path.GetFileName(sourceName)}  {locale}  +{accepted.Count}  (total {table.EntryCount:N0})");
    }

    public IReadOnlyList<ZzzTextTable> Build() => _tables.Values.OrderBy(static table => table.Locale, StringComparer.Ordinal).ToList();

    public string Describe() =>
        $"payloads {_payloads:N0}, strings {_strings:N0}, pairs {_pairs:N0}";
}

/// <summary>Writes ZZZ key -&gt; text tables as JSON and UTF-8-BOM CSV.</summary>
public static class ZzzTextWriter
{
    public static void WriteJson(string path, ZzzTextTable table)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        writer.WriteStartObject();
        foreach (KeyValuePair<string, string> entry in table.Entries())
        {
            writer.WriteString(entry.Key, entry.Value);
        }

        writer.WriteEndObject();
    }

    public static void WriteCsv(string path, ZzzTextTable table)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))
        {
            NewLine = "\r\n",
        };

        writer.WriteLine("key,text");
        foreach (KeyValuePair<string, string> entry in table.Entries())
        {
            writer.Write(EscapeCsv(entry.Key));
            writer.Write(',');
            writer.WriteLine(EscapeCsv(entry.Value));
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
