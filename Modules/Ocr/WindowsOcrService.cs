using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using STool.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace STool.Modules.Ocr;

/// <summary>
/// Windows 内置 OCR 服务。
/// - 语言：auto 时按「界面语言 → 简体中文 → 英文 → 其他已安装语言」选择；结果几乎为空时换一种已安装语言再试一次。
/// - 尺寸：超过引擎上限时等比缩小；很小的截图先放大，小字识别率明显更高；坐标按比例换算回原图。
/// - 像素：直接拷贝 BGRA 像素生成 SoftwareBitmap，不经过图片编解码。
/// </summary>
public class WindowsOcrService : IOcrService
{
    private const string ProviderName = "Windows Local";

    /// <summary>短边低于该值时放大识别。</summary>
    internal const int UpscaleBelowHeight = 200;
    internal const double UpscaleFactor = 2.0;

    private static readonly ConcurrentDictionary<string, OcrEngine?> Engines = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _configuredLanguage;
    private readonly OcrEngine? _primaryEngine;

    public WindowsOcrService(string? language = "auto")
    {
        _configuredLanguage = string.IsNullOrWhiteSpace(language) ? "auto" : language.Trim();
        try
        {
            _primaryEngine = ResolveEngines(_configuredLanguage).FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to initialize Windows OCR engine");
            _primaryEngine = null;
        }
    }

    /// <summary>系统已安装的 OCR 识别语言（BCP-47 标签与显示名），供设置页列出。</summary>
    public static IReadOnlyList<(string Tag, string DisplayName)> GetAvailableLanguages()
    {
        try
        {
            return OcrEngine.AvailableRecognizerLanguages
                .Select(language => (language.LanguageTag, language.DisplayName))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to enumerate Windows OCR languages");
            return Array.Empty<(string, string)>();
        }
    }

    public bool IsAvailable() => _primaryEngine != null;

    public async Task<OcrResult> RecognizeAsync(Bitmap image, CancellationToken cancellationToken = default)
    {
        if (_primaryEngine == null)
            return Failure("Windows 本地 OCR 不可用：系统没有安装可用的 OCR 语言包。");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scale = ComputeScale(image.Width, image.Height, (int)OcrEngine.MaxImageDimension);
            using var softwareBitmap = CreateSoftwareBitmap(image, scale);

            var result = await RecognizeWithEngineAsync(_primaryEngine, softwareBitmap, scale, cancellationToken);

            // auto 模式下结果几乎为空：可能是语言不对（如日文截图用中文引擎），换一种已安装语言再试一次。
            if (_configuredLanguage == "auto" && CountMeaningfulChars(result.FullText) < 2)
            {
                var alternate = ResolveEngines("auto").Skip(1).FirstOrDefault();
                if (alternate != null)
                {
                    var retry = await RecognizeWithEngineAsync(alternate, softwareBitmap, scale, cancellationToken);
                    if (CountMeaningfulChars(retry.FullText) > CountMeaningfulChars(result.FullText))
                        result = retry;
                }
            }

            return result;
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

    private static async Task<OcrResult> RecognizeWithEngineAsync(
        OcrEngine engine,
        SoftwareBitmap bitmap,
        double scale,
        CancellationToken cancellationToken)
    {
        var ocrResult = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
        var result = new OcrResult { Success = true, Provider = ProviderName };
        var lines = new List<string>();

        foreach (var line in ocrResult.Lines)
        {
            var words = line.Words.Select(word => word.Text).ToList();
            lines.Add(JoinWords(words));
            foreach (var word in line.Words)
            {
                result.TextBlocks.Add(new OcrTextBlock
                {
                    Text = word.Text,
                    Confidence = 1.0f, // Windows OCR 不提供置信度
                    BoundingBox = ScaleBack(word.BoundingRect.X, word.BoundingRect.Y, word.BoundingRect.Width, word.BoundingRect.Height, scale)
                });
            }
        }

        result.FullText = string.Join("\n", lines);
        return result;
    }

    /// <summary>
    /// Windows OCR 把每个汉字当成一个词，line.Text 会在字与字之间插空格。
    /// 只在两侧都不是 CJK 字符时保留空格。
    /// </summary>
    internal static string JoinWords(IReadOnlyList<string> words)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            if (word.Length == 0)
                continue;

            if (sb.Length > 0)
            {
                var previous = sb[^1];
                if (!TextScript.IsCjk(previous) && !TextScript.IsCjk(word[0]) && !IsCjkPunctuation(previous) && !IsCjkPunctuation(word[0]))
                    sb.Append(' ');
            }

            sb.Append(word);
        }

