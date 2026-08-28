namespace TomorrowOS.Player;

internal static class AppPaths
{
    public static string ProgramDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TomorrowOS");

    public static string StorageRoot => Path.Combine(ProgramDataRoot, "storage");

    public static string LogDirectory
    {
        get
        {
            var path = Path.Combine(ProgramDataRoot, "logs");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string HeartbeatFile => Path.Combine(ProgramDataRoot, "player.heartbeat");

    public static string MaintenanceFlagFile => Path.Combine(ProgramDataRoot, "maintenance.flag");

    public static string StopFlagFile => Path.Combine(ProgramDataRoot, "player.stop");

    public static string SettingsFile => Path.Combine(ProgramDataRoot, "settings.json");

    /// <summary>
    /// Writes player.stop so the current user can delete it later (Watchdog / CMS reboot / Player start).
    /// ProgramData often inherits Users=RX on files created by elevated writers.
    /// </summary>
    public static void WriteStopFlag()
    {
        EnsureDirectories();
        var path = StopFlagFile;
        File.WriteAllText(path, DateTime.UtcNow.ToString("O"));
        TryGrantUsersModify(path);
    }

    public static bool TryClearStopAndMaintenanceFlags()
    {
        var ok = true;
        ok &= TryDeleteFlag(StopFlagFile);
        ok &= TryDeleteFlag(MaintenanceFlagFile);
        return ok;
    }

    private static bool TryDeleteFlag(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            TryGrantUsersModify(path);
            File.Delete(path);
            return !File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static void TryGrantUsersModify(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            var users = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinUsersSid,
                null);
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                users,
                System.Security.AccessControl.FileSystemRights.Modify,
                System.Security.AccessControl.AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        catch
        {
            // ignore — best effort
        }
    }

    public static string WwwRoot
    {
        get
        {
            var besideExe = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            if (Directory.Exists(besideExe) && File.Exists(Path.Combine(besideExe, "index.html")))
            {
                return besideExe;
            }

            // Dev fallback: repo core/
            var dev = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "core"));
            if (Directory.Exists(dev) && File.Exists(Path.Combine(dev, "index.html")))
            {
                return dev;
            }

            return besideExe;
        }
    }

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(ProgramDataRoot);
        Directory.CreateDirectory(StorageRoot);
        Directory.CreateDirectory(Path.Combine(StorageRoot, "downloads", "tomorrowos", "staging"));
        Directory.CreateDirectory(Path.Combine(StorageRoot, "downloads", "tomorrowos", "current"));
        Directory.CreateDirectory(Path.Combine(StorageRoot, "downloads", "tomorrowos", "widgets"));
        Directory.CreateDirectory(LogDirectory);
    }
}
