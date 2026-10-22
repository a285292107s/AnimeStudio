namespace AnimeStudio.Host;

internal sealed record ImageItem(string Name, string RelativePath, string Url, int Width, int Height, long Bytes);

/// <summary>
/// Lists PNGs under the output directory and reads their dimensions.
/// The images are served to the WebView through the <c>out.local</c> virtual host,
/// so the page can use plain &lt;img&gt; tags instead of base64 payloads.
/// </summary>
internal static class ImageIndex
{
    public const string VirtualHost = "out.local";
    public const string UrlPrefix = "https://out.local/";

    public static List<ImageItem> Scan(string outputDirectory, int maxItems = 200_000)
    {
        var items = new List<ImageItem>();
        if (!Directory.Exists(outputDirectory))
        {
            return items;
        }

        foreach (string path in Directory.EnumerateFiles(outputDirectory, "*.png", SearchOption.AllDirectories))
        {
            if (items.Count >= maxItems)
            {
                break;
            }

            (int width, int height) = ReadPngSize(path);
            string relative = Path.GetRelativePath(outputDirectory, path).Replace('\\', '/');

            items.Add(new ImageItem(
                Path.GetFileNameWithoutExtension(path),
                relative,
                UrlPrefix + Uri.EscapeDataString(relative).Replace("%2F", "/"),
                width,
                height,
                new FileInfo(path).Length));
        }

        items.Sort(static (left, right) => string.Compare(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase));
        return items;
    }

    private static (int Width, int Height) ReadPngSize(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = new byte[24];
            if (stream.Read(header, 0, header.Length) < header.Length)
            {
                return (0, 0);
            }

            if (header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47)
            {
                return (0, 0);
            }

            int width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
            int height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
            return (width, height);
        }
        catch (IOException)
        {
            return (0, 0);
        }
    }
}
