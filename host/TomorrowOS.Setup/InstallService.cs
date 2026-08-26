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
    /// <summary>Installer "Hide mouse cursor after inactivity".</summary>
    public bool HideCursorDuringPlayback { get; set; } = true;
    /// <summary>Prepare Windows → Hide taskbar during playback.</summary>
    public bool HideTaskbarDuringPlayback { get; set; } = true;
    /// <summary>Prepare Windows → Disable screen saver.</summary>
    public bool DisableScreensaver { get; set; }
    /// <summary>Prepare Windows → Disable sleep.</summary>
    public bool DisableSleep { get; set; }
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
            hideCursorDuringPlayback = req.HideCursorDuringPlayback,
            hideTaskbarDuringPlayback = req.HideTaskbarDuringPlayback,
            disableScreensaver = req.DisableScreensaver,
            disableSleep = req.DisableSleep,
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

    /// <summary>
    /// Turns the Windows screen saver off for the current user and applies it immediately.
    /// Registry-only writes often do nothing until logoff; SystemParametersInfo notifies the shell.
    /// </summary>
    public static void ApplyDisableScreensaver()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", writable: true)
                ?? Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop");
            if (key != null)
            {
                key.SetValue("ScreenSaveActive", "0", RegistryValueKind.String);
                key.SetValue("ScreenSaveTimeOut", "0", RegistryValueKind.String);
                key.SetValue("ScreenSaverIsSecure", "0", RegistryValueKind.String);
                key.SetValue("SCRNSAVE.EXE", "", RegistryValueKind.String);
            }
        }
        catch
        {
            // continue — SystemParametersInfo still applies for this session
        }

        const uint SpiSetScreenSaveActive = 0x0011;
        const uint SpiSetScreenSaveTimeout = 0x000F;
        const uint SpifUpdateIniFile = 0x01;
        const uint SpifSendWinIniChange = 0x02;
        var flags = SpifUpdateIniFile | SpifSendWinIniChange;

        NativeMethods.SystemParametersInfo(SpiSetScreenSaveTimeout, 0, IntPtr.Zero, flags);
        NativeMethods.SystemParametersInfo(SpiSetScreenSaveActive, 0, IntPtr.Zero, flags);
    }

    /// <summary>
    /// Toggle off: undo a previous disable so the Windows screen saver can run again.
    /// </summary>
    public static void RestoreScreensaver()
    {
        const uint SpiSetScreenSaveActive = 0x0011;
        const uint SpiSetScreenSaveTimeout = 0x000F;
        const uint SpifUpdateIniFile = 0x01;
        const uint SpifSendWinIniChange = 0x02;
        var flags = SpifUpdateIniFile | SpifSendWinIniChange;

        var timeoutSec = 60;
        var scr = Path.Combine(Environment.SystemDirectory, "scrnsave.scr");

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", writable: true)
                ?? Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop");
            if (key != null)
            {
                var existingScr = key.GetValue("SCRNSAVE.EXE") as string;
                if (!string.IsNullOrWhiteSpace(existingScr) && File.Exists(existingScr.Trim()))
                {
                    scr = existingScr.Trim();
                }

                var existingTimeout = key.GetValue("ScreenSaveTimeOut") as string;
                if (int.TryParse(existingTimeout, out var parsed) && parsed > 0)
                {
                    timeoutSec = parsed;
                }

                key.SetValue("ScreenSaveActive", "1", RegistryValueKind.String);
                key.SetValue("ScreenSaveTimeOut", timeoutSec.ToString(), RegistryValueKind.String);
                key.SetValue("SCRNSAVE.EXE", scr, RegistryValueKind.String);
            }
        }
        catch
        {
            // SystemParametersInfo still applies for this session
        }

        NativeMethods.SystemParametersInfo(SpiSetScreenSaveTimeout, (uint)timeoutSec, IntPtr.Zero, flags);
        NativeMethods.SystemParametersInfo(SpiSetScreenSaveActive, 1, IntPtr.Zero, flags);
    }

    private static string SleepBackupFile
    {
        get
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "TomorrowOS");
            Directory.CreateDirectory(root);
            return Path.Combine(root, "sleep-timeout-backup.json");
        }
    }

    /// <summary>Sets idle sleep to Never for the active power plan (AC and battery).</summary>
    public static void ApplyDisableSleep()
    {
        SaveSleepBackupIfMissing();
        SetSleepTimeouts(0, 0, 0, 0);
    }

    /// <summary>Toggle off: restore sleep timeouts. Does not change display-off.</summary>
    public static void RestoreSleep()
    {
        var standbyAc = 0;
        var standbyDc = 0;
        var unattendAc = 0;
        var unattendDc = 0;
        var path = SleepBackupFile;

        if (File.Exists(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                standbyAc = ReadBackupSec(root, "standbyAcSec");
                standbyDc = ReadBackupSec(root, "standbyDcSec");
                unattendAc = ReadBackupSec(root, "unattendAcSec");
                unattendDc = ReadBackupSec(root, "unattendDcSec");
            }
            catch
            {
                // fall through to live query
            }
        }

        if (standbyAc <= 0)
        {
            standbyAc = QueryPowerSeconds("SUB_SLEEP", "STANDBYIDLE", ac: true) ?? 0;
        }

        if (standbyDc <= 0)
        {
            standbyDc = QueryPowerSeconds("SUB_SLEEP", "STANDBYIDLE", ac: false) ?? 0;
        }

        if (unattendAc <= 0)
        {
            unattendAc = QueryPowerSeconds("SUB_SLEEP", "UNATTENDSLEEP", ac: true) ?? standbyAc;
        }

        if (unattendDc <= 0)
        {
            unattendDc = QueryPowerSeconds("SUB_SLEEP", "UNATTENDSLEEP", ac: false) ?? standbyDc;
        }

        if (standbyAc > 0 || standbyDc > 0)
        {
            SetSleepTimeouts(standbyAc, standbyDc, unattendAc, unattendDc);
        }
    }

    private static int ReadBackupSec(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.TryGetInt32(out var v) ? Math.Max(0, v) : 0;

    private static void SaveSleepBackupIfMissing()
    {
        var path = SleepBackupFile;
        if (File.Exists(path))
        {
            return;
        }

        var payload = new
        {
            standbyAcSec = QueryPowerSeconds("SUB_SLEEP", "STANDBYIDLE", ac: true) ?? 0,
            standbyDcSec = QueryPowerSeconds("SUB_SLEEP", "STANDBYIDLE", ac: false) ?? 0,
            unattendAcSec = QueryPowerSeconds("SUB_SLEEP", "UNATTENDSLEEP", ac: true) ?? 0,
            unattendDcSec = QueryPowerSeconds("SUB_SLEEP", "UNATTENDSLEEP", ac: false) ?? 0
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void SetSleepTimeouts(int standbyAc, int standbyDc, int unattendAc, int unattendDc)
    {
        RunPowerCfg("/SETACVALUEINDEX", "SCHEME_CURRENT", "SUB_SLEEP", "STANDBYIDLE", standbyAc.ToString());
        RunPowerCfg("/SETDCVALUEINDEX", "SCHEME_CURRENT", "SUB_SLEEP", "STANDBYIDLE", standbyDc.ToString());
        RunPowerCfg("/SETACVALUEINDEX", "SCHEME_CURRENT", "SUB_SLEEP", "UNATTENDSLEEP", unattendAc.ToString());
        RunPowerCfg("/SETDCVALUEINDEX", "SCHEME_CURRENT", "SUB_SLEEP", "UNATTENDSLEEP", unattendDc.ToString());
        RunPowerCfg("/SETACTIVE", "SCHEME_CURRENT");
    }

    private static int? QueryPowerSeconds(string subgroup, string setting, bool ac)
    {
        var output = CapturePowerCfg("/query", "SCHEME_CURRENT", subgroup, setting);
        string[] markers = ac
            ?
            [
                "Current AC Power Setting Index:",
                "当前 AC 电源设置索引:"
            ]
            :
            [
                "Current DC Power Setting Index:",
                "当前 DC 电源设置索引:"
            ];

        var idx = -1;
        foreach (var marker in markers)
        {
            idx = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                break;
            }
        }

        if (idx < 0)
        {
            return null;
        }

        var slice = output[idx..];
        var hexStart = slice.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
        if (hexStart < 0)
        {
            return null;
        }

        var hex = new string(slice[(hexStart + 2)..].TakeWhile(Uri.IsHexDigit).ToArray());
        return int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var sec)
            ? sec
            : null;
    }

    private static void RunPowerCfg(params string[] args)
    {
        CapturePowerCfg(args);
    }

    private static string CapturePowerCfg(params string[] args)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = string.Join(" ", args),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process == null)
            {
                return "";
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15000);
            return output;
        }
        catch
        {
            return "";
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);
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

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        process?.WaitForExit(120000);
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

        if (req.DisableScreensaver)
        {
            log?.Invoke("Disabling Windows screen saver…", "info");
            ApplyDisableScreensaver();
        }
        else
        {
            log?.Invoke("Leaving Windows screen saver enabled…", "info");
            RestoreScreensaver();
        }

        if (req.DisableSleep)
        {
            log?.Invoke("Disabling Windows sleep…", "info");
            ApplyDisableSleep();
        }
        else
        {
            log?.Invoke("Leaving Windows sleep enabled…", "info");
            RestoreSleep();
        }

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
