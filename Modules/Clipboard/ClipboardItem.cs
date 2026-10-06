using System;

namespace STool.Modules.Clipboard;

/// <summary>
/// 剪贴板条目类型
/// </summary>
public enum ClipboardItemType
{
    Text,
    Image,
    File
}

/// <summary>
/// 剪贴板条目
/// </summary>
public class ClipboardItem
{
    /// <summary>列表预览保留的文本长度，完整文本只在复制时读取。</summary>
    public const int TextPreviewLength = 300;

    /// <summary>
    /// 唯一标识
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// 类型
    /// </summary>
    public ClipboardItemType Type { get; set; }

    /// <summary>
    /// 文本内容（Type 为 Text 时）。列表查询只加载 <see cref="TextPreview"/>，此处为 null。
    /// </summary>
    public string? TextContent { get; set; }

    /// <summary>文本前若干字符，供列表显示。</summary>
    public string? TextPreview { get; set; }

    /// <summary>
    /// 图片路径（Type 为 Image 时，保存到本地文件）
    /// </summary>
    public string? ImagePath { get; set; }

    public int ImageWidth { get; set; }

    public int ImageHeight { get; set; }

    /// <summary>图片文件字节数。</summary>
    public long ImageBytes { get; set; }

    /// <summary>
    /// 文件路径列表（Type 为 File 时）
    /// </summary>
    public string[]? FilePaths { get; set; }

    /// <summary>内容哈希（SHA-256），用于识别重复复制。</summary>
    public string? ContentHash { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 是否收藏
    /// </summary>
    public bool IsFavorite { get; set; }

    /// <summary>
    /// 来源应用(进程名,如 Code.exe;复制时抓取的前台窗口进程)
    /// </summary>
    public string? SourceApp { get; set; }

    /// <summary>
    /// 标签（可选）
    /// </summary>
    public string? Tag { get; set; }

    /// <summary>
    /// 获取显示文本（用于列表显示）
    /// </summary>
    public string GetDisplayText(int maxLength = 100)
    {
        return Type switch
        {
            ClipboardItemType.Text => Truncate(TextPreview ?? TextContent ?? string.Empty, maxLength),
            ClipboardItemType.Image => $"[图片] {System.IO.Path.GetFileName(ImagePath)}",
            ClipboardItemType.File => $"[文件] {string.Join(", ", FilePaths ?? Array.Empty<string>())}",
            _ => ""
        };
    }

    public static string CreatePreview(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : Truncate(text, TextPreviewLength, appendEllipsis: false);

    private static string Truncate(string text, int maxLength, bool appendEllipsis = true)
    {
        if (text.Length <= maxLength)
            return text;

        // 避免把代理对截成半个字符。
        var length = char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;
        return appendEllipsis ? text[..length] + "..." : text[..length];
    }
}
