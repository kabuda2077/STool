using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using STool.Core;

namespace STool.Modules.Translation;

/// <summary>
/// 截图原位翻译的「内容选择」能力：把 OCR 行交给 LLM，判定哪些行是值得翻译的正文，
/// 过滤掉时间戳、用户名、按钮、状态栏等界面噪声。替代旧的启发式打分规则。
///
/// 依赖 AI（OpenAI 兼容）通道；未配置或失败时返回 null，由调用方回退到整块翻译。
/// </summary>
public class ScreenContentSelector : IDisposable
{
    private readonly string _apiUrl;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly HttpClient _httpClient;
    private readonly IReadOnlyList<string> _endpoints;
    private readonly bool _ownsHttpClient;

    public ScreenContentSelector(
        string apiUrlEncrypted,
        string apiKeyEncrypted,
        string model,
        HttpClient? httpClient = null)
    {
        _apiUrl = SecureStorage.Decrypt(apiUrlEncrypted);
        _apiKey = SecureStorage.Decrypt(apiKeyEncrypted);
        _model = model;
        _httpClient = httpClient ?? HttpDefaults.Shared;
        _ownsHttpClient = httpClient != null && !ReferenceEquals(httpClient, HttpDefaults.Shared);
        _endpoints = string.IsNullOrWhiteSpace(_apiUrl)
            ? Array.Empty<string>()
            : AiApiEndpointResolver.ResolveChatCompletionCandidates(_apiUrl);
    }

    public bool IsAvailable()
    {
        return _endpoints.Count > 0 && !string.IsNullOrEmpty(_apiKey) && !string.IsNullOrEmpty(_model);
    }

    /// <summary>
    /// 返回应翻译的行号（0-based，对应入参顺序）。AI 不可用/调用失败/解析失败时返回 null。
    /// </summary>
    public async Task<IReadOnlyList<int>?> SelectAsync(IReadOnlyList<string> lines, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable() || lines.Count == 0)
            return null;

