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

/// <param name="DisableReasoning">
/// 尽量关闭或压低模型的“思考”。翻译、OCR 等对延迟敏感的任务应开启：按平台发送对应参数，
/// 不支持时由兼容性降级自动去掉。
/// </param>
/// <param name="JsonResponse">要求返回 JSON 对象（response_format: json_object）。提示词里必须出现 "JSON" 字样。</param>
internal sealed record ChatCompletionRequest(
    string Model,
    JsonArray Messages,
    int MaxOutputTokens,
    double? Temperature = null,
    bool DisableReasoning = false,
    bool JsonResponse = false);

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

/// <summary>
/// 同一接口 + 模型组合需要的参数形态。推理模型不接受 max_tokens 和非默认 temperature；
/// 旧模型或部分兼容平台不认识思考控制参数和 response_format。
/// </summary>
internal readonly record struct ChatCompatibility(
    bool UseMaxCompletionTokens,
    bool OmitTemperature,
    bool OmitReasoningControl = false,
    bool OmitResponseFormat = false);

/// <summary>按接口主机识别的平台，用于生成各家不同的思考控制参数。</summary>
internal enum AiEndpointKind
{
    Other,
    OpenAi,
    DeepSeek,
    GoogleAiStudio
}

/// <summary>
/// OpenAI 兼容 Chat Completions 客户端，翻译、AI OCR 与截图智能翻译共用。
/// 首次遇到"参数不受支持"的 400 时自动调整参数重试，并按接口 + 模型记住结果。
/// </summary>
internal static class OpenAiChatClient
{
    /// <summary>推理模型的 max_completion_tokens 包含思考过程，额外预留的额度。</summary>
    private const int ReasoningHeadroomTokens = 4096;
    private const int MaxCompletionTokensCap = 32768;
    private const int MaxAttempts = 5;

    private static readonly ConcurrentDictionary<string, ChatCompatibility> Compatibility =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>指令放 system，正文放 user：避免正文里的文字被当作指令执行。</summary>
    public static JsonArray SystemAndUserMessages(string systemPrompt, string userText) =>
        new(
            new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
            new JsonObject { ["role"] = "user", ["content"] = userText });

    public static JsonArray SystemAndUserMessageWithImage(string systemPrompt, string userText, string imageDataUrl)
    {
        var messages = UserMessageWithImage(userText, imageDataUrl);
        messages.Insert(0, new JsonObject { ["role"] = "system", ["content"] = systemPrompt });
        return messages;
    }

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
        var endpointKind = DetectEndpointKind(endpoints[0]);
        var compatibility = Compatibility.TryGetValue(compatibilityKey, out var known)
            ? known
            : DefaultCompatibility(endpointKind);
        AiApiHttpResult? response = null;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var payloadJson = BuildPayload(request, compatibility, endpointKind).ToJsonString();
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

        Serilog.Log.Warning(
            "[AI] Request failed host={Host} model={Model} status={Status} body={Body}",
            SafeHost(endpoints[0]),
            request.Model,
            response?.StatusCode is { } status ? (int)status : 0,
            Truncate(response?.Body, 300));

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

    internal static AiEndpointKind DetectEndpointKind(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return AiEndpointKind.Other;

        return uri.Host.ToLowerInvariant() switch
        {
            "api.openai.com" => AiEndpointKind.OpenAi,
            "api.deepseek.com" => AiEndpointKind.DeepSeek,
            "generativelanguage.googleapis.com" => AiEndpointKind.GoogleAiStudio,
            _ => AiEndpointKind.Other
        };
    }

    /// <summary>OpenAI 官方接口对所有模型都接受 max_completion_tokens，推理模型则只接受它，直接使用。</summary>
    internal static ChatCompatibility DefaultCompatibility(AiEndpointKind kind) =>
        new(UseMaxCompletionTokens: kind == AiEndpointKind.OpenAi, OmitTemperature: false);

    internal static JsonObject BuildPayload(
        ChatCompletionRequest request,
        ChatCompatibility compatibility,
        AiEndpointKind endpointKind = AiEndpointKind.Other)
    {
        var payload = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = request.Messages.DeepClone()
        };

        var reasoningControlled = request.DisableReasoning &&
                                  !compatibility.OmitReasoningControl &&
                                  AddReasoningControl(payload, endpointKind);

        if (compatibility.UseMaxCompletionTokens)
        {
            // 不能确定思考已关闭时，额度要给思考过程预留空间，否则正文会被截断。
            var headroom = reasoningControlled && endpointKind == AiEndpointKind.OpenAi ? 0 : ReasoningHeadroomTokens;
            payload["max_completion_tokens"] = Math.Min(MaxCompletionTokensCap, request.MaxOutputTokens + headroom);
        }
        else
        {
            payload["max_tokens"] = request.MaxOutputTokens;
        }

        if (request.Temperature is double temperature && !compatibility.OmitTemperature)
            payload["temperature"] = temperature;

        if (request.JsonResponse && !compatibility.OmitResponseFormat)
            payload["response_format"] = new JsonObject { ["type"] = "json_object" };

        return payload;
    }

    /// <summary>按平台写入“关闭/压低思考”的参数。返回是否写入了参数。</summary>
    private static bool AddReasoningControl(JsonObject payload, AiEndpointKind endpointKind)
    {
        switch (endpointKind)
        {
            case AiEndpointKind.OpenAi:
                payload["reasoning_effort"] = "none";
                return true;
            case AiEndpointKind.DeepSeek:
                payload["thinking"] = new JsonObject { ["type"] = "disabled" };
                return true;
            case AiEndpointKind.GoogleAiStudio:
                // Gemini 3 系列不能完全关闭思考，low 是所有型号都支持的最低档。
                payload["reasoning_effort"] = "low";
                return true;
            default:
                return false;
        }
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

        if (!current.OmitReasoningControl &&
            (body.Contains("reasoning_effort", StringComparison.Ordinal) ||
             body.Contains("reasoning effort", StringComparison.Ordinal) ||
             body.Contains("thinking", StringComparison.Ordinal)))
        {
            return current with { OmitReasoningControl = true };
        }

        if (!current.OmitResponseFormat && body.Contains("response_format", StringComparison.Ordinal))
            return current with { OmitResponseFormat = true };

        return null;
    }

    private static string SafeHost(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : "invalid";

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var singleLine = value.ReplaceLineEndings(" ");
        return singleLine.Length <= maxLength ? singleLine : singleLine[..maxLength] + "...";
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
