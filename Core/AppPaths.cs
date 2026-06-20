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
}
