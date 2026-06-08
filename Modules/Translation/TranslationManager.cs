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
        targetLanguage ??= config.TargetLanguage;

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
