using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using STool.Core;

namespace STool.Modules.Translation;

/// <summary>
/// 腾讯云机器翻译服务
/// </summary>
public class TencentTranslationService : ITranslationService
{
    private const string ProviderName = "Tencent Cloud";
    private const string Endpoint = "tmt.tencentcloudapi.com";
    private const string Service = "tmt";
    private const string Version = "2018-03-21";
    private const string Action = "TextTranslate";

    private readonly string _secretId;
    private readonly string _secretKey;

    public TencentTranslationService(string secretIdEncrypted, string secretKeyEncrypted)
    {
        _secretId = SecureStorage.Decrypt(secretIdEncrypted);
        _secretKey = SecureStorage.Decrypt(secretKeyEncrypted);
    }

    public bool IsAvailable()
    {
        return !string.IsNullOrEmpty(_secretId) && !string.IsNullOrEmpty(_secretKey);
    }

    public async Task<TranslationResult> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable())
        {
            return Failure("腾讯云凭据未配置完整。");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var source = LanguageCodes.ToTencent(sourceLanguage);
            var target = LanguageCodes.ToTencent(targetLanguage);
            var payloadJson = JsonSerializer.Serialize(new
            {
                SourceText = text,
                Source = source,
                Target = target,
                ProjectId = 0
            });

            using var request = TencentCloudSigner.CreateRequest(
                _secretId,
                _secretKey,
                Service,
                Endpoint,
                Action,
                Version,
                payloadJson,
                DateTimeOffset.UtcNow);
            using var response = await HttpDefaults.Shared.SendAsync(request, cancellationToken);
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            Log.Information("Tencent translation completed status={StatusCode} elapsedMs={ElapsedMs}", response.StatusCode, stopwatch.ElapsedMilliseconds);
            if (!response.IsSuccessStatusCode)
            {
                return Failure(NetworkErrorMessages.FromStatus(response.StatusCode, responseJson));
            }

            using var jsonDoc = JsonDocument.Parse(responseJson);
            if (!jsonDoc.RootElement.TryGetProperty("Response", out var responseElement))
            {
                return Failure("腾讯云返回格式不正确。");
            }

            if (responseElement.TryGetProperty("Error", out var errorElement))
            {
                var errorMessage = errorElement.GetProperty("Message").GetString();
                return Failure($"腾讯云返回错误：{errorMessage}");
            }

            var translatedText = responseElement.GetProperty("TargetText").GetString() ?? "";
            var detectedSource = responseElement.TryGetProperty("Source", out var sourceElement)
                ? sourceElement.GetString() ?? source
                : source;

            return new TranslationResult
            {
                Success = true,
                SourceLanguage = detectedSource,
                TargetLanguage = target,
                SourceText = text,
                TranslatedText = translatedText,
                Provider = ProviderName
            };
        }
        catch (Exception ex)
        {
            return Failure(NetworkErrorMessages.FromException(ex, cancellationToken));
        }
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
}
