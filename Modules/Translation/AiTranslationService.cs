using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using STool.Core;

namespace STool.Modules.Translation;

/// <summary>
/// AI 翻译服务（OpenAI 兼容 Chat Completions 接口）
/// </summary>
public class AiTranslationService : ITranslationService
{
    public const string OpenAiChatCompletionsUrl = "https://api.openai.com/v1/chat/completions";
    public const string GoogleAiStudioChatCompletionsUrl = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
    public const string DeepSeekChatCompletionsUrl = "https://api.deepseek.com/chat/completions";

    private const string ProviderName = "AI Translation";
    private const double Temperature = 0.3;

    private readonly string _apiUrl;
    private readonly string _apiKey;
    private readonly string _model;

    public AiTranslationService(string apiUrlEncrypted, string apiKeyEncrypted, string model)
        : this(new PlainCredentials(SecureStorage.Decrypt(apiUrlEncrypted), SecureStorage.Decrypt(apiKeyEncrypted), model))
    {
    }

    private AiTranslationService(PlainCredentials credentials)
    {
        _apiUrl = credentials.ApiUrl;
        _apiKey = credentials.ApiKey;
        _model = credentials.Model;
    }

    public bool IsAvailable()
    {
        return !string.IsNullOrWhiteSpace(_apiUrl) &&
               !string.IsNullOrWhiteSpace(_apiKey) &&
               !string.IsNullOrWhiteSpace(_model);
    }

    public static async Task<TranslationResult> TestAsync(string apiUrl, string apiKey, string model)
    {
        if (string.IsNullOrWhiteSpace(apiUrl))
            return Failure("请先填写 API URL");
        if (string.IsNullOrWhiteSpace(apiKey))
            return Failure("请先填写 API Key");
        if (string.IsNullOrWhiteSpace(model))
            return Failure("请先填写模型");

        using var service = new AiTranslationService(new PlainCredentials(apiUrl.Trim(), apiKey.Trim(), model.Trim()));
        return await service.TranslateAsync("你好", "zh", "en");
    }

    public async Task<TranslationResult> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable())
        {
            return Failure("AI 翻译的 API 地址、密钥或模型未配置完整。");
        }

        try
        {
            var targetLanguageName = LanguageCodes.ToEnglishName(targetLanguage);
            var prompt = sourceLanguage == "auto"
                ? $"Translate the following text to {targetLanguageName}. Return only the translation without any explanation:\n\n{text}"
                : $"Translate the following text from {LanguageCodes.ToEnglishName(sourceLanguage)} to {targetLanguageName}. Return only the translation without any explanation:\n\n{text}";

            var request = new ChatCompletionRequest(
                _model,
                OpenAiChatClient.UserMessage(prompt),
                EstimateMaxOutputTokens(text),
                Temperature);
            var result = await OpenAiChatClient.CompleteAsync(HttpDefaults.Shared, _apiUrl, _apiKey, request, cancellationToken);

            return result.Error switch
            {
                ChatCompletionError.None => new TranslationResult
                {
                    Success = true,
                    SourceLanguage = sourceLanguage,
                    TargetLanguage = targetLanguage,
                    SourceText = text,
                    TranslatedText = result.Content,
                    Provider = ProviderName
                },
                ChatCompletionError.Truncated => Failure("译文超出模型单次输出上限，结果不完整。请缩短文本后重试。"),
                ChatCompletionError.ContentFilter => Failure("内容被翻译服务的安全策略拦截。"),
                ChatCompletionError.Refusal => Failure($"模型拒绝翻译：{result.Detail}"),
                ChatCompletionError.Http => Failure(NetworkErrorMessages.FromStatus(result.StatusCode ?? HttpStatusCode.InternalServerError, result.Detail)),
                ChatCompletionError.Empty => Failure("翻译服务返回了空结果。"),
                _ => Failure("翻译服务返回格式不正确。")
            };
        }
        catch (Exception ex)
        {
            return Failure(NetworkErrorMessages.FromException(ex, cancellationToken));
        }
    }

    /// <summary>
    /// 按原文长度估算输出额度：中文与英文互译时译文 token 数通常不超过原文字符数的 2~3 倍，
    /// 下限保证短文本有余量，上限兼容只支持 4K 输出的模型。
    /// </summary>
    internal static int EstimateMaxOutputTokens(string text) =>
        Math.Clamp(text.Length * 3 + 256, 1024, 4096);

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

    private readonly record struct PlainCredentials(string ApiUrl, string ApiKey, string Model);
}
