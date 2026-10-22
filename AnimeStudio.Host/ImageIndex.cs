using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace AnimeStudio.Host;

internal sealed record ImageItem(
    string Name,
    string RelativePath,
    string Category,
    string Url,
    string ThumbUrl,
    string ThumbRelativePath,
    int Width,
    int Height,
    long Bytes);

internal sealed record ImageCategory(string Id, string Label, int Count);

/// <summary>
/// Lists PNGs under a game's output directory and groups them into categories derived from
/// the export path (e.g. <c>spriteoutput/ui/quest</c>). Thumbnails are generated on demand
/// into a cache folder that is served through the same <c>out.local</c> virtual host, so the
/// gallery never has to load multi-megabyte originals just to show a grid.
/// </summary>
internal static class ImageIndex
{
    public const string VirtualHost = "out.local";
    public const string UrlPrefix = "https://out.local/";
    private const int ThumbnailEdge = 160;

    public static (List<ImageItem> Items, List<ImageCategory> Categories) Scan(string scanRoot, string urlRoot)
    {
        var items = new List<ImageItem>();

        if (!Directory.Exists(scanRoot) || !Directory.Exists(urlRoot))
        {
            return (items, []);
        }

        foreach (string path in Directory.EnumerateFiles(scanRoot, "*.png", SearchOption.AllDirectories))
        {
            string relativeToUrlRoot = Path.GetRelativePath(urlRoot, path).Replace('\\', '/');
            string relativeToScanRoot = Path.GetRelativePath(scanRoot, path).Replace('\\', '/');
            string thumbRelative = $".cache/thumbs/{relativeToUrlRoot}";

            (int width, int height) = ReadPngSize(path);
            string name = Path.GetFileNameWithoutExtension(path);

            items.Add(new ImageItem(
                name,
                relativeToScanRoot,
                CategoryOf(relativeToUrlRoot, name),
                UrlPrefix + Escape(relativeToUrlRoot),
                UrlPrefix + Escape(thumbRelative),
                thumbRelative,
                width,
                height,
                new FileInfo(path).Length));
        }

        items.Sort(static (left, right) => string.Compare(left.RelativePath, right.RelativePath, StringComparison.OrdinalIgnoreCase));

        List<ImageCategory> categories = items
            .GroupBy(static item => item.Category, StringComparer.OrdinalIgnoreCase)
            .Select(static group => new ImageCategory(group.Key, group.Key, group.Count()))
            .OrderByDescending(static category => category.Count)
            .Take(40)
            .ToList();

        return (items, categories);
    }

    /// <summary>Creates (or refreshes) a 160px PNG thumbnail; transparent pixels are preserved.</summary>
    public static void EnsureThumbnail(string sourcePath, string thumbPath)
    {
        try
        {
            var sourceInfo = new FileInfo(sourcePath);
            if (!sourceInfo.Exists)
            {
                return;
            }

            var thumbInfo = new FileInfo(thumbPath);
            if (thumbInfo.Exists && thumbInfo.LastWriteTimeUtc >= sourceInfo.LastWriteTimeUtc)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(thumbPath)!);

            using System.Drawing.Image source = System.Drawing.Image.FromFile(sourcePath);
            double scale = Math.Min(1.0, (double)ThumbnailEdge / Math.Max(source.Width, source.Height));
            int width = Math.Max(1, (int)Math.Round(source.Width * scale));
            int height = Math.Max(1, (int)Math.Round(source.Height * scale));

            using var thumb = new System.Drawing.Bitmap(width, height);
            using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(thumb))
            {
                graphics.Clear(System.Drawing.Color.Transparent);
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, 0, 0, width, height);
            }

            thumb.Save(thumbPath, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            // A broken PNG should not fail the whole gallery; the card falls back to the original URL.
        }
    }

    /// <summary>
    /// Turns "SR/ui/assets/asbres/spriteoutput/ui/quest/x.png" into "spriteoutput/ui/quest".
    /// ZZZ has no asset paths (its containers are numeric IDs), so it falls back to a readable
    /// name prefix ("pc", "ps5", "icon", …) instead of a meaningless number.
    /// </summary>
    private static string CategoryOf(string relativeToUrlRoot, string name)
    {
        string[] parts = relativeToUrlRoot.Split('/');
        int asbres = Array.FindIndex(parts, static part => part.Equals("asbres", StringComparison.OrdinalIgnoreCase));

        if (asbres >= 0 && parts.Length > asbres + 2)
        {
            string category = string.Join('/', parts.Skip(asbres + 1).Take(3));
            if (!IsNumericCategory(category))
            {
                return category;
            }
        }

        // Fallback: first two directory segments after <game>/ui/.
        string directoryCategory = string.Join('/', parts.Take(Math.Max(0, parts.Length - 1)).Skip(2).Take(2));
        if (directoryCategory.Length > 0 &&
            !IsNumericCategory(directoryCategory) &&
            !directoryCategory.Equals("Sprite", StringComparison.OrdinalIgnoreCase) &&
            !directoryCategory.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
        {
            return directoryCategory;
        }

        return NamePrefix(name);
    }

    /// <summary>Numeric containers ("10000015666548753987") or empty path segments carry no meaning.</summary>
    private static bool IsNumericCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return true;
        }

        foreach (string part in category.Split('/'))
        {
            if (part.Length > 0 && !part.All(char.IsDigit))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>"pc_btn_primary_default" -> "pc"; "bubble_tooltip_arrow" -> "bubble".</summary>
    private static string NamePrefix(string name)
    {
        string prefix = name.Split('_', '-', ' ')[0].ToLowerInvariant();

        if (prefix.Length is < 2 or > 12 || !prefix.All(char.IsLetter))
        {
            return "其他";
        }

        return prefix;
    }

    private static string Escape(string relativePath) =>
        Uri.EscapeDataString(relativePath).Replace("%2F", "/");

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
