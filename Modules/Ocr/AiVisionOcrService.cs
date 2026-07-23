using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using STool.Core;

namespace STool.Modules.Ocr;

/// <summary>
/// AI Vision OCR service for OpenAI-compatible chat completion APIs.
/// </summary>
public class AiVisionOcrService : IOcrService
{
    internal const string OcrPrompt = """
        You are an OCR engine. Extract all readable text from the image in natural reading order.

        Rules:
        - Return only the transcription. Do not add explanations, labels, or Markdown code fences.
        - Preserve the original language, wording, capitalization, punctuation, line breaks, and paragraph breaks.
        - Do not translate, summarize, rewrite, correct spelling, or invent missing text.
        - Preserve list and table structure with plain text when practical.
        - If some text is unclear, transcribe only the characters that are visibly supported.
        - If there is no readable text, return an empty response.
        """;

    private const int MaxOutputTokens = 4096;
    private const int MaxImageWidth = 4096;
    private const long MaxImagePixels = 24_000_000;
    private const int MaxErrorBodyLength = 1200;

    private readonly string _apiUrl;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly HttpClient _httpClient;

    public AiVisionOcrService(string apiUrlEncrypted, string apiKeyEncrypted, string model)
    {
        _apiUrl = SecureStorage.Decrypt(apiUrlEncrypted);
        _apiKey = SecureStorage.Decrypt(apiKeyEncrypted);
        _model = model;
        _httpClient = HttpDefaults.CreateClient();
    }

    public bool IsAvailable()
    {
        return !string.IsNullOrWhiteSpace(_apiUrl) &&
               !string.IsNullOrWhiteSpace(_apiKey) &&
               !string.IsNullOrWhiteSpace(_model);
    }

    public async Task<OcrResult> RecognizeAsync(Bitmap image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (!IsAvailable())
        {
            return Failure("AI OCR API 地址、密钥或模型未配置完整。");
        }

        try
        {
            var imageBase64 = EncodeImageAsPng(image);
            var payload = new
            {
                model = _model,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = OcrPrompt },
                            new
                            {
                                type = "image_url",
                                image_url = new
                                {
                                    url = $"data:image/png;base64,{imageBase64}"
                                }
                            }
                        }
                    }
                },
                max_tokens = MaxOutputTokens
            };

            var endpoints = AiApiEndpointResolver.ResolveChatCompletionCandidates(_apiUrl);
            var response = await AiApiRequestSender.SendAsync(
                _httpClient,
                endpoints,
                endpoint => CreateRequest(endpoint, payload),
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = Truncate(response.Body, MaxErrorBodyLength);
                return Failure($"AI OCR 请求失败 ({(int)response.StatusCode} {response.ReasonPhrase}): {errorBody}");
            }

            return ParseApiResponse(response.Body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure(ex.Message);
        }
    }

    private HttpRequestMessage CreateRequest(string endpoint, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        return request;
    }

    internal static OcrResult ParseApiResponse(string responseJson)
    {
        try
        {
            using var jsonDoc = JsonDocument.Parse(responseJson);
            var root = jsonDoc.RootElement;

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return Failure("AI OCR 返回格式无效：缺少 choices。");
            }

            var choice = choices[0];
            var finishReason = ReadString(choice, "finish_reason");
            if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
            {
                return Failure("AI OCR 输出达到模型长度上限，结果可能不完整。请缩小截图范围后重试。");
            }

            if (string.Equals(finishReason, "content_filter", StringComparison.OrdinalIgnoreCase))
            {
                return Failure("AI OCR 输出被内容安全策略拦截。");
            }

            if (!choice.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.Object)
            {
                return Failure("AI OCR 返回格式无效：缺少 message。");
            }

            var refusal = ReadString(message, "refusal");
            if (!string.IsNullOrWhiteSpace(refusal))
            {
                return Failure($"AI OCR 拒绝处理此图片：{refusal.Trim()}");
            }

            if (!message.TryGetProperty("content", out var contentElement))
            {
                return Failure("AI OCR 返回格式无效：缺少 content。");
            }

            var content = ReadMessageContent(contentElement).Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                return Failure("AI OCR 未识别到可读文字。");
            }

            return new OcrResult
            {
                Success = true,
                FullText = content,
                Provider = "AI Vision",
                TextBlocks = new List<OcrTextBlock>
                {
                    new()
                    {
                        Text = content,
                        Confidence = 1.0f
                    }
                }
            };
        }
        catch (JsonException ex)
        {
            return Failure($"AI OCR 返回内容不是有效 JSON：{ex.Message}");
        }
    }

    private static string EncodeImageAsPng(Bitmap image)
    {
        using var resized = ResizeIfNeeded(image);
        var imageToEncode = resized ?? image;
        using var stream = new MemoryStream();
        imageToEncode.Save(stream, ImageFormat.Png);
        return Convert.ToBase64String(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private static Bitmap? ResizeIfNeeded(Bitmap image)
    {
        var widthScale = image.Width > MaxImageWidth
            ? (double)MaxImageWidth / image.Width
            : 1.0;
        var pixelCount = (long)image.Width * image.Height;
        var pixelScale = pixelCount > MaxImagePixels
            ? Math.Sqrt((double)MaxImagePixels / pixelCount)
            : 1.0;
        var scale = Math.Min(widthScale, pixelScale);

        if (scale >= 1.0)
        {
            return null;
        }

        var targetWidth = Math.Max(1, (int)Math.Round(image.Width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(image.Height * scale));
        var resized = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
        if (image.HorizontalResolution > 0 && image.VerticalResolution > 0)
        {
            resized.SetResolution(image.HorizontalResolution, image.VerticalResolution);
        }

        using var graphics = Graphics.FromImage(resized);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.DrawImage(image, new Rectangle(0, 0, targetWidth, targetHeight));
        return resized;
    }

    private static string ReadMessageContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                var value = part.GetString();
                if (!string.IsNullOrEmpty(value))
                {
                    parts.Add(value);
                }

                continue;
            }

            if (part.ValueKind == JsonValueKind.Object)
            {
                var value = ReadString(part, "text");
                if (!string.IsNullOrEmpty(value))
                {
                    parts.Add(value);
                }
            }
        }

        return string.Join(Environment.NewLine, parts);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }

    private static OcrResult Failure(string message)
    {
        return new OcrResult
        {
            Success = false,
            ErrorMessage = message,
            Provider = "AI Vision"
        };
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
