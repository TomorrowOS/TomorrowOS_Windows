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
