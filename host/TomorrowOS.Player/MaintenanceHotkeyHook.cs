using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace TomorrowOS.Player;

/// <summary>
/// Order-independent Ctrl+Shift+Alt+M detection via physical key state polling.
/// WebView2 focus lives in a different process, so WPF PreviewKeyDown + PID checks miss
/// many Alt-involved sequences (M-then-modifiers, Alt-then-M, etc.).
/// </summary>
internal static class MaintenanceHotkeyHook
{
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12; // Alt
    private const int VkM = 0x4D;

    private static DispatcherTimer? _timer;
    private static Action? _onChord;
    private static bool _chordWasDown;
    private static int _pid;
    private static IntPtr _playerHwnd;

    public static void Start(Action onChord, IntPtr playerHwnd)
    {
        _onChord = onChord ?? throw new ArgumentNullException(nameof(onChord));
        _playerHwnd = playerHwnd;
        _pid = Environment.ProcessId;
        _chordWasDown = false;

        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    public static void Stop()
    {
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
        }

        _onChord = null;
        _chordWasDown = false;
        _playerHwnd = IntPtr.Zero;
    }

    public static void UpdatePlayerHwnd(IntPtr playerHwnd) => _playerHwnd = playerHwnd;

    public static bool IsChordPhysicallyDown() =>
        IsDown(VkControl) && IsDown(VkShift) && IsDown(VkMenu) && IsDown(VkM);

    private static void OnTick(object? sender, EventArgs e)
    {
        var down = IsChordPhysicallyDown();
        if (down && !_chordWasDown && IsPlayerUiContext())
        {
            _onChord?.Invoke();
        }

        _chordWasDown = down;
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// True when key focus is our WPF window or a WebView2 HWND hosted for this player.
    /// </summary>
    private static bool IsPlayerUiContext()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(fg, out var fgPid);
        if (fgPid == _pid)
        {
            return true;
        }

        // WebView2 render HWNDs belong to msedgewebview2.exe — walk parents toward our window.
        if (_playerHwnd != IntPtr.Zero)
        {
            for (var hwnd = fg; hwnd != IntPtr.Zero; hwnd = GetParent(hwnd))
            {
                if (hwnd == _playerHwnd)
                {
                    return true;
                }
            }
        }

        try
        {
            using var proc = Process.GetProcessById(fgPid);
            var name = proc.ProcessName;
            if (name.Contains("msedgewebview2", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("WebView2", StringComparison.OrdinalIgnoreCase))
            {
                // Kiosk player is showing content — accept chord for this process's UI session.
                return _playerHwnd != IntPtr.Zero && IsWindowVisible(_playerHwnd);
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);
}
