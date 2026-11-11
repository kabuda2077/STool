using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;

namespace STool.Modules.Screenshot;

internal enum TranslationTextAlignment
{
    Left,
    Center,
    Right
}

/// <summary>一个译文块的排版参数（单位：截图物理像素）。</summary>
internal sealed record TranslationBlockStyle(
    Rectangle Box,
    Rectangle MaxBox,
    Color TextColor,
    bool Bold,
    double FontPx,
    double LinePitchPx,
    TranslationTextAlignment Alignment);

/// <summary>原位翻译的渲染准备结果：擦除原文后的底图和每块的样式。</summary>
internal sealed class TranslationRenderPlan : IDisposable
{
    public TranslationRenderPlan(Bitmap background, IReadOnlyList<TranslationBlockStyle> blocks)
    {
        Background = background;
        Blocks = blocks;
    }

    public Bitmap Background { get; }
    public IReadOnlyList<TranslationBlockStyle> Blocks { get; }

    public void Dispose() => Background.Dispose();
}

/// <summary>
/// 原位翻译的图像处理，不依赖 UI，可在后台线程运行：
/// <list type="bullet">
/// <item>擦除：按行用边界像素插值（Coons 曲面）估计背景，与估计值差异大的像素视为文字笔画，
/// 外扩 1–2px 覆盖抗锯齿边缘后，用估计的背景填充。纯色、渐变背景几乎看不出痕迹，不再盖色块。</item>
/// <item>样式：文字颜色取笔画像素里与背景差异最大的一批的均值；粗细按笔画平均宽度判断；
/// 对齐按段内各行的左缘、中心、右缘的离散程度判断；字号按行高估算并在整张图内分档统一。</item>
/// <item>扩展区域：译文放不下时可向右、向下扩展（最多 1.5 倍），不碰到其他文字、扩展区域背景须是单色。</item>
/// </list>
/// </summary>
internal static class ScreenshotTranslationRenderer
{
    /// <summary>与估计背景的通道差之和超过该值即视为笔画像素。</summary>
    internal const int InkThreshold = 72;

    /// <summary>OCR 行高到字号的换算系数（行框通常包含上下伸部）。</summary>
    internal const double FontPerLineHeight = 0.78;

    /// <summary>同一档字号的相对容差。</summary>
    internal const double FontTierTolerance = 0.08;

    internal const double MaxExpandRatio = 1.5;

    public static TranslationRenderPlan Prepare(
        Bitmap crop,
        IReadOnlyList<TranslationParagraph> paragraphs,
        IReadOnlyList<TranslationLine> allLines)
    {
        var background = crop.Clone(new Rectangle(0, 0, crop.Width, crop.Height), PixelFormat.Format32bppArgb);
        var styles = new List<TranslationBlockStyle>(paragraphs.Count);
        var rawFontSizes = new List<double>(paragraphs.Count);
        var bounds = new Rectangle(0, 0, crop.Width, crop.Height);

        // 先在原图上计算扩展上限（需要原始背景判断单色），再擦除。
        var maxBoxes = paragraphs
            .Select(paragraph => ComputeMaxBox(crop, paragraph, allLines))
            .ToList();

        var data = background.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new PixelBuffer(data);
            for (var i = 0; i < paragraphs.Count; i++)
            {
                var paragraph = paragraphs[i];
                var lines = paragraph.Lines.Count > 0
                    ? paragraph.Lines
                    : new[] { new TranslationLine(paragraph.Text, paragraph.Box) };

                var inkStats = new InkStats();
                foreach (var line in lines)
                    EraseLine(pixels, line.Box, inkStats);

                rawFontSizes.Add(paragraph.MedianLineHeight * FontPerLineHeight);
                styles.Add(new TranslationBlockStyle(
                    paragraph.Box,
                    maxBoxes[i],
                    inkStats.TextColor(),
                    inkStats.IsBold(paragraph.MedianLineHeight),
                    0,
                    LinePitch(lines, paragraph.MedianLineHeight),
                    DetectAlignment(lines, crop.Width)));
            }
        }
        finally
        {
            background.UnlockBits(data);
        }

        var tiers = TierFontSizes(rawFontSizes);
        for (var i = 0; i < styles.Count; i++)
            styles[i] = styles[i] with { FontPx = tiers[i] };

