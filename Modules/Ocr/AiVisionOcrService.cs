using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Net;
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

    private const string ProviderName = "AI Vision";
    private const string SampleText = "STool OCR 123";
    private const int MaxOutputTokens = 4096;
    private const int MaxImageWidth = 4096;
    private const long MaxImagePixels = 24_000_000;
    private const int MaxErrorBodyLength = 1200;

    private readonly string _apiUrl;
    private readonly string _apiKey;
    private readonly string _model;

    public AiVisionOcrService(string apiUrlEncrypted, string apiKeyEncrypted, string model)
        : this(new PlainCredentials(SecureStorage.Decrypt(apiUrlEncrypted), SecureStorage.Decrypt(apiKeyEncrypted), model))
    {
    }

    private AiVisionOcrService(PlainCredentials credentials)
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

    /// <summary>用一张生成的示例图片验证设置页填写的接口是否可用。</summary>
    public static async Task<(bool Success, string Message)> TestAsync(
        string apiUrl,
        string apiKey,
        string model,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiUrl))
            return (false, "请先填写 API URL");
        if (string.IsNullOrWhiteSpace(apiKey))
            return (false, "请先填写 API Key");
        if (string.IsNullOrWhiteSpace(model))
            return (false, "请先填写模型");

        using var service = new AiVisionOcrService(new PlainCredentials(apiUrl.Trim(), apiKey.Trim(), model.Trim()));
        using var sample = CreateSampleImage();
        var result = await service.RecognizeAsync(sample, cancellationToken);
        return result.Success
            ? (true, $"识别结果：{result.FullText.ReplaceLineEndings(" ")}")
            : (false, result.ErrorMessage ?? "未知错误");
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
            var imageBase64 = await Task.Run(() => EncodeImageAsPng(image), cancellationToken).ConfigureAwait(false);
            var request = new ChatCompletionRequest(
                _model,
                OpenAiChatClient.UserMessageWithImage(OcrPrompt, $"data:image/png;base64,{imageBase64}"),
                MaxOutputTokens);
            var result = await OpenAiChatClient.CompleteAsync(
                HttpDefaults.Shared,
                _apiUrl,
                _apiKey,
                request,
                cancellationToken);
            return ToOcrResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure(NetworkErrorMessages.FromException(ex, cancellationToken));
        }
    }

    internal static OcrResult ParseApiResponse(string responseJson) =>
        ToOcrResult(OpenAiChatClient.ParseResponse(responseJson));

    private static OcrResult ToOcrResult(ChatCompletionResult result) => result.Error switch
    {
        ChatCompletionError.None => new OcrResult
        {
            Success = true,
            FullText = result.Content,
            Provider = ProviderName,
            TextBlocks = new List<OcrTextBlock>
            {
                new()
                {
                    Text = result.Content,
                    Confidence = 1.0f
                }
            }
        },
        ChatCompletionError.Http => Failure(
            $"AI OCR 请求失败：{NetworkErrorMessages.FromStatus(result.StatusCode ?? HttpStatusCode.InternalServerError, Truncate(result.Detail ?? string.Empty, MaxErrorBodyLength))}"),
        ChatCompletionError.InvalidJson => Failure($"AI OCR 返回内容不是有效 JSON：{result.Detail}"),
        ChatCompletionError.MissingChoices => Failure("AI OCR 返回格式无效：缺少 choices。"),
        ChatCompletionError.Truncated => Failure("AI OCR 输出达到模型长度上限，结果可能不完整。请缩小截图范围后重试。"),
        ChatCompletionError.ContentFilter => Failure("AI OCR 输出被内容安全策略拦截。"),
        ChatCompletionError.MissingMessage => Failure("AI OCR 返回格式无效：缺少 message。"),
        ChatCompletionError.Refusal => Failure($"AI OCR 拒绝处理此图片：{result.Detail}"),
        ChatCompletionError.MissingContent => Failure("AI OCR 返回格式无效：缺少 content。"),
        _ => Failure("AI OCR 未识别到可读文字。")
    };

    private static Bitmap CreateSampleImage()
    {
        var bitmap = new Bitmap(360, 96, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Microsoft YaHei UI", 30, FontStyle.Regular, GraphicsUnit.Pixel);
        graphics.DrawString(SampleText, font, Brushes.Black, new PointF(18, 26));
        return bitmap;
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

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private static OcrResult Failure(string message)
    {
        return new OcrResult
        {
            Success = false,
            ErrorMessage = message,
            Provider = ProviderName
        };
    }

    public void Dispose()
    {
        // 使用进程级共享 HttpClient，无需释放。
    }

    private readonly record struct PlainCredentials(string ApiUrl, string ApiKey, string Model);
}
