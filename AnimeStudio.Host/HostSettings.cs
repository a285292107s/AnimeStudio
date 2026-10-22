using System.Text.Json;

namespace AnimeStudio.Host;

/// <summary>Per-game paths: two installs need two indexes and two maps.</summary>
internal sealed class GameProfile
{
    public string? DataPath { get; set; }

    public string? MapPath { get; set; }

    public string IndexName { get; set; } = "game";
}

/// <summary>Persisted host settings; the UI edits these through the message channel.</summary>
internal sealed class HostSettings
{
    public string Game { get; set; } = "SR";

    /// <summary>Shared output root; each game gets its own subdirectory.</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>Optional explicit CLI path; otherwise auto-detected next to the host.</summary>
    public string? CliPath { get; set; }

    public Dictionary<string, GameProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Legacy single-game fields, only read for migration.
    public string? GameDataPath { get; set; }

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
        HostSettings settings = new();

        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                settings = JsonSerializer.Deserialize<HostSettings>(json, Options) ?? new HostSettings();
            }
        }
        catch (IOException)
        {
            // Corrupt or locked: fall back to defaults.
        }

        // Migrate the old single-game layout into a profile for the current game.
        if (settings.Profiles.Count == 0 && !string.IsNullOrWhiteSpace(settings.GameDataPath))
        {
            settings.Profiles[settings.Game] = new GameProfile
            {
                DataPath = settings.GameDataPath,
                MapPath = settings.MapPath,
                IndexName = string.IsNullOrWhiteSpace(settings.IndexName) ? "game" : settings.IndexName,
            };
        }

        settings.Profiles ??= new Dictionary<string, GameProfile>(StringComparer.OrdinalIgnoreCase);
        return settings;
    }

    public static void Save(HostSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
    }
}
