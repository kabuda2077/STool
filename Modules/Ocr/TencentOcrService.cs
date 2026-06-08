using System;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using STool.Core;

namespace STool.Modules.Ocr;

/// <summary>
/// 腾讯云 OCR 服务
/// </summary>
public class TencentOcrService : IOcrService
{
    private readonly string _secretId;
    private readonly string _secretKey;
    private readonly HttpClient _httpClient;
    private const string Endpoint = "ocr.tencentcloudapi.com";
    private const string Service = "ocr";
    private const string Version = "2018-11-19";
    private const string Action = "GeneralBasicOCR";

    public TencentOcrService(string secretIdEncrypted, string secretKeyEncrypted)
    {
        _secretId = SecureStorage.Decrypt(secretIdEncrypted);
        _secretKey = SecureStorage.Decrypt(secretKeyEncrypted);
        _httpClient = new HttpClient();
    }

    public bool IsAvailable()
    {
        return !string.IsNullOrEmpty(_secretId) && !string.IsNullOrEmpty(_secretKey);
    }

    public async Task<OcrResult> RecognizeAsync(Bitmap image)
    {
        if (!IsAvailable())
        {
            return new OcrResult
            {
                Success = false,
                ErrorMessage = "Tencent Cloud credentials not configured",
                Provider = "Tencent Cloud"
            };
        }

        try
        {
            // 转换图片为 Base64
            string imageBase64;
            using (var ms = new MemoryStream())
            {
                image.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                imageBase64 = Convert.ToBase64String(ms.ToArray());
            }

            // 构建请求
            var payload = new
            {
                ImageBase64 = imageBase64,
                LanguageType = "auto"
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

            request.Headers.Add("Authorization", authorization);
            request.Headers.Add("X-TC-Action", Action);
            request.Headers.Add("X-TC-Version", Version);
            request.Headers.Add("X-TC-Timestamp", timestamp.ToString());
            request.Headers.Add("X-TC-Region", "ap-guangzhou");

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
                    return new OcrResult
                    {
                        Success = false,
                        ErrorMessage = errorMessage,
                        Provider = "Tencent Cloud"
                    };
                }

                var result = new OcrResult
                {
                    Success = true,
                    Provider = "Tencent Cloud"
                };

                if (responseElement.TryGetProperty("TextDetections", out var textDetections))
                {
                    var textLines = new System.Collections.Generic.List<string>();

                    foreach (var detection in textDetections.EnumerateArray())
                    {
                        var text = detection.GetProperty("DetectedText").GetString() ?? "";
                        var confidence = detection.GetProperty("Confidence").GetInt32() / 100f;

                        textLines.Add(text);

                        result.TextBlocks.Add(new OcrTextBlock
                        {
                            Text = text,
                            Confidence = confidence
                        });
                    }

                    result.FullText = string.Join("\n", textLines);
                }

                return result;
            }

            return new OcrResult
            {
                Success = false,
                ErrorMessage = "Invalid response format",
                Provider = "Tencent Cloud"
            };
        }
        catch (Exception ex)
        {
            return new OcrResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Provider = "Tencent Cloud"
            };
        }
    }

    private string CalculateSignature(string payload, long timestamp, string date)
    {
        // 腾讯云签名算法 V3
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
