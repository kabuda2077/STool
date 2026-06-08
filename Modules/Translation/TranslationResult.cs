namespace STool.Modules.Translation;

/// <summary>
/// 翻译结果
/// </summary>
public class TranslationResult
{
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 源语言
    /// </summary>
    public string SourceLanguage { get; set; } = string.Empty;

    /// <summary>
    /// 目标语言
    /// </summary>
    public string TargetLanguage { get; set; } = string.Empty;

    /// <summary>
    /// 原文
    /// </summary>
    public string SourceText { get; set; } = string.Empty;

    /// <summary>
    /// 译文
    /// </summary>
    public string TranslatedText { get; set; } = string.Empty;

    /// <summary>
    /// 错误信息
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 使用的翻译提供商
    /// </summary>
    public string Provider { get; set; } = string.Empty;
}
