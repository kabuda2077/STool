using System;
using System.IO;

namespace STool.Core;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Data");
    public static string ConfigPath => Path.Combine(DataDirectory, "config.json");
    public static string ClipboardDbPath => Path.Combine(DataDirectory, "clipboard.db");
    public static string ClipboardImagesDirectory => Path.Combine(DataDirectory, "ClipboardImages");
    public static string ClipboardThumbnailsDirectory => Path.Combine(DataDirectory, "ClipboardThumbnails");
    public static string LogsDirectory => Path.Combine(DataDirectory, "Logs");
    public static string SecureKeyPath => Path.Combine(DataDirectory, "secure.key");
    public static string LanTransferDevicesPath => Path.Combine(DataDirectory, "lan-devices.json");
    public static string LanTransferHistoryPath => Path.Combine(DataDirectory, "lan-transfer-history.json");

    public static void EnsureDataDirectory()
    {
        Directory.CreateDirectory(DataDirectory);
    }

    public static void EnsureStandardDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ClipboardImagesDirectory);
        Directory.CreateDirectory(ClipboardThumbnailsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    /// <summary>
    /// 创建数据目录并实际写入一次探测文件。便携版放在 Program Files 等受保护位置时，
    /// 目录可能已存在但不可写，只有真实写入才能提前发现。
    /// </summary>
    public static void EnsureWritableDataDirectory()
    {
        try
        {
            EnsureStandardDirectories();
            var probePath = Path.Combine(DataDirectory, $".write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probePath, string.Empty);
            File.Delete(probePath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new DataDirectoryUnavailableException(DataDirectory, ex);
        }
    }
}

public sealed class DataDirectoryUnavailableException(string directory, Exception innerException)
    : Exception($"数据目录不可写：{directory}", innerException)
{
    public string Directory { get; } = directory;
}