        return sb.ToString();
    }

    private static bool IsCjkPunctuation(char ch) =>
        (ch >= 0x3000 && ch <= 0x303F) || (ch >= 0xFF00 && ch <= 0xFFEF);

    /// <summary>
    /// 计算识别时的缩放比例：超过引擎上限时缩小到上限内；高度很小时放大（放大后也不能超上限）。
    /// </summary>
    internal static double ComputeScale(int width, int height, int maxDimension)
    {
        if (width <= 0 || height <= 0)
            return 1.0;

        var longest = Math.Max(width, height);
        if (longest > maxDimension)
            return (double)maxDimension / longest;

        if (height < UpscaleBelowHeight)
        {
            var upscale = Math.Min(UpscaleFactor, (double)maxDimension / longest);
            return upscale > 1.0 ? upscale : 1.0;
        }

        return 1.0;
    }

    internal static Rectangle ScaleBack(double x, double y, double width, double height, double scale)
    {
        if (Math.Abs(scale - 1.0) < 0.0001)
            return new Rectangle((int)x, (int)y, (int)width, (int)height);

        var left = (int)Math.Floor(x / scale);
        var top = (int)Math.Floor(y / scale);
        var right = (int)Math.Ceiling((x + width) / scale);
        var bottom = (int)Math.Ceiling((y + height) / scale);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    /// <summary>按缩放比例生成 BGRA8 SoftwareBitmap，直接拷贝像素。</summary>
    private static SoftwareBitmap CreateSoftwareBitmap(Bitmap source, double scale)
    {
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        Bitmap? scaled = null;
        var bitmap = source;
        if (width != source.Width || height != source.Height || source.PixelFormat != PixelFormat.Format32bppArgb)
        {
            scaled = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(scaled);
            graphics.InterpolationMode = scale > 1.0 ? InterpolationMode.HighQualityBicubic : InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
            bitmap = scaled;
        }

        try
        {
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var stride = width * 4;
                var bytes = new byte[stride * height];
                for (var row = 0; row < height; row++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + row * data.Stride, bytes, row * stride, stride);
                }

                return SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    /// <summary>按优先顺序返回可用的引擎。</summary>
    private static IEnumerable<OcrEngine> ResolveEngines(string configuredLanguage)
    {
        var tags = new List<string>();
        if (!string.Equals(configuredLanguage, "auto", StringComparison.OrdinalIgnoreCase))
        {
            tags.Add(configuredLanguage);
        }
        else
        {
            tags.AddRange(Windows.System.UserProfile.GlobalizationPreferences.Languages);
            tags.Add("zh-Hans");
            tags.Add("en-US");
            tags.AddRange(OcrEngine.AvailableRecognizerLanguages.Select(language => language.LanguageTag));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            var engine = Engines.GetOrAdd(tag, CreateEngine);
            if (engine != null && seen.Add(engine.RecognizerLanguage.LanguageTag))
                yield return engine;
        }
    }

    private static OcrEngine? CreateEngine(string tag)
    {
        try
        {
            var language = new Language(tag);
            return OcrEngine.IsLanguageSupported(language) ? OcrEngine.TryCreateFromLanguage(language) : null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "OCR language {Tag} unavailable", tag);
            return null;
        }
    }

    private static int CountMeaningfulChars(string text) => text.Count(char.IsLetterOrDigit);

    private static OcrResult Failure(string message) => new()
    {
        Success = false,
        ErrorMessage = message,
        Provider = ProviderName
    };

    public void Dispose()
    {
        // 引擎按语言进程级缓存，无需释放。
    }
}
