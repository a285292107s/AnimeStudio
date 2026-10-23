using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnimeStudio.Application.Diff;
using AnimeStudio.Application.Install;

namespace AnimeStudio.Host;

/// <summary>
/// The host's only job: translate WebView messages into CLI invocations and local
/// file listings. Extraction itself stays in AnimeStudio.CLI / AnimeStudio.Application,
/// so the desktop UI and the command line can never drift apart.
/// </summary>
internal sealed class HostService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Action<string> _post;
    private readonly Func<string, string?> _pickFolder;
    private readonly Func<string?, string?> _mapOutputFolder;
    private readonly Func<string, string, string?>? _pickFile;
    private readonly CliRunner _runner;

    private List<ImageItem> _images = [];
    private List<ImageCategory> _categories = [];
    private readonly object _imageGate = new();
    private int _imageVersion;
    private readonly List<(string Game, string From, string To)> _outputSyncNotices = [];
    private CancellationTokenSource? _taskCts;

    private AssetMapScanner.MapStats? _mapStats;
    private string? _mapStatsPath;
    private long _indexCacheBytes;
    private string _indexFormat = string.Empty;
    private bool _mapStatsRunning;
    private bool _installScanRunning;
    private int _diffRunning;
    private bool _containerIndexLoading;
    private int _containerIndexDownloadRunning;

    public HostService(
        Action<string> post,
        Func<string, string?> pickFolder,
        Func<string?, string?> mapOutputFolder,
        Func<string, string, string?>? pickFile = null)
    {
        _post = post;
        _pickFolder = pickFolder;
        _mapOutputFolder = mapOutputFolder;
        _pickFile = pickFile;
        Settings = SettingsStore.Load();
        _runner = new CliRunner(Settings);
    }

    public HostSettings Settings { get; }

    /// <summary>Current game's paths (created on demand).</summary>
    public GameProfile Profile
    {
        get
        {
            if (!Settings.Profiles.TryGetValue(Settings.Game, out GameProfile? profile))
            {
                // A fresh profile inherits the old shared-root layout (<root>\<GAME>) until the
                // user points this game somewhere else.
                profile = new GameProfile
                {
                    OutputDirectory = SettingsStore.LegacyGameRoot(Settings, Settings.Game),
                };
                Settings.Profiles[Settings.Game] = profile;
            }

            return profile;
        }
    }

    /// <summary>Current game's own output directory; each game has an independent one.</summary>
    public string GameRoot => ResolveOutputDirectory(Settings.Game);

    /// <summary>Registers a URL for every configured game output before the page loads.</summary>
    public void WarmOutputMappings()
    {
        _mapOutputFolder(GameRoot);

        foreach (GameProfile profile in Settings.Profiles.Values)
        {
            if (!string.IsNullOrWhiteSpace(profile.OutputDirectory))
            {
                _mapOutputFolder(profile.OutputDirectory);
            }
        }
    }

    /// <summary>Base URL the shell exposes for the current game's output directory.</summary>
    private string OutputUrlPrefix() => _mapOutputFolder(GameRoot) ?? string.Empty;

    public void Handle(string rawJson)
    {
        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse(rawJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        string type = GetString(root, "type") ?? string.Empty;
        int requestId = GetInt(root, "requestId") ?? 0;

        switch (type)
        {
            case "hello":
                SendWelcome();
                break;
            case "settings.pick":
                HandlePick(root);
                break;
            case "settings.save":
                HandleSave(root);
                break;
            case "game.switch":
                SetGame(GetString(root, "game") ?? Settings.Game, notify: true);
                break;
            case "games.scan":
                QueueInstallScan(apply: true);
                break;
            case "diff.run":
                _ = Task.Run(() => PublishDiff(save: false));
                break;
            case "output.verify":
                // Lightweight "did the output folder get renamed?" check (polled by the UI).
                if (_taskCts is null)
                {
                    SynchronizeOutputPaths(notify: true);
                }

                Send(new { type = "ack", requestId, received = "output.verify" });
                break;
            case "task.run":
                _ = RunTaskAsync(root);
                break;
            case "task.cancel":
                _taskCts?.Cancel();
                break;
            case "images.query":
                _ = QueryImagesAsync(root, requestId);
                break;
            case "assets.query":
                _ = QueryAssetsAsync(root, requestId);
                break;
            case "open.folder":
                OpenOutputFolder();
                break;
            case "container.index.reload":
                QueueContainerIndexLoad(force: true);
                break;
            case "container.index.pick":
                PickContainerIndex();
                break;
            case "container.index.download":
                QueueContainerIndexDownload();
                break;
            case "container.index.clear":
                Settings.ContainerIndexPath = null;
                SettingsStore.Save(Settings);
                ContainerIndex.Unload();
                SendContainerIndexStatus();
                break;
            default:
                Send(new { type = "ack", received = type });
                break;
        }
    }

    /* ---------- welcome / settings / game ---------- */

    private void SendWelcome()
    {
        // Follow output folders renamed in Explorer before reporting paths to the UI.
        SynchronizeOutputPaths(notify: false);

        Send(new
        {
            type = "welcome",
            host = new
            {
                version = typeof(HostService).Assembly.GetName().Version?.ToString() ?? "0.1.0",
                cliPath = _runner.ResolveCliPath(),
                settingsFile = "host.settings.json",
            },
            outputSyncs = _outputSyncNotices
                .Select(notice => new { game = notice.Game, from = notice.From, to = notice.To })
                .ToArray(),
            settings = SettingsDto(),
            state = StateDto(),
        });

        _outputSyncNotices.Clear();

        QueueMapStats(force: false);
        QueueContainerIndexLoad(force: false);

        // First run (or a game that was never configured): look for the installs ourselves.
        bool missingPath = false;
        foreach (string game in new[] { GameInstallScanner.HonkaiStarRail, GameInstallScanner.ZenlessZoneZero })
        {
            if (!Settings.Profiles.TryGetValue(game, out GameProfile? profile) ||
                string.IsNullOrWhiteSpace(profile.DataPath))
            {
                missingPath = true;
                break;
            }
        }

        if (missingPath)
        {
            QueueInstallScan(apply: true);
        }
    }

    /// <summary>
    /// Registry + filesystem discovery of installed games. Only fills paths that are still
    /// empty: a manually chosen directory is never overwritten.
    /// </summary>
    private void QueueInstallScan(bool apply)
    {
        if (_installScanRunning)
        {
            Send(new { type = "games.scan.busy" });
            return;
        }

        _installScanRunning = true;
        Send(new { type = "games.scan.pending" });

        _ = Task.Run(() =>
        {
            try
            {
                List<string> roots = LauncherRegistry.GetGameRoots(
                    message => Send(new { type = "log", level = "info", message = $"[host] {message}" }));

                IReadOnlyList<DetectedInstall> installs = GameInstallScanner.Scan(roots);

                var detected = new List<object>();
                bool changed = false;

                foreach (DetectedInstall install in installs)
                {
                    if (!Settings.Profiles.TryGetValue(install.Game, out GameProfile? profile))
                    {
                        profile = new GameProfile();
                        Settings.Profiles[install.Game] = profile;
                    }

                    bool applied = false;
                    if (apply && string.IsNullOrWhiteSpace(profile.DataPath) && Directory.Exists(install.GameDataPath))
                    {
                        profile.DataPath = install.GameDataPath;
                        applied = true;
                        changed = true;
                    }

                    detected.Add(new
                    {
                        game = install.Game,
                        gameDataPath = install.GameDataPath,
                        source = install.Source,
                        applied,
                    });
                }

                if (changed)
                {
                    SettingsStore.Save(Settings);
                }

                Send(new
                {
                    type = "games.detected",
                    installs = detected,
                    settings = SettingsDto(),
                    state = StateDto(),
                });
            }
            catch (Exception exception)
            {
                Send(new { type = "games.scan.failed", message = exception.Message });
            }
            finally
            {
                _installScanRunning = false;
            }
        });
    }

    private void SetGame(string game, bool notify)
    {
        if (string.IsNullOrWhiteSpace(game))
        {
            return;
        }

        SynchronizeOutputPaths(notify: notify);

        if (!string.Equals(game, Settings.Game, StringComparison.OrdinalIgnoreCase))
        {
            Settings.Game = game;
            SettingsStore.Save(Settings);

            InvalidateImageIndex();
            _mapStats = null;
            _mapStatsPath = null;
            _indexCacheBytes = 0;
            _indexFormat = string.Empty;

            // out.local serves the active game's own directory, so PNG URLs stay portable.
            _mapOutputFolder(GameRoot);
        }

        if (notify)
        {
            Send(new { type = "game.switched", settings = SettingsDto(), state = StateDto() });
        }

        if (string.IsNullOrWhiteSpace(Profile.DataPath))
        {
            QueueInstallScan(apply: true);
        }

        QueueMapStats(force: false);
    }

    /// <summary>Resolved output directory for one game (per-game setting, else legacy shared root).</summary>
    private string ResolveOutputDirectory(string game)
    {
        if (Settings.Profiles.TryGetValue(game, out GameProfile? profile) &&
            !string.IsNullOrWhiteSpace(profile.OutputDirectory))
        {
            return profile.OutputDirectory!.Trim();
        }

        return SettingsStore.LegacyGameRoot(Settings, game)?.Trim() ?? string.Empty;
    }

    private object SettingsDto() => new
    {
        game = Settings.Game,
        gameLabel = GameLabel(Settings.Game),
        gameDataPath = Profile.DataPath,
        outputDirectory = GameRoot,
        cliPath = Settings.CliPath,
        containerIndexPath = Settings.ContainerIndexPath,
        mapPath = MapPath(),
        indexName = Profile.IndexName,
        profiles = Settings.Profiles.Keys,
        // Both games, already resolved: the settings dialog and the rail OUTPUT block must
        // never disagree about which directory a game exports to.
        outputDirectories = new[] { GameInstallScanner.HonkaiStarRail, GameInstallScanner.ZenlessZoneZero }
            .Concat(Settings.Profiles.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(game => new { game, path = ResolveOutputDirectory(game) })
            .Where(static entry => entry.path.Length > 0)
            .ToDictionary(static entry => entry.game, static entry => entry.path, StringComparer.OrdinalIgnoreCase),
    };

    private object StateDto() => new
    {
        game = Settings.Game,
        mapExists = File.Exists(MapPath()),
        images = _images.Count,
        categories = _categories,
        indexInfo = new
        {
            game = Settings.Game,
            mapBytes = SafeFileLength(MapPath()),
            mapPath = MapPath(),
            cacheBytes = _indexCacheBytes,
            mapFormat = _indexFormat,
            mapModified = SafeFileModified(MapPath()),
        },
        outputDirectory = GameRoot,
        containerIndex = ContainerIndexDto(),
    };

    /// <summary>ZZZ hash → path table status, shown in the settings dialog and the rail.</summary>
    private object ContainerIndexDto() => new
    {
        loaded = ContainerIndex.IsLoaded,
        count = ContainerIndex.Count,
        path = ContainerIndex.LoadedFrom ?? Settings.ContainerIndexPath,
        loading = _containerIndexLoading,
        downloading = _containerIndexDownloadRunning > 0,
    };

    private void HandlePick(JsonElement root)
    {
        string target = GetString(root, "target") ?? "game";
        string game = GetString(root, "game") ?? Settings.Game;
        string? picked = _pickFolder(target switch
        {
            "game" => "选择游戏的 <Game>_Data 目录",
            "output" => $"选择 {GameLabel(game)} 的输出目录",
            _ => "选择目录",
        });

        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }

        switch (target)
        {
            case "game":
                Profile.DataPath = picked;
                break;
            case "output":
                ApplyOutputDirectory(game, picked);
                break;
            default:
                return;
        }

        SettingsStore.Save(Settings);
        SendSettingsChanged();
    }

    /// <summary>
    /// Points one game at its own output directory (the other game keeps its own path).
    /// Only the active game's caches need to be dropped and reloaded.
    /// </summary>
    private void ApplyOutputDirectory(string game, string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Send(new { type = "log", level = "warn", message = $"[host] 无法创建输出目录：{exception.Message}" });
        }

        GameProfile profile = GetProfile(game);
        profile.OutputDirectory = directory;

        // Stamp the folder so a later Explorer rename can be followed automatically.
        string? outputId = profile.OutputId;
        if (OutputLocator.TryEnsureMarker(directory, game, ref outputId))
        {
            profile.OutputId = outputId;
        }

        _mapOutputFolder(directory);

        if (string.Equals(game, Settings.Game, StringComparison.OrdinalIgnoreCase))
        {
            ResetGameOutputs();
            QueueMapStats(force: true);
        }

        LogInfo($"[host] {GameLabel(game)} 导出目录 → {directory}");
    }

    private GameProfile GetProfile(string game)
    {
        if (!Settings.Profiles.TryGetValue(game, out GameProfile? profile))
        {
            profile = new GameProfile
            {
                OutputDirectory = SettingsStore.LegacyGameRoot(Settings, game),
            };
            Settings.Profiles[game] = profile;
        }

        return profile;
    }

    /// <summary>Drops everything that was read from the previous output directory.</summary>
    private void ResetGameOutputs()
    {
        InvalidateImageIndex();
        _mapStats = null;
        _mapStatsPath = null;
        _indexCacheBytes = 0;
        _indexFormat = string.Empty;
    }

    private void HandleSave(JsonElement root)
    {
        string previousMapPath = MapPath();
        bool switchGame = false;
        bool reloadContainerIndex = false;
        string? gameName = null;

        if (root.TryGetProperty("settings", out JsonElement settings) && settings.ValueKind == JsonValueKind.Object)
        {
            string? game = GetString(settings, "game");
            if (!string.IsNullOrWhiteSpace(game) && !string.Equals(game, Settings.Game, StringComparison.OrdinalIgnoreCase))
            {
                switchGame = true;
                gameName = game;
            }

            if (TryGetOptionalString(settings, "gameDataPath", out string? dataPath))
            {
                Profile.DataPath = Blank(dataPath);
            }

            if (TryGetOptionalString(settings, "cliPath", out string? cliPath))
            {
                Settings.CliPath = Blank(cliPath);
            }

            if (TryGetOptionalString(settings, "mapPath", out string? mapPath))
            {
                Profile.MapPath = Blank(mapPath);
            }

            if (TryGetOptionalString(settings, "indexName", out string? indexName) && !string.IsNullOrWhiteSpace(indexName))
            {
                Profile.IndexName = indexName.Trim();
            }

            if (TryGetOptionalString(settings, "containerIndexPath", out string? containerIndexPath))
            {
                string? trimmed = Blank(containerIndexPath);
                if (!string.Equals(trimmed, Settings.ContainerIndexPath, StringComparison.OrdinalIgnoreCase))
                {
                    Settings.ContainerIndexPath = trimmed;
                    reloadContainerIndex = true;
                }
            }

            // New UI sends both games' paths at once; older markup sends a single path that
            // belongs to the active game.
            if (settings.TryGetProperty("outputDirectories", out JsonElement outputDirectories) &&
                outputDirectories.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty entry in outputDirectories.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string gameKey = entry.Name;
                    string trimmed = entry.Value.GetString()?.Trim() ?? string.Empty;
                    if (trimmed.Length == 0)
                    {
                        continue;
                    }

                    string existing = GetProfile(gameKey).OutputDirectory?.Trim() ?? string.Empty;
                    if (string.Equals(trimmed, existing, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    ApplyOutputDirectory(gameKey, trimmed);
                }
            }
            else if (TryGetOptionalString(settings, "outputDirectory", out string? output))
            {
                string trimmed = output?.Trim() ?? string.Empty;
                if (trimmed.Length > 0 &&
                    !string.Equals(trimmed, GameRoot, StringComparison.OrdinalIgnoreCase))
                {
                    ApplyOutputDirectory(Settings.Game, trimmed);
                }
            }
        }

        if (switchGame)
        {
            SetGame(gameName ?? Settings.Game, notify: true);
        }

        SettingsStore.Save(Settings);
        SynchronizeOutputPaths(notify: true);
        SendSettingsChanged();

        if (!string.Equals(previousMapPath, MapPath(), StringComparison.OrdinalIgnoreCase))
        {
            _mapStats = null;
            _mapStatsPath = null;
            _indexCacheBytes = 0;
            _indexFormat = string.Empty;
            QueueMapStats(force: true);
        }

        if (reloadContainerIndex)
        {
            QueueContainerIndexLoad(force: true);
        }
    }

    private void SendSettingsChanged()
    {
        Send(new { type = "settings.changed", settings = SettingsDto(), state = StateDto() });
    }

    /// <summary>
    /// Follows output folders that were renamed/moved in Explorer. Every configured folder gets
    /// a <c>.anime-studio.json</c> marker with a stable id; when the saved path no longer exists
    /// we search its parent/grandparent for that id and update the settings + map path in place.
    /// </summary>
    private void SynchronizeOutputPaths(bool notify)
    {
        bool changed = false;
        bool activeChanged = false;
        var notices = new List<(string Game, string From, string To)>();

        foreach (string game in KnownGames())
        {
            if (!Settings.Profiles.TryGetValue(game, out GameProfile? profile))
            {
                continue;
            }

            string? configured = profile.OutputDirectory?.Trim();
            if (string.IsNullOrWhiteSpace(configured))
            {
                continue;
            }

            string? outputId = profile.OutputId;
            if (Directory.Exists(configured))
            {
                if (OutputLocator.TryEnsureMarker(configured, game, ref outputId))
                {
                    profile.OutputId = outputId;
                    changed = true;
                }

                continue;
            }

            if (!OutputLocator.TryFindRenamed(configured, profile.OutputId, game, out string moved))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(profile.MapPath) && IsPathUnder(profile.MapPath, configured))
            {
                // The map usually lives in <output>\index; carry it over to the new folder.
                profile.MapPath = moved + profile.MapPath![configured.Length..];
            }

            profile.OutputDirectory = moved;
            outputId = profile.OutputId;
            if (OutputLocator.TryEnsureMarker(moved, game, ref outputId))
            {
                profile.OutputId = outputId;
            }

            changed = true;
            if (string.Equals(game, Settings.Game, StringComparison.OrdinalIgnoreCase))
            {
                activeChanged = true;
            }

            notices.Add((game, configured, moved));
            LogInfo($"[host] {GameLabel(game)} 输出目录已移动，自动同步：{configured} → {moved}");
        }

        if (!changed)
        {
            return;
        }

        SettingsStore.Save(Settings);

        if (activeChanged)
        {
            // Everything cached from the old path (gallery, map stats) is stale.
            InvalidateImageIndex();
            _mapStats = null;
            _mapStatsPath = null;
            _indexCacheBytes = 0;
            _indexFormat = string.Empty;
            if (notify)
            {
                QueueMapStats(force: true);
            }
        }

        if (!notify)
        {
            _outputSyncNotices.AddRange(notices);
            return;
        }

        foreach ((string game, string from, string to) in notices)
        {
            Send(new { type = "output.synced", game, from, to });
        }

        SendSettingsChanged();
    }

    private IEnumerable<string> KnownGames() =>
        new[] { GameInstallScanner.HonkaiStarRail, GameInstallScanner.ZenlessZoneZero }
            .Concat(Settings.Profiles.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or inside it.</summary>
    private static bool IsPathUnder(string path, string root)
    {
        try
        {
            string normalizedPath = Path.GetFullPath(path);
            string normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /* ---------- ZZZ container index (hash → path) ---------- */

    /// <summary>Upstream publishes the index as a plain JSON object; ~80 MB, cached to a binary file on load.</summary>
    private const string ContainerIndexUrl =
        "https://raw.githubusercontent.com/Eleiyas/Z3-Asset-Index/main/Z3-AssetIndex-Eleiyas.json";

    private static string ContainerIndexDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeStudio", "Maps");

    private static string DefaultContainerIndexFile =>
        Path.Combine(ContainerIndexDirectory, ContainerIndex.DefaultFileName);

    /// <summary>Loads the index from the configured path or the default locations, off the UI thread.</summary>
    private void QueueContainerIndexLoad(bool force)
    {
        if (_containerIndexLoading)
        {
            return;
        }

        _containerIndexLoading = true;
        Send(new { type = "container.index.pending" });

        string? configured = Settings.ContainerIndexPath;
        _ = Task.Run(() =>
        {
            try
            {
                string? path = !string.IsNullOrWhiteSpace(configured) && File.Exists(configured)
                    ? configured
                    : ContainerIndex.FindDefault();

                if (path is null)
                {
                    return;
                }

                if (!force &&
                    ContainerIndex.IsLoaded &&
                    string.Equals(ContainerIndex.LoadedFrom, path, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (ContainerIndex.TryLoad(path, out string error))
                {
                    LogInfo($"[containers] {ContainerIndex.Count:N0} 条 hash → 路径（{ContainerIndex.LoadedFrom}）");
                }
                else
                {
                    LogInfo($"[containers] 无法读取容器索引：{error}");
                }
            }
            finally
            {
                _containerIndexLoading = false;
                SendContainerIndexStatus();
            }
        });
    }

    private void SendContainerIndexStatus() => Send(new
    {
        type = "container.index",
        loaded = ContainerIndex.IsLoaded,
        count = ContainerIndex.Count,
        path = ContainerIndex.LoadedFrom ?? Settings.ContainerIndexPath,
        loading = _containerIndexLoading,
        downloading = _containerIndexDownloadRunning > 0,
    });

    private void PickContainerIndex()
    {
        string? picked = _pickFile?.Invoke(
            "选择 ZZZ 容器索引（Z3-AssetIndex-Eleiyas.json）",
            "JSON (*.json)|*.json|所有文件 (*.*)|*.*");

        if (string.IsNullOrWhiteSpace(picked))
        {
            return;
        }

        Settings.ContainerIndexPath = picked;
        SettingsStore.Save(Settings);
        QueueContainerIndexLoad(force: true);
    }

    /// <summary>Downloads the Z3 index into %LOCALAPPDATA%\AnimeStudio\Maps and loads it.</summary>
    private void QueueContainerIndexDownload()
    {
        if (Interlocked.Exchange(ref _containerIndexDownloadRunning, 1) == 1)
        {
            Send(new { type = "container.index.busy" });
            return;
        }

        SendContainerIndexStatus();
        _ = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(ContainerIndexDirectory);
                string target = DefaultContainerIndexFile;
                string temporary = target + ".download";
                Send(new { type = "container.index.progress", received = 0L, total = 0L });

                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                using HttpResponseMessage response =
                    await client.GetAsync(ContainerIndexUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                long total = response.Content.Headers.ContentLength ?? 0;
                long received = 0;
                long nextReport = 0;

                await using (Stream input = await response.Content.ReadAsStreamAsync())
                await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    var buffer = new byte[1 << 20];
                    int read;
                    while ((read = await input.ReadAsync(buffer)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read));
                        received += read;
                        if (received >= nextReport)
                        {
                            nextReport = received + (4L << 20);
                            Send(new { type = "container.index.progress", received, total });
                        }
                    }
                }

                File.Move(temporary, target, overwrite: true);

                Settings.ContainerIndexPath = target;
                SettingsStore.Save(Settings);

                if (ContainerIndex.TryLoad(target, out string error))
                {
                    LogInfo($"[containers] 已下载并加载 {ContainerIndex.Count:N0} 条 hash → 路径");
                }
                else
                {
                    Send(new { type = "log", level = "warn", message = $"[host] 容器索引下载完成，但读取失败：{error}" });
                }
            }
            catch (Exception exception)
            {
                Send(new { type = "container.index.failed", message = Humanize(exception.Message) ?? exception.Message });
            }
            finally
            {
                Interlocked.Exchange(ref _containerIndexDownloadRunning, 0);
                SendContainerIndexStatus();
            }
        });
    }

    /* ---------- map statistics ---------- */

    private void QueueMapStats(bool force)
    {
        string mapPath = MapPath();
        if (mapPath.Length == 0 || !File.Exists(mapPath))
        {
            return;
        }

        if (!force && _mapStats is not null && string.Equals(_mapStatsPath, mapPath, StringComparison.OrdinalIgnoreCase))
        {
            SendMapStats();
            return;
        }

        if (_mapStatsRunning)
        {
            return;
        }

        _mapStatsRunning = true;
        Send(new { type = "map.stats.pending" });

        _ = Task.Run(() =>
        {
            try
            {
                AssetMapScanner.MapStats stats = AssetMapScanner.ScanStats(mapPath);
                _mapStats = stats;
                _mapStatsPath = mapPath;
                _indexCacheBytes = ComputeIndexCacheBytes(mapPath);
                _indexFormat = DetectMapFormat(mapPath);
                SendMapStats();
            }
            catch (Exception exception)
            {
                Send(new { type = "map.stats.failed", message = Humanize(exception.Message) ?? exception.Message });
            }
            finally
            {
                _mapStatsRunning = false;
            }
        });
    }

    private void SendMapStats()
    {
        if (_mapStats is null)
        {
            return;
        }

        Send(new
        {
            type = "map.stats",
            totalEntries = _mapStats.TotalEntries,
            sourceFiles = _mapStats.SourceFiles,
            sprites = _mapStats.CountOf(ClassIDType.Sprite),
            textures = _mapStats.CountOf(ClassIDType.Texture2D),
            textAssets = _mapStats.CountOf(ClassIDType.TextAsset),
            elapsedMs = (long)_mapStats.Elapsed.TotalMilliseconds,
            mapPath = _mapStatsPath,
            mapBytes = SafeFileLength(_mapStatsPath),
            cacheBytes = _indexCacheBytes,
            mapFormat = _indexFormat,
            mapModified = SafeFileModified(_mapStatsPath),
        });
    }

    /* ---------- tasks ---------- */

    private async Task RunTaskAsync(JsonElement root)
    {
        if (_taskCts is not null)
        {
            Send(new { type = "task.busy" });
            return;
        }

        // A task must never write into a stale (renamed) path.
        SynchronizeOutputPaths(notify: true);

        string kind = GetString(root, "kind") ?? string.Empty;
        JsonElement payload = root.TryGetProperty("payload", out JsonElement value) ? value : default;

        string requestedGame = GetString(payload, "game") ?? Settings.Game;
        if (!string.Equals(requestedGame, Settings.Game, StringComparison.OrdinalIgnoreCase))
        {
            SetGame(requestedGame, notify: true);
        }

        string? gameDataPath = GetString(payload, "gameDataPath") ?? Profile.DataPath;

        if (string.IsNullOrWhiteSpace(gameDataPath) || !Directory.Exists(gameDataPath))
        {
            Send(new { type = "task.failed", kind, message = "请先在设置里选择游戏的 <Game>_Data 目录。" });
            return;
        }

        if (GameRoot.Length == 0)
        {
            Send(new { type = "task.failed", kind, message = $"请先为 {GameLabel(Settings.Game)} 选择输出目录。" });
            return;
        }

        List<string> arguments;
        try
        {
            arguments = BuildArguments(kind, Settings.Game, gameDataPath, payload);
        }
        catch (InvalidOperationException exception)
        {
            Send(new { type = "task.failed", kind, message = exception.Message });
            return;
        }

        _taskCts = new CancellationTokenSource();
        string id = Guid.NewGuid().ToString("N")[..8];
        Send(new { type = "task.started", id, kind });

        try
        {
            double lastFraction = -1;
            long stableTotal = -1;
            var hintsShown = new HashSet<string>(StringComparer.Ordinal);

            int exitCode = await _runner
                .RunAsync(arguments, (line, isError) =>
                {
                    string level = LevelOf(line, isError);
                    Send(new
                    {
                        type = "log",
                        level,
                        message = line,
                    });

                    string? hint = Humanize(line);
                    if (hint is not null && hintsShown.Add(hint))
                    {
                        Send(new { type = "task.hint", message = hint });
                    }                    if (!TryParseProgress(line, out double fraction, out long total))
                    {
                        return;
                    }

                    // A denominator that changes between lines means the CLI is reporting
                    // per-bundle progress (each block has its own 1..N); that bar would jump
                    // backwards, so switch the UI to an indeterminate pulse instead.
                    bool stable = total <= 0 || stableTotal <= 0 || total == stableTotal;
                    if (total > 0 && stableTotal <= 0)
                    {
                        stableTotal = total;
                    }

                    if (!stable)
                    {
                        Send(new { type = "task.progress", fraction = (double?)null, message = line });
                        return;
                    }

                    if (lastFraction < 0 || Math.Abs(fraction - lastFraction) >= 0.01)
                    {
                        lastFraction = fraction;
                        Send(new { type = "task.progress", fraction, message = line });
                    }
                }, _taskCts.Token)
                .ConfigureAwait(false);

            ReloadOutputs(kind);
            if (kind == "index")
            {
                QueueMapStats(force: true);
            }

            if (exitCode == 0 && kind == "images")
            {
                // Snapshot + diff is a few seconds of gzip work; do not hold up task.done.
                _ = Task.Run(() => PublishDiff(save: true));
            }

            Send(new
            {
                type = "task.done",
                id,
                kind,
                exitCode,
                ok = exitCode == 0,
                state = StateDto(),
            });
        }
        catch (OperationCanceledException)
        {
            Send(new { type = "task.cancelled", id, kind });
        }
        catch (Exception exception)
        {
            Send(new { type = "task.failed", id, kind, message = Humanize(exception.Message) ?? exception.Message });
        }
        finally
        {
            _taskCts.Dispose();
            _taskCts = null;
        }
    }

    private List<string> BuildArguments(string kind, string game, string gameDataPath, JsonElement payload)
    {
        switch (kind)
        {
            case "index":
                {
                    string indexDirectory = Path.Combine(GameRoot, "index");
                    string mapName = string.IsNullOrWhiteSpace(Profile.IndexName) ? "game" : Profile.IndexName;
                    Profile.MapPath = Path.Combine(indexDirectory, mapName + ".map");
                    SettingsStore.Save(Settings);

                    return
                    [
                        gameDataPath, indexDirectory,
                        "--game", game,
                        "--map_op", "AssetMap",
                        "--map_type", "MessagePack",
                        "--map_name", mapName,
                        .. ContainerIndexArguments(),
                    ];
                }

            case "images":
                {
                    string mapPath = MapPath();
                    if (!File.Exists(mapPath))
                    {
                        throw new InvalidOperationException("找不到 AssetMap，请先建索引。");
                    }

                    var arguments = new List<string>
                    {
                        gameDataPath, Path.Combine(GameRoot, "ui"),
                        "--game", game,
                        "--map_op", "AssetMap,Load",
                        "--map_type", "MessagePack",
                        "--map_name", mapPath,
                        "--group_assets", "ByContainer",
                    };

                    string[] types = GetStringArray(payload, "types");
                    arguments.Add("--types");
                    if (types.Length > 0)
                    {
                        arguments.AddRange(types);
                    }
                    else
                    {
                        arguments.Add("Sprite");
                        arguments.Add("Texture2D");
                    }

                    string[] containers = GetStringArray(payload, "containers");
                    if (containers.Length > 0)
                    {
                        arguments.Add("--containers");
                        arguments.AddRange(containers);
                    }

                    string[] names = GetStringArray(payload, "names");
                    if (names.Length > 0)
                    {
                        // Picking specific images should not load every block that happens to
                        // contain a same-named duplicate.
                        arguments.Add("--dedupe_names");
                        arguments.Add("--names");
                        arguments.AddRange(names);
                    }

                    arguments.AddRange(ContainerIndexArguments());
                    return arguments;
                }

            default:
                throw new InvalidOperationException($"未知任务：{kind}");
        }
    }

    /// <summary>Passes the index path to the CLI so a custom location works for builds and exports.</summary>
    private string[] ContainerIndexArguments()
    {
        string? path = ContainerIndex.LoadedFrom ?? Settings.ContainerIndexPath;
        return string.IsNullOrWhiteSpace(path) || !File.Exists(path)
            ? []
            : ["--container_index", path];
    }

    private void ReloadOutputs(string kind)
    {
        switch (kind)
        {
            case "images":
                InvalidateImageIndex();
                if (GameRoot.Length > 0)
                {
                    EnsureImageIndex();
                }

                break;
            case "index":
                Profile.MapPath = MapPath();
                SettingsStore.Save(Settings);
                break;
        }
    }

    /* ---------- queries ---------- */

    /// <summary>
    /// Image query runs off the message thread: scanning a freshly selected output folder can
    /// walk tens of thousands of PNGs, and doing that inline froze the whole window.
    /// </summary>
    private Task QueryImagesAsync(JsonElement root, int requestId) => Task.Run(() =>
    {
        try
        {
            if (_images.Count == 0 && GameRoot.Length > 0)
            {
                // Tell the UI why the grid stays empty for a moment; no requestId on purpose
                // (a matching id would resolve the pending call with the wrong message type).
                Send(new { type = "images.scanning" });
            }

            EnsureImageIndex();

            string query = GetString(root, "query") ?? string.Empty;
            string category = GetString(root, "category") ?? "all";
            int offset = Math.Max(0, GetInt(root, "offset") ?? 0);
            int limit = Math.Clamp(GetInt(root, "limit") ?? 120, 1, 500);

            IEnumerable<ImageItem> filtered = _images;

            if (!string.IsNullOrWhiteSpace(query))
            {
                string needle = query.Trim();
                filtered = filtered.Where(item => item.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(category) && !category.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                filtered = filtered.Where(item => item.Category.StartsWith(category, StringComparison.OrdinalIgnoreCase));
            }

            List<ImageItem> matched = filtered.ToList();
            List<ImageItem> page = matched.Skip(offset).Take(limit).ToList();
            string gameRoot = GameRoot;

            // Thumbnails for a fresh page: GDI+ resizing in parallel cuts the first-page wait.
            var thumbOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 4),
            };

            Parallel.ForEach(page, thumbOptions, item =>
            {
                ImageIndex.EnsureThumbnail(
                    Path.Combine(gameRoot, item.RelativePath),
                    Path.Combine(gameRoot, item.ThumbRelativePath));
            });

            Send(new
            {
                type = "images.page",
                requestId,
                total = matched.Count,
                offset,
                available = _images.Count,
                categories = _categories,
                items = page,
            });
        }
        catch (Exception exception)
        {
            Send(new { type = "images.failed", requestId, message = exception.Message });
        }
    });

    /* ---------- asset browser (AssetMap filters) ---------- */

    /// <summary>Asset Browser query, mirroring the original GUI: filter by name/container/source/type.</summary>
    private Task QueryAssetsAsync(JsonElement root, int requestId) => Task.Run(() =>
    {
        string mapPath = MapPath();
        if (!File.Exists(mapPath))
        {
            Send(new { type = "assets.failed", requestId, message = "找不到 AssetMap，请先建索引。" });
            return;
        }

        try
        {
            bool rawRegex = root.ValueKind == JsonValueKind.Object &&
                            root.TryGetProperty("regex", out JsonElement regexElement) &&
                            regexElement.ValueKind == JsonValueKind.True;

            Regex[] nameFilter = BuildRegexFilters(GetString(root, "name") ?? string.Empty, rawRegex);
            Regex[] containerFilter = BuildRegexFilters(GetString(root, "container") ?? string.Empty, rawRegex);
            Regex[] sourceFilter = BuildRegexFilters(GetString(root, "source") ?? string.Empty, rawRegex);
            ClassIDType[] typeFilter = ParseTypes(GetStringArray(root, "types"));
            int limit = Math.Clamp(GetInt(root, "limit") ?? 300, 1, 1000);

            AssetMapScanner.QueryResult result = AssetMapScanner.Query(
                mapPath, typeFilter, nameFilter, containerFilter, sourceFilter, limit);

            Send(new
            {
                type = "assets.page",
                requestId,
                total = result.MatchedEntries,
                available = result.TotalEntries,
                sources = result.Sources.Count,
                truncated = result.Truncated,
                elapsedMs = (long)result.Elapsed.TotalMilliseconds,
                items = result.Items.Select(static item => new
                {
                    name = item.Name,
                    container = item.Container,
                    source = Path.GetFileName(item.Source),
                    sourcePath = item.Source,
                    type = item.Type.ToString(),
                    // ZZZ path ids are 64-bit hashes: send them as text so the UI does not round them.
                    pathId = item.PathId.ToString(CultureInfo.InvariantCulture),
                    hash = item.Hash,
                }),
            });
        }
        catch (Exception exception)
        {
            Send(new { type = "assets.failed", requestId, message = Humanize(exception.Message) ?? exception.Message });
        }
    });

    private static Regex[] BuildRegexFilters(string text, bool rawRegex)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        string pattern = rawRegex ? text : Regex.Escape(text.Trim());
        return [new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)];
    }

    private static ClassIDType[] ParseTypes(IEnumerable<string> names)
    {
        var types = new List<ClassIDType>();

        foreach (string name in names)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Enum.TryParse(name, ignoreCase: true, out ClassIDType type))
            {
                types.Add(type);
            }
        }

        return types.ToArray();
    }

    /// <summary>
    /// Scans the output directory once, off the UI thread. A reset (output change / game switch)
    /// bumps the generation so an in-flight scan cannot publish results for the old directory.
    /// </summary>
    private void EnsureImageIndex()
    {
        if (_images.Count > 0 || GameRoot.Length == 0)
        {
            return;
        }

        lock (_imageGate)
        {
            if (_images.Count > 0)
            {
                return;
            }

            string gameRoot = GameRoot;
            if (gameRoot.Length == 0)
            {
                return;
            }

            int version = Volatile.Read(ref _imageVersion);
            (List<ImageItem> items, List<ImageCategory> categories) = ImageIndex.Scan(gameRoot, OutputUrlPrefix());
            if (version == Volatile.Read(ref _imageVersion))
            {
                _images = items;
                _categories = categories;
            }
        }
    }

    private void InvalidateImageIndex()
    {
        Interlocked.Increment(ref _imageVersion);
        _images = [];
        _categories = [];
    }

    /* ---------- version diff ---------- */

    private string SnapshotPath() => GameRoot.Length == 0
        ? string.Empty
        : Path.Combine(GameRoot, ".cache", "snapshots", "snapshot.json.gz");

    /// <summary>
    /// Snapshots used to live in the shared root (<c>root\.cache\snapshots\SR.json.gz</c>).
    /// Load that old file when the per-game one does not exist yet, and drop the game prefix
    /// from image keys (old keys were <c>SR/ui/…</c>, new keys are <c>ui/…</c>).
    /// </summary>
    private Snapshot? LoadPreviousSnapshot(string path)
    {
        Snapshot? previous = SnapshotStore.Load(path);

        if (previous is null && SettingsStore.LegacyGameRoot(Settings, Settings.Game) is { } legacyRoot)
        {
            previous = SnapshotStore.Load(
                Path.Combine(legacyRoot, ".cache", "snapshots", Settings.Game + ".json.gz"));
        }

        if (previous is not null)
        {
            StripLegacyImagePrefix(previous, Settings.Game);
        }

        return previous;
    }

    private static void StripLegacyImagePrefix(Snapshot snapshot, string game)
    {
        string prefix = game + "/";
        var rewritten = new Dictionary<string, ImageSnapshotEntry>(snapshot.Images.Count, StringComparer.OrdinalIgnoreCase);
        bool changed = false;

        foreach ((string key, ImageSnapshotEntry value) in snapshot.Images)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                rewritten[key[prefix.Length..]] = value;
                changed = true;
            }
            else
            {
                rewritten[key] = value;
            }
        }

        if (changed)
        {
            snapshot.Images = rewritten;
        }
    }

    /// <summary>
    /// Builds a snapshot of the current output, compares it with the previous extraction and
    /// (on export runs) stores the new baseline. First run just records the baseline.
    /// </summary>
    private void PublishDiff(bool save)
    {
        string path = SnapshotPath();
        if (path.Length == 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _diffRunning, 1) == 1)
        {
            return;
        }

        try
        {
            Snapshot? previous = LoadPreviousSnapshot(path);
            Snapshot current = BuildSnapshot();
            DiffResult diff = SnapshotDiff.Compare(previous, current);

            // Export runs refresh the baseline; a manual compare only records it when missing.
            bool wroteBaseline = save || previous is null;
            if (wroteBaseline)
            {
                SnapshotStore.Save(path, current);
            }

            Send(new
            {
                type = "diff.result",
                saved = wroteBaseline,
                hasPrevious = diff.HasPrevious,
                images = new
                {
                    added = diff.ImagesAdded,
                    removed = diff.ImagesRemoved,
                    changed = diff.ImagesChanged,
                    samples = diff.ImageSamples,
                },
            });
        }
        catch (Exception exception)
        {
            Send(new { type = "log", level = "warn", message = $"[host] 版本对比失败：{exception.Message}" });
        }
        finally
        {
            Interlocked.Exchange(ref _diffRunning, 0);
        }
    }

    private Snapshot BuildSnapshot()
    {
        EnsureImageIndex();

        var snapshot = new Snapshot
        {
            Game = Settings.Game,
            CreatedAt = DateTimeOffset.Now.ToString("O"),
        };

        foreach (ImageItem image in _images)
        {
            snapshot.Images[image.RelativePath] = new ImageSnapshotEntry
            {
                Width = image.Width,
                Height = image.Height,
                Bytes = image.Bytes,
            };
        }

        return snapshot;
    }

    private void OpenOutputFolder()
    {
        SynchronizeOutputPaths(notify: true);
        string directory = GameRoot;
        if (directory.Length == 0 || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Explorer unavailable: not worth failing the request.
        }
    }

    /* ---------- helpers ---------- */

    private static string GameLabel(string game) => game.ToUpperInvariant() switch
    {
        "SR" or "STARRAIL" => "星穹铁道",
        "ZZZ" or "ZENLESS" => "绝区零",
        _ => game,
    };

    /// <summary>Diagnostics from the service into the UI log.</summary>
    public void LogInfo(string message) => Send(new { type = "log", level = "info", message });

    public string MapPath()
    {
        if (!string.IsNullOrWhiteSpace(Profile.MapPath))
        {
            return Profile.MapPath!;
        }

        if (GameRoot.Length == 0)
        {
            return string.Empty;
        }

        string mapName = string.IsNullOrWhiteSpace(Profile.IndexName) ? "game" : Profile.IndexName;
        return Path.Combine(GameRoot, "index", mapName + ".map");
    }

    private void Send(object payload) => _post(JsonSerializer.Serialize(payload, JsonOptions));

    private static readonly Regex BracketProgress = new(@"\[(\d+)\s*/\s*(\d+)\]", RegexOptions.Compiled);
    private static readonly Regex PercentProgress = new(@"(\d{1,3})\s*%", RegexOptions.Compiled);

    /// <summary>Pulls a fraction out of CLI output ("[5/41] …" or "进度 42%").</summary>
    private static bool TryParseProgress(string line, out double fraction, out long total)
    {
        fraction = 0;
        total = -1;

        Match bracket = BracketProgress.Match(line);
        if (bracket.Success &&
            long.TryParse(bracket.Groups[1].Value, out long current) &&
            long.TryParse(bracket.Groups[2].Value, out long count) &&
            count > 0)
        {
            fraction = Math.Clamp((double)current / count, 0, 1);
            total = count;
            return true;
        }

        Match percent = PercentProgress.Match(line);
        if (percent.Success && int.TryParse(percent.Groups[1].Value, out int value) && value is >= 0 and <= 100)
        {
            fraction = value / 100.0;
            total = 100;
            return true;
        }

        return false;
    }

    private static string LevelOf(string line, bool isError)
    {
        if (isError ||
            line.Contains("[Error]", StringComparison.Ordinal) ||
            line.StartsWith("System.", StringComparison.Ordinal) ||
            line.Contains("Exception", StringComparison.Ordinal) ||
            line.StartsWith("   at ", StringComparison.Ordinal) ||
            line.StartsWith("--- End of", StringComparison.Ordinal))
        {
            return "error";
        }

        if (line.Contains("[Warning]", StringComparison.Ordinal))
        {
            return "warn";
        }

        return "info";
    }

    private static readonly (Regex Pattern, string Hint)[] ErrorHints =
    [
        (new Regex("application to execute does not exist|CLI.*不存在|找不到.*CLI", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "找不到 CLI 程序：重新运行 build.ps1，或在「设置」里指定 AnimeStudio.CLI.exe 的路径。"),

        (new Regex("Access is denied|UnauthorizedAccess|0x80070005|拒绝访问", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "没有权限访问目录：把输出目录换到可写位置，或以管理员身份运行。"),

        (new Regex("Unable to read beyond the end of the stream|Cannot parse shader|unsupported texture", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "部分贴图为特殊格式（atlas-only / 非标准压缩），裁切时会跳过；缺图较多时优先导出 Texture2D。"),

        (new Regex("Not a raw \\(uncompressed\\) AssetMap|Corrupt AssetMap|MessagePack|Deserialize", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "地图文件损坏或版本不对：重建索引后再试。"),

        (new Regex("OutOfMemory|内存不足", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "内存不足：关掉其它占用内存的程序后重试，或分几批导出。"),

        (new Regex("NullReference|Object reference not set", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "解析资源时空引用，通常意味着地图与游戏版本不匹配：重建索引后再试。"),

        (new Regex("No such file|Could not find file|文件不存在", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "缺少文件：游戏安装可能不完整，或输出目录被其它程序占用。"),
    ];

    /// <summary>Maps raw CLI failures to one actionable sentence; returns null when nothing matches.</summary>
    private static string? Humanize(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        foreach ((Regex pattern, string hint) in ErrorHints)
        {
            if (pattern.IsMatch(message))
            {
                return hint;
            }
        }

        return null;
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    /// <summary>Distinguishes "property absent" from "explicitly null" when saving settings.</summary>
    private static bool TryGetOptionalString(JsonElement element, string property, out string? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out JsonElement found))
        {
            return false;
        }

        value = found.ValueKind == JsonValueKind.String ? found.GetString() : null;
        return true;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static long SafeFileLength(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static string? SafeFileModified(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                ? new FileInfo(path).LastWriteTime.ToString("yyyy-MM-dd HH:mm")
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>"compact" (v2) / "messagepack" (v1) / "unknown"; read from the map's magic bytes.</summary>
    private static string DetectMapFormat(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return string.Empty;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16);
            Span<byte> magic = stackalloc byte[4];
            int read = stream.Read(magic);
            if (read == 4 && magic.SequenceEqual(AssetMapV2.MagicBytes))
            {
                return "compact";
            }

            return read > 0 && magic[0] == 0x92 ? "messagepack" : "unknown";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Size of the incremental map cache that sits next to the map file
    /// (<c>&lt;index dir&gt;\.cache\&lt;name&gt;.mapcache</c>). Zero for maps built before the cache existed.
    /// </summary>
    private static long ComputeIndexCacheBytes(string? mapPath)
    {
        try
        {
            string? directory = IndexCacheDirectory(mapPath);
            if (directory is null || !Directory.Exists(directory))
            {
                return 0;
            }

            long total = 0;
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The cache can be rewritten while we count; skip the flaky entry.
                }
            }

            return total;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string? IndexCacheDirectory(string? mapPath)
    {
        if (string.IsNullOrWhiteSpace(mapPath))
        {
            return null;
        }

        string? directory = Path.GetDirectoryName(mapPath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        return Path.Combine(directory, ".cache", Path.GetFileNameWithoutExtension(mapPath) + ".mapcache");
    }

    private static int? GetInt(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(property, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out int number))
        {
            return number;
        }

        return null;
    }

    private static string[] GetStringArray(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()!)
            .ToArray();
    }
}
