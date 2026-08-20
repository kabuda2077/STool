using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using STool.Core;

namespace STool.Modules.Translation;

/// <summary>
/// 谷歌翻译 —— 使用免费的 web 端点(translate.googleapis.com),无需 API Key。
/// 注意:为非官方公开端点,适合个人轻量使用,频繁调用可能被限流。
/// </summary>
public class GoogleTranslationService : ITranslationService
{
    private static readonly HttpClient HttpClient = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = HttpDefaults.CreateClient();
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
        return client;
    }

    public bool IsAvailable() => true;

    public async Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var sl = MapLanguageCode(sourceLanguage);
            var tl = MapLanguageCode(targetLanguage);
            var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={sl}&tl={tl}&dt=t&q={Uri.EscapeDataString(text)}";

            var json = await HttpClient.GetStringAsync(url, cancellationToken);
            using var document = JsonDocument.Parse(json);
            var parsed = ParseResponse(document.RootElement, sl);
            if (string.IsNullOrWhiteSpace(parsed.TranslatedText))
                throw new InvalidOperationException("翻译服务返回了空结果。");

            return new TranslationResult
            {
                Success = true,
                SourceText = text,
                TranslatedText = parsed.TranslatedText,
                SourceLanguage = parsed.SourceLanguage,
                TargetLanguage = tl,
                Provider = "Google"
            };
        }
        catch (Exception ex)
        {
            return new TranslationResult
            {
                Success = false,
                ErrorMessage = NetworkErrorMessages.FromException(ex, cancellationToken),
                Provider = "Google"
            };
        }
    }

    internal static (string TranslatedText, string SourceLanguage) ParseResponse(
        JsonElement root,
        string fallbackLanguage)
    {
        if (root.ValueKind != JsonValueKind.Array ||
            root.GetArrayLength() == 0 ||
            root[0].ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("翻译服务返回格式不正确。");
        }

        var translated = new StringBuilder();
        foreach (var segment in root[0].EnumerateArray())
        {
            if (segment.ValueKind == JsonValueKind.Array &&
                segment.GetArrayLength() > 0 &&
                segment[0].ValueKind == JsonValueKind.String)
            {
                translated.Append(segment[0].GetString());
            }
        }

        var detected = root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String
            ? root[2].GetString() ?? fallbackLanguage
            : fallbackLanguage;
        return (translated.ToString().Trim(), detected);
    }

    private static string MapLanguageCode(string code) => code.ToLowerInvariant() switch
    {
        "auto" => "auto",
        "zh" or "zh-cn" or "chinese" => "zh-CN",
        "en" or "english" => "en",
        "ja" or "japanese" => "ja",
        "ko" or "korean" => "ko",
        "fr" or "french" => "fr",
        "de" or "german" => "de",
        "es" or "spanish" => "es",
        "ru" or "russian" => "ru",
        _ => code
    };

    public void Dispose()
    {
        // Shared process-level HttpClient.
    }
}
