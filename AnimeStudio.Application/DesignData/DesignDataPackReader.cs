using System.Text;

namespace AnimeStudio.Application.DesignData;

/// <summary>One localized string entry read from a DesignData pack.</summary>
public sealed record DesignDataString(ulong Id, ulong TextMapHash, string Text);

public sealed class DesignDataReadResult
{
    /// <summary>Value of the file header. It counts strings + index nodes, not just strings.</summary>
    public long DeclaredCount { get; init; }

    public long ParsedCount => Strings.Count;

    public long BytesConsumed { get; init; }

    public long FileSize { get; init; }

    /// <summary>True when the walk stopped at the pack's index section (the normal, healthy case).</summary>
    public bool ReachedIndexSection { get; init; }

    public required IReadOnlyList<DesignDataString> Strings { get; init; }
}

/// <summary>
/// Reads miHoYo's DesignData text pack — the file behind
/// <c>&lt;Game&gt;_Data/Persistent/DesignData/Windows/&lt;lang&gt;/&lt;md5&gt;.bytes</c>
/// (Honkai: Star Rail) — without third-party dumps and without touching Unity bundles.
///
/// Layout (reverse-engineered, validated against a 960k-entry walk and a
/// community TextMap dump; see tools/extract_textmap2.py):
/// <code>
/// file   := varint(declared_count) record*
/// record := tag varint(id) varint(textmap_hash) varint(utf8_len) utf8_text [0x01]
/// tag    := 0x03 (plain) | 0x07 (markup, always followed by a 0x01 trailer)
/// </code>
/// The string section ends where the pack switches to its index section.
/// </summary>
public static class DesignDataPackReader
{
    public const byte TagPlain = 0x03;
    public const byte TagMarkup = 0x07;
    private const byte MarkupTrailer = 0x01;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static DesignDataReadResult Read(string path) => Read(File.ReadAllBytes(path));

    public static DesignDataReadResult Read(byte[] data)
    {
        var bytes = new ReadOnlySpan<byte>(data);
        int position = 0;
        ulong declaredCount = Varint.Read(bytes, ref position);

        var strings = new List<DesignDataString>((int)Math.Min(declaredCount, 1_000_000));
        bool reachedIndexSection = false;

        while (position < bytes.Length)
        {
            byte tag = bytes[position];
            if (tag != TagPlain && tag != TagMarkup)
            {
                // Not a string record: the pack continues with its index section.
                reachedIndexSection = true;
                break;
            }

            position++;
            ulong id = Varint.Read(bytes, ref position);
            ulong textMapHash = Varint.Read(bytes, ref position);
            ulong length = Varint.Read(bytes, ref position);

            if (length > int.MaxValue || position + (int)length > bytes.Length)
            {
                throw new InvalidDataException(
                    $"Corrupt DesignData pack: string length {length} at offset {position} runs past the end of the file.");
            }

            string text = length == 0
                ? string.Empty
                : StrictUtf8.GetString(bytes.Slice(position, (int)length));
            position += (int)length;

            if (tag == TagMarkup)
            {
                if (position >= bytes.Length || bytes[position] != MarkupTrailer)
                {
                    throw new InvalidDataException(
                        $"Corrupt DesignData pack: markup record at offset {position} is missing its 0x01 trailer.");
                }

                position++;
            }

            strings.Add(new DesignDataString(id, textMapHash, text));
        }

        return new DesignDataReadResult
        {
            DeclaredCount = (long)declaredCount,
            BytesConsumed = position,
            FileSize = bytes.Length,
            ReachedIndexSection = reachedIndexSection,
            Strings = strings,
        };
    }
}
