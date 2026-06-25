using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
    public Task<TranslationResult> TranslateAsync(string text, string? sourceLanguage = null, string? targetLanguage = null, CancellationToken cancellationToken = default)
    {
        var provider = _configManager.Get().Translation.Provider;
        return TranslateAsync(text, sourceLanguage, targetLanguage, provider, cancellationToken);
    }

    public async Task<BlockTranslationResult> TranslateBlocksAsync(IReadOnlyList<string> blocks, string? sourceLanguage = null, string? targetLanguage = null, CancellationToken cancellationToken = default)
    {
        var normalized = blocks
            .Select(block => block?.Trim() ?? string.Empty)
            .Where(block => !string.IsNullOrWhiteSpace(block))
            .ToArray();

        if (normalized.Length == 0)
        {
            return new BlockTranslationResult
            {
                Success = false,
                ErrorMessage = "没有可翻译的文本块"
            };
        }

        var config = _configManager.Get().Translation;
        sourceLanguage ??= config.SourceLanguage;
        targetLanguage ??= ResolveTargetLanguage(string.Join("\n", normalized), config.TranslationMode);

        var packedText = PackBlocks(normalized, config.Provider);
        var result = await TranslateAsync(packedText, sourceLanguage, targetLanguage, config.Provider, cancellationToken);
        if (!result.Success)
        {
            return new BlockTranslationResult
            {
                Success = false,
                ErrorMessage = result.ErrorMessage
            };
        }

        var translatedBlocks = TryUnpackBlocks(result.TranslatedText, normalized.Length);
        if (translatedBlocks == null)
        {
            Log.Warning(
                "[ScreenshotTranslate] Block translation count mismatch expected={Expected} rawLength={RawLength}",
                normalized.Length,
                result.TranslatedText.Length);

            return new BlockTranslationResult
            {
                Success = false,
                ErrorMessage = "分块翻译结果数量不匹配"
            };
        }

        return new BlockTranslationResult
        {
            Success = true,
            SourceLanguage = result.SourceLanguage,
            TargetLanguage = result.TargetLanguage,
            Provider = result.Provider,
            TranslatedBlocks = translatedBlocks
        };
    }

    /// <summary>
    /// 翻译文本(指定提供商,供翻译面板的提供商切换使用)
    /// </summary>
    public async Task<TranslationResult> TranslateAsync(string text, string? sourceLanguage, string? targetLanguage, TranslationProvider provider, CancellationToken cancellationToken = default)
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
        var result = await service.TranslateAsync(text, sourceLanguage, targetLanguage, cancellationToken);

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

    internal static string PackBlocks(IReadOnlyList<string> blocks, TranslationProvider provider)
    {
        var sb = new StringBuilder();
        if (provider == TranslationProvider.OpenAI)
        {
            sb.AppendLine("Translate each marked item. Keep every <<<STOOL_###>>> marker exactly once and in the same order. Return only the translated marked items, one item per line.");
        }

        for (var i = 0; i < blocks.Count; i++)
        {
            sb.Append("<<<STOOL_");
            sb.Append((i + 1).ToString("D3"));
            sb.Append(">>> ");
            sb.AppendLine(blocks[i]);
        }

        return sb.ToString();
    }

    internal static IReadOnlyList<string>? TryUnpackBlocks(string text, int expectedCount)
    {
        var markerPattern = @"(?:\[\[STOOL-(\d{3})\]\]|<<<STOOL_(\d{3})>>>)";
        var matches = Regex.Matches(text, markerPattern, RegexOptions.Singleline);

        if (matches.Count != expectedCount)
        {
            var looseLines = text
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();

            return looseLines.Length == expectedCount ? looseLines : null;
        }

        var results = new string[expectedCount];
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var numberText = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (!int.TryParse(numberText, out var number))
                return null;

            var index = number - 1;
            if (index < 0 || index >= expectedCount || results[index] != null)
                return null;

            var start = match.Index + match.Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var value = text[start..end].Trim();
            if (string.IsNullOrWhiteSpace(value))
                return null;

            results[index] = value;
        }

        return results.Any(string.IsNullOrWhiteSpace) ? null : results;
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

    public ScreenContentSelector? TryCreateContentSelector()
    {
        var config = _configManager.Get().Translation;
        if (string.IsNullOrEmpty(config.AiApiUrlEncrypted) ||
            string.IsNullOrEmpty(config.AiApiKeyEncrypted) ||
            string.IsNullOrEmpty(config.AiModel))
        {
            return null;
        }

        var selector = new ScreenContentSelector(
            config.AiApiUrlEncrypted,
            config.AiApiKeyEncrypted,
            config.AiModel);

        return selector.IsAvailable() ? selector : null;
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

public class BlockTranslationResult
{
    public bool Success { get; set; }

    public string SourceLanguage { get; set; } = string.Empty;

    public string TargetLanguage { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public IReadOnlyList<string> TranslatedBlocks { get; set; } = Array.Empty<string>();

    public string? ErrorMessage { get; set; }
}