        return new TranslationRenderPlan(background, styles);
    }

    // ---------- 擦除 ----------

    private static void EraseLine(PixelBuffer pixels, Rectangle lineBox, InkStats stats)
    {
        var margin = Math.Max(2, lineBox.Height / 8);
        var area = Rectangle.Intersect(Rectangle.Inflate(lineBox, margin, margin), new Rectangle(0, 0, pixels.Width, pixels.Height));
        if (area.Width < 3 || area.Height < 3)
            return;

        var estimate = EstimateBackground(pixels, area);
        var w = area.Width;
        var h = area.Height;
        var mask = new bool[w * h];

        for (var y = 1; y < h - 1; y++)
        {
            for (var x = 1; x < w - 1; x++)
            {
                var actual = pixels.Get(area.X + x, area.Y + y);
                var expected = estimate[y * w + x];
                var distance = Distance(actual, expected);
                if (distance <= InkThreshold)
                    continue;

                mask[y * w + x] = true;
                stats.Add(actual, distance);
            }
        }

        stats.AddRuns(mask, w, h);

        var dilate = lineBox.Height >= 28 ? 2 : 1;
        var dilated = Dilate(mask, w, h, dilate);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (dilated[y * w + x])
                    pixels.Set(area.X + x, area.Y + y, estimate[y * w + x]);
            }
        }
    }

    /// <summary>
    /// 用区域四条边界（每点取邻近 5 像素的中位数，抑制压在边上的笔画）做 Coons 曲面插值，
    /// 得到区域内每个像素的背景估计。纯色时等于背景色，线性渐变时精确还原。
    /// </summary>
    internal static int[] EstimateBackground(PixelBuffer pixels, Rectangle area)
    {
        var w = area.Width;
        var h = area.Height;
        var top = new int[w];
        var bottom = new int[w];
        var left = new int[h];
        var right = new int[h];
        for (var x = 0; x < w; x++)
        {
            top[x] = MedianAlong(pixels, area, x, 0, horizontal: true);
            bottom[x] = MedianAlong(pixels, area, x, h - 1, horizontal: true);
        }

        for (var y = 0; y < h; y++)
        {
            left[y] = MedianAlong(pixels, area, 0, y, horizontal: false);
            right[y] = MedianAlong(pixels, area, w - 1, y, horizontal: false);
        }

        var result = new int[w * h];
        var tl = top[0];
        var tr = top[w - 1];
        var bl = bottom[0];
        var br = bottom[w - 1];
        for (var y = 0; y < h; y++)
        {
            var v = h == 1 ? 0 : (double)y / (h - 1);
            for (var x = 0; x < w; x++)
            {
                var u = w == 1 ? 0 : (double)x / (w - 1);
                result[y * w + x] = Coons(u, v, left[y], right[y], top[x], bottom[x], tl, tr, bl, br);
            }
        }

        return result;
    }

    private static int Coons(double u, double v, int l, int r, int t, int b, int tl, int tr, int bl, int br)
    {
        int Channel(int shift)
        {
            double C(int value) => (value >> shift) & 0xFF;
            var value =
                (1 - u) * C(l) + u * C(r) + (1 - v) * C(t) + v * C(b)
                - ((1 - u) * (1 - v) * C(tl) + u * (1 - v) * C(tr) + (1 - u) * v * C(bl) + u * v * C(br));
            return Math.Clamp((int)Math.Round(value), 0, 255);
        }

        return (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }

    private static int MedianAlong(PixelBuffer pixels, Rectangle area, int x, int y, bool horizontal)
    {
        Span<int> r = stackalloc int[5];
        Span<int> g = stackalloc int[5];
        Span<int> b = stackalloc int[5];
        var count = 0;
        for (var d = -2; d <= 2; d++)
        {
            var px = area.X + (horizontal ? x + d : x);
            var py = area.Y + (horizontal ? y : y + d);
            if (px < area.Left || px >= area.Right || py < area.Top || py >= area.Bottom)
                continue;
            var value = pixels.Get(px, py);
            r[count] = (value >> 16) & 0xFF;
            g[count] = (value >> 8) & 0xFF;
            b[count] = value & 0xFF;
            count++;
        }

        r[..count].Sort();
        g[..count].Sort();
        b[..count].Sort();
        var mid = count / 2;
        return (r[mid] << 16) | (g[mid] << 8) | b[mid];
    }

    private static bool[] Dilate(bool[] mask, int w, int h, int radius)
    {
        var result = new bool[mask.Length];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (!mask[y * w + x])
                    continue;
                for (var dy = -radius; dy <= radius; dy++)
                {
                    var ny = y + dy;
                    if (ny < 0 || ny >= h)
                        continue;
                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        var nx = x + dx;
                        if (nx >= 0 && nx < w)
                            result[ny * w + nx] = true;
                    }
                }
            }
        }

        return result;
    }

    internal static int Distance(int a, int b) =>
        Math.Abs(((a >> 16) & 0xFF) - ((b >> 16) & 0xFF)) +
        Math.Abs(((a >> 8) & 0xFF) - ((b >> 8) & 0xFF)) +
        Math.Abs((a & 0xFF) - (b & 0xFF));

    // ---------- 样式 ----------

    /// <summary>段内各行的左缘、中心、右缘中离散最小者决定对齐；单行只在明显居中时判为居中。</summary>
    internal static TranslationTextAlignment DetectAlignment(IReadOnlyList<TranslationLine> lines, int cropWidth)
    {
        if (lines.Count == 0)
            return TranslationTextAlignment.Left;

        var height = Math.Max(1, lines.Select(line => line.Box.Height).Max());
        if (lines.Count == 1)
        {
            var box = lines[0].Box;
            var center = box.Left + box.Width / 2.0;
            var centered = Math.Abs(center - cropWidth / 2.0) <= Math.Max(4, cropWidth * 0.02) &&
                           box.Width <= cropWidth * 0.7;
            return centered ? TranslationTextAlignment.Center : TranslationTextAlignment.Left;
        }

        var lefts = Spread(lines.Select(line => (double)line.Box.Left));
        var centers = Spread(lines.Select(line => line.Box.Left + line.Box.Width / 2.0));
        var rights = Spread(lines.Select(line => (double)line.Box.Right));
        var tolerance = height * 0.5;

        if (lefts <= tolerance && lefts <= centers)
            return TranslationTextAlignment.Left;
        if (centers <= tolerance && centers < lefts)
            return TranslationTextAlignment.Center;
        if (rights <= tolerance && rights < lefts)
            return TranslationTextAlignment.Right;
        return TranslationTextAlignment.Left;
    }

    private static double Spread(IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Max() - list.Min();
    }

    /// <summary>行距：相邻行顶部间距的中位数；单行时按行高的 1.25 倍。</summary>
    private static double LinePitch(IReadOnlyList<TranslationLine> lines, int medianLineHeight)
    {
        if (lines.Count < 2)
            return medianLineHeight * 1.25;

        var gaps = new List<int>();
        for (var i = 1; i < lines.Count; i++)
            gaps.Add(lines[i].Box.Top - lines[i - 1].Box.Top);
        gaps.Sort();
        var median = gaps[gaps.Count / 2];
        return median > medianLineHeight * 0.8 ? median : medianLineHeight * 1.25;
    }

    /// <summary>把相差不超过 <see cref="FontTierTolerance"/> 的字号归为一档，统一取该档的均值。</summary>
    internal static IReadOnlyList<double> TierFontSizes(IReadOnlyList<double> sizes)
    {
        var order = sizes.Select((size, index) => (size, index)).OrderBy(item => item.size).ToList();
        var result = new double[sizes.Count];
        var start = 0;
        while (start < order.Count)
        {
            var end = start;
            while (end + 1 < order.Count && order[end + 1].size <= order[start].size * (1 + FontTierTolerance * 2))
                end++;
            var mean = 0.0;
            for (var i = start; i <= end; i++)
                mean += order[i].size;
            mean /= end - start + 1;
            for (var i = start; i <= end; i++)
                result[order[i].index] = mean;
            start = end + 1;
        }

        return result;
    }

    // ---------- 可扩展区域 ----------

    /// <summary>
    /// 译文放不下时允许占用的最大区域：向右、向下扩展，不超过原尺寸的 1.5 倍，
    /// 不碰到其他识别出的文字，扩展部分的背景须为单色。
    /// </summary>
    internal static Rectangle ComputeMaxBox(Bitmap crop, TranslationParagraph paragraph, IReadOnlyList<TranslationLine> allLines)
    {
        var box = paragraph.Box;
        var own = new HashSet<TranslationLine>(paragraph.Lines, ReferenceEqualityComparer.Instance);
        var others = allLines.Where(line => !own.Contains(line) && !box.Contains(line.Box)).Select(line => line.Box).ToList();
        var gap = Math.Max(2, paragraph.MedianLineHeight / 4);

        var maxRight = Math.Min(crop.Width, box.Left + (int)(box.Width * MaxExpandRatio));
        foreach (var other in others)
        {
            var overlapsVertically = other.Top < box.Bottom && other.Bottom > box.Top;
            if (overlapsVertically && other.Left >= box.Right)
                maxRight = Math.Min(maxRight, other.Left - gap);
        }

        var maxBottom = Math.Min(crop.Height, box.Top + (int)(box.Height * MaxExpandRatio) + paragraph.MedianLineHeight);
        foreach (var other in others)
        {
            var overlapsHorizontally = other.Left < maxRight && other.Right > box.Left;
            if (overlapsHorizontally && other.Top >= box.Bottom)
                maxBottom = Math.Min(maxBottom, other.Top - gap);
        }

        maxRight = Math.Max(box.Right, maxRight);
        maxBottom = Math.Max(box.Bottom, maxBottom);
        var candidate = Rectangle.FromLTRB(box.Left, box.Top, maxRight, maxBottom);
        if (candidate == box)
            return box;

        // 扩展部分背景不是单色（图片、渐变）时不扩展，避免译文压在复杂背景上。
        var (_, uniform) = ScreenshotTextLayout.SampleBackground(crop, candidate, paragraph.MedianLineHeight);
        return uniform ? candidate : box;
    }

    // ---------- 辅助类型 ----------

    /// <summary>直接读写 32bpp ARGB 像素（0xRRGGBB，忽略 alpha）。</summary>
    internal readonly unsafe struct PixelBuffer
    {
        private readonly byte* _scan0;
        private readonly int _stride;

        public PixelBuffer(BitmapData data)
        {
            _scan0 = (byte*)data.Scan0;
            _stride = data.Stride;
            Width = data.Width;
            Height = data.Height;
        }

        public int Width { get; }
        public int Height { get; }

        public int Get(int x, int y)
        {
            var p = _scan0 + (long)y * _stride + x * 4;
            return (p[2] << 16) | (p[1] << 8) | p[0];
        }

        public void Set(int x, int y, int rgb)
        {
            var p = _scan0 + (long)y * _stride + x * 4;
            p[0] = (byte)(rgb & 0xFF);
            p[1] = (byte)((rgb >> 8) & 0xFF);
            p[2] = (byte)((rgb >> 16) & 0xFF);
            p[3] = 255;
        }
    }

    private sealed class InkStats
    {
        private readonly List<(int Color, int Distance)> _samples = new();
        private long _runTotal;
        private int _runCount;

        public void Add(int color, int distance)
        {
            if (_samples.Count < 4096)
                _samples.Add((color, distance));
        }

        /// <summary>统计每行笔画的水平连续长度，用于估计笔画粗细。</summary>
        public void AddRuns(bool[] mask, int w, int h)
        {
            for (var y = 0; y < h; y++)
            {
                var run = 0;
                for (var x = 0; x < w; x++)
                {
                    if (mask[y * w + x])
                    {
                        run++;
                        continue;
                    }

                    if (run > 0)
                    {
                        _runTotal += run;
                        _runCount++;
                        run = 0;
                    }
                }

                if (run > 0)
                {
                    _runTotal += run;
                    _runCount++;
                }
            }
        }

        /// <summary>取与背景差异最大的前 30% 笔画像素的均值：排除抗锯齿边缘的混合色。</summary>
        public Color TextColor()
        {
            if (_samples.Count == 0)
                return Color.Black;

            var core = _samples.OrderByDescending(sample => sample.Distance)
                .Take(Math.Max(1, _samples.Count * 3 / 10))
                .ToList();
            var r = (int)core.Average(sample => (sample.Color >> 16) & 0xFF);
            var g = (int)core.Average(sample => (sample.Color >> 8) & 0xFF);
            var b = (int)core.Average(sample => sample.Color & 0xFF);
            return Color.FromArgb(r, g, b);
        }

        /// <summary>平均笔画宽度超过行高的 13% 判为粗体（常规字重约 8%–10%）。</summary>
        public bool IsBold(int lineHeight) =>
            _runCount > 0 && lineHeight > 0 && (double)_runTotal / _runCount / lineHeight > 0.13;
    }
}
