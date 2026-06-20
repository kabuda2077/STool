using System;
using System.Threading.Tasks;
using Serilog;
using STool.Core;
using STool.Models;

namespace STool.Modules.Translation;

/// <summary>
/// 翻译管理器
/// </summary>
public class TranslationManager : IDisposable
{
    private readonly ConfigManager _configManager;
    private ITranslationService? _service;
    private string? _serviceSignature;

    public TranslationManager(ConfigManager configManager)
    {
        _configManager = configManager;
    }

    public TranslationProvider GetConfiguredProvider()
    {
        return _configManager.Get().Translation.Provider;
    }

    public void SaveConfiguredProvider(TranslationProvider provider)
    {
        var config = _configManager.Get();
        if (config.Translation.Provider == provider)
        {
            return;
        }

        config.Translation.Provider = provider;
        _configManager.Save(config);
    }

    public string GetConfiguredTargetLanguage()
    {
        return _configManager.Get().Translation.TargetLanguage;
    }

    public string GetConfiguredTranslationMode()
    {
        return _configManager.Get().Translation.TranslationMode;
    }

    public void SaveConfiguredTranslationMode(string mode)
    {
        var config = _configManager.Get();
        var targetLanguage = ResolveTargetLanguage(string.Empty, mode);
        if (config.Translation.TranslationMode == mode &&
            config.Translation.SourceLanguage == "auto" &&
            config.Translation.TargetLanguage == targetLanguage)
        {
            return;
        }

        config.Translation.TranslationMode = mode;
        config.Translation.SourceLanguage = "auto";
        config.Translation.TargetLanguage = targetLanguage;
        _configManager.Save(config);
    }

    public void SaveConfiguredLanguages(string sourceLanguage, string targetLanguage)
    {
        var config = _configManager.Get();
        if (config.Translation.SourceLanguage == sourceLanguage &&
            config.Translation.TargetLanguage == targetLanguage)
        {
            return;
        }

        config.Translation.SourceLanguage = sourceLanguage;
        config.Translation.TargetLanguage = targetLanguage;
        _configManager.Save(config);
    }

    /// <summary>
    /// 翻译文本(使用配置中的默认提供商)
    /// </summary>
    public Task<TranslationResult> TranslateAsync(string text, string? sourceLanguage = null, string? targetLanguage = null)
    {
        var provider = _configManager.Get().Translation.Provider;
        return TranslateAsync(text, sourceLanguage, targetLanguage, provider);
    }

    /// <summary>
    /// 翻译文本(指定提供商,供翻译面板的提供商切换使用)
    /// </summary>
    public async Task<TranslationResult> TranslateAsync(string text, string? sourceLanguage, string? targetLanguage, TranslationProvider provider)
    {
        var config = _configManager.Get().Translation;

        // 使用配置的默认语言
        sourceLanguage ??= config.SourceLanguage;
        targetLanguage ??= ResolveTargetLanguage(text, config.TranslationMode);

        var service = GetOrCreateService(provider, config);

        if (service == null || !service.IsAvailable())
        {
            Log.Warning($"Translation service {provider} not available");
            return new TranslationResult
            {
                Success = false,
                ErrorMessage = $"翻译服务 {provider} 未配置",
                Provider = provider.ToString()
            };
        }

        Log.Information($"Translating with provider: {provider}");
        var result = await service.TranslateAsync(text, sourceLanguage, targetLanguage);

        if (result.Success)
        {
            Log.Information($"Translation succeeded with provider: {provider}");
        }
        else
        {
            Log.Warning($"Translation failed with provider {provider}: {result.ErrorMessage}");
        }

        return result;
    }

    public static string ResolveTargetLanguage(string text, string? mode)
    {
        return mode switch
        {
            "auto-en" => "en",
            "auto-ja" => "ja",
            "auto-ko" => "ko",
            "zh-en" => ResolveChineseEnglishTarget(text),
            "en" or "ja" or "ko" or "zh" => mode,
            _ => "zh"
        };
    }

    private static string ResolveChineseEnglishTarget(string text)
    {
        var chinese = 0;
        var latin = 0;
        var japanese = 0;
        var korean = 0;

        foreach (var ch in text)
        {
            if (IsChinese(ch))
            {
                chinese++;
            }
            else if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z'))
            {
                latin++;
            }
            else if ((ch >= '\u3040' && ch <= '\u30ff') || (ch >= '\u31f0' && ch <= '\u31ff'))
            {
                japanese++;
            }
            else if (ch >= '\uac00' && ch <= '\ud7af')
            {
                korean++;
            }
        }

        if (chinese > 0)
        {
            return chinese >= latin ? "en" : "zh";
        }

        if (latin > 0)
        {
            return "zh";
        }

        if (japanese > 0 || korean > 0)
        {
            return "zh";
        }

        return "zh";
    }

    private static bool IsChinese(char ch)
    {
        return (ch >= '\u4e00' && ch <= '\u9fff')
            || (ch >= '\u3400' && ch <= '\u4dbf');
    }

    private ITranslationService? CreateTencentService(TranslationConfig config)
    {
        if (string.IsNullOrEmpty(config.TencentSecretIdEncrypted) ||
            string.IsNullOrEmpty(config.TencentSecretKeyEncrypted))
        {
            return null;
        }

        return new TencentTranslationService(
            config.TencentSecretIdEncrypted,
            config.TencentSecretKeyEncrypted
        );
    }

    private ITranslationService? CreateAiService(TranslationConfig config)
    {
        if (string.IsNullOrEmpty(config.AiApiUrlEncrypted) ||
            string.IsNullOrEmpty(config.AiApiKeyEncrypted) ||
            string.IsNullOrEmpty(config.AiModel))
        {
            return null;
        }

        return new AiTranslationService(
            config.AiApiUrlEncrypted,
            config.AiApiKeyEncrypted,
            config.AiModel
        );
    }

    private ITranslationService? GetOrCreateService(TranslationProvider provider, TranslationConfig config)
    {
        var signature = provider + "|" + CreateSignature(config);
        if (_service != null && _serviceSignature == signature)
        {
            return _service;
        }

        _service?.Dispose();
        _service = provider switch
        {
            TranslationProvider.Tencent => CreateTencentService(config),
            TranslationProvider.OpenAI => CreateAiService(config),
            TranslationProvider.Google => new GoogleTranslationService(),
            _ => null
        };
        _serviceSignature = signature;

        return _service;
    }

    private static string CreateSignature(TranslationConfig config)
    {
        return string.Join("|",
            config.Provider,
            config.TencentSecretIdEncrypted,
            config.TencentSecretKeyEncrypted,
            config.AiApiUrlEncrypted,
            config.AiApiKeyEncrypted,
            config.AiModel);
    }

    public void Dispose()
    {
        _service?.Dispose();
    }
}
