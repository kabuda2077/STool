using System.IO;

namespace STool.Modules.LanTransfer;

internal sealed class SharedFileCatalog
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SharedFileEntry> _items = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<SharedFileEntry> AddPaths(IEnumerable<string> paths, bool allowDuplicatePaths = false)
    {
        var added = new List<SharedFileEntry>();
        lock (_gate)
        {
            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    AddFile(path, Path.GetFileName(path), added, allowDuplicatePaths);
                }
                else if (Directory.Exists(path))
                {
                    AddDirectory(path, added, allowDuplicatePaths);
                }
            }
        }
        return added;
    }

    public IReadOnlyList<SharedFileEntry> Snapshot()
    {
        lock (_gate)
            return _items.Values
                .OrderByDescending(item => item.IsFolder)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    public bool TryGet(string id, out SharedFileEntry? entry)
    {
        lock (_gate)
            return _items.TryGetValue(id, out entry);
    }

    public bool TryGetFolderContents(string id, string? requestedPath, out FolderContentsDto? contents)
    {
        contents = null;
        if (!TryNormalizeFolderPath(requestedPath, out var folderPath))
            return false;

        lock (_gate)
        {
            if (!_items.TryGetValue(id, out var folder) || !folder.IsFolder || !folder.IsAvailable)
                return false;

            var prefix = string.IsNullOrEmpty(folderPath) ? string.Empty : folderPath + "/";
            var directories = new Dictionary<string, (long Size, int FileCount)>(StringComparer.OrdinalIgnoreCase);
            var files = new List<FolderItemDto>();
            foreach (var file in folder.FolderFiles)
            {
                if (!File.Exists(file.FullPath))
                    continue;

                var relativePath = NormalizeStoredPath(file.RelativePath);
                if (!relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var remainder = relativePath[prefix.Length..];
                var separator = remainder.IndexOf('/');
                if (separator >= 0)
                {
                    var directoryName = remainder[..separator];
                    var directoryPath = prefix + directoryName;
                    directories.TryGetValue(directoryName, out var aggregate);
                    directories[directoryName] = (
                        AddSaturating(aggregate.Size, file.Size),
                        aggregate.FileCount + 1);
                    continue;
                }

                files.Add(new FolderItemDto(
                    remainder,
                    relativePath,
                    file.Size,
                    false,
                    1));
            }

            var items = directories
                .Select(pair => new FolderItemDto(
                    pair.Key,
                    prefix + pair.Key,
                    pair.Value.Size,
                    true,
                    pair.Value.FileCount))
                .Concat(files)
                .OrderByDescending(item => item.IsFolder)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var name = string.IsNullOrEmpty(folderPath)
                ? folder.Name
                : folderPath[(folderPath.LastIndexOf('/') + 1)..];
            contents = new FolderContentsDto(folder.Id, name, folderPath, items);
            return true;
        }
    }

    public bool TryGetFolderFile(string id, string? requestedPath, out SharedFolderFile? file)
    {
        file = null;
        if (!TryNormalizeFolderPath(requestedPath, out var relativePath) || string.IsNullOrEmpty(relativePath))
            return false;

        lock (_gate)
        {
            if (!_items.TryGetValue(id, out var folder) || !folder.IsFolder || !folder.IsAvailable)
                return false;

            file = folder.FolderFiles.FirstOrDefault(candidate =>
                string.Equals(NormalizeStoredPath(candidate.RelativePath), relativePath, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(candidate.FullPath));
            return file != null;
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
            _items.Remove(id);
    }

    public void Clear()
    {
        lock (_gate)
            _items.Clear();
    }

    private void AddFile(
        string fullPath,
        string relativePath,
        List<SharedFileEntry> added,
        bool allowDuplicatePaths)
    {
        var normalized = Path.GetFullPath(fullPath);
        if (!allowDuplicatePaths &&
            _items.Values.Any(item => string.Equals(item.FullPath, normalized, StringComparison.OrdinalIgnoreCase)))
            return;

        var info = new FileInfo(normalized);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return;

        var entry = new SharedFileEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = info.Name,
            RelativePath = relativePath,
            FullPath = normalized,
            Size = info.Length,
            ModifiedUtc = info.LastWriteTimeUtc,
            IsFolder = false,
            FolderFiles = []
        };
        _items[entry.Id] = entry;
        added.Add(entry);
    }

    private void AddDirectory(string fullPath, List<SharedFileEntry> added, bool allowDuplicatePaths)
    {
        var normalized = Path.GetFullPath(fullPath);
        if (!allowDuplicatePaths &&
            _items.Values.Any(item => string.Equals(item.FullPath, normalized, StringComparison.OrdinalIgnoreCase)))
            return;

        var directory = new DirectoryInfo(normalized);
        if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return;

        var files = new List<SharedFolderFile>();
        var totalSize = 0L;
        foreach (var filePath in EnumerateFiles(normalized))
        {
            try
            {
                var info = new FileInfo(filePath);
                if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                files.Add(new SharedFolderFile(
                    info.FullName,
                    Path.GetRelativePath(normalized, info.FullName),
                    info.Length));
                totalSize = totalSize > long.MaxValue - info.Length ? long.MaxValue : totalSize + info.Length;
            }
            catch
            {
                // Skip files that become unavailable while the folder is scanned.
            }
        }

        var entry = new SharedFileEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = directory.Name,
            RelativePath = directory.Name,
            FullPath = normalized,
            Size = totalSize,
            ModifiedUtc = directory.LastWriteTimeUtc,
            IsFolder = true,
            FolderFiles = files
        };
        _items[entry.Id] = entry;
        added.Add(entry);
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> files;
            IEnumerable<string> directories;
            try
            {
                files = Directory.EnumerateFiles(current).ToArray();
                directories = Directory.EnumerateDirectories(current).ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
                yield return file;
            foreach (var directory in directories)
            {
                try
                {
                    if (!new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
                        pending.Push(directory);
                }
                catch
                {
                    // Skip inaccessible directory metadata.
                }
            }
        }
    }

    private static bool TryNormalizeFolderPath(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var candidate = value.Replace('\\', '/').Trim('/');
        if (candidate.Length == 0)
            return true;
        if (candidate.Contains(':', StringComparison.Ordinal))
            return false;

        var parts = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            return false;
        normalized = string.Join('/', parts);
        return true;
    }

    private static string NormalizeStoredPath(string value) => value.Replace('\\', '/').Trim('/');

    private static long AddSaturating(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;
}
