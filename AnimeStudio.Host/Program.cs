using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AnimeStudio.Host;

internal static class Program
{
    private static readonly string LogDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeStudio");

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // The host must not die because one message handler or background continuation threw.
        global::System.Windows.Forms.Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        global::System.Windows.Forms.Application.ThreadException += (_, e) => LogAndReport(e.Exception, fatal: false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogAndReport(e.ExceptionObject as Exception, fatal: true);

        using var form = new HostForm();
        System.Windows.Forms.Application.Run(form);
    }

    internal static void LogAndReport(Exception? exception, bool fatal)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(
                Path.Combine(LogDirectory, "host-crash.log"),
                $"[{DateTimeOffset.Now:O}] fatal={fatal}\n{exception}\n\n");
        }
        catch (Exception)
        {
            // Logging must never throw.
        }

        if (!fatal)
        {
            MessageBox.Show(
                $"界面出现异常，已记录到 %LOCALAPPDATA%\\AnimeStudio\\host-crash.log，程序会继续运行。\n\n{exception.Message}",
                "AnimeStudio",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }
}

/// <summary>
/// Thin WebView2 shell. All UI lives in wwwroot (plain HTML/CSS/JS) so it can also be
/// developed in a normal browser; this form only hosts it, serves the output folder over
/// a virtual host, and forwards messages to <see cref="HostService"/>.
/// </summary>
internal sealed class HostForm : Form
{
    private const string AppVirtualHost = "app.local";

    /// <summary>
    /// Prefix for exported files. <c>out.local</c> is not a real host: the
    /// <see cref="CoreWebView2.WebResourceRequested"/> handler serves files from the game's own
    /// output directory, so HSR and ZZZ can live on any drive and change at runtime.
    /// </summary>
    private const string OutputVirtualHost = "out.local";

    private readonly WebView2 _webView = new();
    private readonly ConcurrentDictionary<string, string> _outputRoots = new(StringComparer.OrdinalIgnoreCase);
    private HostService? _service;

    public HostForm()
    {
        Text = "AnimeStudio · UI 图片提取";

        // WinForms sizes are device pixels. On a 200% display, a fixed 1560px window
        // leaves WebView2 a ~780px CSS viewport and the three-column layout collapses,
        // so size against the working area instead.
        Rectangle workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 1000);
        Width = Math.Min((int)(workingArea.Width * 0.94), 2600);
        Height = Math.Min((int)(workingArea.Height * 0.94), 1500);
        MinimumSize = new Size((int)(workingArea.Width * 0.55), (int)(workingArea.Height * 0.5));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = System.Drawing.Color.FromArgb(10, 10, 10);

        _webView.Dock = DockStyle.Fill;
        _webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(10, 10, 10);
        Controls.Add(_webView);

        Load += async (_, _) =>
        {
            try
            {
                await InitializeAsync();
            }
            catch (Exception exception)
            {
                Program.LogAndReport(exception, fatal: false);
            }
        };
    }

    private async Task InitializeAsync()
    {
        string userDataFolder = Path.Combine(Path.GetTempPath(), "AnimeStudio.Host.WebView2");
        CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
        await _webView.EnsureCoreWebView2Async(environment);

        CoreWebView2 core = _webView.CoreWebView2;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;

        string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        core.SetVirtualHostNameToFolderMapping(AppVirtualHost, wwwroot, CoreWebView2HostResourceAccessKind.Allow);

        // Exported PNGs are served by the handler below instead of a folder mapping: a
        // WebView2 virtual-host mapping cannot be re-pointed reliably at runtime
        // (WebView2Feedback#1018) and each game now has its own output directory.
        core.AddWebResourceRequestedFilter(
            $"https://{OutputVirtualHost}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;

        _service = new HostService(PostJson, PickFolder, EnsureOutputMapping);
        _service.WarmOutputMappings();

        core.WebMessageReceived += (_, e) =>
        {
            try
            {
                _service?.Handle(e.WebMessageAsJson);
            }
            catch (Exception exception)
            {
                // One malformed request must never take the whole window down.
                PostJson(JsonSerializer.Serialize(new
                {
                    type = "log",
                    level = "error",
                    message = $"[host] 处理消息失败：{exception.Message}",
                }));
                Program.LogAndReport(exception, fatal: false);
            }
        };
        core.Navigate($"https://{AppVirtualHost}/index.html");
    }

    /// <summary>Thread-safe: the service may answer from a background continuation.</summary>
    private void PostJson(string json)
    {
        if (IsDisposed || _webView.IsDisposed)
        {
            return;
        }

        if (_webView.InvokeRequired)
        {
            _webView.BeginInvoke(new Action(() => PostJson(json)));
            return;
        }

        try
        {
            _webView.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (InvalidOperationException)
        {
            // WebView went away between the check and the call.
        }
    }

    private static string? PickFolder(string title)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = title,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };

        return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null;
    }

    /// <summary>
    /// Makes one output directory readable by the page and returns its URL prefix
    /// (<c>https://out.local/&lt;key&gt;/</c>). HSR and ZZZ extract into independent
    /// directories that can live on any drive, and the key is derived from the path, so
    /// changing the path needs no WebView2 re-registration.
    /// </summary>
    private string? EnsureOutputMapping(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        string normalized;
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        string key = OutputKeyFor(normalized);
        _outputRoots[key] = normalized;
        return $"https://{OutputVirtualHost}/{key}/";
    }

    private static string OutputKeyFor(string directory)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToLowerInvariant()));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    /// <summary>Serves <c>https://out.local/&lt;key&gt;/…</c> from the registered output root.</summary>
    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        CoreWebView2? core = _webView.CoreWebView2;
        if (core is null)
        {
            return;
        }

        try
        {
            (int status, byte[]? content, string contentType) = ResolveOutputFile(e.Request.Uri);
            Stream? stream = content is null ? null : new MemoryStream(content, writable: false);

            e.Response = core.Environment.CreateWebResourceResponse(
                stream,
                status,
                status == 200 ? "OK" : "Not Found",
                $"Content-Type: {contentType}\r\nCache-Control: no-store");
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.Runtime.InteropServices.COMException)
        {
            // A failed image must never leave a dangling response or take the window down.
        }
    }

    private (int Status, byte[]? Content, string ContentType) ResolveOutputFile(string requestUri)
    {
        try
        {
            if (!Uri.TryCreate(requestUri, UriKind.Absolute, out Uri? uri))
            {
                return (400, null, "text/plain");
            }

            string[] parts = uri.AbsolutePath.TrimStart('/').Split('/', 3);
            if (parts.Length < 2 || !_outputRoots.TryGetValue(parts[0], out string? root))
            {
                return (404, null, "text/plain");
            }

            string relative = Uri.UnescapeDataString(parts.Length > 2 ? $"{parts[1]}/{parts[2]}" : parts[1]);
            if (relative.Length == 0)
            {
                return (404, null, "text/plain");
            }

            string rootFull = Path.GetFullPath(root + Path.DirectorySeparatorChar);
            string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                return (404, null, "text/plain");
            }

            string contentType = Path.GetExtension(full).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                _ => "application/octet-stream",
            };

            return (200, File.ReadAllBytes(full), contentType);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (404, null, "text/plain");
        }
    }
}
