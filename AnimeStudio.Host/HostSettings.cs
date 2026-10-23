using System.Text.Json;

namespace AnimeStudio.Host;

/// <summary>
/// Per-game paths. Each game owns its own output directory as well: HSR and ZZZ extract
/// independently, so one game's re-export must never overwrite the other's files.
/// </summary>
internal sealed class GameProfile
{
    public string? DataPath { get; set; }

    public string? MapPath { get; set; }

    public string IndexName { get; set; } = "game";

    /// <summary>This game's output directory (text/, ui/, index/ and .cache/ live here).</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>
    /// Stable id written into the output folder (<c>.anime-studio.json</c>). If the folder is
    /// renamed in Explorer, the host can find it again by this id instead of asking the user.
    /// </summary>
    public string? OutputId { get; set; }
}

/// <summary>Persisted host settings; the UI edits these through the message channel.</summary>
internal sealed class HostSettings
{
    public string Game { get; set; } = "SR";

    /// <summary>
    /// Legacy shared output root (one root with SR/ and ZZZ/ subfolders). Kept only so older
    /// settings files can be migrated into per-game <see cref="GameProfile.OutputDirectory"/>.
    /// </summary>
    public string? OutputDirectory { get; set; }

    /// <summary>Optional explicit CLI path; otherwise auto-detected next to the host.</summary>
    public string? CliPath { get; set; }

    /// <summary>Client config-table dump root (StarRailData-style, contains <c>ExcelOutput/</c>).</summary>
    public string? ConfigTableSource { get; set; }

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
        MigrateSharedOutputRoot(settings);
        return settings;
    }

    public static void Save(HostSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
    }

    /// <summary>The directory a game extracted into before per-game output paths existed.</summary>
    public static string? LegacyGameRoot(HostSettings settings, string game) =>
        string.IsNullOrWhiteSpace(settings.OutputDirectory)
            ? null
            : Path.Combine(settings.OutputDirectory!, game);

    /// <summary>
    /// Older hosts kept one shared root and wrote into <c>root\SR</c> / <c>root\ZZZ</c>.
    /// Turn that layout into per-game settings, move the shared snapshots under each game's
    /// own cache folder, and leave a not-yet-configured game with the same default.
    /// </summary>
    private static void MigrateSharedOutputRoot(HostSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.OutputDirectory))
        {
            return;
        }

        foreach ((string game, GameProfile profile) in settings.Profiles)
        {
            MigrateProfile(settings, game, profile);
        }

        // The active game always gets a profile so the UI has a path to show.
        if (!settings.Profiles.ContainsKey(settings.Game))
        {
            settings.Profiles[settings.Game] = new GameProfile
            {
                OutputDirectory = Path.Combine(settings.OutputDirectory!, settings.Game),
            };
        }
    }

    private static void MigrateProfile(HostSettings settings, string game, GameProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.OutputDirectory))
        {
            profile.OutputDirectory = Path.Combine(settings.OutputDirectory!, game);
        }

        string? legacySnapshot = Path.Combine(
            settings.OutputDirectory!, ".cache", "snapshots", game + ".json.gz");
        string targetSnapshot = Path.Combine(
            profile.OutputDirectory!, ".cache", "snapshots", "snapshot.json.gz");

        if (!File.Exists(legacySnapshot) || File.Exists(targetSnapshot))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetSnapshot)!);
            File.Move(legacySnapshot, targetSnapshot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Snapshots are optional; the next compare simply starts a fresh baseline.
        }
    }
}
