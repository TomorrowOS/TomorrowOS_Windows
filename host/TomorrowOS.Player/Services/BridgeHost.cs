using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace TomorrowOS.Player.Services;

internal sealed class BridgeHost
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly WebView2 _webView;
    private readonly Window _window;
    private readonly StorageService _storage = new();
    private readonly DeviceInfoService _deviceInfo = new();
    private readonly DownloadService _downloads;
    private readonly ScreenshotService _screenshots = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private bool _displayMuted;
    private DispatcherTimer? _heartbeatTimer;

    public const string AppHost = "tomorrowos.app";
    public const string CacheHost = "tomorrowos.cache";

    public BridgeHost(WebView2 webView, Window window)
    {
        _webView = webView;
        _window = window;
        _downloads = new DownloadService(_storage);
    }

    public async Task InitializeAsync()
    {
        AppPaths.EnsureDirectories();
        WriteHeartbeat();

        var env = await CoreWebView2Environment.CreateAsync(
            userDataFolder: Path.Combine(AppPaths.ProgramDataRoot, "webview2"));
        await _webView.EnsureCoreWebView2Async(env);

        var core = _webView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;

        core.SetVirtualHostNameToFolderMapping(
            AppHost,
            AppPaths.WwwRoot,
            CoreWebView2HostResourceAccessKind.Allow);

        core.SetVirtualHostNameToFolderMapping(
            CacheHost,
            AppPaths.StorageRoot,
            CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationCompleted += (_, args) =>
        {
            if (!args.IsSuccess)
            {
                Debug.WriteLine("Navigation failed: " + args.WebErrorStatus);
            }
        };

        core.Navigate($"https://{AppHost}/index.html");

        _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _heartbeatTimer.Tick += (_, _) => WriteHeartbeat();
        _heartbeatTimer.Start();
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
            case "host.getBootstrap":
                return new
                {
                    storageRoot = AppPaths.StorageRoot.Replace('\\', '/'),
                    cacheVirtualHost = $"https://{CacheHost}/",
                    deviceInfo = _deviceInfo.GetDeviceInfo()
                };

            case "app.heartbeat":
                WriteHeartbeat();
                return new { ok = true };

            case "app.requestMaintenance":
                await _window.Dispatcher.InvokeAsync(() =>
                {
                    if (_window is MainWindow main)
                    {
                        main.RequestMaintenancePrompt();
                    }
                });
                return new { ok = true };

            case "fs.resolve":
                return _storage.Resolve(
                    GetString(paramsEl, "path") ?? "",
                    GetString(paramsEl, "mode") ?? "r");

            case "fs.list":
                return _storage.List(GetString(paramsEl, "path") ?? "");

            case "fs.mkdir":
                _storage.Mkdir(GetString(paramsEl, "path") ?? "");
                return new { ok = true };

            case "download.start":
                return await _downloads.StartAsync(
                    GetString(paramsEl, "id") ?? Guid.NewGuid().ToString("N"),
                    GetString(paramsEl, "url") ?? throw new InvalidOperationException("url required"),
                    GetString(paramsEl, "destination") ?? "downloads/tomorrowos/staging",
                    GetString(paramsEl, "fileName") ?? "download.bin");

            case "download.cancel":
                _downloads.Cancel(GetString(paramsEl, "id") ?? "");
                return new { ok = true };

            case "archive.extract":
                _storage.ExtractZip(
                    GetString(paramsEl, "zipPath") ?? throw new InvalidOperationException("zipPath required"),
                    GetString(paramsEl, "targetDir") ?? throw new InvalidOperationException("targetDir required"));
                return new { ok = true };

            case "device.captureScreenshot":
            {
                object? capture = null;
                await _window.Dispatcher.InvokeAsync(() =>
                {
                    capture = _screenshots.CaptureWindow(_window);
                });
                return capture ?? throw new InvalidOperationException("Screenshot failed");
            }

            case "device.reboot":
                Process.Start(new ProcessStartInfo
                {
                    FileName = "shutdown",
                    Arguments = "/r /t 3 /c \"TomorrowOS reboot\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                return new { ok = true };

            case "display.setMuted":
            {
                _displayMuted = ReadMutedFlag(paramsEl);
                await _window.Dispatcher.InvokeAsync(() =>
                {
                    if (_window is MainWindow main)
                    {
                        main.SetQuietOverlay(_displayMuted);
                    }
                });
                // Explicit muted:true during timer-off so CMS/device logs are not false.
                return new { muted = _displayMuted, quiet = _displayMuted };
            }

            case "http.probe":
            {
                var url = GetString(paramsEl, "url") ?? throw new InvalidOperationException("url required");
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                return new { ok = response.IsSuccessStatusCode, status = (int)response.StatusCode };
            }

            case "http.getJson":
            {
                var url = GetString(paramsEl, "url") ?? throw new InvalidOperationException("url required");
                using var response = await _http.GetAsync(url);
                var body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");
                }

                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                return new { json = doc.RootElement.Clone() };
            }

            default:
                throw new InvalidOperationException("Unknown host method: " + method);
        }
    }

    private void WriteHeartbeat()
    {
        try
        {
            File.WriteAllText(AppPaths.HeartbeatFile, DateTime.UtcNow.ToString("O"));
        }
        catch
        {
            // ignore
        }
    }

    private void Post(object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        _webView.Dispatcher.Invoke(() =>
        {
            _webView.CoreWebView2?.PostWebMessageAsString(json);
        });
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        return element.TryGetProperty(name, out var value) ? value.GetString() : null;
    }

    private static bool ReadMutedFlag(JsonElement paramsEl)
    {
        if (paramsEl.ValueKind != JsonValueKind.Object ||
            !paramsEl.TryGetProperty("muted", out var mutedEl))
        {
            return false;
        }

        return mutedEl.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => mutedEl.TryGetInt32(out var n) && n != 0,
            JsonValueKind.String => bool.TryParse(mutedEl.GetString(), out var b) && b,
            _ => false
        };
    }
}
