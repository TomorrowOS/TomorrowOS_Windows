using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    /// <summary>Prepare Windows → Prevent screen turn-off (Windows "Turn off my screen after").</summary>
    public bool PreventDisplayOff { get; set; }
    /// <summary>Prepare Windows → Disable sleep.</summary>
    public bool DisableSleep { get; set; }
    /// <summary>Prepare Windows → Disable hibernation.</summary>
    public bool DisableHibernate { get; set; }
    /// <summary>Prepare Windows → Disable fullscreen game overlays.</summary>
    public bool DisableGameOverlays { get; set; }
    /// <summary>Prepare Windows → Configure Windows Update maintenance window.</summary>
    public bool ConfigureWindowsUpdate { get; set; }
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
            preventDisplayOff = req.PreventDisplayOff,
            disableSleep = req.DisableSleep,
            disableHibernate = req.DisableHibernate,
            disableGameOverlays = req.DisableGameOverlays,
            configureWindowsUpdate = req.ConfigureWindowsUpdate,
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
    /// Turns the Windows screen saver off for the current user and applies it immediately.
    /// When <paramref name="preserveIdleSleep"/> is true (Disable sleep = OFF), only remove
    /// the .scr payload so no saver overlay appears — do NOT set ScreenSaveActive=0 via SPI,
    /// which on some Windows builds stops the idle path from reaching sleep/hibernate.
    /// </summary>
    public static void ApplyDisableScreensaver(bool preserveIdleSleep = false)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", writable: true)
                ?? Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop");
            if (key != null)
            {
                // Empty SCRNSAVE.EXE → Windows has nothing to launch as a saver overlay.
                key.SetValue("SCRNSAVE.EXE", "", RegistryValueKind.String);
                key.SetValue("ScreenSaverIsSecure", "0", RegistryValueKind.String);

                if (preserveIdleSleep)
                {
                    // Keep ScreenSaveActive enabled so the idle continuum can still
                    // progress to sleep; with no .scr file, no visual saver appears.
                    key.SetValue("ScreenSaveActive", "1", RegistryValueKind.String);
                }
                else
                {
                    key.SetValue("ScreenSaveActive", "0", RegistryValueKind.String);
                    key.SetValue("ScreenSaveTimeOut", "0", RegistryValueKind.String);
                }
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

        if (preserveIdleSleep)
        {
            // Do not force ScreenSaveActive=0 — that path blocked sleep while saver was "on".
            NativeMethods.SystemParametersInfo(SpiSetScreenSaveActive, 1, IntPtr.Zero, flags);
        }
        else
        {
            NativeMethods.SystemParametersInfo(SpiSetScreenSaveTimeout, 0, IntPtr.Zero, flags);
            NativeMethods.SystemParametersInfo(SpiSetScreenSaveActive, 0, IntPtr.Zero, flags);
        }
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

    /// <summary>Disables Xbox Game Bar capture, background access, and Win+G at install time.</summary>
    public static void ApplyDisableGameOverlays()
    {
        SaveGameOverlayBackupIfMissing();
        SetGameOverlayCaptureEnabled(false);
        SetGamingOverlayBackgroundDisabled(true);
        TrySetMachineGameDvrPolicy(false);
        NotifyGameOverlaySettingsChanged();
        TryStopOverlayProcesses();
    }

    /// <summary>Toggle off: restore prior Game Bar registry values.</summary>
    public static void RestoreGameOverlays()
    {
        if (!TryRestoreGameOverlayFromBackup())
        {
            SetGameOverlayCaptureEnabled(true);
            SetGamingOverlayBackgroundDisabled(false);
        }

        TrySetMachineGameDvrPolicy(true);
        NotifyGameOverlaySettingsChanged();
    }

    private const string BackgroundAppsPath =
        @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications";

    private static readonly string[] KnownGamingOverlayAppIds =
    {
        "Microsoft.Xbox.GamingOverlay_8wekyb3d8bbwe!App",
        "Microsoft.Xbox.GamingOverlay_8wekyb3d8bbwe!GameBar",
    };

    private static readonly string[] OverlayProcessNames =
    {
        "GameBar",
        "XboxGameBar",
        "XboxGamingOverlay",
    };

    private static string GameOverlayBackupFile
    {
        get
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "TomorrowOS");
            Directory.CreateDirectory(root);
            return Path.Combine(root, "game-overlay-backup.json");
        }
    }

    private static readonly (string SubKey, string Name)[] GameOverlayRegistryKeys =
    {
        (@"System\GameConfigStore", "GameDVR_Enabled"),
        (@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled"),
        (@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled"),
        (@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "GameBarEnabled"),
        (@"Software\Microsoft\GameBar", "AutoGameModeEnabled"),
        (@"Software\Microsoft\GameBar", "AllowAutoGameMode"),
        (@"Software\Microsoft\GameBar", "UseNexusForGameBarEnabled"),
        (@"Software\Microsoft\GameBar", "ShowStartupPanel"),
    };

    private static void SetGameOverlayCaptureEnabled(bool enabled)
    {
        var dword = enabled ? 1 : 0;
        foreach (var (subKey, name) in GameOverlayRegistryKeys)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(subKey);
                key?.SetValue(name, dword, RegistryValueKind.DWord);
            }
            catch
            {
                // Player runtime hook still protects playback
            }
        }
    }

    private static void SaveGameOverlayBackupIfMissing()
    {
        var path = GameOverlayBackupFile;
        if (File.Exists(path))
        {
            return;
        }

        var snapshot = new Dictionary<string, int?>();
        foreach (var (subKey, name) in GameOverlayRegistryKeys)
        {
            snapshot[$"{subKey}|{name}"] = ReadGameOverlayDword(subKey, name);
        }

        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(snapshot));
        }
        catch
        {
            // ignore
        }
    }

    private static bool TryRestoreGameOverlayFromBackup()
    {
        var path = GameOverlayBackupFile;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var (subKey, name) in GameOverlayRegistryKeys)
            {
                var lookup = $"{subKey}|{name}";
                if (!doc.RootElement.TryGetProperty(lookup, out var value) ||
                    value.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                using var key = Registry.CurrentUser.CreateSubKey(subKey);
                key?.SetValue(name, value.GetInt32(), RegistryValueKind.DWord);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int? ReadGameOverlayDword(string subKey, string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(subKey);
            return key?.GetValue(name) is int i ? i : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryStopOverlayProcesses()
    {
        foreach (var name in OverlayProcessNames)
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    using (proc)
                    {
                        try
                        {
                            proc.CloseMainWindow();
                            if (!proc.WaitForExit(400))
                            {
                                proc.Kill();
                            }
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void SetGamingOverlayBackgroundDisabled(bool disabled)
    {
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var baseKey = Registry.CurrentUser.OpenSubKey(BackgroundAppsPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(BackgroundAppsPath);
            if (baseKey != null)
            {
                foreach (var subKeyName in baseKey.GetSubKeyNames())
                {
                    if (!subKeyName.Contains("Xbox.GamingOverlay", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    touched.Add(subKeyName);
                    WriteBackgroundDisabled(subKeyName, disabled);
                }
            }
        }
        catch
        {
            // continue
        }

        foreach (var appId in KnownGamingOverlayAppIds)
        {
            if (!touched.Contains(appId))
            {
                WriteBackgroundDisabled(appId, disabled);
            }
        }
    }

    private static void WriteBackgroundDisabled(string appId, bool disabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{BackgroundAppsPath}\{appId}");
            if (key == null)
            {
                return;
            }

            if (disabled)
            {
                key.SetValue("DisabledByUser", 1, RegistryValueKind.DWord);
            }
            else
            {
                key.DeleteValue("DisabledByUser", throwOnMissingValue: false);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void TrySetMachineGameDvrPolicy(bool enabled)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\GameDVR");
            key?.SetValue("AllowGameDVR", enabled ? 1 : 0, RegistryValueKind.DWord);
        }
        catch
        {
            // HKLM may require elevation.
        }
    }

    private static void NotifyGameOverlaySettingsChanged()
    {
        try
        {
            NativeMethods.SendMessageTimeout(
                new IntPtr(0xffff),
                0x001A,
                IntPtr.Zero,
                "TraySettings",
                0x0002,
                1000,
                out _);
        }
        catch
        {
            // ignore
        }
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

    private static string DisplayOffBackupFile
    {
        get
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "TomorrowOS");
            Directory.CreateDirectory(root);
            return Path.Combine(root, "display-off-backup.json");
        }
    }

    /// <summary>
    /// Sets Windows "Turn off my screen after" to Never for the active power plan (AC and battery).
    /// </summary>
    public static void ApplyPreventDisplayOff()
    {
        SaveDisplayOffBackupIfMissing();
        SetDisplayTimeouts(0, 0);
    }

    /// <summary>Toggle off: restore display-off timeouts from before the first apply.</summary>
    public static void RestoreDisplayOff()
    {
        var path = DisplayOffBackupFile;
        if (!File.Exists(path))
        {
            // We never changed display-off — leave Windows settings alone.
            return;
        }

        int videoAc;
        int videoDc;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            videoAc = ReadBackupSec(root, "videoAcSec");
            videoDc = ReadBackupSec(root, "videoDcSec");
        }
        catch
        {
            return;
        }

        // No prior non-Never timeout recorded — leave whatever Windows is set to now.
        if (videoAc <= 0 && videoDc <= 0)
        {
            return;
        }

        if (videoAc <= 0) videoAc = videoDc;
        if (videoDc <= 0) videoDc = videoAc;
        SetDisplayTimeouts(videoAc, videoDc);
    }

    private static void SaveDisplayOffBackupIfMissing()
    {
        var path = DisplayOffBackupFile;
        if (File.Exists(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var ac = ReadBackupSec(doc.RootElement, "videoAcSec");
                var dc = ReadBackupSec(doc.RootElement, "videoDcSec");
                // Keep the first real (non-Never) snapshot only.
                if (ac > 0 || dc > 0)
                {
                    return;
                }
            }
            catch
            {
                // rewrite below
            }
        }

        var queriedAc = QueryPowerSeconds("SUB_VIDEO", "VIDEOIDLE", ac: true);
        var queriedDc = QueryPowerSeconds("SUB_VIDEO", "VIDEOIDLE", ac: false);
        if (queriedAc is null && queriedDc is null)
        {
            // Could not read current timeouts — do not invent a backup value.
            return;
        }

        var payload = new
        {
            videoAcSec = queriedAc ?? queriedDc ?? 0,
            videoDcSec = queriedDc ?? queriedAc ?? 0
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void SetDisplayTimeouts(int videoAc, int videoDc)
    {
        RunPowerCfg("/SETACVALUEINDEX", "SCHEME_CURRENT", "SUB_VIDEO", "VIDEOIDLE", videoAc.ToString());
        RunPowerCfg("/SETDCVALUEINDEX", "SCHEME_CURRENT", "SUB_VIDEO", "VIDEOIDLE", videoDc.ToString());
        RunPowerCfg("/SETACTIVE", "SCHEME_CURRENT");
    }

    private static string HibernateBackupFile
    {
        get
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "TomorrowOS");
            Directory.CreateDirectory(root);
            return Path.Combine(root, "hibernate-backup.json");
        }
    }

    /// <summary>Turns hibernation off and sets hibernate-after-idle to Never.</summary>
    public static void ApplyDisableHibernate()
    {
        SaveHibernateBackupIfMissing();
        SetHibernateEnabled(false);
        SetHibernateTimeouts(0, 0);
    }

    /// <summary>Toggle off: restore hibernation state from before the first disable.</summary>
    public static void RestoreHibernate()
    {
        var path = HibernateBackupFile;
        if (!File.Exists(path))
        {
            return;
        }

        var hibernateEnabled = false;
        var hibernateAc = 0;
        var hibernateDc = 0;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("hibernateEnabled", out var enabledEl) &&
                (enabledEl.ValueKind == JsonValueKind.True || enabledEl.ValueKind == JsonValueKind.False))
            {
                hibernateEnabled = enabledEl.GetBoolean();
            }

            hibernateAc = ReadBackupSec(root, "hibernateAcSec");
            hibernateDc = ReadBackupSec(root, "hibernateDcSec");
        }
        catch
        {
            return;
        }

        SetHibernateEnabled(hibernateEnabled);
        if (hibernateEnabled)
        {
            SetHibernateTimeouts(hibernateAc, hibernateDc);
        }
    }

    private static void SaveHibernateBackupIfMissing()
    {
        var path = HibernateBackupFile;
        if (File.Exists(path))
        {
            return;
        }

        var payload = new
        {
            hibernateEnabled = QueryHibernateEnabled(),
            hibernateAcSec = QueryPowerSeconds("SUB_SLEEP", "HIBERNATEIDLE", ac: true) ?? 0,
            hibernateDcSec = QueryPowerSeconds("SUB_SLEEP", "HIBERNATEIDLE", ac: false) ?? 0
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool QueryHibernateEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
            if (key?.GetValue("HibernateEnabled") is int enabled)
            {
                return enabled != 0;
            }
        }
        catch
        {
            // fall through to powercfg
        }

        var output = CapturePowerCfg("/a");
        return output.Contains("Hibernate", StringComparison.OrdinalIgnoreCase) &&
               !output.Contains("has not been enabled", StringComparison.OrdinalIgnoreCase);
    }

    private static void SetHibernateEnabled(bool enabled)
    {
        CapturePowerCfg("/hibernate", enabled ? "on" : "off");
    }

    private static void SetHibernateTimeouts(int hibernateAc, int hibernateDc)
    {
        RunPowerCfg("/SETACVALUEINDEX", "SCHEME_CURRENT", "SUB_SLEEP", "HIBERNATEIDLE", hibernateAc.ToString());
        RunPowerCfg("/SETDCVALUEINDEX", "SCHEME_CURRENT", "SUB_SLEEP", "HIBERNATEIDLE", hibernateDc.ToString());
        RunPowerCfg("/SETACTIVE", "SCHEME_CURRENT");
    }

    private static int ReadBackupSec(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.TryGetInt32(out var v) ? Math.Max(0, v) : 0;

    private const string WindowsUpdateUxKey = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    private const string WindowsUpdateAuPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";

    private static string WindowsUpdateBackupFile
    {
        get
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "TomorrowOS");
            Directory.CreateDirectory(root);
            return Path.Combine(root, "windows-update-backup.json");
        }
    }

    /// <summary>
    /// Schedules Windows Update installs/restarts inside the maintenance window and keeps
    /// updates enabled. Active Hours are set to the inverse window so playback hours
    /// are not interrupted by automatic restarts.
    /// </summary>
    public static void ApplyWindowsUpdateMaintenanceWindow(string maintenanceWindow)
    {
        if (!TryParseMaintenanceWindow(maintenanceWindow, out var maintStartMin, out var maintEndMin))
        {
            maintStartMin = 120;
            maintEndMin = 240;
        }

        SaveWindowsUpdateBackupIfMissing();

        using (var ux = Registry.LocalMachine.CreateSubKey(WindowsUpdateUxKey))
        {
            // Active hours = outside maintenance (no auto-restart during playback).
            ux?.SetValue("ActiveHoursStart", maintEndMin, RegistryValueKind.DWord);
            ux?.SetValue("ActiveHoursEnd", maintStartMin, RegistryValueKind.DWord);
            ux?.SetValue("SmartActiveHours", 0, RegistryValueKind.DWord);
            ux?.SetValue("IsActiveHoursEnabled", 1, RegistryValueKind.DWord);
        }

        using (var au = Registry.LocalMachine.CreateSubKey(WindowsUpdateAuPolicyKey))
        {
            // Keep updates on; schedule install at maintenance start; avoid reboot while signed in.
            au?.SetValue("AUOptions", 4, RegistryValueKind.DWord);
            au?.SetValue("ScheduledInstallDay", 0, RegistryValueKind.DWord);
            au?.SetValue("ScheduledInstallTime", maintStartMin / 60, RegistryValueKind.DWord);
            au?.SetValue("NoAutoRebootWithLoggedOnUsers", 1, RegistryValueKind.DWord);
        }
    }

    /// <summary>Toggle off: restore Windows Update UX/policy keys from before first apply.</summary>
    public static void RestoreWindowsUpdate()
    {
        var path = WindowsUpdateBackupFile;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            RestoreRegistryDwords(
                Registry.LocalMachine,
                WindowsUpdateUxKey,
                doc.RootElement.TryGetProperty("uxSettings", out var ux) ? ux : default);
            RestoreRegistryDwords(
                Registry.LocalMachine,
                WindowsUpdateAuPolicyKey,
                doc.RootElement.TryGetProperty("auPolicies", out var au) ? au : default);
        }
        catch
        {
            // ignore
        }
    }

    internal static bool TryParseMaintenanceWindow(string input, out int startMinutes, out int endMinutes)
    {
        startMinutes = 0;
        endMinutes = 0;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var normalized = input
            .Replace('\u2013', '-')
            .Replace('\u2014', '-')
            .Replace("–", "-", StringComparison.Ordinal)
            .Replace("—", "-", StringComparison.Ordinal);
        var parts = normalized.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        if (!TryParseTimeOfDay(parts[0], out startMinutes) ||
            !TryParseTimeOfDay(parts[1], out endMinutes))
        {
            return false;
        }

        return startMinutes != endMinutes;
    }

    private static bool TryParseTimeOfDay(string text, out int minutes)
    {
        minutes = 0;
        text = text.Trim();
        if (TimeSpan.TryParse(text, out var ts))
        {
            minutes = (int)ts.TotalMinutes;
            return minutes is >= 0 and < 1440;
        }

        var match = Regex.Match(text, @"^(\d{1,2}):(\d{2})$");
        if (!match.Success)
        {
            return false;
        }

        var hours = int.Parse(match.Groups[1].Value);
        var mins = int.Parse(match.Groups[2].Value);
        if (hours is < 0 or > 23 || mins is < 0 or > 59)
        {
            return false;
        }

        minutes = hours * 60 + mins;
        return true;
    }

    private static void SaveWindowsUpdateBackupIfMissing()
    {
        var path = WindowsUpdateBackupFile;
        if (File.Exists(path))
        {
            return;
        }

        var payload = new
        {
            uxSettings = SnapshotRegistryDwords(Registry.LocalMachine, WindowsUpdateUxKey, new[]
            {
                "ActiveHoursStart",
                "ActiveHoursEnd",
                "SmartActiveHours",
                "IsActiveHoursEnabled"
            }),
            auPolicies = SnapshotRegistryDwords(Registry.LocalMachine, WindowsUpdateAuPolicyKey, new[]
            {
                "AUOptions",
                "ScheduledInstallDay",
                "ScheduledInstallTime",
                "NoAutoRebootWithLoggedOnUsers"
            })
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Dictionary<string, int?> SnapshotRegistryDwords(
        RegistryKey root,
        string subKeyPath,
        IEnumerable<string> valueNames)
    {
        var snapshot = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = root.OpenSubKey(subKeyPath);
            foreach (var name in valueNames)
            {
                snapshot[name] = key?.GetValue(name) is int i ? i : null;
            }
        }
        catch
        {
            foreach (var name in valueNames)
            {
                snapshot[name] = null;
            }
        }

        return snapshot;
    }

    private static void RestoreRegistryDwords(RegistryKey root, string subKeyPath, JsonElement snapshot)
    {
        if (snapshot.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var hadAny = snapshot.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.Null);
        if (!hadAny)
        {
            try
            {
                root.DeleteSubKeyTree(subKeyPath, throwOnMissingSubKey: false);
            }
            catch
            {
                // ignore
            }

            return;
        }

        using var key = root.OpenSubKey(subKeyPath, writable: true)
            ?? root.CreateSubKey(subKeyPath);
        if (key == null)
        {
            return;
        }

        foreach (var prop in snapshot.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Null)
            {
                key.DeleteValue(prop.Name, throwOnMissingValue: false);
            }
            else if (prop.Value.TryGetInt32(out var value))
            {
                key.SetValue(prop.Name, value, RegistryValueKind.DWord);
            }
        }
    }

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
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        // English + Chinese (both "AC"/"DC" and 交流/直流 wordings used across builds).
        string[] markers = ac
            ?
            [
                "Current AC Power Setting Index:",
                "当前 AC 电源设置索引:",
                "当前交流电源设置索引:"
            ]
            :
            [
                "Current DC Power Setting Index:",
                "当前 DC 电源设置索引:",
                "当前直流电源设置索引:"
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

        // Fallback: last hex on the AC/DC line when wording differs slightly.
        if (idx < 0)
        {
            var lineKey = ac
                ? new[] { "AC Power Setting", "交流电源" }
                : new[] { "DC Power Setting", "直流电源" };
            foreach (var line in output.Split('\n'))
            {
                if (!lineKey.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var match = Regex.Match(line, @"0x([0-9a-fA-F]+)");
                if (match.Success &&
                    int.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var fromLine))
                {
                    return fromLine;
                }
            }

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
                RedirectStandardError = true,
                // Localized powercfg text (e.g. 当前交流电源设置索引) needs the ANSI code page.
                StandardOutputEncoding = Encoding.Default,
                StandardErrorEncoding = Encoding.Default
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

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            int msg,
            IntPtr wParam,
            string lParam,
            uint fuFlags,
            uint uTimeout,
            out IntPtr lpdwResult);
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
            log?.Invoke(
                req.DisableSleep
                    ? "Disabling Windows screen saver…"
                    : "Disabling screen saver overlay (keeping idle sleep)…",
                "info");
            // Sleep OFF + Saver ON: soft disable so idle sleep/hibernate still work.
            ApplyDisableScreensaver(preserveIdleSleep: !req.DisableSleep);
        }
        else
        {
            log?.Invoke("Leaving Windows screen saver enabled…", "info");
            RestoreScreensaver();
        }

        if (req.PreventDisplayOff)
        {
            log?.Invoke("Preventing automatic screen turn-off…", "info");
            ApplyPreventDisplayOff();
        }
        else
        {
            log?.Invoke("Leaving Windows screen turn-off settings unchanged…", "info");
            RestoreDisplayOff();
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

        if (req.DisableHibernate)
        {
            log?.Invoke("Disabling Windows hibernation…", "info");
            ApplyDisableHibernate();
        }
        else
        {
            log?.Invoke("Leaving Windows hibernation enabled…", "info");
            RestoreHibernate();
        }

        if (req.DisableGameOverlays)
        {
            log?.Invoke("Keeping player above fullscreen overlays…", "info");
            ApplyDisableGameOverlays();
        }
        else
        {
            log?.Invoke("Allowing overlays above the player…", "info");
            RestoreGameOverlays();
        }

        if (req.ConfigureWindowsUpdate)
        {
            log?.Invoke("Configuring Windows Update maintenance window…", "info");
            ApplyWindowsUpdateMaintenanceWindow(req.MaintenanceWindow);
        }
        else
        {
            log?.Invoke("Leaving Windows Update settings unchanged…", "info");
            RestoreWindowsUpdate();
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
        else
        {
            log?.Invoke("Removing startup registration…", "info");
            RemoveAutoStart();
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
        if (req.ConfigureWindowsUpdate)
        {
            ApplyWindowsUpdateMaintenanceWindow(req.MaintenanceWindow);
        }
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
