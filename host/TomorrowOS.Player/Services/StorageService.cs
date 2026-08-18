using System.IO.Compression;
using System.Text.Json;

namespace TomorrowOS.Player.Services;

internal sealed class StorageService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string NormalizeRelPath(string? input)
    {
        var value = (input ?? string.Empty).Replace('\\', '/').Trim('/');
        while (value.Contains("..", StringComparison.Ordinal))
        {
            value = value.Replace("..", "", StringComparison.Ordinal);
        }
        return value;
    }

    public string ToAbsPath(string? relPath)
    {
        var rel = NormalizeRelPath(relPath);
        if (string.IsNullOrEmpty(rel))
        {
            return AppPaths.StorageRoot;
        }

        var candidate = Path.GetFullPath(Path.Combine(AppPaths.StorageRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(AppPaths.StorageRoot);
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Path escapes storage root");
        }

        return candidate;
    }

    public object? Resolve(string pathInput, string mode)
    {
        var abs = ToAbsPath(pathInput);
        if (Directory.Exists(abs) || File.Exists(abs))
        {
            return MakeEntry(abs);
        }

        if (string.Equals(mode, "rw", StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(abs);
            return MakeEntry(abs);
        }

        return null;
    }

    public List<object> List(string pathInput)
    {
        var abs = ToAbsPath(pathInput);
        if (!Directory.Exists(abs))
        {
            return [];
        }

        return Directory.EnumerateFileSystemEntries(abs)
            .Select(MakeEntry)
            .Cast<object>()
            .ToList();
    }

    public void Mkdir(string pathInput)
    {
        Directory.CreateDirectory(ToAbsPath(pathInput));
    }

    public void ExtractZip(string zipRel, string targetRel)
    {
        var zipAbs = ToAbsPath(zipRel);
        var targetAbs = ToAbsPath(targetRel);
        Directory.CreateDirectory(targetAbs);

        using var archive = ZipFile.OpenRead(zipAbs);
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(targetAbs, entry.FullName));
            if (!destination.StartsWith(targetAbs, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("ZIP slip blocked: " + entry.FullName);
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private object MakeEntry(string absPath)
    {
        var isDir = Directory.Exists(absPath);
        var root = Path.GetFullPath(AppPaths.StorageRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(absPath);
        var rel = full.Length <= root.Length
            ? ""
            : full[(root.Length + 1)..].Replace('\\', '/');

        return new
        {
            isDirectory = isDir,
            fullPath = rel,
            absPath = full.Replace('\\', '/'),
            name = Path.GetFileName(full)
        };
    }

    public string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);
}
