using System.Diagnostics;
using System.Text;

namespace AnimeStudio.Host;

/// <summary>
/// Runs AnimeStudio.CLI as a child process and streams its output line by line.
/// The host never re-implements the extraction pipeline; the CLI is the single
/// implementation, already covered by the probes and cross-checks.
/// </summary>
internal sealed class CliRunner
{
    private readonly HostSettings _settings;

    public CliRunner(HostSettings settings)
    {
        _settings = settings;
    }

    public string? ResolveCliPath()
    {
        if (!string.IsNullOrWhiteSpace(_settings.CliPath) && File.Exists(_settings.CliPath))
        {
            return _settings.CliPath;
        }

        string baseDirectory = AppContext.BaseDirectory;

        string[] candidates =
        [
            Path.Combine(baseDirectory, "AnimeStudio.CLI.exe"),
            // dist layout: bin\AnimeStudio.Host.exe with the patched CLI one level up
            Path.Combine(baseDirectory, "..", "AnimeStudio.CLI.exe"),
            Path.Combine(baseDirectory, "..", "..", "AnimeStudio.CLI.exe"),
            // development layout: build.ps1 patches the dev CLI apphost into bin\ layout, so it
            // cannot be launched from AnimeStudio.CLI\bin any more — prefer the dist copy.
            Path.Combine(baseDirectory, "..", "..", "..", "..", "dist", "AnimeStudio.CLI.exe"),
            // dev layout: AnimeStudio.Host/bin/<config>/<tfm>/ -> repo root
            Path.Combine(baseDirectory, "..", "..", "..", "..", "AnimeStudio.CLI", "bin", "Release", "net10.0-windows", "AnimeStudio.CLI.exe"),
            Path.Combine(baseDirectory, "..", "..", "..", "..", "AnimeStudio.CLI", "bin", "Debug", "net10.0-windows", "AnimeStudio.CLI.exe"),
        ];

        foreach (string candidate in candidates)
        {
            string full = Path.GetFullPath(candidate);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    /// <summary>
    /// Starts the CLI. <paramref name="onLine"/> receives stdout+stderr lines as they arrive.
    /// Returns the process exit code; throws if the CLI cannot be located.
    /// </summary>
    public async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        Action<string, bool> onLine,
        CancellationToken cancellationToken)
    {
        string cliPath = ResolveCliPath()
            ?? throw new FileNotFoundException(
                "找不到 AnimeStudio.CLI.exe。请在设置里手动指定 CLI 路径，或先运行 build.ps1。");

        var startInfo = new ProcessStartInfo
        {
            FileName = cliPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(cliPath) ?? AppContext.BaseDirectory,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                onLine(e.Data, false);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                onLine(e.Data, true);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动 AnimeStudio.CLI。");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            throw;
        }

        return process.ExitCode;
    }
}
