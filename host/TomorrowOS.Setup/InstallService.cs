using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace TomorrowOS.Setup;

internal sealed class InstallRequest
{
    public string InstallDir { get; set; } = @"C:\Program Files\TomorrowOS";
    public string CmsEndpoint { get; set; } = "";
    public string Orientation { get; set; } = "landscape";
    public int DisplayIndex { get; set; }
    public string ContentFit { get; set; } = "contain";
    public string Passcode { get; set; } = "";
    public string SetupType { get; set; } = "standard";
    public string Role { get; set; } = "dedicated";
    public string DeviceName { get; set; } = "";
    public string SiteName { get; set; } = "";
    public string TimeZone { get; set; } = "";
    public string MaintenanceWindow { get; set; } = "02:00–04:00";
    public bool AutoStart { get; set; } = true;
    public bool ApplyHardening { get; set; }
    public bool StartWatchdog { get; set; } = true;
}

internal static class InstallService
{
    public static string? FindPayloadDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "payload"),
            Path.Combine(AppContext.BaseDirectory, "..", "payload"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "build", "windows", "payload"))
        };

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(Path.Combine(full, "TomorrowOS.Player.exe")) &&
                File.Exists(Path.Combine(full, "TomorrowOS.Watchdog.exe")))
            {
                return full;
            }
        }

        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "TomorrowOS.Player.exe")))
        {
            return AppContext.BaseDirectory;
        }

        return null;
    }

    public static void CopyDirectory(string source, string destination)
    {
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, destination));
        }

        foreach (var filePath in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = filePath.Replace(source, destination);
            var targetDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            File.Copy(filePath, target, overwrite: true);
        }
    }

    public static string HashPasscode(string passcode) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("tomorrowos:" + passcode)));

    public static void WriteConfig(string installDir, string cms, string orientation, int displayIndex, string contentFit)
    {
        var www = Path.Combine(installDir, "wwwroot");
        Directory.CreateDirectory(www);
        var escapedCms = cms.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var escapedFit = contentFit.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var content =
            "window.TOMORROWOS_CONFIG = {\n" +
            $"  cmsEndpoint: \"{escapedCms}\",\n" +
            $"  orientation: \"{orientation}\",\n" +
            $"  displayIndex: {displayIndex},\n" +
            $"  contentFit: \"{escapedFit}\"\n" +
            "};\n";
        File.WriteAllText(Path.Combine(www, "config.js"), content);
    }

    public static void WriteSettings(InstallRequest req)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TomorrowOS");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "cache"));
        Directory.CreateDirectory(Path.Combine(root, "storage"));

        var payload = new
        {
            maintenancePasscodeHash = HashPasscode(req.Passcode),
            displayIndex = req.DisplayIndex,
            cmsEndpoint = req.CmsEndpoint,
            orientation = req.Orientation,
            contentFit = req.ContentFit,
            setupType = req.SetupType,
            role = req.Role,
            deviceName = req.DeviceName,
            siteName = req.SiteName,
            timeZone = req.TimeZone,
            maintenanceWindow = req.MaintenanceWindow,
            installedAt = DateTime.UtcNow.ToString("O")
        };
        File.WriteAllText(
            Path.Combine(root, "settings.json"),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void RegisterAutoStart(string watchdogPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        key?.SetValue("TomorrowOSWatchdog", "\"" + watchdogPath + "\"");
    }

    public static void RunHardeningScript(string installDir)
    {
        var candidates = new[]
        {
            Path.Combine(installDir, "hardening", "apply-signage-hardening.ps1"),
            Path.Combine(AppContext.BaseDirectory, "hardening", "apply-signage-hardening.ps1"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "hardening", "apply-signage-hardening.ps1"))
        };

        var script = candidates.FirstOrDefault(File.Exists);
        if (script == null)
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
            UseShellExecute = true,
            Verb = "runas"
        })?.WaitForExit(120000);
    }

    public static void LaunchWatchdog(string installDir)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(installDir, "TomorrowOS.Watchdog.exe"),
            WorkingDirectory = installDir,
            UseShellExecute = true
        });
    }

    public static void LaunchPlayer(string installDir, bool forceRestart = false)
    {
        var playerExe = Path.Combine(installDir, "TomorrowOS.Player.exe");
        if (!File.Exists(playerExe))
        {
            return;
        }

        if (forceRestart)
        {
            StopPlayerProcesses();
            Thread.Sleep(400);
        }
        else if (Process.GetProcessesByName("TomorrowOS.Player").Length > 0)
        {
            TouchHeartbeat();
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = playerExe,
            WorkingDirectory = installDir,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        });

        TouchHeartbeat();
    }

    private static void StopPlayerProcesses()
    {
        foreach (var process in Process.GetProcessesByName("TomorrowOS.Player"))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void TouchHeartbeat()
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "TomorrowOS");
            Directory.CreateDirectory(root);
            File.WriteAllText(
                Path.Combine(root, "player.heartbeat"),
                DateTime.UtcNow.ToString("O"));
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Copies binaries and applies local settings. CMS endpoint may be filled later.
    /// </summary>
    public static void InstallCore(InstallRequest req, Action<string, string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(req.Passcode))
        {
            throw new InvalidOperationException("Maintenance passcode is required.");
        }

        var payloadDir = FindPayloadDirectory();
        if (payloadDir == null)
        {
            throw new InvalidOperationException(
                "Could not find published player payload. Build with npm run build first.");
        }

        log?.Invoke("Installing player…", "info");
        Directory.CreateDirectory(req.InstallDir);
        CopyDirectory(payloadDir, req.InstallDir);

        log?.Invoke("Creating local cache…", "info");
        WriteConfig(req.InstallDir, req.CmsEndpoint, req.Orientation, req.DisplayIndex, req.ContentFit);
        WriteSettings(req);

        if (req.ApplyHardening)
        {
            log?.Invoke("Applying Windows signage settings…", "info");
            RunHardeningScript(req.InstallDir);
        }

        if (req.AutoStart)
        {
            log?.Invoke("Registering startup…", "info");
            RegisterAutoStart(Path.Combine(req.InstallDir, "TomorrowOS.Watchdog.exe"));
        }

        log?.Invoke("Verifying installation…", "info");
        if (!File.Exists(Path.Combine(req.InstallDir, "TomorrowOS.Player.exe")))
        {
            throw new InvalidOperationException("Player executable missing after copy.");
        }
    }

    public static void FinalizeAndLaunch(InstallRequest req)
    {
        ClearRuntimeFlags();
        WriteConfig(req.InstallDir, req.CmsEndpoint, req.Orientation, req.DisplayIndex, req.ContentFit);
        WriteSettings(req);
        LaunchPlayer(req.InstallDir, forceRestart: true);
        if (req.StartWatchdog)
        {
            LaunchWatchdog(req.InstallDir);
        }
    }

    public static void ClearRuntimeFlags()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "TomorrowOS");
        foreach (var name in new[] { "maintenance.flag", "player.stop" })
        {
            try
            {
                var path = Path.Combine(root, name);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // ignore — launch still proceeds; Watchdog may skip Player if flag remains
            }
        }
    }
}
