using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace TomorrowOS.Player;

/// <summary>
/// Disables Xbox Game Bar the same way Windows Settings does:
/// capture/registry toggles + BackgroundAccessApplications = Never for GamingOverlay.
/// </summary>
internal static class GameOverlayPolicy
{
    private const string BackgroundAppsPath =
        @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications";

    private static readonly (string SubKey, string Name)[] CaptureKeys =
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

    private static readonly string[] KnownGamingOverlayAppIds =
    {
        "Microsoft.Xbox.GamingOverlay_8wekyb3d8bbwe!App",
        "Microsoft.Xbox.GamingOverlay_8wekyb3d8bbwe!GameBar",
    };

    /// <summary>Overlay UWP processes safe to stop. Never touch GameBarFTServer (system critical).</summary>
    private static readonly string[] OverlayProcessNames =
    {
        "GameBar",
        "XboxGameBar",
        "XboxGamingOverlay",
    };

    public static void ApplyDisable()
    {
        ApplyRegistryDisable();
        StopOverlayProcesses();
    }

    /// <summary>Full runtime disable — registry + background permission + stop overlay app.</summary>
    public static void ApplyRegistryDisable()
    {
        SaveBackupIfMissing();
        SetCaptureEnabled(false);
        SetGamingOverlayBackgroundDisabled(true);
        NotifySettingsChanged();
    }

    public static void RestoreEnable()
    {
        if (!TryRestoreFromBackup())
        {
            SetCaptureEnabled(true);
            SetGamingOverlayBackgroundDisabled(false);
        }

        NotifySettingsChanged();
    }

    /// <summary>Stop the Game Bar overlay app if Windows launched it anyway.</summary>
    public static void StopOverlayProcesses()
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
                            if (!proc.HasExited)
                            {
                                proc.CloseMainWindow();
                                if (!proc.WaitForExit(500))
                                {
                                    proc.Kill();
                                }
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

    public static void HideVisibleOverlayWindows()
    {
        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hwnd) || !IsGameBarWindow(hwnd))
                {
                    return true;
                }

                ShowWindow(hwnd, SwHide);
            }
            catch
            {
                // ignore
            }

            return true;
        }, IntPtr.Zero);
    }

    private static void SetCaptureEnabled(bool enabled)
    {
        var dword = enabled ? 1 : 0;
        foreach (var (subKey, name) in CaptureKeys)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(subKey);
                key?.SetValue(name, dword, RegistryValueKind.DWord);
            }
            catch
            {
                // continue
            }
        }

        TrySetMachinePolicy(enabled);
    }

    private static void TrySetMachinePolicy(bool enabled)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\GameDVR");
            key?.SetValue("AllowGameDVR", enabled ? 1 : 0, RegistryValueKind.DWord);
        }
        catch
        {
            // HKLM may require elevation — HKCU background block still applies.
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
            // continue with known ids
        }

        foreach (var appId in KnownGamingOverlayAppIds)
        {
            if (touched.Contains(appId))
            {
                continue;
            }

            WriteBackgroundDisabled(appId, disabled);
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

    private static void NotifySettingsChanged()
    {
        try
        {
            const int wmSettingChange = 0x001A;
            SendMessageTimeout(
                HwndBroadcast,
                wmSettingChange,
                IntPtr.Zero,
                "TraySettings",
                SmtoAbortifHung,
                1000,
                out _);
        }
        catch
        {
            // ignore
        }
    }

    private static string BackupFile
    {
        get
        {
            AppPaths.EnsureDirectories();
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "TomorrowOS",
                "game-overlay-backup.json");
        }
    }

    private static void SaveBackupIfMissing()
    {
        var path = BackupFile;
        if (File.Exists(path))
        {
            return;
        }

        var snapshot = new Dictionary<string, JsonElement?>();
        foreach (var (subKey, name) in CaptureKeys)
        {
            snapshot[$"cap|{subKey}|{name}"] = JsonSerializer.SerializeToElement(ReadDword(subKey, name));
        }

        foreach (var appId in EnumerateGamingOverlayAppIds())
        {
            snapshot[$"bg|{appId}"] =
                JsonSerializer.SerializeToElement(ReadBackgroundDisabledByUser(appId));
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

    private static bool TryRestoreFromBackup()
    {
        var path = BackupFile;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var (subKey, name) in CaptureKeys)
            {
                var lookup = $"cap|{subKey}|{name}";
                if (!doc.RootElement.TryGetProperty(lookup, out var value) ||
                    value.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                using var key = Registry.CurrentUser.CreateSubKey(subKey);
                if (value.ValueKind == JsonValueKind.Number)
                {
                    key?.SetValue(name, value.GetInt32(), RegistryValueKind.DWord);
                }
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!prop.Name.StartsWith("bg|", StringComparison.Ordinal))
                {
                    continue;
                }

                var appId = prop.Name["bg|".Length..];
                if (prop.Value.ValueKind == JsonValueKind.Null)
                {
                    WriteBackgroundDisabled(appId, disabled: false);
                    continue;
                }

                using var key = Registry.CurrentUser.CreateSubKey($@"{BackgroundAppsPath}\{appId}");
                if (prop.Value.TryGetInt32(out var disabledByUser))
                {
                    key?.SetValue("DisabledByUser", disabledByUser, RegistryValueKind.DWord);
                }
                else
                {
                    key?.DeleteValue("DisabledByUser", throwOnMissingValue: false);
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateGamingOverlayAppIds()
    {
        var ids = new HashSet<string>(KnownGamingOverlayAppIds, StringComparer.OrdinalIgnoreCase);
        try
        {
            using var baseKey = Registry.CurrentUser.OpenSubKey(BackgroundAppsPath);
            if (baseKey != null)
            {
                foreach (var subKeyName in baseKey.GetSubKeyNames())
                {
                    if (subKeyName.Contains("Xbox.GamingOverlay", StringComparison.OrdinalIgnoreCase))
                    {
                        ids.Add(subKeyName);
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        return ids;
    }

    private static int? ReadDword(string subKey, string name)
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

    private static int? ReadBackgroundDisabledByUser(string appId)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{BackgroundAppsPath}\{appId}");
            return key?.GetValue("DisabledByUser") is int i ? i : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsGameBarWindow(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            using var proc = Process.GetProcessById(pid);
            return OverlayProcessNames.Contains(proc.ProcessName, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private static readonly IntPtr HwndBroadcast = new(0xffff);
    private const uint SmtoAbortifHung = 0x0002;
    private const int SwHide = 0;

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        int msg,
        IntPtr wParam,
        string lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);
}
