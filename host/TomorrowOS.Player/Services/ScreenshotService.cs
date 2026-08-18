using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TomorrowOS.Player.Services;

internal sealed class ScreenshotService
{
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public object CaptureWindow(Window window)
    {
        var helper = new WindowInteropHelper(window);
        var hwnd = helper.Handle;
        if (hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("Player window handle unavailable");
        }

        if (!GetWindowRect(hwnd, out var rect))
        {
            throw new InvalidOperationException("GetWindowRect failed");
        }

        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
        }

        // Downscale for CMS transfer if very large
        using var export = width > 1920
            ? new Bitmap(bitmap, new System.Drawing.Size(1920, Math.Max(1, height * 1920 / width)))
            : bitmap;

        using var ms = new MemoryStream();
        export.Save(ms, ImageFormat.Jpeg);
        var bytes = ms.ToArray();

        return new
        {
            mimeType = "image/jpeg",
            width = export.Width,
            height = export.Height,
            capturedAt = DateTime.UtcNow.ToString("O"),
            dataBase64 = Convert.ToBase64String(bytes),
            source = "window-gdi"
        };
    }
}
