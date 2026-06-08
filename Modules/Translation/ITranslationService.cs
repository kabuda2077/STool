using System;
using System.Threading.Tasks;

namespace STool.Modules.Translation;

/// <summary>
/// 翻译服务接口
/// </summary>
public interface ITranslationService : IDisposable
{
    /// <summary>
    /// 翻译文本
    /// </summary>
    /// <param name="text">要翻译的文本</param>
    /// <param name="sourceLanguage">源语言（auto 表示自动检测）</param>
    /// <param name="targetLanguage">目标语言</param>
    Task<TranslationResult> TranslateAsync(string text, string sourceLanguage, string targetLanguage);

    /// <summary>
    /// 服务是否可用
    /// </summary>
    bool IsAvailable();
}
