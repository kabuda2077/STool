using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
    private const string BatchAction = "TextTranslateBatch";

    /// <summary>TextTranslateBatch 单次请求所有文本总长须小于 6000 字符，留出余量。</summary>
    internal const int MaxBatchChars = 5500;

    /// <summary>默认频率限制 5 次/秒，组与组之间顺序请求并留出间隔。</summary>
    private static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(220);

    private readonly string _secretId;
    private readonly string _secretKey;

    public TencentTranslationService(string secretIdEncrypted, string secretKeyEncrypted)
        : this(SecureStorage.Decrypt(secretIdEncrypted), SecureStorage.Decrypt(secretKeyEncrypted), plain: true)
    {
    }

    private TencentTranslationService(string secretId, string secretKey, bool plain)
    {
        _secretId = secretId;
        _secretKey = secretKey;
    }

    /// <summary>用设置页里的明文凭据做一次翻译，验证账号是否可用。</summary>
    public static async Task<(bool Success, string Message)> TestAsync(string secretId, string secretKey)
    {
        if (string.IsNullOrWhiteSpace(secretId) || string.IsNullOrWhiteSpace(secretKey))
            return (false, "请先填写 SecretId 和 SecretKey");

        using var service = new TencentTranslationService(secretId.Trim(), secretKey.Trim(), plain: true);
        var result = await service.TranslateAsync("你好", "zh", "en");
        return result.Success
            ? (true, $"翻译结果：{result.TranslatedText}")
            : (false, result.ErrorMessage ?? "未知错误");
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

    public async Task<BatchTranslationResult> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable())
            return BatchTranslationResult.Failed("腾讯云凭据未配置完整。", ProviderName);
        if (texts.Count == 0)
            return BatchTranslationResult.Succeeded(Array.Empty<string>(), ProviderName);

        var source = LanguageCodes.ToTencent(sourceLanguage);
        var target = LanguageCodes.ToTencent(targetLanguage);
        var results = new List<string>(texts.Count);
        var groups = GroupForBatch(texts, MaxBatchChars);
        try
        {
            for (var g = 0; g < groups.Count; g++)
            {
                if (g > 0)
                    await Task.Delay(BatchInterval, cancellationToken);

                var group = groups[g];
                // 单段超过上限时改走单条接口（单条接口也有长度限制，由服务端返回错误）。
                if (group.Count == 1 && group[0].Length >= MaxBatchChars)
                {
                    var single = await TranslateAsync(group[0], sourceLanguage, targetLanguage, cancellationToken);
                    if (!single.Success)
                        return BatchTranslationResult.Failed(single.ErrorMessage, ProviderName);
                    results.Add(single.TranslatedText);
                    continue;
                }

                var payloadJson = JsonSerializer.Serialize(new
                {
                    Source = source,
                    Target = target,
                    ProjectId = 0,
                    SourceTextList = group
                });
                var (responseElement, error, document) = await SendAsync(BatchAction, payloadJson, cancellationToken);
                using (document)
                {
                    if (error != null)
                        return BatchTranslationResult.Failed(error, ProviderName);

                    if (!responseElement.TryGetProperty("TargetTextList", out var list) ||
                        list.ValueKind != JsonValueKind.Array ||
                        list.GetArrayLength() != group.Count)
                    {
                        return BatchTranslationResult.Failed("腾讯云批量翻译返回数量不一致。", ProviderName);
                    }

                    results.AddRange(list.EnumerateArray().Select(item => item.GetString() ?? string.Empty));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return BatchTranslationResult.Failed(NetworkErrorMessages.FromException(ex, cancellationToken), ProviderName);
        }

        return BatchTranslationResult.Succeeded(results, ProviderName);
    }

    /// <summary>按总字符数把文本分组，保持原顺序。</summary>
    internal static List<List<string>> GroupForBatch(IReadOnlyList<string> texts, int maxChars)
    {
        var groups = new List<List<string>>();
        var current = new List<string>();
        var currentChars = 0;
        foreach (var text in texts)
        {
            if (current.Count > 0 && currentChars + text.Length >= maxChars)
            {
                groups.Add(current);
                current = new List<string>();
                currentChars = 0;
            }

            current.Add(text);
            currentChars += text.Length;
        }

        if (current.Count > 0)
            groups.Add(current);
        return groups;
    }

    /// <summary>发送签名请求。成功时返回 Response 节点；document 由调用方释放。</summary>
    private async Task<(JsonElement Response, string? Error, JsonDocument? Document)> SendAsync(
        string action,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var request = TencentCloudSigner.CreateRequest(
            _secretId,
            _secretKey,
            Service,
            Endpoint,
            action,
            Version,
            payloadJson,
            DateTimeOffset.UtcNow);
        using var response = await HttpDefaults.Shared.SendAsync(request, cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        Log.Information("Tencent {Action} completed status={StatusCode} elapsedMs={ElapsedMs}",
            action, response.StatusCode, stopwatch.ElapsedMilliseconds);
        if (!response.IsSuccessStatusCode)
            return (default, NetworkErrorMessages.FromStatus(response.StatusCode, responseJson), null);

        var document = JsonDocument.Parse(responseJson);
        if (!document.RootElement.TryGetProperty("Response", out var responseElement))
        {
            document.Dispose();
            return (default, "腾讯云返回格式不正确。", null);
        }

        if (responseElement.TryGetProperty("Error", out var errorElement))
        {
            var message = errorElement.TryGetProperty("Message", out var m) ? m.GetString() : null;
            document.Dispose();
            return (default, $"腾讯云返回错误：{message}", null);
        }

        return (responseElement, null, document);
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
