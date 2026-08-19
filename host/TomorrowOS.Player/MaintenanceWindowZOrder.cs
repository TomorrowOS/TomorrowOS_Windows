using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TomorrowOS.Player;

/// <summary>
/// Keeps the maintenance passcode window above the hardened Topmost player.
/// Other floating windows are not exempt — only this maintenance gate uses it.
/// </summary>
internal sealed class MaintenanceWindowZOrder
{
    private static readonly IntPtr HwndTopmost = new(-1);

    private const uint SwpNomove = 0x0002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpShowwindow = 0x0040;

    private readonly Window _window;
    private readonly DispatcherTimer _timer;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    public MaintenanceWindowZOrder(Window window)
    {
        _window = window;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Raise();
    }

    public void Start()
    {
        Raise();
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
    }

    private void Raise()
    {
        var helper = new WindowInteropHelper(_window);
        if (helper.Handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(
            helper.Handle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNomove | SwpNosize | SwpShowwindow);
    }
}
