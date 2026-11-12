using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
    private readonly Func<TranslationProvider, AccountsConfig, ITranslationService?>? _serviceFactory;

    // 服务实例无内部状态、共用 HttpClient，可并发使用；锁只保护实例的创建与替换。
    private readonly object _serviceGate = new();
    private readonly Dictionary<TranslationProvider, (string Signature, ITranslationService? Service)> _services = new();
    private readonly List<ITranslationService> _retiredServices = new();
    private ScreenContentSelector? _contentSelector;
    private string? _contentSelectorSignature;

    public TranslationManager(ConfigManager configManager) : this(configManager, null)
    {
    }

    internal TranslationManager(
        ConfigManager configManager,
        Func<TranslationProvider, AccountsConfig, ITranslationService?>? serviceFactory)
    {
        _configManager = configManager;
        _serviceFactory = serviceFactory;
    }

    public bool IsProviderAvailable(TranslationProvider provider) =>
        GetOrCreateService(provider, _configManager.Get().Accounts)?.IsAvailable() == true;

    public TranslationProvider GetConfiguredProvider()
    {
        return _configManager.Get().Translation.Provider;
    }

    public void SaveConfiguredProvider(TranslationProvider provider)
    {
        if (_configManager.Get().Translation.Provider == provider)
            return;

        _configManager.Update(config => config.Translation.Provider = provider);
    }

    public string GetConfiguredTranslationMode()
    {
        return _configManager.Get().Translation.TranslationMode;
    }

    public ScreenshotTranslationMode GetConfiguredScreenshotMode()
    {
        return _configManager.Get().Translation.ScreenshotMode;
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

        _configManager.Update(current =>
        {
            current.Translation.TranslationMode = mode;
            current.Translation.SourceLanguage = "auto";
            current.Translation.TargetLanguage = targetLanguage;
        });
    }

    /// <summary>
    /// 翻译文本(使用配置中的默认提供商)
    /// </summary>
    public Task<TranslationResult> TranslateAsync(string text, string? sourceLanguage = null, string? targetLanguage = null, CancellationToken cancellationToken = default)
    {
        var config = _configManager.Get();
        return TranslateAsync(text, sourceLanguage, targetLanguage, config.Translation.Provider, config, cancellationToken);
    }

    /// <summary>
    /// 批量翻译互相独立的文本块（截图原位翻译），使用配置中的翻译引擎和各服务原生的批量接口。
    /// 结果与输入一一对应。
    /// </summary>
    public async Task<BlockTranslationResult> TranslateBlocksAsync(IReadOnlyList<string> blocks, string? sourceLanguage = null, string? targetLanguage = null, CancellationToken cancellationToken = default)
    {
        var normalized = blocks.Select(block => block?.Trim() ?? string.Empty).ToArray();
        if (normalized.Length == 0 || normalized.Any(string.IsNullOrWhiteSpace))
        {
            return new BlockTranslationResult
            {
                Success = false,
                ErrorMessage = "没有可翻译的文本块"
            };
        }

        var config = _configManager.Get();
        var provider = config.Translation.Provider;
        sourceLanguage ??= config.Translation.SourceLanguage;
        targetLanguage ??= ResolveTargetLanguage(string.Join("\n", normalized), config.Translation.TranslationMode);

        var service = GetOrCreateService(provider, config.Accounts);
        if (service == null || !service.IsAvailable())
        {
            return new BlockTranslationResult
            {
                Success = false,
                ErrorMessage = UnavailableMessage(provider)
            };
        }

        Log.Information("Batch translating blocks={Count} provider={Provider}", normalized.Length, provider);
        var result = await service.TranslateBatchAsync(normalized, sourceLanguage, targetLanguage, cancellationToken);
        if (!result.Success || result.Translations.Count != normalized.Length)
        {
            Log.Warning("Batch translation failed provider={Provider}: {Error}", provider, result.ErrorMessage);
            return new BlockTranslationResult
            {
                Success = false,
                ErrorMessage = result.ErrorMessage ?? "分块翻译结果数量不匹配"
            };
        }

        return new BlockTranslationResult
        {
            Success = true,
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
            Provider = result.Provider,
            TranslatedBlocks = result.Translations
        };
    }

    /// <summary>
    /// 翻译文本(指定提供商,供翻译面板的提供商切换使用)
    /// </summary>
    public Task<TranslationResult> TranslateAsync(string text, string? sourceLanguage, string? targetLanguage, TranslationProvider provider, CancellationToken cancellationToken = default)
    {
        return TranslateAsync(text, sourceLanguage, targetLanguage, provider, _configManager.Get(), cancellationToken);
    }

    private async Task<TranslationResult> TranslateAsync(
        string text,
        string? sourceLanguage,
        string? targetLanguage,
        TranslationProvider provider,
        AppConfig config,
        CancellationToken cancellationToken)
    {
        sourceLanguage ??= config.Translation.SourceLanguage;
        targetLanguage ??= ResolveTargetLanguage(text, config.Translation.TranslationMode);

        var service = GetOrCreateService(provider, config.Accounts);
        if (service == null || !service.IsAvailable())
        {
            Log.Warning("Translation service {Provider} not available", provider);
            return new TranslationResult
            {
                Success = false,
                ErrorMessage = UnavailableMessage(provider),
                Provider = provider.ToString()
            };
        }

        Log.Information("Translating with provider: {Provider}", provider);
        var result = await service.TranslateAsync(text, sourceLanguage, targetLanguage, cancellationToken);

        if (result.Success)
            Log.Information("Translation succeeded with provider: {Provider}", provider);
        else
            Log.Warning("Translation failed with provider {Provider}: {ErrorMessage}", provider, result.ErrorMessage);

        return result;
    }

    private static string UnavailableMessage(TranslationProvider provider) => provider switch
    {
        TranslationProvider.Tencent => "腾讯云账号未配置，请在「设置 → 服务账号」中填写。",
        TranslationProvider.OpenAI => "AI 文本模型未配置，请在「设置 → 服务账号」中填写。",
        _ => $"翻译服务 {provider} 不可用"
    };

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
            if (TextScript.IsHan(ch))
            {
                chinese++;
            }
            else if (TextScript.IsLatinLetter(ch))
            {
                latin++;
            }
            else if (TextScript.IsKana(ch))
            {
                japanese++;
            }
            else if (TextScript.IsHangul(ch))
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

    /// <summary>
    /// 截图翻译智能模式的内容选择器，使用「服务账号」里的 AI 文本模型，与当前翻译引擎无关。
    /// 未配置时返回 null。
    /// </summary>
    public ScreenContentSelector? TryCreateContentSelector()
    {
        var account = _configManager.Get().Accounts.AiText;
        lock (_serviceGate)
        {
            if (!account.IsConfigured)
            {
                _contentSelector = null;
                _contentSelectorSignature = null;
                return null;
            }

            var signature = string.Join("|", account.ApiUrlEncrypted, account.ApiKeyEncrypted, account.Model);
            if (_contentSelector == null || _contentSelectorSignature != signature)
            {
                _contentSelector = new ScreenContentSelector(account.ApiUrlEncrypted!, account.ApiKeyEncrypted!, account.Model!);
                _contentSelectorSignature = signature;
            }

            return _contentSelector.IsAvailable() ? _contentSelector : null;
        }
    }

    /// <summary>
    /// 每个引擎缓存一个服务实例，账号变化时才重建。翻译面板来回切换引擎不再反复销毁、重建。
    /// 被替换的旧实例可能仍有请求在用，延后到 Dispose 时统一释放。
    /// </summary>
    private ITranslationService? GetOrCreateService(TranslationProvider provider, AccountsConfig accounts)
    {
        var signature = ServiceSignature(provider, accounts);
        lock (_serviceGate)
        {
            if (_services.TryGetValue(provider, out var cached) && cached.Signature == signature)
                return cached.Service;

            if (cached.Service != null)
                _retiredServices.Add(cached.Service);

            var service = _serviceFactory != null
                ? _serviceFactory(provider, accounts)
                : CreateService(provider, accounts);
            _services[provider] = (signature, service);
            return service;
        }
    }

    private static string ServiceSignature(TranslationProvider provider, AccountsConfig accounts) => provider switch
    {
        TranslationProvider.Tencent => string.Join("|", accounts.Tencent.SecretIdEncrypted, accounts.Tencent.SecretKeyEncrypted),
        TranslationProvider.OpenAI => string.Join("|", accounts.AiText.ApiUrlEncrypted, accounts.AiText.ApiKeyEncrypted, accounts.AiText.Model),
        _ => string.Empty
    };

    private static ITranslationService? CreateService(TranslationProvider provider, AccountsConfig accounts)
    {
        switch (provider)
        {
            case TranslationProvider.Tencent when accounts.Tencent.IsConfigured:
                return new TencentTranslationService(accounts.Tencent.SecretIdEncrypted!, accounts.Tencent.SecretKeyEncrypted!);
            case TranslationProvider.OpenAI when accounts.AiText.IsConfigured:
                return new AiTranslationService(accounts.AiText.ApiUrlEncrypted!, accounts.AiText.ApiKeyEncrypted!, accounts.AiText.Model!);
            case TranslationProvider.Google:
                return new GoogleTranslationService();
            default:
                return null;
        }
    }

    public void Dispose()
    {
        lock (_serviceGate)
        {
            foreach (var (_, service) in _services.Values)
                service?.Dispose();
            foreach (var service in _retiredServices)
                service.Dispose();
            _services.Clear();
            _retiredServices.Clear();
        }
    }
}

public class BlockTranslationResult
{
    public bool Success { get; set; }

    public string SourceLanguage { get; set; } = string.Empty;

    public string TargetLanguage { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public IReadOnlyList<string> TranslatedBlocks { get; set; } = Array.Empty<string>();

    public string? ErrorMessage { get; set; }
}
