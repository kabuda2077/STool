using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using STool.Core;

namespace STool.Modules.Translation;

/// <summary>
/// AI 翻译服务（OpenAI/Claude）
/// </summary>
public class AiTranslationService : ITranslationService
{
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
