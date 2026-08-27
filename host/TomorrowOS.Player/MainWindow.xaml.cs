using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using TomorrowOS.Player.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace TomorrowOS.Player;

public partial class MainWindow : Window
{
    private readonly BridgeHost _bridge;
    private readonly DispatcherTimer _focusTimer;
    private readonly DispatcherTimer _cursorIdleTimer;
    private readonly DispatcherTimer _heartbeatTimer;
    private System.Threading.Timer? _backgroundHeartbeat;
    private bool _maintenancePromptOpen;
    private bool _allowClose;
    private bool _displayQuiet;
    private bool _hideCursorDuringPlayback = true;
    private readonly bool _disableScreensaver;
    private readonly bool _disableSleep;
    private readonly bool _hideTaskbarDuringPlayback;
    private readonly bool _disableGameOverlays;
    private PlayerTopmostGuard? _topmostGuard;
    private PasscodeDialog? _passcodeDialog;
    private DateTime _lastMaintenanceRequestUtc = DateTime.MinValue;

    // Avoid F12 combos — many OEM / Windows utilities bind those to Calculator.
    private const string HotkeyChordHint = "Ctrl+Shift+Alt+M";
    private static readonly TimeSpan MaintenanceDebounce = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan CursorIdleHideAfter = TimeSpan.FromSeconds(2);

    private const uint SwpNozorder = 0x0004;
    private const uint SwpShowwindow = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    public MainWindow()
    {
        InitializeComponent();
        TouchHeartbeatFile();

        // UI timer + background timer: heartbeat must keep updating even if the
        // dispatcher is briefly busy (WebView2 / overlay work).
        _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _heartbeatTimer.Tick += (_, _) => TouchHeartbeatFile();
        _heartbeatTimer.Start();
        _backgroundHeartbeat = new System.Threading.Timer(
            _ => TouchHeartbeatFile(),
            null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5));

        try
        {
            if (File.Exists(AppPaths.StopFlagFile))
            {
                File.Delete(AppPaths.StopFlagFile);
            }
        }
        catch
        {
            // ignore
        }

        _bridge = new BridgeHost(WebView, this);
        _hideCursorDuringPlayback = ReadHideCursorSetting();
        _disableScreensaver = ReadBoolSetting("disableScreensaver", fallback: true);
        _disableSleep = ReadBoolSetting("disableSleep", fallback: true);
        _hideTaskbarDuringPlayback = ReadBoolSetting("hideTaskbarDuringPlayback", fallback: true);
        _disableGameOverlays = ReadBoolSetting("disableGameOverlays", fallback: true);

        // WPF WebView2 forwards accelerator keys here (not to Window.KeyDown).
        WebView.PreviewKeyDown += WebView_PreviewKeyDown;
        PreviewKeyDown += Window_PreviewKeyDown;

