using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace TomorrowOS.Player;

/// <summary>
/// When sleep is allowed, clear this process's execution-state flags and keep
/// Chromium from holding display/system wake locks. Does not re-enable the
/// Windows screen saver — that stays under the separate saver toggle.
/// </summary>
internal static class IdleSleepPolicy
{
    private const uint EsContinuous = 0x80000000;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    /// <summary>
    /// Clears ES_SYSTEM_REQUIRED / ES_DISPLAY_REQUIRED previously set by this
    /// process. WebView2 child processes need browser flags + JS release too.
    /// </summary>
    public static void ClearHostPowerRequests()
    {
        try
        {
            SetThreadExecutionState(EsContinuous);
        }
        catch
        {
            // ignore
        }
    }

    public static string WebView2UserDataFolder(bool allowIdleSleep)
    {
        // Separate profile so --disable-features=WakeLock actually takes effect.
        // Reusing the same user-data folder with different browser args is ignored
        // when an existing WebView2 browser process is already running.
        AppPaths.EnsureDirectories();
        return Path.Combine(
            AppPaths.ProgramDataRoot,
            allowIdleSleep ? "webview2-allow-idle" : "webview2");
    }

    public static string BrowserArgumentsForIdleSleep() =>
        "--disable-features=WakeLock,IdleDetection,HardwareMediaKeyHandling";

    /// <summary>
    /// Best-effort release of any page-level wake lock / media session that can
    /// keep msedgewebview2.exe in powercfg /requests.
    /// </summary>
    public static string ReleaseWakeLockScript { get; } =
        @"(() => {
          try {
            if (navigator.wakeLock && navigator.wakeLock.request) {
              const orig = navigator.wakeLock.request.bind(navigator.wakeLock);
              navigator.wakeLock.request = async (...args) => {
                const sentinel = await orig(...args);
                try { await sentinel.release(); } catch (_) {}
                throw new DOMException('Wake lock blocked by TomorrowOS idle policy', 'NotAllowedError');
              };
            }
          } catch (_) {}
          try {
            if (navigator.mediaSession) {
              navigator.mediaSession.playbackState = 'none';
            }
          } catch (_) {}
          true;
        })()";

    public static void TryLog(string message)
    {
        try
        {
            AppPaths.EnsureDirectories();
            File.AppendAllText(
                Path.Combine(AppPaths.LogDirectory, "player.log"),
                $"[{DateTime.Now:O}] {message}\n");
        }
        catch
        {
            // ignore
        }
    }
}
