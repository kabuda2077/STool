using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using STool.Core;

namespace STool.Modules.Ocr;

/// <summary>
/// 腾讯云 OCR 服务
/// </summary>
public class TencentOcrService : IOcrService
{
    private const string ProviderName = "Tencent Cloud";
    private const string Endpoint = "ocr.tencentcloudapi.com";
    private const string Service = "ocr";
    private const string Version = "2018-11-19";
    private const string Action = "GeneralBasicOCR";

    private readonly string _secretId;
    private readonly string _secretKey;

    public TencentOcrService(string secretIdEncrypted, string secretKeyEncrypted)
    {
        _secretId = SecureStorage.Decrypt(secretIdEncrypted);
        _secretKey = SecureStorage.Decrypt(secretKeyEncrypted);
    }

    public bool IsAvailable()
    {
        return !string.IsNullOrEmpty(_secretId) && !string.IsNullOrEmpty(_secretKey);
    }

    public async Task<OcrResult> RecognizeAsync(Bitmap image, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable())
        {
            return Failure("腾讯云凭据未配置完整。");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            string imageBase64;
            using (var ms = new MemoryStream())
            {
                image.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                imageBase64 = Convert.ToBase64String(ms.GetBuffer(), 0, checked((int)ms.Length));
            }

            var payloadJson = JsonSerializer.Serialize(new
            {
                ImageBase64 = imageBase64,
                LanguageType = "auto"
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
            Log.Information("Tencent OCR completed status={StatusCode} elapsedMs={ElapsedMs}", response.StatusCode, stopwatch.ElapsedMilliseconds);
            if (!response.IsSuccessStatusCode)
            {
                return Failure(NetworkErrorMessages.FromStatus(response.StatusCode, responseJson));
            }

            using var jsonDoc = JsonDocument.Parse(responseJson);
            var root = jsonDoc.RootElement;

            if (!root.TryGetProperty("Response", out var responseElement))
            {
                return Failure("腾讯云返回格式不正确。");
            }

            if (responseElement.TryGetProperty("Error", out var errorElement))
            {
                var errorMessage = errorElement.GetProperty("Message").GetString();
                return Failure($"腾讯云返回错误：{errorMessage}");
            }

            var result = new OcrResult
            {
                Success = true,
                Provider = ProviderName
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
        catch (Exception ex)
        {
            return Failure(NetworkErrorMessages.FromException(ex, cancellationToken));
        }
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
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
            return false;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value))
            return true;

        if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out value))
            return true;

        return false;
    }

    private static OcrResult Failure(string message) => new()
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
