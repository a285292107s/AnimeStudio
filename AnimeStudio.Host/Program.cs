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
/// developed in a normal browser against mock data; this form only hosts it and forwards
/// messages to the application layer.
/// </summary>
internal sealed class HostForm : Form
{
    private const string VirtualHost = "app.local";

    private readonly WebView2 _webView = new();

    public HostForm()
    {
        Text = "AnimeStudio · 文本与 UI 提取";
        Width = 1560;
        Height = 960;
        MinimumSize = new Size(1180, 720);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(10, 10, 10);

        _webView.Dock = DockStyle.Fill;
        _webView.DefaultBackgroundColor = Color.FromArgb(10, 10, 10);
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
        core.SetVirtualHostNameToFolderMapping(VirtualHost, wwwroot, CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += OnWebMessageReceived;
        core.Navigate($"https://{VirtualHost}/index.html");
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw = e.WebMessageAsJson;

        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);
            JsonElement root = document.RootElement;

            string type = root.TryGetProperty("type", out JsonElement typeProperty)
                ? typeProperty.GetString() ?? string.Empty
                : string.Empty;

            switch (type)
            {
                case "ping":
                    Reply(new { type = "pong", host = "AnimeStudio.Host", version = "0.1.0" });
                    break;

                default:
                    // Unknown message: acknowledge so the UI can show it in the log panel.
                    Reply(new { type = "ack", received = type });
                    break;
            }
        }
        catch (JsonException)
        {
            Reply(new { type = "error", message = "malformed message" });
        }
    }

    private void Reply(object payload)
    {
        string json = JsonSerializer.Serialize(payload);
        _webView.CoreWebView2?.PostWebMessageAsJson(json);
    }
}
