using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using STool.Core;

namespace STool.Modules.Translation;

/// <summary>
/// 腾讯云机器翻译服务
/// </summary>
public class TencentTranslationService : ITranslationService
{
    private readonly string _secretId;
    private readonly string _secretKey;
    private readonly HttpClient _httpClient;
    private const string Endpoint = "tmt.tencentcloudapi.com";
    private const string Service = "tmt";
    private const string Version = "2018-03-21";
    private const string Action = "TextTranslate";

    public TencentTranslationService(string secretIdEncrypted, string secretKeyEncrypted)
    {
        _secretId = SecureStorage.Decrypt(secretIdEncrypted);
        _secretKey = SecureStorage.Decrypt(secretKeyEncrypted);
        _httpClient = new HttpClient();
    }

    public bool IsAvailable()
    {
        return !string.IsNullOrEmpty(_secretId) && !string.IsNullOrEmpty(_secretKey);
    }

    public async Task<TranslationResult> TranslateAsync(string text, string sourceLanguage, string targetLanguage)
    {
        if (!IsAvailable())
        {
            return new TranslationResult
            {
                Success = false,
                ErrorMessage = "Tencent Cloud credentials not configured",
                Provider = "Tencent Cloud"
            };
        }

        try
        {
            // 语言代码映射
            var source = MapLanguageCode(sourceLanguage);
            var target = MapLanguageCode(targetLanguage);

            // 构建请求
            var payload = new
            {
                SourceText = text,
                Source = source,
                Target = target,
                ProjectId = 0
            };

            var payloadJson = JsonSerializer.Serialize(payload);
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var date = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");

            // 计算签名
            var authorization = CalculateSignature(payloadJson, timestamp, date);

            // 发送请求
            var request = new HttpRequestMessage(HttpMethod.Post, $"https://{Endpoint}/")
            {
                Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
            };

            // 手动设置 Content-Type 为纯 application/json (移除 StringContent 自动添加的 charset)
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            // Authorization 含 "/" 等非 token 字符,.NET 的强校验会抛 FormatException;
            // 用 TryAddWithoutValidation 绕过校验,原样发送。
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
            request.Headers.TryAddWithoutValidation("X-TC-Action", Action);
            request.Headers.TryAddWithoutValidation("X-TC-Version", Version);
            request.Headers.TryAddWithoutValidation("X-TC-Timestamp", timestamp.ToString());
            request.Headers.TryAddWithoutValidation("X-TC-Region", "ap-guangzhou");

            var response = await _httpClient.SendAsync(request);
            var responseJson = await response.Content.ReadAsStringAsync();

            // 解析响应
            var jsonDoc = JsonDocument.Parse(responseJson);
            var root = jsonDoc.RootElement;

            if (root.TryGetProperty("Response", out var responseElement))
            {
                if (responseElement.TryGetProperty("Error", out var errorElement))
                {
                    var errorMessage = errorElement.GetProperty("Message").GetString();
                    return new TranslationResult
                    {
                        Success = false,
                        ErrorMessage = errorMessage,
                        Provider = "Tencent Cloud"
                    };
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
                    Provider = "Tencent Cloud"
                };
            }

            return new TranslationResult
            {
                Success = false,
                ErrorMessage = "Invalid response format",
                Provider = "Tencent Cloud"
            };
        }
        catch (Exception ex)
        {
            return new TranslationResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Provider = "Tencent Cloud"
            };
        }
    }

    private string MapLanguageCode(string code)
    {
        // 简化的语言代码映射
        return code.ToLower() switch
        {
            "auto" => "auto",
            "zh" or "zh-cn" or "chinese" => "zh",
            "en" or "english" => "en",
            "ja" or "japanese" => "ja",
            "ko" or "korean" => "ko",
            "fr" or "french" => "fr",
            "de" or "german" => "de",
            "es" or "spanish" => "es",
            "ru" or "russian" => "ru",
            _ => code
        };
    }

    private string CalculateSignature(string payload, long timestamp, string date)
    {
        var canonicalRequest = $"POST\n/\n\ncontent-type:application/json\nhost:{Endpoint}\n\ncontent-type;host\n{Sha256Hex(payload)}";
        var credentialScope = $"{date}/{Service}/tc3_request";
        var stringToSign = $"TC3-HMAC-SHA256\n{timestamp}\n{credentialScope}\n{Sha256Hex(canonicalRequest)}";

        var secretDate = HmacSha256(Encoding.UTF8.GetBytes($"TC3{_secretKey}"), Encoding.UTF8.GetBytes(date));
        var secretService = HmacSha256(secretDate, Encoding.UTF8.GetBytes(Service));
        var secretSigning = HmacSha256(secretService, Encoding.UTF8.GetBytes("tc3_request"));
        var signature = HmacSha256Hex(secretSigning, Encoding.UTF8.GetBytes(stringToSign));

        return $"TC3-HMAC-SHA256 Credential={_secretId}/{credentialScope}, SignedHeaders=content-type;host, Signature={signature}";
    }

    private static string Sha256Hex(string data)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(data));
        return BitConverter.ToString(bytes).Replace("-", "").ToLower();
    }

    private static byte[] HmacSha256(byte[] key, byte[] data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data);
    }

    private static string HmacSha256Hex(byte[] key, byte[] data)
    {
        var hash = HmacSha256(key, data);
        return BitConverter.ToString(hash).Replace("-", "").ToLower();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
