using System.Collections.Generic;

namespace STool.Modules.Ocr;

/// <summary>
/// OCR 识别结果
/// </summary>
public class OcrResult
{
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 完整文本
    /// </summary>
    public string FullText { get; set; } = string.Empty;

    /// <summary>
    /// 文本块列表
    /// </summary>
    public List<OcrTextBlock> TextBlocks { get; set; } = new();

    /// <summary>
    /// 错误信息
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 使用的 OCR 提供商
    /// </summary>
    public string Provider { get; set; } = string.Empty;
}

/// <summary>
/// OCR 文本块（带位置信息）
/// </summary>
public class OcrTextBlock
{
    /// <summary>
    /// 文本内容
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// 置信度 (0-1)
    /// </summary>
    public float Confidence { get; set; }

    /// <summary>
    /// 边界框 (X, Y, Width, Height)
    /// </summary>
    public System.Drawing.Rectangle BoundingBox { get; set; }
}
