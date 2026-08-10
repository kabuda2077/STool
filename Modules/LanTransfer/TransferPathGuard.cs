using System.IO;

namespace STool.Modules.LanTransfer;

internal static class TransferPathGuard
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
        "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4",
        "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string SanitizeRelativePath(string name, string? relativePath)
    {
        var source = string.IsNullOrWhiteSpace(relativePath) ? name : relativePath;
        if (Path.IsPathRooted(source) || source.Contains(':'))
            throw new InvalidDataException("不允许使用绝对路径。 ");

        var segments = source
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
            segments = [name];
        if (string.Equals(segments[0], ".stool-transfer", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("该目录名由 STool 保留。 ");

        var safeSegments = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment is "." or "..")
                throw new InvalidDataException("文件路径包含不安全的目录跳转。 ");
            safeSegments.Add(SanitizeSegment(segment));
        }

        safeSegments[^1] = SanitizeSegment(name);
        return Path.Combine(safeSegments.ToArray());
    }

    public static string ResolveUnderRoot(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("目标路径超出接收目录。 ");
        return fullPath;
    }

    public static string CreateUniquePath(string requestedPath, Func<string, bool>? isReserved = null)
    {
        isReserved ??= _ => false;
        if (!File.Exists(requestedPath) && !Directory.Exists(requestedPath) && !isReserved(requestedPath))
            return requestedPath;

        var directory = Path.GetDirectoryName(requestedPath) ?? string.Empty;
        var extension = Path.GetExtension(requestedPath);
        var stem = Path.GetFileNameWithoutExtension(requestedPath);
        for (var index = 1; index < 10000; index++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate) && !isReserved(candidate))
                return candidate;
        }

        throw new IOException("无法为接收文件分配可用名称。 ");
    }

    private static string SanitizeSegment(string segment)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var buffer = segment.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var result = new string(buffer).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(result))
            result = "未命名文件";

        var stem = Path.GetFileNameWithoutExtension(result);
        if (ReservedNames.Contains(stem))
            result = "_" + result;

        return result.Length <= 180 ? result : result[..180];
    }
}
