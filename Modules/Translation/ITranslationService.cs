using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace STool.Modules.Translation;

/// <summary>
/// 翻译服务接口。实现不应持有请求间的可变状态，可被并发调用。
/// </summary>
public interface ITranslationService : IDisposable
{
    /// <summary>
    /// 翻译文本
    /// </summary>
    /// <param name="text">要翻译的文本</param>
    /// <param name="sourceLanguage">源语言（auto 表示自动检测）</param>
    /// <param name="targetLanguage">目标语言</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task<TranslationResult> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量翻译多段互相独立的文本，结果与输入一一对应、顺序一致。
    /// 默认逐段调用 <see cref="TranslateAsync"/>；有原生批量接口的服务应覆盖。
    /// </summary>
    async Task<BatchTranslationResult> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var results = new string[texts.Count];
        TranslationResult? last = null;
        for (var i = 0; i < texts.Count; i++)
        {
            last = await TranslateAsync(texts[i], sourceLanguage, targetLanguage, cancellationToken);
            if (!last.Success)
                return BatchTranslationResult.Failed(last.ErrorMessage, last.Provider);
            results[i] = last.TranslatedText;
        }

        return BatchTranslationResult.Succeeded(results, last?.Provider ?? string.Empty);
    }

    /// <summary>
    /// 服务是否可用
    /// </summary>
    bool IsAvailable();
}

/// <summary>批量翻译结果。成功时 <see cref="Translations"/> 与输入等长。</summary>
public sealed class BatchTranslationResult
{
    public bool Success { get; init; }
    public IReadOnlyList<string> Translations { get; init; } = Array.Empty<string>();
    public string Provider { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }

    public static BatchTranslationResult Succeeded(IReadOnlyList<string> translations, string provider) =>
        new() { Success = true, Translations = translations.ToArray(), Provider = provider };

    public static BatchTranslationResult Failed(string? message, string provider = "") =>
        new() { Success = false, ErrorMessage = message ?? "翻译失败", Provider = provider };
}
