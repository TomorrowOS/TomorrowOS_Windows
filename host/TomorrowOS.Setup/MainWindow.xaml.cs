using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;
using Screen = System.Windows.Forms.Screen;
using Form = System.Windows.Forms.Form;
using FormBorderStyle = System.Windows.Forms.FormBorderStyle;
using FormStartPosition = System.Windows.Forms.FormStartPosition;
using Label = System.Windows.Forms.Label;
using ContentAlignment = System.Drawing.ContentAlignment;

namespace TomorrowOS.Setup;

public partial class MainWindow : Window
{
    private InstallRequest _pending = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private const int WmNclButtonDown = 0xA1;
    private const int HtCaption = 0x2;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            try
            {
                await InitWebViewAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Failed to start installer UI:\n" + ex.Message,
                    "TomorrowOS Setup",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Close();
            }
        };
    }

    private async Task InitWebViewAsync()
    {
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TomorrowOS",
            "setup-webview");
        Directory.CreateDirectory(userData);

        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
        await WebView.EnsureCoreWebView2Async(env);

        var core = WebView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;

        var www = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var installerHtml = Path.Combine(www, "installer.html");
        if (!File.Exists(installerHtml))
        {
            throw new FileNotFoundException(
                "Installer UI was not packed into this Setup.exe.\n\n" +
                "Rebuild with npm run build, then use:\n" +
                "  build\\windows\\TomorrowOS-Windows-Setup.exe\n\n" +
                "Looked for:\n" + installerHtml);
        }

        core.SetVirtualHostNameToFolderMapping(
            "tomorrowos.setup",
            www,
            CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += OnWebMessageReceived;
        core.Navigate("https://tomorrowos.setup/installer.html");
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var root = doc.RootElement;
            id = root.TryGetProperty("id", out var idEl) ? idEl.ToString() : null;
            var method = root.TryGetProperty("method", out var methodEl) ? methodEl.GetString() : null;
            var paramsEl = root.TryGetProperty("params", out var p) ? p : default;

            var result = await DispatchAsync(method, paramsEl);
            Post(new { id, ok = true, result });
        }
        catch (Exception ex)
        {
            Post(new { id, ok = false, error = ex.Message });
        }
    }

    private async Task<object?> DispatchAsync(string? method, JsonElement paramsEl)
    {
        switch (method)
        {
            case "host.bootstrap":
                return new
                {
                    version = "1.0.0",
                    installDir = @"C:\Program Files\TomorrowOS",
                    cacheDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "TomorrowOS",
                        "cache"),
                    displays = EnumerateDisplays()
                };

            case "host.pickFolder":
            {
                var dialog = new OpenFolderDialog
                {
                    Title = "Choose install location",
                    InitialDirectory = GetString(paramsEl, "path", @"C:\Program Files\TomorrowOS")
                };
                return dialog.ShowDialog() == true ? dialog.FolderName : null;
            }

            case "host.identifyDisplays":
                await IdentifyDisplaysAsync();
                return true;

            case "host.testCms":
            {
                var url = GetString(paramsEl, "url", "").Trim().TrimEnd('/');
                if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("CMS URL must use HTTPS.");
                }

                using var res = await Http.GetAsync(url);
                return new
                {
                    status = (int)res.StatusCode,
                    ok = res.IsSuccessStatusCode || (int)res.StatusCode is >= 200 and < 500,
                    message = res.IsSuccessStatusCode
                        ? "Reached CMS endpoint successfully."
                        : $"HTTP {(int)res.StatusCode} — endpoint responded."
                };
            }

            case "host.install":
            {
                _pending = ParseInstallRequest(paramsEl);
                EnsureInstallPathWritable(_pending.InstallDir);
                await Task.Run(() => InstallService.InstallCore(_pending, (msg, level) =>
                {
                    Dispatcher.Invoke(() => PostEvent("install.log", new { message = msg, level }));
                }));
                return new { installDir = _pending.InstallDir };
            }

            case "host.finalize":
            {
                ApplyFinalizeParams(paramsEl, _pending);
                InstallService.FinalizeAndLaunch(_pending);
                // Close the installer immediately so the player window is visible right away.
                _ = Dispatcher.BeginInvoke(Close);
                return true;
            }

            case "host.beginDrag":
                Dispatcher.Invoke(() =>
                {
                    var helper = new WindowInteropHelper(this);
                    ReleaseCapture();
                    SendMessage(helper.Handle, WmNclButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
                });
                return true;

            case "host.close":
                Dispatcher.Invoke(Close);
                return true;

            default:
                throw new InvalidOperationException("Unknown method: " + method);
        }
    }

    private static void EnsureInstallPathWritable(string installDir)
    {
        try
        {
            Directory.CreateDirectory(installDir);
            var probe = Path.Combine(installDir, ".tomorrowos-write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "Access denied writing to \"" + installDir + "\". " +
                "TomorrowOS Setup must run as Administrator to install under Program Files. " +
                "Right-click TomorrowOS-Windows-Setup.exe and choose Run as administrator, " +
                "or approve the UAC prompt.");
        }
        catch (IOException ex) when (ex is DirectoryNotFoundException || ex.HResult == unchecked((int)0x80070005))
        {
            throw new InvalidOperationException(
                "Cannot write to \"" + installDir + "\": " + ex.Message);
        }
    }

    private static InstallRequest ParseInstallRequest(JsonElement p)
    {
        var hardening = p.TryGetProperty("hardening", out var h) ? h : default;
        bool Harden(string key) =>
            hardening.ValueKind == JsonValueKind.Object &&
            hardening.TryGetProperty(key, out var v) &&
            v.ValueKind == JsonValueKind.True;

        var applyHardening =
            Harden("saver") || Harden("notif");

        return new InstallRequest
        {
            InstallDir = GetString(p, "installDir", @"C:\Program Files\TomorrowOS"),
            Orientation = GetString(p, "orientation", "landscape"),
            DisplayIndex = Math.Max(0, GetInt(p, "displayIndex", 0)),
            ContentFit = GetString(p, "fit", "contain"),
            Passcode = GetString(p, "passcode", ""),
            SetupType = GetString(p, "setupType", "standard"),
            Role = GetString(p, "role", "dedicated"),
            AutoStart = Harden("autostart"),
            ApplyHardening = applyHardening && GetString(p, "role", "dedicated") != "shared",
            DisableScreensaver = Harden("saver"),
            PreventDisplayOff = Harden("display"),
            DisableSleep = Harden("sleep"),
            DisableHibernate = Harden("hibernate"),
            StartWatchdog = Harden("watchdog"),
            HideCursorDuringPlayback = Harden("cursor"),
            HideTaskbarDuringPlayback = Harden("taskbar"),
            DisableGameOverlays = Harden("overlay"),
            ConfigureWindowsUpdate = Harden("updates"),
            CmsEndpoint = ""
        };
    }

    private static void ApplyFinalizeParams(JsonElement p, InstallRequest req)
    {
        req.CmsEndpoint = GetString(p, "cmsEndpoint", req.CmsEndpoint);
        req.TimeZone = GetString(p, "timeZone", req.TimeZone);
        req.MaintenanceWindow = GetString(p, "maintenanceWindow", req.MaintenanceWindow);
        req.CreateDesktopShortcut = GetBool(p, "createDesktopShortcut", fallback: true);
    }

    private static Screen[] GetSortedScreens() =>
        Screen.AllScreens
            .OrderBy(s => s.Bounds.Left)
            .ThenBy(s => s.Bounds.Top)
            .ToArray();

    private static object[] EnumerateDisplays()
    {
        // Sort by position so indices match the Player (DisplayService).
        var screens = GetSortedScreens();
        var list = new object[screens.Length];
        for (var i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            var portrait = s.Bounds.Height > s.Bounds.Width;
            list[i] = new
            {
                id = i,
                name = "Display " + (i + 1),
                res = $"{s.Bounds.Width} × {s.Bounds.Height}",
                hz = "—",
                primary = s.Primary,
                portrait,
                model = s.DeviceName
            };
        }

        return list;
    }

    private async Task IdentifyDisplaysAsync()
    {
        // Use WinForms overlays: Screen.Bounds and Form.Bounds share the same
        // coordinate space. WPF Left/Top are DIPs and mis-place on multi-monitor / DPI setups.
        var overlays = new List<Form>();
        var screens = GetSortedScreens();
        for (var i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            var n = i + 1;
            var form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Bounds = s.Bounds,
                TopMost = true,
                ShowInTaskbar = false,
                BackColor = Color.FromArgb(20, 19, 17),
                Opacity = 0.88,
                ShowIcon = false
            };
            form.Controls.Add(new Label
            {
                Text = n.ToString(),
                Dock = System.Windows.Forms.DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Font = new Font(
                    new System.Drawing.FontFamily("Segoe UI"),
                    96f,
                    System.Drawing.FontStyle.Bold,
                    GraphicsUnit.Point)
            });
            form.Show();
            overlays.Add(form);
        }

        await Task.Delay(1800);
        foreach (var form in overlays)
        {
            form.Close();
            form.Dispose();
        }
    }

    private void Post(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        WebView.CoreWebView2?.PostWebMessageAsString(json);
    }

    private void PostEvent(string method, object data) =>
        Post(new { eventName = method, data });

    private static string GetString(JsonElement p, string name, string fallback) =>
        p.ValueKind == JsonValueKind.Object &&
        p.TryGetProperty(name, out var el) &&
        el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? fallback
            : fallback;

    private static int GetInt(JsonElement p, string name, int fallback)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var el))
        {
            return fallback;
        }

        if (el.TryGetInt32(out var i32))
        {
            return i32;
        }

        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d))
        {
            return (int)Math.Round(d);
        }

        if (el.ValueKind == JsonValueKind.String &&
            int.TryParse(el.GetString(), out var fromString))
        {
            return fromString;
        }

        return fallback;
    }

    private static bool GetBool(JsonElement p, string name, bool fallback)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var el))
        {
            return fallback;
        }

        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(el.GetString(), out var fromString) => fromString,
            _ => fallback
        };
    }
}
