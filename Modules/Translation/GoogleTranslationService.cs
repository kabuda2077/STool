using System;
using System.Collections.Generic;
using System.Net;
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
/// 原文放在 URL 里，长文本按编码后长度分段请求，避免超出 URL 长度限制。
/// </summary>
public class GoogleTranslationService : ITranslationService
{
    private const string ProviderName = "Google";

    /// <summary>单次请求 q 参数编码后的最大长度。中文按 UTF-8 百分号编码后约膨胀 9 倍。</summary>
    internal const int MaxEncodedChunkLength = 5000;

    public bool IsAvailable() => true;

    public async Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var sl = LanguageCodes.ToGoogle(sourceLanguage);
            var tl = LanguageCodes.ToGoogle(targetLanguage);
            var detected = sl;
            var translated = new StringBuilder();

            foreach (var chunk in SplitIntoChunks(text, MaxEncodedChunkLength))
            {
                translated.Append(chunk.SeparatorBefore);
                if (string.IsNullOrWhiteSpace(chunk.Text))
                {
                    translated.Append(chunk.Text);
                    continue;
                }

                var parsed = await TranslateChunkAsync(chunk.Text, sl, tl, cancellationToken);
                if (string.Equals(detected, "auto", StringComparison.OrdinalIgnoreCase))
                    detected = parsed.SourceLanguage;
                translated.Append(parsed.TranslatedText);
            }

            var result = translated.ToString().Trim();
            if (string.IsNullOrWhiteSpace(result))
                throw new InvalidOperationException("翻译服务返回了空结果。");

            return new TranslationResult
            {
                Success = true,
                SourceText = text,
                TranslatedText = result,
                SourceLanguage = detected,
                TargetLanguage = tl,
                Provider = ProviderName
            };
        }
        catch (GoogleHttpException ex)
        {
            return Failure(NetworkErrorMessages.FromStatus(ex.StatusCode, ex.Body));
        }
        catch (Exception ex)
        {
            return Failure(NetworkErrorMessages.FromException(ex, cancellationToken));
        }
    }

    private static async Task<(string TranslatedText, string SourceLanguage)> TranslateChunkAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={sourceLanguage}&tl={targetLanguage}&dt=t&q={Uri.EscapeDataString(text)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");

        using var response = await HttpDefaults.Shared.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new GoogleHttpException(response.StatusCode, body);

        using var document = JsonDocument.Parse(body);
        return ParseResponse(document.RootElement, sourceLanguage);
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

    /// <summary>
    /// 按行把文本切成编码后不超过上限的片段；单行过长时再在空白或标点处切开。
    /// SeparatorBefore 记录拼接译文时片段前应补回的分隔符。
    /// </summary>
    internal static IReadOnlyList<TextChunk> SplitIntoChunks(string text, int maxEncodedLength)
    {
        var chunks = new List<TextChunk>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var current = new StringBuilder();
        var currentEncoded = 0;
        var currentSeparator = string.Empty;
        var hasCurrent = false;

        void FlushCurrent()
        {
            if (!hasCurrent)
                return;
            chunks.Add(new TextChunk(current.ToString(), currentSeparator));
            current.Clear();
            currentEncoded = 0;
            hasCurrent = false;
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var separator = index == 0 ? string.Empty : "\n";
            var lineEncoded = EncodedLength(line);

            if (lineEncoded > maxEncodedLength)
            {
                FlushCurrent();
                AddLongLine(chunks, line, separator, maxEncodedLength);
                continue;
            }

            if (hasCurrent && currentEncoded + EncodedLength(separator) + lineEncoded <= maxEncodedLength)
            {
                current.Append(separator).Append(line);
                currentEncoded += EncodedLength(separator) + lineEncoded;
                continue;
            }

            FlushCurrent();
            current.Append(line);
            currentEncoded = lineEncoded;
            currentSeparator = separator;
            hasCurrent = true;
        }

        FlushCurrent();
        return chunks;
    }

    private static void AddLongLine(List<TextChunk> chunks, string line, string separatorBefore, int maxEncodedLength)
    {
        var builder = new StringBuilder();
        var encoded = 0;
        var lastBreak = -1;
        var separator = separatorBefore;

        foreach (var rune in line.EnumerateRunes())
        {
            var runeLength = EncodedLength(rune);
            if (builder.Length > 0 && encoded + runeLength > maxEncodedLength)
            {
                var cut = lastBreak > builder.Length / 2 ? lastBreak : builder.Length;
                var piece = builder.ToString(0, cut);
                var remainder = builder.ToString(cut, builder.Length - cut);
                var brokeAtSpace = piece.Length > 0 && char.IsWhiteSpace(piece[^1]);

                chunks.Add(new TextChunk(piece.TrimEnd(), separator));
                separator = brokeAtSpace ? " " : string.Empty;

                builder.Clear().Append(remainder.TrimStart());
                encoded = EncodedLength(builder.ToString());
                lastBreak = -1;
            }

            builder.Append(rune.ToString());
            encoded += runeLength;
            if (Rune.IsWhiteSpace(rune) || IsSentenceBreak(rune))
                lastBreak = builder.Length;
        }

        if (builder.Length > 0)
            chunks.Add(new TextChunk(builder.ToString(), separator));
    }

    private static bool IsSentenceBreak(Rune rune) =>
        rune.Value is '.' or ',' or ';' or '!' or '?' or 0x3002 or 0xFF0C or 0xFF1B or 0xFF01 or 0xFF1F;

    internal static int EncodedLength(string text)
    {
        var length = 0;
        foreach (var rune in text.EnumerateRunes())
            length += EncodedLength(rune);
        return length;
    }

    // Uri.EscapeDataString 只保留 RFC 3986 的非保留字符，其余按 UTF-8 字节编码为 %XX。
    private static int EncodedLength(Rune rune)
    {
        if (!rune.IsAscii)
            return 3 * rune.Utf8SequenceLength;

        var ch = (char)rune.Value;
        return char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or '~' ? 1 : 3;
    }

    private static TranslationResult Failure(string message) => new()
    {
        Success = false,
        ErrorMessage = message,
        Provider = ProviderName
    };

    public void Dispose()
    {
        // 使用进程级共享 HttpClient，无需释放。
    }

    internal readonly record struct TextChunk(string Text, string SeparatorBefore);

    private sealed class GoogleHttpException(HttpStatusCode statusCode, string body) : Exception(body)
    {
        public HttpStatusCode StatusCode { get; } = statusCode;
        public string Body { get; } = body;
    }
}
