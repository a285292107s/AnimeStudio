using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private readonly Action<string?> _mapOutputFolder;
    private readonly CliRunner _runner;
    private readonly TextTableStore _text = new();

    private List<ImageItem> _images = [];
    private List<ImageCategory> _categories = [];
    private CancellationTokenSource? _taskCts;

    private AssetMapScanner.MapStats? _mapStats;
    private string? _mapStatsPath;
    private bool _mapStatsRunning;
    private bool _installScanRunning;

    public HostService(Action<string> post, Func<string, string?> pickFolder, Action<string?> mapOutputFolder)
    {
        _post = post;
        _pickFolder = pickFolder;
        _mapOutputFolder = mapOutputFolder;
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
                profile = new GameProfile();
                Settings.Profiles[Settings.Game] = profile;
            }

            return profile;
        }
    }

    /// <summary>Each game owns a subdirectory of the shared output root.</summary>
    public string GameRoot => string.IsNullOrWhiteSpace(Settings.OutputDirectory)
        ? string.Empty
        : Path.Combine(Settings.OutputDirectory!, Settings.Game);

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
            case "task.run":
                _ = RunTaskAsync(root);
                break;
            case "task.cancel":
                _taskCts?.Cancel();
                break;
            case "text.query":
                HandleTextQuery(root, requestId);
                break;
            case "images.query":
                HandleImagesQuery(root, requestId);
                break;
            case "open.folder":
                OpenOutputFolder();
                break;
            default:
                Send(new { type = "ack", received = type });
                break;
        }
    }

    /* ---------- welcome / settings / game ---------- */

    private void SendWelcome()
    {
        if (_text.Tables.Count == 0 && GameRoot.Length > 0)
        {
            _text.Load(Path.Combine(GameRoot, "text"));
        }

        Send(new
        {
            type = "welcome",
            host = new
            {
                version = typeof(HostService).Assembly.GetName().Version?.ToString() ?? "0.1.0",
                cliPath = _runner.ResolveCliPath(),
                settingsFile = "host.settings.json",
            },
            settings = SettingsDto(),
            state = StateDto(),
        });

        QueueMapStats(force: false);

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

        if (!string.Equals(game, Settings.Game, StringComparison.OrdinalIgnoreCase))
        {
            Settings.Game = game;
            SettingsStore.Save(Settings);

            _text.Clear();
            _images = [];
            _categories = [];
            _mapStats = null;
            _mapStatsPath = null;

            if (GameRoot.Length > 0)
            {
                _text.Load(Path.Combine(GameRoot, "text"));
            }
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

    private object SettingsDto() => new
    {
        game = Settings.Game,
        gameDataPath = Profile.DataPath,
        outputDirectory = Settings.OutputDirectory,
        gameRoot = GameRoot,
        cliPath = Settings.CliPath,
        mapPath = MapPath(),
        indexName = Profile.IndexName,
        profiles = Settings.Profiles.Keys,
    };

    private object StateDto() => new
    {
        game = Settings.Game,
        mapExists = File.Exists(MapPath()),
        textTables = _text.Tables.Select(static table => new { locale = table.Locale, count = table.Count }),
        locales = _text.Locales,
        images = _images.Count,
        categories = _categories,
        outputDirectory = Settings.OutputDirectory,
        gameRoot = GameRoot,
    };

    private void HandlePick(JsonElement root)
    {
        string target = GetString(root, "target") ?? "game";
        string? picked = _pickFolder(target switch
        {
            "game" => "选择游戏的 <Game>_Data 目录",
            "output" => "选择输出目录",
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
                Settings.OutputDirectory = picked;
                _mapOutputFolder(picked);
                break;
            default:
                return;
        }

        SettingsStore.Save(Settings);
        SendSettingsChanged();
    }

    private void HandleSave(JsonElement root)
    {
        string previousMapPath = MapPath();
        bool switchGame = false;
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

            if (TryGetOptionalString(settings, "outputDirectory", out string? output) &&
                !string.IsNullOrWhiteSpace(output) &&
                output != Settings.OutputDirectory)
            {
                Settings.OutputDirectory = output;
                _mapOutputFolder(output);
                _images = [];
                _categories = [];
            }
        }

        if (switchGame)
        {
            SetGame(gameName ?? Settings.Game, notify: true);
        }

        SettingsStore.Save(Settings);
        SendSettingsChanged();

        if (!string.Equals(previousMapPath, MapPath(), StringComparison.OrdinalIgnoreCase))
        {
            _mapStats = null;
            _mapStatsPath = null;
            QueueMapStats(force: true);
        }
    }

    private void SendSettingsChanged()
    {
        Send(new { type = "settings.changed", settings = SettingsDto(), state = StateDto() });
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

        if (string.IsNullOrWhiteSpace(Settings.OutputDirectory))
        {
            Send(new { type = "task.failed", kind, message = "请先选择输出目录。" });
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
                    ];
                }

            case "text":
                {
                    var arguments = new List<string>
                    {
                        gameDataPath, Path.Combine(GameRoot, "text"),
                        "--game", game,
                        "--textmap",
                    };

                    string[] languages = GetStringArray(payload, "languages");
                    if (languages.Length > 0)
                    {
                        arguments.Add("--lang");
                        arguments.AddRange(languages);
                    }

                    return arguments;
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
                        "--types", "Sprite", "Texture2D",
                        "--group_assets", "ByContainer",
                    };

                    string[] containers = GetStringArray(payload, "containers");
                    if (containers.Length > 0)
                    {
                        arguments.Add("--containers");
                        arguments.AddRange(containers);
                    }

                    string[] names = GetStringArray(payload, "names");
                    if (names.Length > 0)
                    {
                        arguments.Add("--names");
                        arguments.AddRange(names);
                    }

                    return arguments;
                }

            default:
                throw new InvalidOperationException($"未知任务：{kind}");
        }
    }

    private void ReloadOutputs(string kind)
    {
        switch (kind)
        {
            case "text":
                _text.Load(Path.Combine(GameRoot, "text"));
                break;
            case "images":
                if (!string.IsNullOrWhiteSpace(Settings.OutputDirectory))
                {
                    (_images, _categories) = ImageIndex.Scan(GameRoot, Settings.OutputDirectory!);
                }

                break;
            case "index":
                Profile.MapPath = MapPath();
                SettingsStore.Save(Settings);
                break;
        }
    }

    /* ---------- queries ---------- */

    private void HandleTextQuery(JsonElement root, int requestId)
    {
        if (!_text.IsLoaded && GameRoot.Length > 0)
        {
            _text.Load(Path.Combine(GameRoot, "text"));
        }

        string[] locales = _text.Locales;
        string? requested = GetString(root, "locale");

        // The UI clears its locale when switching games; fall back to the first installed one
        // instead of querying an empty locale (which would return 0 and zero out the counters).
        string locale;
        if (!string.IsNullOrWhiteSpace(requested) && locales.Contains(requested, StringComparer.OrdinalIgnoreCase))
        {
            locale = locales.First(candidate => candidate.Equals(requested, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            // Prefer Simplified Chinese when installed, then Traditional, then whatever exists.
            locale = locales.FirstOrDefault(candidate => candidate.Equals("zh-CN", StringComparison.OrdinalIgnoreCase))
                     ?? locales.FirstOrDefault(candidate => candidate.Equals("zh-TW", StringComparison.OrdinalIgnoreCase))
                     ?? locales.FirstOrDefault()
                     ?? (string.IsNullOrWhiteSpace(requested) ? "zh-CN" : requested!);
        }

        string query = GetString(root, "query") ?? string.Empty;
        int offset = GetInt(root, "offset") ?? 0;
        int limit = Math.Clamp(GetInt(root, "limit") ?? 200, 1, 1000);

        (int total, List<KeyValuePair<string, string>> rows) = _text.Query(locale, query, offset, limit);

        Send(new
        {
            type = "text.page",
            requestId,
            locale,
            total,
            offset,
            locales,
            rows = rows.Select(static row => new[] { row.Key, row.Value }),
        });
    }

    private void HandleImagesQuery(JsonElement root, int requestId)
    {
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
        string urlRoot = Settings.OutputDirectory ?? string.Empty;
        string gameRoot = GameRoot;

        // Thumbnails are rendered off the message thread: the first page of a fresh export
        // can need a hundred of them.
        _ = Task.Run(() =>
        {
            foreach (ImageItem item in page)
            {
                ImageIndex.EnsureThumbnail(
                    Path.Combine(gameRoot, item.RelativePath),
                    Path.Combine(urlRoot, item.ThumbRelativePath));
            }

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
        });
    }

    private void EnsureImageIndex()
    {
        if (_images.Count > 0 || GameRoot.Length == 0 || string.IsNullOrWhiteSpace(Settings.OutputDirectory))
        {
            return;
        }

        (_images, _categories) = ImageIndex.Scan(GameRoot, Settings.OutputDirectory!);
    }

    private void OpenOutputFolder()
    {
        string directory = GameRoot.Length > 0 ? GameRoot : Settings.OutputDirectory ?? string.Empty;
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

    public string MapPath()
    {
        if (!string.IsNullOrWhiteSpace(Profile.MapPath))
        {
            return Profile.MapPath!;
        }

        if (string.IsNullOrWhiteSpace(Settings.OutputDirectory))
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

        (new Regex("Not a raw \\(uncompressed\\) AssetMap|Corrupt AssetMap|MessagePack|Deserialize", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "地图文件损坏或版本不对：重建索引后再试。"),

        (new Regex("OutOfMemory|内存不足", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "内存不足：关掉其它占用内存的程序后重试，或分几批导出。"),

        (new Regex("DesignData|TextMap", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "找不到文本数据：确认选中完整的 <Game>_Data 目录，并至少启动过一次游戏。"),

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
