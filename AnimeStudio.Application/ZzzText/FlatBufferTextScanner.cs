using System.Text;

namespace AnimeStudio.Application.ZzzText;

/// <summary>
/// Pulls strings out of a FlatBuffers payload without its .fbs schema.
///
/// FlatBuffers stores a string as: <c>u32 length</c>, UTF-8 bytes, <c>0x00</c>.
/// ZZZ's localisation tables are FlatBuffers, so scanning for that pattern
/// recovers every string in order, which is all we need to pair
/// "localized text" with its key.
/// </summary>
internal static class FlatBufferTextScanner
{
    private const int MinStringLength = 2;
    private const int MaxStringLength = 300;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static List<(int Offset, string Text)> Scan(byte[] payload)
    {
        var results = new List<(int Offset, string Text)>(capacity: 256);
        var data = new ReadOnlySpan<byte>(payload);

        int position = 0;
        while (position + 5 <= data.Length)
        {
            uint length = (uint)(data[position] | (data[position + 1] << 8) | (data[position + 2] << 16) | (data[position + 3] << 24));

            if (length < MinStringLength || length > MaxStringLength || position + 4 + length >= data.Length)
            {
                position++;
                continue;
            }

            if (data[position + 4 + (int)length] != 0)
            {
                position++;
                continue;
            }

            ReadOnlySpan<byte> textBytes = data.Slice(position + 4, (int)length);

            bool clean = true;
            foreach (byte value in textBytes)
            {
                if (value < 0x20 && value != 0x09 && value != 0x0A && value != 0x0D)
                {
                    clean = false;
                    break;
                }
            }

            if (!clean)
            {
                position++;
                continue;
            }

            string text;
            try
            {
                text = StrictUtf8.GetString(textBytes);
            }
            catch (DecoderFallbackException)
            {
                position++;
                continue;
            }

            results.Add((position, text));
            position += 4 + (int)length + 1;
        }

        return results;
    }
}
