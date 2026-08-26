using System.Runtime.InteropServices;
using System.Windows;

namespace TomorrowOS.Player.Services;

internal readonly record struct MonitorLayout(Rect Bounds, Rect WorkArea);

internal static class DisplayService
{
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    /// <summary>
    /// Monitors sorted by (Left, Top) so indices match TomorrowOS Setup.
    /// Bounds = full screen; WorkArea excludes the taskbar / docks.
    /// </summary>
    public static IReadOnlyList<MonitorLayout> GetMonitors()
    {
        var list = new List<MonitorLayout>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                list.Add(new MonitorLayout(info.rcMonitor.ToRect(), info.rcWork.ToRect()));
            }
            else
            {
                var bounds = rect.ToRect();
                list.Add(new MonitorLayout(bounds, bounds));
            }

            return true;
        }, IntPtr.Zero);

        if (list.Count == 0)
        {
            var bounds = new Rect(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.PrimaryScreenWidth,
                SystemParameters.PrimaryScreenHeight);
            list.Add(new MonitorLayout(bounds, bounds));
        }

        return list
            .OrderBy(m => m.Bounds.Left)
            .ThenBy(m => m.Bounds.Top)
            .ToList();
    }

    /// <summary>Full monitor bounds only (legacy callers).</summary>
    public static IReadOnlyList<Rect> GetMonitorBounds() =>
        GetMonitors().Select(m => m.Bounds).ToList();
}
