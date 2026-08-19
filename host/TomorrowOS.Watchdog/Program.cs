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

    private static DateTime _lastPlayerStartUtc = DateTime.MinValue;

    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatStaleAfter = TimeSpan.FromSeconds(25);
    // Brief grace only after we start the player — not 45s (that caused ~45s visible delay).
    private static readonly TimeSpan StartupGraceAfterPlayerStart = TimeSpan.FromSeconds(10);

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

        EnsurePlayerRunning("startup");

        while (true)
        {
            try
            {
                if (File.Exists(MaintenanceFlag) || File.Exists(StopFlag))
                {
                    Thread.Sleep(CheckEvery);
                    continue;
                }

                if (IsShuttingDown())
                {
                    return;
                }

                if (!IsPlayerAlive())
                {
                    StartPlayer("process-missing");
                }
                else if (IsHeartbeatStale())
                {
                    RestartPlayer("heartbeat-stale");
                }
            }
            catch (Exception ex)
            {
                TryLog(ex.ToString());
            }

            Thread.Sleep(CheckEvery);
        }
    }

    private static bool IsPlayerAlive()
    {
        return Process.GetProcessesByName("TomorrowOS.Player").Length > 0;
    }

    private static bool InStartupGraceAfterPlayerStart()
    {
        if (_lastPlayerStartUtc == DateTime.MinValue)
        {
            return false;
        }

        return (DateTime.UtcNow - _lastPlayerStartUtc) < StartupGraceAfterPlayerStart;
    }

    private static bool IsHeartbeatStale()
    {
        if (InStartupGraceAfterPlayerStart())
        {
            return false;
        }

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

            return (DateTime.UtcNow - ts.ToUniversalTime()) > HeartbeatStaleAfter;
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
            StartPlayer(reason);
        }
    }

    private static void StartPlayer(string reason)
    {
        if (!File.Exists(PlayerExe))
        {
            TryLog("Player exe missing: " + PlayerExe);
            return;
        }

        TryLog($"Starting player ({reason})");
        Process.Start(new ProcessStartInfo
        {
            FileName = PlayerExe,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = true
        });

        _lastPlayerStartUtc = DateTime.UtcNow;
        TouchHeartbeat();
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

        StartPlayer(reason);
    }

    private static void TouchHeartbeat()
    {
        try
        {
            File.WriteAllText(HeartbeatFile, DateTime.UtcNow.ToString("O"));
        }
        catch
        {
            // ignore
        }
    }

    private static bool IsShuttingDown()
    {
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
