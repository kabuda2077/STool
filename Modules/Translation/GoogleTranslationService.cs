using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace STool.Modules.Translation;

/// <summary>
/// 谷歌翻译 —— 使用免费的 web 端点(translate.googleapis.com),无需 API Key。
/// 注意:为非官方公开端点,适合个人轻量使用,频繁调用可能被限流。
/// </summary>
public class GoogleTranslationService : ITranslationService
{
    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
        return c;
    }

    public bool IsAvailable() => true;

    public async Task<TranslationResult> TranslateAsync(string text, string sourceLanguage, string targetLanguage)
    {
        try
        {
            var sl = MapLanguageCode(sourceLanguage);
            var tl = MapLanguageCode(targetLanguage);
            var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={sl}&tl={tl}&dt=t&q={Uri.EscapeDataString(text)}";

            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 响应形如:[[["译文","原文",...],...], null, "检测到的源语言", ...]
            var sb = new StringBuilder();
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 && root[0].ValueKind == JsonValueKind.Array)
            {
                foreach (var seg in root[0].EnumerateArray())
                {
                    if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0 && seg[0].ValueKind == JsonValueKind.String)
                        sb.Append(seg[0].GetString());
                }
            }

            var detected = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String
                ? root[2].GetString() ?? sl
                : sl;

            return new TranslationResult
            {
                Success = true,
                SourceText = text,
                TranslatedText = sb.ToString(),
                SourceLanguage = detected,
                TargetLanguage = tl,
                Provider = "Google"
            };
        }
        catch (Exception ex)
        {
            return new TranslationResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Provider = "Google"
            };
        }
    }

    private static string MapLanguageCode(string code) => code.ToLower() switch
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

    public void Dispose() { /* 共享静态 HttpClient,不在此释放 */ }
}
