using System.Text.Json;

namespace AnimeStudio.Host;

/// <summary>Persisted host settings; the UI edits these through the message channel.</summary>
internal sealed class HostSettings
{
    public string Game { get; set; } = "SR";

    /// <summary>The game's <c>&lt;Game&gt;_Data</c> directory.</summary>
    public string? GameDataPath { get; set; }

    /// <summary>Where indexes, extracted text and images are written.</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>Optional explicit CLI path; otherwise auto-detected next to the host.</summary>
    public string? CliPath { get; set; }

    public string? MapPath { get; set; }

    public string IndexName { get; set; } = "game";
}

internal static class SettingsStore
{
    private static readonly string DirectoryPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeStudio");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "host.settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static HostSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                HostSettings? settings = JsonSerializer.Deserialize<HostSettings>(json, Options);
                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch (IOException)
        {
            // Corrupt or locked: fall back to defaults.
        }

        return new HostSettings();
    }

    public static void Save(HostSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
    }
}
