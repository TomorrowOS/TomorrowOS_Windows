using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace TomorrowOS.Uninstall;

internal static class UninstallService
{
    public static string DefaultInstallDir =>
        Path.GetDirectoryName(Environment.ProcessPath)
        ?? AppContext.BaseDirectory;

    public static string ProgramDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TomorrowOS");

    public static string LocalAppDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TomorrowOS");

    public static void StopTomorrowOsProcesses()
    {
        foreach (var name in new[] { "TomorrowOS.Watchdog", "TomorrowOS.Player", "TomorrowOS-Windows-Setup" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    if (process.Id == Environment.ProcessId) continue;
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(4000);
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    public static void RemoveAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            key?.DeleteValue("TomorrowOSWatchdog", throwOnMissingValue: false);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Deletes the desktop shortcut created by Setup ("TomorrowOS Player.lnk").
    /// </summary>
    public static void RemoveDesktopShortcut()
    {
        var names = new[] { "TomorrowOS Player.lnk", "TomorrowOS.lnk" };
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        };

        foreach (var folder in folders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var name in names)
            {
                TryDeleteFile(Path.Combine(folder, name));
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch
        {
            // ignore — shortcut may already be gone or locked
        }
    }

    public static void WriteStopFlag()
    {
        try
        {
            Directory.CreateDirectory(ProgramDataRoot);
            File.WriteAllText(Path.Combine(ProgramDataRoot, "player.stop"), DateTime.UtcNow.ToString("O"));
        }
        catch
        {
            // ignore
        }
    }

    public static void DeleteDirectoryBestEffort(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                catch
                {
                    // ignore locked files
                }
            }

            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // ignore
        }
    }

    public static void DeleteInstallDir(string installDir, string? runningFrom)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            return;
        }

        var skipSelf = false;
        if (!string.IsNullOrEmpty(runningFrom))
        {
            try
            {
                skipSelf = string.Equals(
                    Path.GetFullPath(runningFrom),
                    Path.GetFullPath(Environment.ProcessPath ?? ""),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                skipSelf = false;
            }
        }

        foreach (var file in Directory.GetFiles(installDir, "*", SearchOption.AllDirectories))
        {
            try
            {
                if (skipSelf && string.Equals(file, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            catch
            {
                // ignore
            }
        }

        try
        {
            Directory.Delete(installDir, recursive: true);
        }
        catch
        {
            // leftover empty dirs / locked self
        }
    }
}
