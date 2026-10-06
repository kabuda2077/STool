using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace STool.Core;

internal enum ChatCompletionError
{
    None,
    Http,
    InvalidJson,
    MissingChoices,
    MissingMessage,
    MissingContent,
    Refusal,
    ContentFilter,
    Truncated,
    Empty
}

internal sealed record ChatCompletionRequest(
    string Model,
    JsonArray Messages,
    int MaxOutputTokens,
    double? Temperature = null);

/// <summary>Chat Completions 调用结果。Truncated 时 Content 保留已生成的部分文本。</summary>
internal sealed record ChatCompletionResult(
    ChatCompletionError Error,
    string Content,
    string? FinishReason = null,
    string? Detail = null,
    HttpStatusCode? StatusCode = null,
    long ElapsedMilliseconds = 0)
{
    public bool Success => Error == ChatCompletionError.None;
}

/// <summary>同一接口 + 模型组合需要的参数形态。推理模型不接受 max_tokens 和非默认 temperature。</summary>
internal readonly record struct ChatCompatibility(bool UseMaxCompletionTokens, bool OmitTemperature);

/// <summary>
/// OpenAI 兼容 Chat Completions 客户端，翻译、AI OCR 与截图智能翻译共用。
/// 首次遇到"参数不受支持"的 400 时自动调整参数重试，并按接口 + 模型记住结果。
/// </summary>
internal static class OpenAiChatClient
{
    /// <summary>推理模型的 max_completion_tokens 包含思考过程，额外预留的额度。</summary>
    private const int ReasoningHeadroomTokens = 4096;
    private const int MaxCompletionTokensCap = 32768;
    private const int MaxAttempts = 3;

    private static readonly ConcurrentDictionary<string, ChatCompatibility> Compatibility =
        new(StringComparer.OrdinalIgnoreCase);

    public static JsonArray UserMessage(string text) =>
        new(new JsonObject
        {
            ["role"] = "user",
            ["content"] = text
        });

