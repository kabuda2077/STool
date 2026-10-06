using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Text.RegularExpressions;
using STool.Core;

namespace STool.Modules.Screenshot;

/// <summary>快速模式的内容过滤与背景色采样。</summary>
internal static partial class ScreenshotTextLayout
{
    /// <summary>判断一行 OCR 文本是否值得翻译：过滤时间戳、用户名、按钮、链接等界面元素。</summary>
    public static bool IsLikelyTranslatableContent(
        TranslationLine line,
        int cropWidth,
        int cropHeight,
        string targetLanguage)
    {
        var text = line.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var visualChars = CountVisualCharacters(text);
        if (visualChars <= 2)
            return false;

        if (IsUrlLike(text) ||
            IsTimestampLike(text) ||
            IsMostlySymbols(text) ||
            PureNumberRegex().IsMatch(text.Trim()) ||
            IsLikelyAccountMetadata(text) ||
            IsLikelyActionRow(text))
        {
            return false;
        }

        var topRatio = line.Box.Top / Math.Max(1.0, cropHeight);
        var bottomRatio = line.Box.Bottom / Math.Max(1.0, cropHeight);
        if ((topRatio < 0.04 || bottomRatio > 0.96) && visualChars <= 12)
            return false;

        if (!HasNaturalLanguageSignal(text))
            return false;

        if (!HasSourceLanguageSignal(text, targetLanguage))
            return false;

        if (visualChars <= 5 && !LooksLikeSentenceText(text))
            return false;

        if (IsLikelyShortControlLabel(text, line, cropWidth, cropHeight))
            return false;

        return true;
    }

    private static bool HasSourceLanguageSignal(string text, string targetLanguage)
    {
        var latin = text.Count(TextScript.IsLatinLetter);
        var cjk = text.Count(TextScript.IsCjk);

        return targetLanguage.ToLowerInvariant() switch
        {
            "zh" or "zh-cn" => latin >= 4 || text.Any(ch => TextScript.IsKana(ch) || TextScript.IsHangul(ch)),
            "en" => cjk >= 2,
            _ => latin >= 4 || cjk >= 2
        };
    }

    private static bool IsLikelyAccountMetadata(string text)
    {
        var normalized = text.Trim();
        if (LooksLikeSentenceText(normalized) && normalized.Any(ch => ch is ',' or '，' or ':' or '：' or ';' or '；'))
            return false;

        return AccountHandleRegex().IsMatch(normalized);
    }

    private static bool IsLikelyActionRow(string text)
    {
        var normalized = WhitespaceRegex().Replace(text.Trim(), " ");
        var matches = ActionWordRegex()
            .Matches(normalized)
            .Select(match => match.Value.ToLowerInvariant())
            .Distinct()
            .Count();

        return matches >= 2 ||
               (matches == 1 && CountVisualCharacters(normalized) <= 8);
    }

    private static int CountVisualCharacters(string text)
    {
        var count = 0;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
                continue;

            count += TextScript.IsCjk(ch) ? 2 : 1;
        }

