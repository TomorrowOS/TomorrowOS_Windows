using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using TomorrowOS.Player.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;

namespace TomorrowOS.Player;

public partial class MainWindow : Window
{
    private readonly BridgeHost _bridge;
    private readonly DispatcherTimer _focusTimer;
    private bool _maintenanceMode;
    private bool _allowClose;
    private bool _displayQuiet;

    // Avoid F12 combos — many OEM / Windows utilities bind those to Calculator.
    private const string HotkeyChordHint = "Ctrl+Shift+Alt+M";

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

        // WPF WebView2 forwards accelerator keys here (not to Window.KeyDown).
        WebView.PreviewKeyDown += WebView_PreviewKeyDown;

        // Playback: never show cursor, even while moving the mouse.
        SetCursorVisible(false);

        _focusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _focusTimer.Tick += (_, _) =>
        {
            // Never Activate during quiet — that wakes the monitor and caused instability.
            if (_maintenanceMode || _allowClose || _displayQuiet) return;
            if (!IsActive)
            {
                Activate();
                Focus();
            }
        };
        _focusTimer.Start();

        MouseMove += (_, _) =>
        {
            if (_maintenanceMode) return;
            // Keep forcing hide — WebView2 can restore its own cursor on move.
            SetCursorVisible(false);
        };

        Loaded += async (_, _) =>
        {
            PlaceOnConfiguredDisplay();
            SetCursorVisible(false);
            try
            {
                await _bridge.InitializeAsync();
                if (WebView.CoreWebView2 != null)
                {
                    WebView.CoreWebView2.NavigationCompleted += (_, args) =>
                    {
                        if (args.IsSuccess && !_maintenanceMode)
                        {
                            SetCursorVisible(false);
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Failed to start WebView2 player host:\n" + ex.Message,
                    "TomorrowOS",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        };
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

            var screens = DisplayService.GetMonitorBounds();
            if (screens.Count == 0) return;
            if (index < 0 || index >= screens.Count) index = 0;

            var bounds = screens[index];
            WindowStartupLocation = WindowStartupLocation.Manual;
            WindowState = WindowState.Normal;

            // Physical pixels via SetWindowPos — WPF Left/Top are DIPs and
            // mis-place under PerMonitorV2 / mixed DPI.
            var helper = new WindowInteropHelper(this);
            helper.EnsureHandle();
            SetWindowPos(
                helper.Handle,
                IntPtr.Zero,
                (int)bounds.Left,
                (int)bounds.Top,
                (int)bounds.Width,
                (int)bounds.Height,
                SwpNozorder | SwpShowwindow);

            WindowState = WindowState.Maximized;
        }
        catch
        {
            // keep default maximized primary
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (IsMaintenanceChord(e.Key))
        {
            e.Handled = true;
            RequestMaintenancePrompt();
            return;
        }

        if (!_maintenanceMode && (e.Key == Key.System || e.SystemKey == Key.F4))
        {
            e.Handled = true;
        }
    }

    private void WebView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsMaintenanceChord(e.Key))
        {
            return;
        }

        e.Handled = true;
        RequestMaintenancePrompt();
    }

    private static bool IsMaintenanceChord(Key key)
    {
        if (key != Key.M)
        {
            return false;
        }

        return (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) &&
               (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) &&
               (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt));
    }

    public void RequestMaintenancePrompt()
    {
        if (_maintenanceMode)
        {
            return;
        }

        PromptMaintenance();
    }

    private void PromptMaintenance()
    {
        // Lab / V1: hotkey alone is enough to enter maintenance (no passcode gate).
        EnterMaintenanceMode();
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

    private void EnterMaintenanceMode()
    {
        _maintenanceMode = true;
        Topmost = false;
        ShowInTaskbar = true;
        MaintenanceBanner.Visibility = Visibility.Visible;
        SetCursorVisible(true);
        try
        {
            File.WriteAllText(AppPaths.MaintenanceFlagFile, DateTime.UtcNow.ToString("O"));
        }
        catch
        {
            // ignore
        }

        // Custom dialog so title-bar X means continue (same as No), not an ambiguous Cancel.
        if (ExitConfirmDialog.ConfirmExit())
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
            return;
        }

        ExitMaintenanceMode();
    }

    private void ExitMaintenanceMode()
    {
        _maintenanceMode = false;
        Topmost = true;
        ShowInTaskbar = false;
        MaintenanceBanner.Visibility = Visibility.Collapsed;
        SetCursorVisible(false);
        try
        {
            if (File.Exists(AppPaths.MaintenanceFlagFile))
            {
                File.Delete(AppPaths.MaintenanceFlagFile);
            }
        }
        catch
        {
            // ignore
        }

        Activate();
        WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// Hide cursor during playback; show only in maintenance (Ctrl+Shift+Alt+M).
    /// Must also set CSS inside WebView2 — WPF OverrideCursor does not cover the HWND.
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

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose && !_maintenanceMode)
        {
            e.Cancel = true;
        }
    }
}
