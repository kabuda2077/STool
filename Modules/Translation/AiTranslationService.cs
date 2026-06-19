using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using STool.Core;
using STool.Models;

namespace STool.Modules.Translation;

/// <summary>
/// AI 翻译服务（OpenAI/Claude）
/// </summary>
public class AiTranslationService : ITranslationService
{
    public const string OpenAiChatCompletionsUrl = "https://api.openai.com/v1/chat/completions";
    public const string GoogleAiStudioChatCompletionsUrl = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
    public const string DeepSeekChatCompletionsUrl = "https://api.deepseek.com/chat/completions";

    private readonly string _apiUrl;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly HttpClient _httpClient;

    public AiTranslationService(string apiUrlEncrypted, string apiKeyEncrypted, string model)
    {
        _apiUrl = SecureStorage.Decrypt(apiUrlEncrypted);
        _apiKey = SecureStorage.Decrypt(apiKeyEncrypted);
        _model = model;
        _httpClient = new HttpClient();
    }

    public bool IsAvailable()
    {
        return !string.IsNullOrEmpty(_apiUrl) && !string.IsNullOrEmpty(_apiKey);
    }

    public static string GetDefaultApiUrl(TranslationAiPlatform platform)
    {
        return platform switch
        {
            TranslationAiPlatform.OpenAI => OpenAiChatCompletionsUrl,
            TranslationAiPlatform.GoogleAiStudio => GoogleAiStudioChatCompletionsUrl,
            TranslationAiPlatform.DeepSeek => DeepSeekChatCompletionsUrl,
            _ => string.Empty
        };
    }

    public static async Task<IReadOnlyList<string>> FetchModelsAsync(string apiUrl, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            throw new InvalidOperationException("请先填写 API URL");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("请先填写 API Key");
        }

        using var httpClient = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildModelsUrl(apiUrl.Trim()));
        request.Headers.Add("Authorization", $"Bearer {apiKey.Trim()}");

        using var response = await httpClient.SendAsync(request);
        var responseJson = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"获取模型失败：{response.StatusCode} - {responseJson}");
        }

        using var jsonDoc = JsonDocument.Parse(responseJson);
        if (!jsonDoc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("模型接口返回格式不正确");
        }

        return data.EnumerateArray()
            .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static async Task<TranslationResult> TestAsync(string apiUrl, string apiKey, string model)
    {
        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            return new TranslationResult { Success = false, ErrorMessage = "请先填写 API URL", Provider = "AI Translation" };
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new TranslationResult { Success = false, ErrorMessage = "请先填写 API Key", Provider = "AI Translation" };
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return new TranslationResult { Success = false, ErrorMessage = "请先填写模型", Provider = "AI Translation" };
        }

        using var service = new AiTranslationService(
            SecureStorage.Encrypt(apiUrl.Trim()),
            SecureStorage.Encrypt(apiKey.Trim()),
            model.Trim());

        return await service.TranslateAsync("你好", "zh", "en");
    }

    private static string BuildModelsUrl(string apiUrl)
    {
        var uri = new Uri(apiUrl);
        var builder = new UriBuilder(uri);
        var path = builder.Path.TrimEnd('/');

        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^"/chat/completions".Length] + "/models";
        }
        else if (!path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            path += "/models";
        }

        builder.Path = path;
        builder.Query = string.Empty;
        return builder.Uri.ToString();
    }

    public async Task<TranslationResult> TranslateAsync(string text, string sourceLanguage, string targetLanguage)
    {
        if (!IsAvailable())
        {
            return new TranslationResult
            {
                Success = false,
                ErrorMessage = "AI API credentials not configured",
                Provider = "AI Translation"
            };
        }

        try
        {
            var targetLangName = GetLanguageName(targetLanguage);
            var prompt = sourceLanguage == "auto"
                ? $"Translate the following text to {targetLangName}. Return only the translation without any explanation:\n\n{text}"
                : $"Translate the following text from {GetLanguageName(sourceLanguage)} to {targetLangName}. Return only the translation without any explanation:\n\n{text}";

            // 构建 OpenAI 兼容请求
            var payload = new
            {
                model = _model,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = prompt
                    }
                },
                temperature = 0.3,
                max_tokens = 2000
            };

            var payloadJson = JsonSerializer.Serialize(payload);

            // 发送请求
            var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl)
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Authorization", $"Bearer {_apiKey}");

            var response = await _httpClient.SendAsync(request);
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return new TranslationResult
                {
                    Success = false,
                    ErrorMessage = $"API error: {response.StatusCode} - {responseJson}",
                    Provider = "AI Translation"
                };
            }

            // 解析响应
            var jsonDoc = JsonDocument.Parse(responseJson);
            var root = jsonDoc.RootElement;

            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var message = choices[0].GetProperty("message");
                var content = message.GetProperty("content").GetString() ?? "";

                return new TranslationResult
                {
                    Success = true,
                    SourceLanguage = sourceLanguage,
                    TargetLanguage = targetLanguage,
                    SourceText = text,
                    TranslatedText = content.Trim(),
                    Provider = "AI Translation"
                };
            }

            return new TranslationResult
            {
                Success = false,
                ErrorMessage = "Invalid response format",
                Provider = "AI Translation"
            };
        }
        catch (Exception ex)
        {
            return new TranslationResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Provider = "AI Translation"
            };
        }
    }

    private string GetLanguageName(string code)
    {
        return code.ToLower() switch
        {
            "zh" or "zh-cn" or "chinese" => "Chinese",
            "en" or "english" => "English",
            "ja" or "japanese" => "Japanese",
            "ko" or "korean" => "Korean",
            "fr" or "french" => "French",
            "de" or "german" => "German",
            "es" or "spanish" => "Spanish",
            "ru" or "russian" => "Russian",
            _ => code
        };
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