        return Math.Max(1, count);
    }

    private static bool HasNaturalLanguageSignal(string text)
    {
        var letters = 0;
        var cjk = 0;
        var spaces = 0;

        foreach (var ch in text)
        {
            if (TextScript.IsCjk(ch))
                cjk++;
            else if (char.IsLetter(ch))
                letters++;
            else if (char.IsWhiteSpace(ch))
                spaces++;
        }

        return cjk >= 2 || letters >= 4 || (letters >= 2 && spaces > 0);
    }

    private static bool LooksLikeSentenceText(string text)
    {
        var normalized = text.Trim();
        if (normalized.Length == 0)
            return false;

        return normalized.Any(char.IsWhiteSpace) ||
               normalized.Any(TextScript.IsCjk) ||
               normalized.Any(ch => ch is '.' or ',' or '，' or '。' or '!' or '?' or '！' or '？' or ':' or '：' or ';' or '；');
    }

    private static bool IsUrlLike(string text)
    {
        return text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            || DomainRegex().IsMatch(text);
    }

    private static bool IsTimestampLike(string text)
    {
        var normalized = text.Trim();
        return ClockRegex().IsMatch(normalized)
            || ChineseAgoRegex().IsMatch(normalized)
            || EnglishDurationRegex().IsMatch(normalized);
    }

    private static bool IsMostlySymbols(string text)
    {
        var meaningful = text.Count(ch => char.IsLetterOrDigit(ch) || TextScript.IsCjk(ch));
        return meaningful <= Math.Max(1, text.Length / 3);
    }

    private static bool IsLikelyShortControlLabel(string text, TranslationLine line, int cropWidth, int cropHeight)
    {
        var visualChars = CountVisualCharacters(text);
        if (visualChars > 8)
            return false;

        // 底部 50% 且宽度不超 16% 且无句子特征 → 可能是按钮
        var bottomHalf = line.Box.Top > cropHeight * 0.52;
        return bottomHalf && line.Box.Width <= cropWidth * 0.16 && !LooksLikeSentenceText(text);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{2,30}(?:\s*[•·.]?\s*(?:\d+\s*)?(?:秒|分钟|小时|天|周|月|年|s|m|h|d|w|mo|y)?前?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex AccountHandleRegex();

    [GeneratedRegex(@"(?<![A-Za-z])(?:reply|award|share|more|like|comment|save|回复|奖励|分享|更多|点赞|评论|收藏)(?![A-Za-z])", RegexOptions.IgnoreCase)]
    private static partial Regex ActionWordRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\b[a-z0-9-]+\.(com|net|org|io|dev|app|cn)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DomainRegex();

    [GeneratedRegex(@"^\d{1,2}[:：]\d{2}$")]
    private static partial Regex ClockRegex();

    [GeneratedRegex(@"\b\d+\s*(秒|分钟|小时|天|周|月|年)前\b")]
    private static partial Regex ChineseAgoRegex();

    [GeneratedRegex(@"\b\d+\s*(s|sec|secs|min|mins|h|hr|hrs|hour|hours|d|day|days|w|week|weeks|mo|month|months|y|year|years)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EnglishDurationRegex();

    [GeneratedRegex(@"^[\d\s.,:%+\-]+$")]
    private static partial Regex PureNumberRegex();

    // ---------- 背景色 ----------

    /// <summary>
    /// 采样文字框周边一圈的背景色。外扩宽度按行高而不是整段高度计算，多行段落也只看文字附近；
    /// 大区域跨步采样，最多取 <see cref="MaxBackgroundSamples"/> 个像素。
    /// </summary>
    public static (Color Color, bool Uniform) SampleBackground(Bitmap bitmap, Rectangle box, int lineHeight)
    {
        var bitmapBounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var marginX = Math.Max(8, lineHeight);
        var marginY = Math.Max(5, lineHeight / 2);
        var outer = Rectangle.Intersect(Rectangle.Inflate(box, marginX, marginY), bitmapBounds);
        if (outer.Width <= 0 || outer.Height <= 0)
            return (Color.White, true);

        var center = Rectangle.Intersect(Rectangle.Inflate(box, 1, 1), bitmapBounds);
        var step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)outer.Width * outer.Height / MaxBackgroundSamples)));
        var samples = new List<int>(Math.Min(MaxBackgroundSamples * 2, outer.Width * outer.Height));

        var data = bitmap.LockBits(outer, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (var y = 0; y < outer.Height; y += step)
                {
                    var row = (byte*)data.Scan0 + (long)y * data.Stride;
                    for (var x = 0; x < outer.Width; x += step)
                    {
                        if (center.Contains(outer.X + x, outer.Y + y))
                            continue;

                        var pixel = row + x * 4;
                        samples.Add((pixel[2] << 16) | (pixel[1] << 8) | pixel[0]);
                    }
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return AnalyzeBackground(samples);
    }

    /// <summary>
    /// 从采样像素（0xRRGGBB）中取主色：亮色足够多时只在亮色里找（文字多为深色），量化分桶后取最多的一档；
    /// 与主色相近的样本占比高则认为背景是单色。
    /// </summary>
    internal static (Color Color, bool Uniform) AnalyzeBackground(IReadOnlyList<int> samples)
    {
        if (samples.Count == 0)
            return (Color.White, true);

        var lightCount = samples.Count(sample => Luminance(sample) >= LightLuminance);
        var useLightOnly = lightCount >= Math.Max(24, samples.Count / 5);

        const int quantizeStep = 10;
        var buckets = new Dictionary<int, (int Count, long R, long G, long B)>();
        foreach (var sample in samples)
        {
            if (useLightOnly && Luminance(sample) < LightLuminance)
                continue;

            var (r, g, b) = Channels(sample);
            var key = ((r / quantizeStep) << 16) | ((g / quantizeStep) << 8) | (b / quantizeStep);
            buckets.TryGetValue(key, out var bucket);
            buckets[key] = (bucket.Count + 1, bucket.R + r, bucket.G + g, bucket.B + b);
        }

        var best = buckets.Values
            .OrderByDescending(bucket => bucket.Count)
            .ThenByDescending(bucket => bucket.R + bucket.G + bucket.B)
            .First();
        var dominant = Color.FromArgb(
            (int)(best.R / best.Count),
            (int)(best.G / best.Count),
            (int)(best.B / best.Count));

        var near = samples.Count(sample =>
        {
            var (r, g, b) = Channels(sample);
            return Math.Abs(r - dominant.R) + Math.Abs(g - dominant.G) + Math.Abs(b - dominant.B) <= UniformColorDistance;
        });

        return (dominant, (double)near / samples.Count >= UniformRatio);
    }

    private static (int R, int G, int B) Channels(int sample) =>
        ((sample >> 16) & 0xFF, (sample >> 8) & 0xFF, sample & 0xFF);

    private static double Luminance(int sample)
    {
        var (r, g, b) = Channels(sample);
        return 0.299 * r + 0.587 * g + 0.114 * b;
    }
}
