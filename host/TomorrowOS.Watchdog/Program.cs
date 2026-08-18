using System.Diagnostics;

namespace TomorrowOS.Watchdog;

internal static class Program
{
    private static readonly string ProgramDataRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TomorrowOS");

    private static readonly string HeartbeatFile = Path.Combine(ProgramDataRoot, "player.heartbeat");
    private static readonly string MaintenanceFlag = Path.Combine(ProgramDataRoot, "maintenance.flag");
    private static readonly string StopFlag = Path.Combine(ProgramDataRoot, "player.stop");
    private static readonly string MutexName = "Global\\TomorrowOS.Watchdog";

    private static string PlayerExe =>
        Path.Combine(AppContext.BaseDirectory, "TomorrowOS.Player.exe");

    [STAThread]
    private static void Main(string[] args)
    {
        Directory.CreateDirectory(ProgramDataRoot);

        using var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            return;
        }

        var staleSeconds = 20;
        var checkEvery = TimeSpan.FromSeconds(5);

        EnsurePlayerRunning("startup");

        while (true)
        {
            try
            {
                if (File.Exists(MaintenanceFlag) || File.Exists(StopFlag))
                {
                    Thread.Sleep(checkEvery);
                    continue;
                }

                if (IsShuttingDown())
                {
                    return;
                }

                if (!IsPlayerAlive() || IsHeartbeatStale(staleSeconds))
                {
                    RestartPlayer("heartbeat-or-process");
                }
            }
            catch (Exception ex)
            {
                TryLog(ex.ToString());
            }

            Thread.Sleep(checkEvery);
        }
    }

    private static bool IsPlayerAlive()
    {
        return Process.GetProcessesByName("TomorrowOS.Player").Length > 0;
    }

    private static bool IsHeartbeatStale(int staleSeconds)
    {
        if (!File.Exists(HeartbeatFile))
        {
            return true;
        }

        try
        {
            var text = File.ReadAllText(HeartbeatFile).Trim();
            if (!DateTime.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var ts))
            {
                return true;
            }

            return (DateTime.UtcNow - ts.ToUniversalTime()).TotalSeconds > staleSeconds;
        }
        catch
        {
            return true;
        }
    }

    private static void EnsurePlayerRunning(string reason)
    {
        if (File.Exists(StopFlag) || File.Exists(MaintenanceFlag))
        {
            return;
        }

        if (!IsPlayerAlive())
        {
            RestartPlayer(reason);
        }
    }

    private static void RestartPlayer(string reason)
    {
        TryLog($"Restarting player ({reason})");

        foreach (var process in Process.GetProcessesByName("TomorrowOS.Player"))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch
            {
                // ignore
            }
        }

        if (!File.Exists(PlayerExe))
        {
            TryLog("Player exe missing: " + PlayerExe);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = PlayerExe,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = true
        });

        // Give the new process time to write a heartbeat.
        Thread.Sleep(3000);
    }

    private static bool IsShuttingDown()
    {
        // Simple heuristic: if user initiated logoff/shutdown, avoid fighting it.
        return Environment.HasShutdownStarted;
    }

    private static void TryLog(string message)
    {
        try
        {
            var dir = Path.Combine(ProgramDataRoot, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "watchdog.log"), $"[{DateTime.Now:O}] {message}\n");
        }
        catch
        {
            // ignore
        }
    }
}
