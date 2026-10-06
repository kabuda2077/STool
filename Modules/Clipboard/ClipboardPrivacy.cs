using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace STool.Modules.Clipboard;

/// <summary>剪贴板内容哈希，用于识别重复复制。文件列表按存储时的 JSON 形式计算，保证新旧记录一致。</summary>
internal static class ClipboardContentHash
{
    public static string ForText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string ForFiles(IReadOnlyList<string> paths) =>
        ForText(JsonSerializer.Serialize(paths.ToArray()));

    /// <summary>按行计算哈希，避免为多屏大图再申请一份完整的 BGRA 缓冲。</summary>
    public static string ForImage(System.Windows.Media.Imaging.BitmapSource image)
    {
        var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(
            image, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var stride = checked(image.PixelWidth * 4);
        var rowsPerBlock = Math.Min(image.PixelHeight, Math.Max(1, 64 * 1024 / stride));
        var pixels = new byte[checked(stride * rowsPerBlock)];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> size = stackalloc byte[8];
        BitConverter.TryWriteBytes(size[..4], image.PixelWidth);
        BitConverter.TryWriteBytes(size[4..], image.PixelHeight);
        hash.AppendData(size);
        for (var y = 0; y < image.PixelHeight; y += rowsPerBlock)
        {
            var rows = Math.Min(rowsPerBlock, image.PixelHeight - y);
            converted.CopyPixels(new System.Windows.Int32Rect(0, y, image.PixelWidth, rows), pixels, stride, 0);
            hash.AppendData(pixels.AsSpan(0, stride * rows));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static string ForImage(int width, int height, ReadOnlySpan<byte> pixels)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> size = stackalloc byte[8];
        BitConverter.TryWriteBytes(size[..4], width);
        BitConverter.TryWriteBytes(size[4..], height);
        hash.AppendData(size);
        hash.AppendData(pixels);
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

/// <summary>
/// 剪贴板隐私规则。密码管理器等应用写入特定格式，约定剪贴板历史工具跳过该内容
/// （Windows 自带的剪贴板历史同样遵守）；此外可按来源进程排除。
/// </summary>
internal static class ClipboardPrivacy
{
    public const string ExcludeFromMonitorFormat = "ExcludeClipboardContentFromMonitorProcessing";
    public const string CanIncludeInHistoryFormat = "CanIncludeInClipboardHistory";
    public const string ClipboardViewerIgnoreFormat = "Clipboard Viewer Ignore";

    /// <param name="hasExcludeFormat">存在 ExcludeClipboardContentFromMonitorProcessing。</param>
    /// <param name="hasViewerIgnoreFormat">存在 Clipboard Viewer Ignore。</param>
    /// <param name="canIncludeInHistory">CanIncludeInClipboardHistory 的 DWORD 值；格式不存在时为 null。</param>
    public static bool ShouldSkip(bool hasExcludeFormat, bool hasViewerIgnoreFormat, int? canIncludeInHistory) =>
        hasExcludeFormat || hasViewerIgnoreFormat || canIncludeInHistory == 0;

    /// <summary>来源进程是否在排除列表中。列表项可写 KeePass.exe 或 KeePass，不区分大小写。</summary>
    public static bool IsExcludedApp(string? sourceApp, IEnumerable<string>? excludedApps)
    {
        if (string.IsNullOrWhiteSpace(sourceApp) || excludedApps == null)
            return false;

        var source = NormalizeAppName(sourceApp);
        return excludedApps.Any(app =>
            !string.IsNullOrWhiteSpace(app) &&
            string.Equals(NormalizeAppName(app), source, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>把设置页里逗号、分号或换行分隔的进程列表拆成规范化的名称。</summary>
    public static List<string> ParseAppList(string? text) =>
        (text ?? string.Empty)
            .Split(new[] { ',', ';', '，', '；', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeAppName)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => name + ".exe")
            .ToList();

    private static string NormalizeAppName(string app)
    {
        var name = app.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}
