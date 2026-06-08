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
    /// <summary>
    /// 唯一标识
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// 类型
    /// </summary>
    public ClipboardItemType Type { get; set; }

    /// <summary>
    /// 文本内容（Type 为 Text 时）
    /// </summary>
    public string? TextContent { get; set; }

    /// <summary>
    /// 图片路径（Type 为 Image 时，保存到本地文件）
    /// </summary>
    public string? ImagePath { get; set; }

    /// <summary>
    /// 文件路径列表（Type 为 File 时）
    /// </summary>
    public string[]? FilePaths { get; set; }

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
            ClipboardItemType.Text => TextContent?.Length > maxLength
                ? TextContent.Substring(0, maxLength) + "..."
                : TextContent ?? "",
            ClipboardItemType.Image => $"[图片] {System.IO.Path.GetFileName(ImagePath)}",
            ClipboardItemType.File => $"[文件] {string.Join(", ", FilePaths ?? Array.Empty<string>())}",
            _ => ""
        };
    }
}
