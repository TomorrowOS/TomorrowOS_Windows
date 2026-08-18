using System.Runtime.InteropServices;
using System.Windows;

namespace TomorrowOS.Player.Services;

internal static class DisplayService
{
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>
    /// Monitor bounds in physical pixels, sorted by (Left, Top) so indices match
    /// TomorrowOS Setup (which sorts Screen.AllScreens the same way).
    /// </summary>
    public static IReadOnlyList<Rect> GetMonitorBounds()
    {
        var list = new List<Rect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            list.Add(new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
            return true;
        }, IntPtr.Zero);

        if (list.Count == 0)
        {
            list.Add(new Rect(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.PrimaryScreenWidth,
                SystemParameters.PrimaryScreenHeight));
        }

        return list
            .OrderBy(r => r.Left)
            .ThenBy(r => r.Top)
            .ToList();
    }
}