        try
        {
            var prompt = BuildPrompt(lines);
            var completion = await SendPromptAsync(prompt, 400, cancellationToken);
            if (completion == null)
                return null;

            return TryParseIndices(completion.Content, lines.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ContentSelector] selection failed");
            return null;
        }
    }

    /// <summary>
    /// 智能模式：让 AI 一次完成「选正文行 + 翻译」。返回的 i 对应 lines 里的 Index。
    /// </summary>
    public async Task<IReadOnlyList<ScreenTranslationItem>?> SelectAndTranslateAsync(
        IReadOnlyList<ScreenContentLine> lines,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable() || lines.Count == 0)
            return null;

        try
        {
            var prompt = BuildTranslatePrompt(lines, targetLanguage);
            var maxTokens = CalculateTranslateMaxTokens(lines);
            var completion = await SendPromptAsync(prompt, maxTokens, cancellationToken);
            if (completion == null)
                return null;

            var translated = TryParseTranslations(
                completion.Content,
                lines.Select(line => line.Index).ToHashSet());
            if (translated == null || translated.Count == 0)
            {
                Log.Warning(
                    "[ContentSelector] invalid translation output finishReason={FinishReason} content={Content}",
                    completion.FinishReason ?? "unknown",
                    TruncateForLog(completion.Content));
            }

            return translated;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ContentSelector] smart translation failed");
            return null;
        }
    }

    internal static string BuildPrompt(IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are filtering OCR lines from a screenshot for in-place translation.");
        sb.AppendLine("Keep only lines that are real readable body content (sentences, paragraphs, article/post/message text).");
        sb.AppendLine("Discard UI chrome: timestamps, usernames/handles, button labels, menu items, status bar, counts, URLs, icons, isolated numbers/symbols.");
        sb.AppendLine("Return ONLY a JSON array of the 0-based indices to keep, in ascending order. No prose. Example: [1,2,5]");
        sb.AppendLine();
        sb.AppendLine("Lines:");
        for (var i = 0; i < lines.Count; i++)
        {
            sb.Append(i);
            sb.Append(": ");
            sb.AppendLine(lines[i].ReplaceLineEndings(" "));
        }

        return sb.ToString();
    }

    internal static string BuildTranslatePrompt(IReadOnlyList<ScreenContentLine> lines, string targetLanguage)
    {
        var sb = new StringBuilder();
        sb.Append("Translate screenshot body text to ");
        sb.Append(GetLanguageName(targetLanguage));
        sb.AppendLine(" for in-place replacement.");
        sb.AppendLine("Keep readable sentences/messages; skip UI chrome, names, timestamps, buttons, menus, counts, URLs, icons and isolated symbols.");
        sb.AppendLine("Keep meaning and translations concise.");
        sb.AppendLine("Return ONLY a valid JSON array in this exact shape: [{\"i\":2,\"t\":\"translated text\"}]");
        sb.AppendLine("Use each input index at most once. Do not return Markdown, explanations, analysis, comments, or extra keys.");
        sb.AppendLine("If no row should be translated, return [].");
        sb.AppendLine("Input rows are [i,x,y,w,h,text]:");

        foreach (var line in lines)
        {
            sb.Append('[');
            sb.Append(line.Index);
            sb.Append(',');
            sb.Append(line.X);
            sb.Append(',');
            sb.Append(line.Y);
            sb.Append(',');
            sb.Append(line.Width);
            sb.Append(',');
            sb.Append(line.Height);
            sb.Append(',');
            sb.Append(JsonSerializer.Serialize(line.Text.ReplaceLineEndings(" ")));
            sb.AppendLine("]");
        }

        return sb.ToString();
    }

    internal static int CalculateTranslateMaxTokens(IReadOnlyList<ScreenContentLine> lines)
    {
        var textLength = lines.Sum(line => line.Text.Length);
        return Math.Clamp(textLength * 2 + lines.Count * 20 + 160, 384, 2200);
    }

    /// <summary>
    /// 从模型回复文本中解析出行号数组。容忍代码块包裹、附带文字；越界/重复会被剔除。
    /// 解析不到合法数组返回 null。
    /// </summary>
    internal static IReadOnlyList<int>? TryParseIndices(string content, int lineCount)
    {
        if (string.IsNullOrWhiteSpace(content) || lineCount <= 0)
            return null;

        var match = Regex.Match(content, @"\[[\s\d,\-]*\]", RegexOptions.Singleline);
        if (!match.Success)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(match.Value);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            var result = new List<int>();
            var seen = new HashSet<int>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var index))
                    continue;
                if (index < 0 || index >= lineCount || !seen.Add(index))
                    continue;
                result.Add(index);
            }

            result.Sort();
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<ScreenTranslationItem>? TryParseTranslations(string content, IReadOnlySet<int> validIndices)
    {
        if (string.IsNullOrWhiteSpace(content) || validIndices.Count == 0)
            return null;

        var match = Regex.Match(content, @"\[[\s\S]*\]", RegexOptions.Singleline);
        if (!match.Success)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(match.Value);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            var result = new List<ScreenTranslationItem>();
            var seen = new HashSet<int>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                if (!TryReadIndex(item, out var index) ||
                    !validIndices.Contains(index) ||
                    !seen.Add(index))
                {
                    continue;
                }

                if (!TryReadTranslation(item, out var translation))
                    continue;

                result.Add(new ScreenTranslationItem(index, translation));
            }

            result.Sort((a, b) => a.Index.CompareTo(b.Index));
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<ScreenCompletion?> SendPromptAsync(string prompt, int maxTokens, CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = _model,
            messages = new[] { new { role = "user", content = prompt } },
            temperature = 0.0,
            max_tokens = maxTokens
        };

        var response = await AiApiRequestSender.SendAsync(
            _httpClient,
            _endpoints,
            endpoint =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                };
                request.Headers.Add("Authorization", $"Bearer {_apiKey}");
                return request;
            },
            cancellationToken);

        Log.Information(
            "[ContentSelector] model={Model} promptChars={PromptChars} maxTokens={MaxTokens} headers={HeadersMs}ms total={TotalMs}ms status={Status}",
            _model,
            prompt.Length,
            maxTokens,
            response.HeadersElapsedMilliseconds,
            response.TotalElapsedMilliseconds,
            (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("[ContentSelector] API error {Status}: {Body}", response.StatusCode, TruncateForLog(response.Body));
            return null;
        }

        return ExtractCompletion(response.Body);
    }

    private static bool TryReadIndex(JsonElement item, out int index)
    {
        index = 0;
        if (item.TryGetProperty("i", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt32(out index))
            return true;

        if (item.TryGetProperty("index", out var indexElement) &&
            indexElement.ValueKind == JsonValueKind.Number &&
            indexElement.TryGetInt32(out index))
        {
            return true;
        }

        return false;
    }

    private static bool TryReadTranslation(JsonElement item, out string translation)
    {
        translation = string.Empty;
        if (item.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.String)
        {
            translation = t.GetString()?.Trim() ?? string.Empty;
        }
        else if (item.TryGetProperty("translation", out var translationElement) &&
                 translationElement.ValueKind == JsonValueKind.String)
        {
            translation = translationElement.GetString()?.Trim() ?? string.Empty;
        }

        return !string.IsNullOrWhiteSpace(translation);
    }

    private static ScreenCompletion? ExtractCompletion(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var contentElement))
            {
                var content = contentElement.ValueKind == JsonValueKind.String
                    ? contentElement.GetString()
                    : null;
                var finishReason = choices[0].TryGetProperty("finish_reason", out var finishReasonElement) &&
                                   finishReasonElement.ValueKind == JsonValueKind.String
                    ? finishReasonElement.GetString()
                    : null;

                if (!string.IsNullOrWhiteSpace(content))
                {
                    return new ScreenCompletion(content, finishReason);
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string GetLanguageName(string code)
    {
        return code.ToLowerInvariant() switch
        {
            "zh" or "zh-cn" or "chinese" => "Chinese",
            "en" or "english" => "English",
            "ja" or "japanese" => "Japanese",
            "ko" or "korean" => "Korean",
            _ => code
        };
    }

    private static string TruncateForLog(string text)
    {
        var normalized = text.ReplaceLineEndings(" ");
        return normalized.Length <= 200 ? normalized : normalized[..200] + "...";
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}

public sealed record ScreenContentLine(int Index, string Text, int X, int Y, int Width, int Height);

public sealed record ScreenTranslationItem(int Index, string Translation);

internal sealed record ScreenCompletion(string Content, string? FinishReason);
