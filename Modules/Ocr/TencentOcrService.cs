using System;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
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
        _httpClient = HttpDefaults.CreateClient();
    }

    public bool IsAvailable()
    {
        return !string.IsNullOrEmpty(_secretId) && !string.IsNullOrEmpty(_secretKey);
    }

    public async Task<OcrResult> RecognizeAsync(Bitmap image, CancellationToken cancellationToken = default)
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

            // 手动设置 Content-Type 为纯 application/json (移除 StringContent 自动添加的 charset)
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            // Authorization 含 "/" 等非 token 字符,.NET 的强校验会抛 FormatException;
            // 用 TryAddWithoutValidation 绕过校验,原样发送。
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
            request.Headers.TryAddWithoutValidation("X-TC-Action", Action);
            request.Headers.TryAddWithoutValidation("X-TC-Version", Version);
            request.Headers.TryAddWithoutValidation("X-TC-Timestamp", timestamp.ToString());
            request.Headers.TryAddWithoutValidation("X-TC-Region", "ap-guangzhou");

            var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

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
                            Confidence = confidence,
                            BoundingBox = TryReadBoundingBox(detection)
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

    private static System.Drawing.Rectangle TryReadBoundingBox(JsonElement detection)
    {
        if (detection.TryGetProperty("Polygon", out var polygon) &&
            TryReadPolygonBounds(polygon, out var polygonBounds))
        {
            return polygonBounds;
        }

        if (detection.TryGetProperty("ItemPolygon", out var itemPolygon))
        {
            if (TryReadPolygonBounds(itemPolygon, out var itemPolygonBounds))
                return itemPolygonBounds;

            if (TryReadRectBounds(itemPolygon, out var rectBounds))
                return rectBounds;
        }

        return System.Drawing.Rectangle.Empty;
    }

    private static bool TryReadPolygonBounds(JsonElement element, out System.Drawing.Rectangle bounds)
    {
        bounds = System.Drawing.Rectangle.Empty;
        if (element.ValueKind != JsonValueKind.Array)
            return false;

        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;
        var count = 0;

        foreach (var point in element.EnumerateArray())
        {
            if (!TryReadInt(point, "X", out var x) || !TryReadInt(point, "Y", out var y))
                continue;

            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
            count++;
        }

        if (count == 0 || maxX <= minX || maxY <= minY)
            return false;

        bounds = new System.Drawing.Rectangle(minX, minY, maxX - minX, maxY - minY);
        return true;
    }

    private static bool TryReadRectBounds(JsonElement element, out System.Drawing.Rectangle bounds)
    {
        bounds = System.Drawing.Rectangle.Empty;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (!TryReadInt(element, "X", out var x) ||
            !TryReadInt(element, "Y", out var y) ||
            !TryReadInt(element, "Width", out var width) ||
            !TryReadInt(element, "Height", out var height) ||
            width <= 0 ||
            height <= 0)
        {
            return false;
        }

        bounds = new System.Drawing.Rectangle(x, y, width, height);
        return true;
    }

    private static bool TryReadInt(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var property))
            return false;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value))
            return true;

        if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out value))
            return true;

        return false;
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
