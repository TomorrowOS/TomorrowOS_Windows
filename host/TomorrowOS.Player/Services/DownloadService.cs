using System.Collections.Concurrent;
using System.Net.Http;

namespace TomorrowOS.Player.Services;

internal sealed class DownloadService
{
    private readonly StorageService _storage;
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30)
    };
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();

    public DownloadService(StorageService storage)
    {
        _storage = storage;
    }

    public async Task<object> StartAsync(string id, string url, string destination, string fileName, CancellationToken outer = default)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _active[id] = cts;

        try
        {
            var destDir = _storage.ToAbsPath(destination);
            Directory.CreateDirectory(destDir);

            var safeName = Path.GetFileName(string.IsNullOrWhiteSpace(fileName) ? "download.bin" : fileName);
            var partial = Path.Combine(destDir, safeName + ".partial");
            var finalAbs = Path.Combine(destDir, safeName);

            using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cts.Token);
                await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, cts.Token);
            }

            if (File.Exists(finalAbs))
            {
                File.Delete(finalAbs);
            }

            File.Move(partial, finalAbs);

            var root = Path.GetFullPath(AppPaths.StorageRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var full = Path.GetFullPath(finalAbs);
            var rel = full.Length <= root.Length
                ? safeName
                : full[(root.Length + 1)..].Replace('\\', '/');

            return new { fullPath = rel, absPath = full.Replace('\\', '/') };
        }
        finally
        {
            _active.TryRemove(id, out _);
            cts.Dispose();
        }
    }

    public void Cancel(string id)
    {
        if (_active.TryGetValue(id, out var cts))
        {
            cts.Cancel();
        }
    }
}
