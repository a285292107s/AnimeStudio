using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AnimeStudio.Host;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var form = new HostForm();
        System.Windows.Forms.Application.Run(form);
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

    private readonly WebView2 _webView = new();
    private HostService? _service;

    public HostForm()
    {
        Text = "AnimeStudio · 文本与 UI 提取";

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

        Load += async (_, _) => await InitializeAsync();
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

        _service = new HostService(PostJson, PickFolder, MapOutputFolder);
        MapOutputFolder(_service.Settings.OutputDirectory);

        core.WebMessageReceived += (_, e) => _service?.Handle(e.WebMessageAsJson);
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

    /// <summary>Expose the output directory as https://out.local/ so &lt;img&gt; can load PNGs directly.</summary>
    private void MapOutputFolder(string? directory)
    {
        if (_webView.CoreWebView2 is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                ImageIndex.VirtualHost,
                directory,
                CoreWebView2HostResourceAccessKind.Allow);
        }
        catch (ArgumentException)
        {
            // Invalid path for a virtual host mapping; the UI will simply not show images.
        }
    }
}