        // Alt + mixed order combos often never reach PreviewKeyDown inside WebView2.
        // Poll physical key state instead of a low-level hook (WebView2 is another PID).
        Loaded += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            MaintenanceHotkeyHook.Start(RequestMaintenancePrompt, hwnd);
            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                source.AddHook(WndProc);
            }
        };
        Closed += (_, _) =>
        {
            _heartbeatTimer.Stop();
            try { _backgroundHeartbeat?.Dispose(); } catch { /* ignore */ }
            _backgroundHeartbeat = null;
            MaintenanceHotkeyHook.Stop();
            _topmostGuard?.Stop();
            _topmostGuard?.Dispose();
            IdleSleepPolicy.TryLog("MainWindow Closed allowClose=" + _allowClose);
        };

        _cursorIdleTimer = new DispatcherTimer { Interval = CursorIdleHideAfter };
        _cursorIdleTimer.Tick += (_, _) =>
        {
            _cursorIdleTimer.Stop();
            if (_maintenancePromptOpen || !_hideCursorDuringPlayback) return;
            SetCursorVisible(false);
        };

        ApplyPlaybackCursorPolicy(forceHide: true);

        IdleSleepPolicy.TryLog(
            $"Player settings disableSleep={_disableSleep} disableScreensaver={_disableScreensaver} " +
            $"disableGameOverlays={_disableGameOverlays}");

        _focusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _focusTimer.Tick += (_, _) =>
        {
            // Only when "Disable sleep" is on. Saver toggle must not gate this.
            if (!_disableSleep) return;
            // Never Activate during quiet — that wakes the monitor and caused instability.
            if (_maintenancePromptOpen || _allowClose || _displayQuiet) return;
            if (!IsActive)
            {
                Activate();
                Focus();
            }
        };
        _focusTimer.Start();

        MouseMove += (_, _) =>
        {
            if (_maintenancePromptOpen) return;
            OnPlaybackMouseMove();
        };

        Loaded += async (_, _) =>
        {
            PlaceOnConfiguredDisplay();
            ApplyPlaybackCursorPolicy(forceHide: true);
            await StartWebViewWithRetryAsync();
        };
    }

    private async Task StartWebViewWithRetryAsync()
    {
        const int maxAttempts = 8;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await _bridge.InitializeAsync(
                    allowIdleSleep: !_disableSleep,
                    allowScreensaver: !_disableScreensaver);
                ApplyOverlayTopmostPolicy();
                if (WebView.CoreWebView2 != null)
                {
                    WebView.CoreWebView2.NavigationCompleted += (_, args) =>
                    {
                        if (args.IsSuccess && !_maintenancePromptOpen)
                        {
                            ApplyPlaybackCursorPolicy(forceHide: true);
                        }
                    };
                }

                IdleSleepPolicy.TryLog($"WebView2 ready (attempt {attempt})");
                return;
            }
            catch (Exception ex)
            {
                IdleSleepPolicy.TryLog($"WebView2 init failed attempt {attempt}/{maxAttempts}: {ex.Message}");
                if (attempt >= maxAttempts)
                {
                    IdleSleepPolicy.TryLog("WebView2 init gave up — process stays alive for Watchdog.");
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, attempt * 2)));
            }
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmClose = 0x0010;
        const int wmActivate = 0x0006;
        const int wmSyscommand = 0x0112;
        const int scClose = 0xF060;
        const int scScreensaver = 0xF140;
        const int scMonitorpower = 0xF170;

        // Block external close attempts (Taskbar, Alt+F4 via SC_CLOSE, etc.)
        // unless maintenance unlock explicitly allowed exit.
        if (!_allowClose)
        {
            if (msg == wmClose)
            {
                handled = true;
                return IntPtr.Zero;
            }

            if (msg == wmSyscommand && (wParam.ToInt32() & 0xFFF0) == scClose)
            {
                handled = true;
                return IntPtr.Zero;
            }
        }

        if (msg == wmActivate && _disableGameOverlays && wParam.ToInt32() == 0)
        {
            _topmostGuard?.Raise();
        }

        if (msg == wmSyscommand)
        {
            var cmd = wParam.ToInt32() & 0xFFF0;

            // Critical: when sleep is allowed, never swallow SC_SCREENSAVE / SC_MONITORPOWER.
            // Handling SC_SCREENSAVE while "Disable screen saver" is on was enough to stop
            // Windows idle sleep on the test machines — even with no video playing.
            if (!_disableSleep)
            {
                return IntPtr.Zero;
            }

            if (cmd == scScreensaver)
            {
                handled = _disableScreensaver;
            }
            else if (cmd == scMonitorpower)
            {
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Quiet hours: black out content inside WebView2 (keeps CMS/WebSocket alive).
    /// Never use SC_MONITORPOWER / hide WebView — that froze the UI thread and marked the device offline.
    /// </summary>
    public void SetQuietOverlay(bool muted)
    {
        if (_displayQuiet == muted)
        {
            return;
        }

        _displayQuiet = muted;
        // WPF overlay cannot paint above WebView2 HWND; keep it as a no-op fallback only.
        QuietOverlay.Visibility = Visibility.Collapsed;

        try
        {
            var core = WebView.CoreWebView2;
            if (core == null)
            {
                return;
            }

            // Injected overlay lives in the page — player process + WS stay up (Tizen/BS behaviour).
            var script = muted
                ? @"(() => {
                    let el = document.getElementById('tomorrowos-quiet-overlay');
                    if (!el) {
                      el = document.createElement('div');
                      el.id = 'tomorrowos-quiet-overlay';
                      el.setAttribute('aria-hidden', 'true');
                      el.style.cssText = 'position:fixed;inset:0;background:#000;z-index:2147483647;pointer-events:none;';
                      (document.body || document.documentElement).appendChild(el);
                    }
                    el.style.display = 'block';
                    document.querySelectorAll('video, audio').forEach((media) => {
                      try {
                        if (media.dataset.tosQuietMuted == null) {
                          media.dataset.tosQuietMuted = media.muted ? '1' : '0';
                        }
                        media.muted = true;
                      } catch (_) {}
                    });
                    true;
                  })()"
                : @"(() => {
                    const el = document.getElementById('tomorrowos-quiet-overlay');
                    if (el) el.style.display = 'none';
                    document.querySelectorAll('video, audio').forEach((media) => {
                      try {
                        if (media.dataset.tosQuietMuted === '0') media.muted = false;
                        delete media.dataset.tosQuietMuted;
                      } catch (_) {}
                    });
                    true;
                  })()";

            _ = core.ExecuteScriptAsync(script);
        }
        catch
        {
            // ignore script failures — quiet flag still prevents focus reclaim
        }
    }

    private void PlaceOnConfiguredDisplay()
    {
        try
        {
            // Indices match Setup: both sort monitors by (Left, Top).
            var index = 0;
            var settingsPath = AppPaths.SettingsFile;
            if (File.Exists(settingsPath))
            {
                var json = File.ReadAllText(settingsPath);
                var match = System.Text.RegularExpressions.Regex.Match(json, "\"displayIndex\"\\s*:\\s*(\\d+)");
                if (match.Success)
                {
                    index = int.Parse(match.Groups[1].Value);
                }
            }

            var monitors = DisplayService.GetMonitors();
            if (monitors.Count == 0) return;
            if (index < 0 || index >= monitors.Count) index = 0;

            // Hide taskbar ON → cover full monitor. OFF → stay in work area so the
            // Windows taskbar remains visible beside the player.
            var area = _hideTaskbarDuringPlayback
                ? monitors[index].Bounds
                : monitors[index].WorkArea;

            WindowStartupLocation = WindowStartupLocation.Manual;
            WindowState = WindowState.Normal;
            Topmost = _disableGameOverlays;

            // Physical pixels via SetWindowPos — WPF Left/Top are DIPs and
            // mis-place under PerMonitorV2 / mixed DPI.
            var helper = new WindowInteropHelper(this);
            helper.EnsureHandle();
            SetWindowPos(
                helper.Handle,
                IntPtr.Zero,
                (int)area.Left,
                (int)area.Top,
                (int)area.Width,
                (int)area.Height,
                SwpNozorder | SwpShowwindow);

            if (_hideTaskbarDuringPlayback)
            {
                WindowState = WindowState.Maximized;
            }
        }
        catch
        {
            // keep default maximized primary
        }
    }

    private void ApplyOverlayTopmostPolicy()
    {
        _topmostGuard?.Stop();
        _topmostGuard?.Dispose();
        _topmostGuard = null;

        if (_disableGameOverlays)
        {
            _topmostGuard = new PlayerTopmostGuard(this);
            _topmostGuard.Start();
        }
        else
        {
            Topmost = false;
            GameOverlayPolicy.RestoreEnable();
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_maintenancePromptOpen && (e.Key == Key.System || e.SystemKey == Key.F4))
        {
            e.Handled = true;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        TryHandleMaintenanceChord(e);
    }

    private void WebView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        TryHandleMaintenanceChord(e);
    }

    private void TryHandleMaintenanceChord(KeyEventArgs e)
    {
        if (!MaintenanceHotkeyHook.IsChordPhysicallyDown())
        {
            return;
        }

        e.Handled = true;
        RequestMaintenancePrompt();
    }

    public void RequestMaintenancePrompt()
    {
        if (_maintenancePromptOpen || _allowClose)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now - _lastMaintenanceRequestUtc < MaintenanceDebounce)
        {
            return;
        }

        _lastMaintenanceRequestUtc = now;
        PromptMaintenance();
    }

    private void PromptMaintenance()
    {
        _maintenancePromptOpen = true;
        _topmostGuard?.Stop();
        // Drop player below the maintenance gate; Topmost otherwise covers it.
        Topmost = false;
        SetCursorVisible(true);

        _passcodeDialog = PasscodeDialog.ShowFloating(ValidatePasscode);
        _passcodeDialog.Unlocked += OnMaintenanceUnlocked;
        _passcodeDialog.Closed += OnPasscodeDialogClosed;
    }

    private void OnMaintenanceUnlocked()
    {
        ExitAfterMaintenanceUnlock();
    }

    private void OnPasscodeDialogClosed(object? sender, EventArgs e)
    {
        if (_passcodeDialog != null)
        {
            _passcodeDialog.Unlocked -= OnMaintenanceUnlocked;
            _passcodeDialog.Closed -= OnPasscodeDialogClosed;
            _passcodeDialog = null;
        }

        _maintenancePromptOpen = false;

        if (!_allowClose)
        {
            ApplyOverlayTopmostPolicy();

            // Must run AFTER clearing _maintenancePromptOpen (policy ignores updates while open).
            ApplyPlaybackCursorPolicy(forceHide: true);
        }
    }

    private static bool ValidatePasscode(string pass)
    {
        try
        {
            var settingsPath = AppPaths.SettingsFile;
            if (!File.Exists(settingsPath))
            {
                return pass == "tomorrow";
            }

            var json = File.ReadAllText(settingsPath);
            var match = System.Text.RegularExpressions.Regex.Match(json, "\"maintenancePasscodeHash\"\\s*:\\s*\"([^\"]+)\"");
            if (!match.Success)
            {
                return pass == "tomorrow";
            }

            var expected = match.Groups[1].Value;
            var actual = HashPasscode(pass);
            return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static string HashPasscode(string pass)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("tomorrowos:" + pass));
        return Convert.ToHexString(bytes);
    }

    private void ExitAfterMaintenanceUnlock()
    {
        try
        {
            File.WriteAllText(AppPaths.StopFlagFile, DateTime.UtcNow.ToString("O"));
            if (File.Exists(AppPaths.MaintenanceFlagFile))
            {
                File.Delete(AppPaths.MaintenanceFlagFile);
            }
        }
        catch
        {
            // ignore
        }

        _allowClose = true;
        Close();
    }

    private static bool ReadBoolSetting(string propertyName, bool fallback)
    {
        try
        {
            var settingsPath = AppPaths.SettingsFile;
            if (!File.Exists(settingsPath))
            {
                return fallback;
            }

            var json = File.ReadAllText(settingsPath);
            var match = System.Text.RegularExpressions.Regex.Match(
                json,
                "\"" + propertyName + "\"\\s*:\\s*(true|false)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return fallback;
            }

            return match.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return fallback;
        }
    }

    private static bool ReadHideCursorSetting() =>
        ReadBoolSetting("hideCursorDuringPlayback", fallback: true);

    /// <summary>
    /// Applies installer "Hide mouse cursor after inactivity".
    /// Off → cursor stays visible during playback.
    /// On → hide after idle; mouse move briefly shows it again.
    /// </summary>
    private void ApplyPlaybackCursorPolicy(bool forceHide)
    {
        if (_maintenancePromptOpen)
        {
            return;
        }

        if (!_hideCursorDuringPlayback)
        {
            _cursorIdleTimer.Stop();
            SetCursorVisible(true);
            return;
        }

        if (forceHide)
        {
            _cursorIdleTimer.Stop();
            SetCursorVisible(false);
        }
    }

    private void OnPlaybackMouseMove()
    {
        if (!_hideCursorDuringPlayback)
        {
            SetCursorVisible(true);
            return;
        }

        // Option on: show while moving, hide again after inactivity.
        SetCursorVisible(true);
        _cursorIdleTimer.Stop();
        _cursorIdleTimer.Start();
    }

    /// <summary>
    /// Hide/show cursor. Must also set CSS inside WebView2 — WPF OverrideCursor does not cover the HWND.
    /// </summary>
    private void SetCursorVisible(bool visible)
    {
        if (visible)
        {
            Cursor = System.Windows.Input.Cursors.Arrow;
            Mouse.OverrideCursor = null;
            WebView.Cursor = System.Windows.Input.Cursors.Arrow;
        }
        else
        {
            Cursor = System.Windows.Input.Cursors.None;
            Mouse.OverrideCursor = System.Windows.Input.Cursors.None;
            WebView.Cursor = System.Windows.Input.Cursors.None;
        }

        try
        {
            var core = WebView.CoreWebView2;
            if (core == null) return;

            var script = visible
                ? @"(() => {
                    const s = document.getElementById('tomorrowos-cursor-style');
                    if (s) s.remove();
                    document.documentElement.style.cursor = '';
                    if (document.body) document.body.style.cursor = '';
                    true;
                  })()"
                : @"(() => {
                    let s = document.getElementById('tomorrowos-cursor-style');
                    if (!s) {
                      s = document.createElement('style');
                      s.id = 'tomorrowos-cursor-style';
                      (document.head || document.documentElement).appendChild(s);
                    }
                    s.textContent = '*, *::before, *::after { cursor: none !important; }';
                    document.documentElement.style.cursor = 'none';
                    if (document.body) document.body.style.cursor = 'none';
                    true;
                  })()";
            _ = core.ExecuteScriptAsync(script);
        }
        catch
        {
            // ignore
        }
    }

    private static void TouchHeartbeatFile()
    {
        try
        {
            AppPaths.EnsureDirectories();
            File.WriteAllText(AppPaths.HeartbeatFile, DateTime.UtcNow.ToString("O"));
        }
        catch
        {
            // ignore
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            IdleSleepPolicy.TryLog("Blocked unexpected close attempt");
        }
        else
        {
            IdleSleepPolicy.TryLog("Allowing close after maintenance unlock");
        }
    }
}
