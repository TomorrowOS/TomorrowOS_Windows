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
    private static DateTime _lastRestartAttemptUtc = DateTime.MinValue;
    private static DateTime _lastStaleLogUtc = DateTime.MinValue;
    private static int _rapidRestartCount;

    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(5);
    // Heartbeat is informational only — never kill a live player for a stale file.
    private static readonly TimeSpan HeartbeatStaleLogAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MinRestartGap = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RapidRestartWindow = TimeSpan.FromMinutes(2);
    private const int MaxRapidRestarts = 6;

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
                if (File.Exists(MaintenanceFlag) || ShouldHonorStopFlag())
                {
                    if ((DateTime.UtcNow - _lastStaleLogUtc) > TimeSpan.FromMinutes(2))
                    {
                        _lastStaleLogUtc = DateTime.UtcNow;
                        TryLog(
                            File.Exists(StopFlag)
                                ? "Skipping player start: player.stop present (maintenance exit)."
                                : "Skipping player start: maintenance.flag present.");
                    }

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
                else
                {
                    // Do NOT kill for heartbeat-stale. False positives (UI freeze, disk delay,
                    // clock skew) were exiting healthy players and causing flash loops.
                    LogStaleHeartbeatIfNeeded();
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

    private static void LogStaleHeartbeatIfNeeded()
    {
        if (!File.Exists(HeartbeatFile))
        {
            return;
        }

        try
        {
            var text = File.ReadAllText(HeartbeatFile).Trim();
            if (!DateTime.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var ts))
            {
                return;
            }

            var age = DateTime.UtcNow - ts.ToUniversalTime();
            if (age <= HeartbeatStaleLogAfter)
            {
                return;
            }

            if ((DateTime.UtcNow - _lastStaleLogUtc) < TimeSpan.FromMinutes(5))
            {
                return;
            }

            _lastStaleLogUtc = DateTime.UtcNow;
            TryLog($"Warning: player heartbeat stale ({(int)age.TotalSeconds}s) but process still alive — not killing.");
        }
        catch
        {
            // ignore
        }
    }

    private static void EnsurePlayerRunning(string reason)
    {
        if (File.Exists(MaintenanceFlag) || ShouldHonorStopFlag())
        {
            return;
        }

        if (!IsPlayerAlive())
        {
            StartPlayer(reason);
        }
    }

    /// <summary>
    /// Honor player.stop only when the current user can modify it (real maintenance exit).
    /// An elevated/undeletable leftover must not permanently block Player after reboot.
    /// </summary>
    private static bool ShouldHonorStopFlag()
    {
        if (!File.Exists(StopFlag))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(
                StopFlag,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            TryLog("Ignoring undeletable player.stop (ACL); starting player.");
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return true;
        }
    }

    private static bool ShouldBackoffRestart(string reason)
    {
        var now = DateTime.UtcNow;
        if (_lastRestartAttemptUtc != DateTime.MinValue &&
            (now - _lastRestartAttemptUtc) < MinRestartGap)
        {
            TryLog($"Skipping start ({reason}): too soon after previous attempt.");
            return true;
        }

        if (_lastRestartAttemptUtc != DateTime.MinValue &&
            (now - _lastRestartAttemptUtc) > RapidRestartWindow)
        {
            _rapidRestartCount = 0;
        }

        if (_rapidRestartCount >= MaxRapidRestarts)
        {
            var wait = TimeSpan.FromSeconds(45);
            if ((now - _lastRestartAttemptUtc) < wait)
            {
                TryLog($"Skipping start ({reason}): backoff after {_rapidRestartCount} rapid starts.");
                return true;
            }

            _rapidRestartCount = 0;
        }

        return false;
    }

    private static void NoteRestartAttempt()
    {
        var now = DateTime.UtcNow;
        if (_lastRestartAttemptUtc != DateTime.MinValue &&
            (now - _lastRestartAttemptUtc) <= RapidRestartWindow)
        {
            _rapidRestartCount++;
        }
        else
        {
            _rapidRestartCount = 1;
        }

        _lastRestartAttemptUtc = now;
    }

    private static void StartPlayer(string reason)
    {
        if (!File.Exists(PlayerExe))
        {
            TryLog("Player exe missing: " + PlayerExe);
            return;
        }

        if (IsPlayerAlive())
        {
            return;
        }

        if (ShouldBackoffRestart(reason))
        {
            return;
        }

        NoteRestartAttempt();
        ClearStaleWebView2Locks();

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

    /// <summary>
    /// After a hard kill, Chromium may leave a lockfile that blocks the next instance
    /// from initializing — which then looks like another crash to Watchdog.
    /// </summary>
    private static void ClearStaleWebView2Locks()
    {
        if (IsPlayerAlive())
        {
            return;
        }

        foreach (var profile in new[] { "webview2", "webview2-allow-idle" })
        {
            var lockPath = Path.Combine(ProgramDataRoot, profile, "EBWebView", "lockfile");
            try
            {
                if (File.Exists(lockPath))
                {
                    File.Delete(lockPath);
                    TryLog("Cleared stale WebView2 lock: " + lockPath);
                }
            }
            catch (Exception ex)
            {
                TryLog("Could not clear WebView2 lock: " + ex.Message);
            }
        }
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