    public static JsonArray UserMessageWithImage(string text, string imageDataUrl) =>
        new(new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = text },
                new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = imageDataUrl }
                })
        });

    /// <summary>
    /// 发送 Chat Completions 请求。地址无效时抛出 InvalidOperationException，
    /// 网络异常与取消按原样抛出，由调用方转换成面向用户的提示。
    /// </summary>
    public static async Task<ChatCompletionResult> CompleteAsync(
        HttpClient httpClient,
        string apiUrl,
        string apiKey,
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var endpoints = AiApiEndpointResolver.ResolveChatCompletionCandidates(apiUrl);
        var compatibilityKey = endpoints[0] + "|" + request.Model;
        var compatibility = Compatibility.TryGetValue(compatibilityKey, out var known) ? known : default;
        AiApiHttpResult? response = null;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var payloadJson = BuildPayload(request, compatibility).ToJsonString();
            response = await AiApiRequestSender.SendAsync(
                httpClient,
                endpoints,
                endpoint => CreateRequest(endpoint, apiKey, payloadJson),
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                Compatibility[compatibilityKey] = compatibility;
                return ParseResponse(response.Body, response.TotalElapsedMilliseconds);
            }

            if (response.StatusCode != HttpStatusCode.BadRequest)
                break;

            var adjusted = AdjustCompatibility(response.Body, compatibility);
            if (adjusted is null)
                break;
            compatibility = adjusted.Value;
        }

        return new ChatCompletionResult(
            ChatCompletionError.Http,
            string.Empty,
            Detail: response?.Body,
            StatusCode: response?.StatusCode,
            ElapsedMilliseconds: response?.TotalElapsedMilliseconds ?? 0);
    }

    public static async Task<IReadOnlyList<string>> FetchModelsAsync(
        string apiUrl,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiUrl))
            throw new InvalidOperationException("请先填写 API URL");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("请先填写 API Key");

        var key = apiKey.Trim();
        var response = await AiApiRequestSender.SendAsync(
            HttpDefaults.Shared,
            AiApiEndpointResolver.ResolveModelsCandidates(apiUrl),
            endpoint =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                return request;
            },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"获取模型失败：{NetworkErrorMessages.FromStatus(response.StatusCode, response.Body)}");

        using var document = JsonDocument.Parse(response.Body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("模型接口返回格式不正确");
        }

        return data.EnumerateArray()
            .Select(item => ReadString(item, "id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static JsonObject BuildPayload(ChatCompletionRequest request, ChatCompatibility compatibility)
    {
        var payload = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = request.Messages.DeepClone()
        };

        if (compatibility.UseMaxCompletionTokens)
            payload["max_completion_tokens"] = Math.Min(MaxCompletionTokensCap, request.MaxOutputTokens + ReasoningHeadroomTokens);
        else
            payload["max_tokens"] = request.MaxOutputTokens;

        if (request.Temperature is double temperature && !compatibility.OmitTemperature)
            payload["temperature"] = temperature;

        return payload;
    }

    /// <summary>根据 400 错误内容判断是否需要换一种参数形态重试；无法识别时返回 null。</summary>
    internal static ChatCompatibility? AdjustCompatibility(string responseBody, ChatCompatibility current)
    {
        var body = responseBody.ToLowerInvariant();
        var unsupported = body.Contains("unsupported", StringComparison.Ordinal) ||
                          body.Contains("not supported", StringComparison.Ordinal) ||
                          body.Contains("does not support", StringComparison.Ordinal) ||
                          body.Contains("only the default", StringComparison.Ordinal);

        if (!current.UseMaxCompletionTokens &&
            body.Contains("max_tokens", StringComparison.Ordinal) &&
            (body.Contains("max_completion_tokens", StringComparison.Ordinal) || unsupported))
        {
            return current with { UseMaxCompletionTokens = true };
        }

        if (!current.OmitTemperature &&
            body.Contains("temperature", StringComparison.Ordinal) &&
            unsupported)
        {
            return current with { OmitTemperature = true };
        }

        return null;
    }

    internal static ChatCompletionResult ParseResponse(string responseJson, long elapsedMilliseconds = 0)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return Failure(ChatCompletionError.MissingChoices, elapsedMilliseconds);
            }

            var choice = choices[0];
            var finishReason = ReadString(choice, "finish_reason");
            var hasMessage = choice.ValueKind == JsonValueKind.Object &&
                             choice.TryGetProperty("message", out _) &&
                             choice.GetProperty("message").ValueKind == JsonValueKind.Object;
            var message = hasMessage ? choice.GetProperty("message") : default;
            var hasContent = hasMessage && message.TryGetProperty("content", out _);
            var content = hasContent ? ReadMessageContent(message.GetProperty("content")).Trim() : string.Empty;

            if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
                return new ChatCompletionResult(ChatCompletionError.Truncated, content, finishReason, ElapsedMilliseconds: elapsedMilliseconds);

            if (string.Equals(finishReason, "content_filter", StringComparison.OrdinalIgnoreCase))
                return new ChatCompletionResult(ChatCompletionError.ContentFilter, string.Empty, finishReason, ElapsedMilliseconds: elapsedMilliseconds);

            if (!hasMessage)
                return Failure(ChatCompletionError.MissingMessage, elapsedMilliseconds, finishReason);

            var refusal = ReadString(message, "refusal");
            if (!string.IsNullOrWhiteSpace(refusal))
                return new ChatCompletionResult(ChatCompletionError.Refusal, string.Empty, finishReason, refusal.Trim(), ElapsedMilliseconds: elapsedMilliseconds);

            if (!hasContent)
                return Failure(ChatCompletionError.MissingContent, elapsedMilliseconds, finishReason);

            if (string.IsNullOrWhiteSpace(content))
                return Failure(ChatCompletionError.Empty, elapsedMilliseconds, finishReason);

            return new ChatCompletionResult(ChatCompletionError.None, content, finishReason, ElapsedMilliseconds: elapsedMilliseconds);
        }
        catch (JsonException ex)
        {
            return new ChatCompletionResult(ChatCompletionError.InvalidJson, string.Empty, Detail: ex.Message, ElapsedMilliseconds: elapsedMilliseconds);
        }
    }

    private static ChatCompletionResult Failure(ChatCompletionError error, long elapsedMilliseconds, string? finishReason = null) =>
        new(error, string.Empty, finishReason, ElapsedMilliseconds: elapsedMilliseconds);

    private static HttpRequestMessage CreateRequest(string endpoint, string apiKey, string payloadJson)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    private static string ReadMessageContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? string.Empty;

        if (content.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var parts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            var value = part.ValueKind switch
            {
                JsonValueKind.String => part.GetString(),
                JsonValueKind.Object => ReadString(part, "text"),
                _ => null
            };
            if (!string.IsNullOrEmpty(value))
                parts.Add(value);
        }

        return string.Join(Environment.NewLine, parts);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }
}
