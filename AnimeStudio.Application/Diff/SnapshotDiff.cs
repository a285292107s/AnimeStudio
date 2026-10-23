using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnimeStudio.Application.Diff;

/// <summary>A single image in a snapshot: only cheap metadata, enough to spot re-exports.</summary>
public sealed class ImageSnapshotEntry
{
    public int Width { get; set; }

    public int Height { get; set; }

    public long Bytes { get; set; }
}

/// <summary>
/// Baseline of one export run: every exported image's size. Stored gzip-compressed next to the
/// output, so "what changed after the game update?" can be answered without keeping the whole
/// previous dump.
/// </summary>
public sealed class Snapshot
{
    public string Game { get; set; } = string.Empty;

    public string CreatedAt { get; set; } = string.Empty;

    public Dictionary<string, ImageSnapshotEntry> Images { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record ImageChange(string Path, string Kind, ImageSnapshotEntry? Old, ImageSnapshotEntry? New);

public sealed class DiffResult
{
    public bool HasPrevious { get; set; }

    public int ImagesAdded { get; set; }

    public int ImagesRemoved { get; set; }

    public int ImagesChanged { get; set; }

    public List<ImageChange> ImageSamples { get; set; } = [];

    [JsonIgnore]
    public int TotalImageChanges => ImagesAdded + ImagesRemoved + ImagesChanged;
}

public static class SnapshotStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Snapshot? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using FileStream file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<Snapshot>(gzip, Options);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    public static void Save(string path, Snapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using FileStream file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
        JsonSerializer.Serialize(gzip, snapshot, Options);
    }
}

/// <summary>Compares two snapshots and keeps a bounded sample of the changes for the UI.</summary>
public static class SnapshotDiff
{
    private const int SampleLimit = 40;

    public static DiffResult Compare(Snapshot? previous, Snapshot current)
    {
        var result = new DiffResult
        {
            HasPrevious = previous is not null,
        };

        if (previous is null)
        {
            return result;
        }

        foreach (KeyValuePair<string, ImageSnapshotEntry> image in current.Images)
        {
            if (!previous.Images.TryGetValue(image.Key, out ImageSnapshotEntry? old))
            {
                result.ImagesAdded++;
                if (result.ImageSamples.Count < SampleLimit)
                {
                    result.ImageSamples.Add(new ImageChange(image.Key, "added", null, image.Value));
                }
            }
            else if (old.Width != image.Value.Width || old.Height != image.Value.Height || old.Bytes != image.Value.Bytes)
            {
                result.ImagesChanged++;
                if (result.ImageSamples.Count < SampleLimit)
                {
                    result.ImageSamples.Add(new ImageChange(image.Key, "changed", old, image.Value));
                }
            }
        }

        foreach (string path in previous.Images.Keys)
        {
            if (!current.Images.ContainsKey(path))
            {
                result.ImagesRemoved++;
                if (result.ImageSamples.Count < SampleLimit)
                {
                    result.ImageSamples.Add(new ImageChange(path, "removed", previous.Images[path], null));
                }
            }
        }

        return result;
    }
}
