using System.Diagnostics;
using System.Text.Json;

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
    private CancellationTokenSource? _taskCts;

    public HostService(Action<string> post, Func<string, string?> pickFolder, Action<string?> mapOutputFolder)
    {
        _post = post;
        _pickFolder = pickFolder;
        _mapOutputFolder = mapOutputFolder;
        Settings = SettingsStore.Load();
        _runner = new CliRunner(Settings);
    }

    public HostSettings Settings { get; }

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

    /* ---------- welcome / settings ---------- */

    private void SendWelcome()
    {
        if (_text.Tables.Count == 0 && !string.IsNullOrEmpty(Settings.OutputDirectory))
        {
            _text.Load(Path.Combine(Settings.OutputDirectory, "text"));
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
    }

    private object SettingsDto() => new
    {
        game = Settings.Game,
        gameDataPath = Settings.GameDataPath,
        outputDirectory = Settings.OutputDirectory,
        cliPath = Settings.CliPath,
        mapPath = MapPath(),
        indexName = Settings.IndexName,
    };

    private object StateDto() => new
    {
        mapExists = File.Exists(MapPath()),
        textTables = _text.Tables.Select(static table => new { locale = table.Locale, count = table.Count }),
        locales = _text.Locales,
        images = _images.Count,
        outputDirectory = Settings.OutputDirectory,
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
                Settings.GameDataPath = picked;
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
        if (root.TryGetProperty("settings", out JsonElement settings) && settings.ValueKind == JsonValueKind.Object)
        {
            Settings.Game = GetString(settings, "game") ?? Settings.Game;
            Settings.GameDataPath = GetString(settings, "gameDataPath") ?? Settings.GameDataPath;
            Settings.CliPath = GetString(settings, "cliPath") ?? Settings.CliPath;

            string? output = GetString(settings, "outputDirectory");
            if (!string.IsNullOrWhiteSpace(output) && output != Settings.OutputDirectory)
            {
                Settings.OutputDirectory = output;
                _mapOutputFolder(output);
            }
        }

        SettingsStore.Save(Settings);
        SendSettingsChanged();
    }

    private void SendSettingsChanged()
    {
        Send(new { type = "settings.changed", settings = SettingsDto(), state = StateDto() });
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

        string game = GetString(payload, "game") ?? Settings.Game;
        string? gameDataPath = GetString(payload, "gameDataPath") ?? Settings.GameDataPath;

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
            arguments = BuildArguments(kind, game, gameDataPath, payload);
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
            int exitCode = await _runner
                .RunAsync(arguments, (line, isError) => Send(new
                {
                    type = "log",
                    level = LevelOf(line, isError),
                    message = line,
                }), _taskCts.Token)
                .ConfigureAwait(false);

            ReloadOutputs(kind);
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
            Send(new { type = "task.failed", id, kind, message = exception.Message });
        }
        finally
        {
            _taskCts.Dispose();
            _taskCts = null;
        }
    }

    private List<string> BuildArguments(string kind, string game, string gameDataPath, JsonElement payload)
    {
        string outputRoot = Settings.OutputDirectory!;

        switch (kind)
        {
            case "index":
                {
                    string indexDirectory = Path.Combine(outputRoot, "index");
                    string mapName = string.IsNullOrWhiteSpace(Settings.IndexName) ? "game" : Settings.IndexName;
                    Settings.MapPath = Path.Combine(indexDirectory, mapName + ".map");
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
                        gameDataPath, Path.Combine(outputRoot, "text"),
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
                        gameDataPath, Path.Combine(outputRoot, "ui"),
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
        string outputRoot = Settings.OutputDirectory!;

        switch (kind)
        {
            case "text":
                _text.Load(Path.Combine(outputRoot, "text"));
                break;
            case "images":
                _images = ImageIndex.Scan(outputRoot);
                break;
            case "index":
                Settings.MapPath = MapPath();
                SettingsStore.Save(Settings);
                break;
        }
    }

    /* ---------- queries ---------- */

    private void HandleTextQuery(JsonElement root, int requestId)
    {
        if (!_text.IsLoaded && !string.IsNullOrEmpty(Settings.OutputDirectory))
        {
            _text.Load(Path.Combine(Settings.OutputDirectory, "text"));
        }

        string locale = GetString(root, "locale") ?? _text.Locales.FirstOrDefault() ?? "zh-CN";
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
            locales = _text.Locales,
            rows = rows.Select(static row => new[] { row.Key, row.Value }),
        });
    }

    private void HandleImagesQuery(JsonElement root, int requestId)
    {
        if (_images.Count == 0 && !string.IsNullOrEmpty(Settings.OutputDirectory))
        {
            _images = ImageIndex.Scan(Settings.OutputDirectory);
        }

        string query = GetString(root, "query") ?? string.Empty;
        int offset = Math.Max(0, GetInt(root, "offset") ?? 0);
        int limit = Math.Clamp(GetInt(root, "limit") ?? 120, 1, 500);

        List<ImageItem> filtered = string.IsNullOrWhiteSpace(query)
            ? _images
            : _images.Where(item => item.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

        Send(new
        {
            type = "images.page",
            requestId,
            total = filtered.Count,
            offset,
            available = _images.Count,
            items = filtered.Skip(offset).Take(limit),
        });
    }

    private void OpenOutputFolder()
    {
        if (string.IsNullOrWhiteSpace(Settings.OutputDirectory) || !Directory.Exists(Settings.OutputDirectory))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", Settings.OutputDirectory) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Explorer unavailable: not worth failing the request.
        }
    }

    /* ---------- helpers ---------- */

    public string MapPath()
    {
        if (!string.IsNullOrWhiteSpace(Settings.MapPath))
        {
            return Settings.MapPath!;
        }

        if (string.IsNullOrWhiteSpace(Settings.OutputDirectory))
        {
            return string.Empty;
        }

        string mapName = string.IsNullOrWhiteSpace(Settings.IndexName) ? "game" : Settings.IndexName;
        return Path.Combine(Settings.OutputDirectory!, "index", mapName + ".map");
    }

    private void Send(object payload) => _post(JsonSerializer.Serialize(payload, JsonOptions));

    private static string LevelOf(string line, bool isError)
    {
        if (isError || line.Contains("[Error]", StringComparison.Ordinal) || line.StartsWith("System.", StringComparison.Ordinal))
        {
            return "error";
        }

        if (line.Contains("[Warning]", StringComparison.Ordinal))
        {
            return "warn";
        }

        return "info";
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
