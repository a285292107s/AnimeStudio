using Microsoft.Win32;

namespace AnimeStudio.Host;

/// <summary>
/// Reads the miHoYo / HoYoPlay launcher registry keys, which record the install path of
/// every game the launcher manages. Falls back to nothing on failure — the filesystem
/// scan in <see cref="Application.Install.GameInstallScanner"/> still covers default spots.
/// </summary>
internal static class LauncherRegistry
{
    private const string HypRoot = @"SOFTWARE\miHoYo\HYP";

    public static List<string> GetGameRoots(Action<string>? log = null)
    {
        var roots = new List<string>();

        try
        {
            using RegistryKey? hyp = Registry.CurrentUser.OpenSubKey(HypRoot);
            if (hyp is null)
            {
                return roots;
            }

            foreach (string versionName in hyp.GetSubKeyNames())
            {
                using RegistryKey? version = hyp.OpenSubKey(versionName);
                if (version is null)
                {
                    continue;
                }

                string? launcherPath = version.GetValue("InstallPath") as string;
                if (!string.IsNullOrWhiteSpace(launcherPath))
                {
                    string games = Path.Combine(launcherPath, "games");
                    if (Directory.Exists(games))
                    {
                        roots.Add(games);
                    }
                }

                foreach (string bizName in version.GetSubKeyNames())
                {
                    using RegistryKey? game = version.OpenSubKey(bizName);
                    string? installPath = game?.GetValue("GameInstallPath") as string;
                    if (!string.IsNullOrWhiteSpace(installPath))
                    {
                        roots.Add(installPath);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            log?.Invoke($"读取启动器注册表失败：{exception.Message}");
        }

        return roots;
    }
}
