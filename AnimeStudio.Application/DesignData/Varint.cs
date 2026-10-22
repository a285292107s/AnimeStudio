namespace AnimeStudio.Application.DesignData;

/// <summary>
/// Minimal LEB128 reader used by miHoYo's DesignData packs.
/// </summary>
public static class Varint
{
    public static ulong Read(ReadOnlySpan<byte> data, ref int position)
    {
        ulong value = 0;
        int shift = 0;

        while (true)
        {
            if ((uint)position >= (uint)data.Length)
            {
                throw new InvalidDataException(
                    $"Unexpected end of DesignData pack while reading a varint at offset {position}.");
            }

            byte current = data[position++];
            value |= (ulong)(current & 0x7F) << shift;

            if ((current & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
            if (shift > 63)
            {
                throw new InvalidDataException(
                    $"DesignData varint at offset {position} is wider than 64 bits.");
            }
        }
    }
}
