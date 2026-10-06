using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using STool.Core;

namespace STool.Modules.Translation;

/// <summary>
/// 截图原位翻译的智能模式：把 OCR 行交给 LLM，一次完成"挑出正文行 + 翻译"，
/// 过滤掉时间戳、用户名、按钮、状态栏等界面噪声。
///
/// 依赖 AI（OpenAI 兼容）通道；未配置或失败时返回 null，由调用方回退到整块翻译。
/// </summary>
public sealed class ScreenContentSelector
{
    private const double Temperature = 0.0;

    private readonly string _apiUrl;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly bool _hasValidEndpoint;

    public ScreenContentSelector(string apiUrlEncrypted, string apiKeyEncrypted, string model)
    {
        _apiUrl = SecureStorage.Decrypt(apiUrlEncrypted);
        _apiKey = SecureStorage.Decrypt(apiKeyEncrypted);
        _model = model;
        _hasValidEndpoint = HasValidEndpoint(_apiUrl);
    }

    public bool IsAvailable()
    {
        return _hasValidEndpoint && !string.IsNullOrEmpty(_apiKey) && !string.IsNullOrEmpty(_model);
    }

    /// <summary>
    /// 让 AI 一次完成「选正文行 + 翻译」。返回项的 Index 对应 lines 里的 Index；
    /// AI 不可用、调用失败或输出无法解析时返回 null。
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
            var request = new ChatCompletionRequest(
                _model,
                OpenAiChatClient.UserMessage(prompt),
                CalculateTranslateMaxTokens(lines),
                Temperature);
            var completion = await OpenAiChatClient.CompleteAsync(
                HttpDefaults.Shared,
                _apiUrl,
                _apiKey,
                request,
                cancellationToken);

            // 只记录耗时、状态和长度，不把截图里的原文或译文写进日志。
            Log.Information(
                "[ContentSelector] model={Model} promptChars={PromptChars} maxTokens={MaxTokens} elapsed={ElapsedMs}ms status={Status} finishReason={FinishReason}",
                _model,
                prompt.Length,
                request.MaxOutputTokens,
                completion.ElapsedMilliseconds,
                completion.Error,
                completion.FinishReason ?? "unknown");

            if (!completion.Success)
                return null;

            var translated = TryParseTranslations(
                completion.Content,
                lines.Select(line => line.Index).ToHashSet());
            if (translated == null || translated.Count == 0)
            {
                Log.Warning(
                    "[ContentSelector] invalid translation output length={Length} finishReason={FinishReason}",
                    completion.Content.Length,
                    completion.FinishReason ?? "unknown");
            }

            return translated;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ContentSelector] smart translation failed");
            return null;
        }
    }

    internal static string BuildTranslatePrompt(IReadOnlyList<ScreenContentLine> lines, string targetLanguage)
    {
        var sb = new StringBuilder();
        sb.Append("Translate screenshot body text to ");
        sb.Append(LanguageCodes.ToEnglishName(targetLanguage));
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

    private static bool HasValidEndpoint(string apiUrl)
    {
        if (string.IsNullOrWhiteSpace(apiUrl))
            return false;

        try
        {
            return AiApiEndpointResolver.ResolveChatCompletionCandidates(apiUrl).Count > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
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
}

public sealed record ScreenContentLine(int Index, string Text, int X, int Y, int Width, int Height);

public sealed record ScreenTranslationItem(int Index, string Translation);
