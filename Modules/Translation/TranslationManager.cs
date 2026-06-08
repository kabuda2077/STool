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
    /// 翻译文本
    /// </summary>
    public async Task<TranslationResult> TranslateAsync(string text, string? sourceLanguage = null, string? targetLanguage = null)
    {
        var config = _configManager.Get().Translation;

        // 使用配置的默认语言
        sourceLanguage ??= config.SourceLanguage;
        targetLanguage ??= config.TargetLanguage;

        var service = GetOrCreateService(config);

        if (service == null || !service.IsAvailable())
        {
            Log.Warning($"Translation service {config.Provider} not available");
            return new TranslationResult
            {
                Success = false,
                ErrorMessage = $"Translation service {config.Provider} not configured",
                Provider = config.Provider.ToString()
            };
        }

        Log.Information($"Translating with provider: {config.Provider}");
        var result = await service.TranslateAsync(text, sourceLanguage, targetLanguage);

        if (result.Success)
        {
            Log.Information($"Translation succeeded with provider: {config.Provider}");
        }
        else
        {
            Log.Warning($"Translation failed with provider {config.Provider}: {result.ErrorMessage}");
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

    private ITranslationService? GetOrCreateService(TranslationConfig config)
    {
        var signature = CreateSignature(config);
        if (_service != null && _serviceSignature == signature)
        {
            return _service;
        }

        _service?.Dispose();
        _service = config.Provider switch
        {
            TranslationProvider.Tencent => CreateTencentService(config),
            TranslationProvider.OpenAI => CreateAiService(config),
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
