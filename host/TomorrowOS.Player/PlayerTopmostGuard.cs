using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TomorrowOS.Player;

/// <summary>
/// While overlay protection is on: disable Game Bar via registry, stop overlay app if it
/// launches, hide any overlay windows, and keep the player topmost.
/// </summary>
internal sealed class PlayerTopmostGuard : IDisposable
{
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotopmost = new(-2);
    private static readonly IntPtr HwndBottom = new(1);

    private const uint SwpNomove = 0x0002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpShowwindow = 0x0040;
    private const int SwHide = 0;

    private readonly Window _window;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _registryTimer;
    private readonly GameBarInputBlocker _inputBlocker;
    private IntPtr _playerHwnd;
    private bool _disposed;

    public PlayerTopmostGuard(Window window)
    {
        _window = window;
        _inputBlocker = new GameBarInputBlocker();
        _inputBlocker.ShortcutBlocked += OnShortcutBlocked;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _timer.Tick += (_, _) => EnforceOverlayBlock();
        _registryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _registryTimer.Tick += (_, _) => GameOverlayPolicy.ApplyRegistryDisable();
    }

    public void Start()
    {
        var helper = new WindowInteropHelper(_window);
        helper.EnsureHandle();
        _playerHwnd = helper.Handle;

        GameOverlayPolicy.ApplyDisable();
        _window.Topmost = true;
        _inputBlocker.Start();
        EnforceOverlayBlock();
        _timer.Start();
        _registryTimer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _registryTimer.Stop();
        _inputBlocker.Stop();
    }

    public void Raise() => EnforceOverlayBlock();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _inputBlocker.ShortcutBlocked -= OnShortcutBlocked;
        _inputBlocker.Dispose();
        _disposed = true;
    }

    private void OnShortcutBlocked()
    {
        _window.Dispatcher.BeginInvoke(EnforceOverlayBlock, DispatcherPriority.Background);
    }

    private void EnforceOverlayBlock()
    {
        if (_playerHwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            GameOverlayPolicy.StopOverlayProcesses();
            GameOverlayPolicy.HideVisibleOverlayWindows();
            RefreshTopmost();
            DemoteForegroundOverlayIfAny();
        }
        catch
        {
            // Never let overlay protection take the player down.
        }
    }

    private void RefreshTopmost()
    {
        SetWindowPos(
            _playerHwnd,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNomove | SwpNosize | SwpNoactivate | SwpShowwindow);
    }

    private void DemoteForegroundOverlayIfAny()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || IsPlayerWindow(fg) || !IsGameOverlayWindow(fg))
        {
            return;
        }

        var flags = SwpNomove | SwpNosize | SwpNoactivate;
        ShowWindow(fg, SwHide);
        SetWindowPos(fg, HwndNotopmost, 0, 0, 0, 0, flags);
        SetWindowPos(fg, HwndBottom, 0, 0, 0, 0, flags);
        RefreshTopmost();
    }

    private bool IsPlayerWindow(IntPtr hwnd)
    {
        if (hwnd == _playerHwnd)
        {
            return true;
        }

        _ = GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == Environment.ProcessId)
        {
            return true;
        }

        for (var current = hwnd; current != IntPtr.Zero; current = GetParent(current))
        {
            if (current == _playerHwnd)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGameOverlayWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd))
        {
            return false;
        }

        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            using var proc = Process.GetProcessById(pid);
            var name = proc.ProcessName;
            return name.Equals("GameBar", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("XboxGameBar", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("XboxGamingOverlay", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
