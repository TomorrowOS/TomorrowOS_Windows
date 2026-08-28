using System.IO;
using Microsoft.Win32;

namespace TomorrowOS.Setup;

internal static class SilentInstaller
{
    public static void Run(string[] args)
    {
        string GetArg(string name, string fallback = "")
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return fallback;
        }

        var req = new InstallRequest
        {
            InstallDir = GetArg("/dir", @"C:\Program Files\TomorrowOS"),
            CmsEndpoint = GetArg("/cms", ""),
            Orientation = GetArg("/orientation", "landscape"),
            DisplayIndex = int.TryParse(GetArg("/display", "0"), out var idx) ? idx : 0,
            ContentFit = GetArg("/fit", "contain"),
            Passcode = GetArg("/passcode", ""),
            AutoStart = !args.Any(a => a.Equals("/noautostart", StringComparison.OrdinalIgnoreCase)),
            ApplyHardening = args.Any(a => a.Equals("/harden", StringComparison.OrdinalIgnoreCase)),
            DisableScreensaver = args.Any(a => a.Equals("/harden", StringComparison.OrdinalIgnoreCase)),
            DisableSleep = args.Any(a => a.Equals("/harden", StringComparison.OrdinalIgnoreCase)),
            DisableHibernate = args.Any(a => a.Equals("/harden", StringComparison.OrdinalIgnoreCase)),
            StartWatchdog = !args.Any(a => a.Equals("/nowatchdog", StringComparison.OrdinalIgnoreCase)),
            HideCursorDuringPlayback = !args.Any(a => a.Equals("/showcursor", StringComparison.OrdinalIgnoreCase)),
            HideTaskbarDuringPlayback = !args.Any(a => a.Equals("/showtaskbar", StringComparison.OrdinalIgnoreCase)),
            DisableGameOverlays = args.Any(a => a.Equals("/harden", StringComparison.OrdinalIgnoreCase))
                && !args.Any(a => a.Equals("/allowgameoverlays", StringComparison.OrdinalIgnoreCase)),
            ConfigureWindowsUpdate = !args.Any(a => a.Equals("/noupdatewindow", StringComparison.OrdinalIgnoreCase))
                && (args.Any(a => a.Equals("/harden", StringComparison.OrdinalIgnoreCase))
                    || !string.IsNullOrWhiteSpace(GetArg("/window", ""))),
            DeviceName = GetArg("/name", ""),
            SiteName = GetArg("/site", ""),
            MaintenanceWindow = GetArg("/window", "02:00–04:00")
        };

        if (string.IsNullOrWhiteSpace(req.Passcode))
        {
            throw new InvalidOperationException("Silent install requires /passcode <value>");
        }

        InstallService.InstallCore(req);
        InstallService.ClearRuntimeFlags();
        InstallService.LaunchPlayer(req.InstallDir, forceRestart: true);
        if (req.StartWatchdog)
        {
            InstallService.LaunchWatchdog(req.InstallDir);
        }
    }
}
