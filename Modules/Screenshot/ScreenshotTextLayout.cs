using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using STool.Core;
using STool.Modules.Ocr;

namespace STool.Modules.Screenshot;

internal sealed record TranslationLine(string Text, Rectangle Box);

internal sealed record TranslationParagraph(string Text, Rectangle Box, int MedianLineHeight);

/// <summary>
/// 截图原位翻译的版面分析：OCR 词块合并成行、行归并成段、过滤界面噪声、采样背景色。
/// 与界面无关，可单独测试。坐标均为截图裁剪区域内的物理像素。
/// </summary>
internal static partial class ScreenshotTextLayout
{
    private const int MaxBackgroundSamples = 4096;
    private const int UniformColorDistance = 36;
    private const double UniformRatio = 0.62;
    private const double LightLuminance = 145;

    // ---------- 行与段落 ----------

    public static List<TranslationLine> BuildLines(OcrResult result, int cropWidth, int cropHeight)
    {
        var blocks = result.TextBlocks
            .Where(block => !string.IsNullOrWhiteSpace(block.Text))
            .Where(block => block.BoundingBox.Width > 1 && block.BoundingBox.Height > 1)
            .Select(block => new TranslationLine(block.Text.Trim(), ClampBox(block.BoundingBox, cropWidth, cropHeight)))
            .Where(line => line.Box.Width > 1 && line.Box.Height > 1)
            .Where(line => !IsLikelyIconOcrArtifact(line))
            .OrderBy(line => line.Box.Top)
            .ThenBy(line => line.Box.Left)
            .ToList();

        return blocks.Count < 2 ? blocks : MergeIntoLines(blocks);
    }

    /// <summary>按垂直中心把词块归入同一行，再按水平间距切成行内片段。每组维护并集框，不重复计算。</summary>
    internal static List<TranslationLine> MergeIntoLines(IReadOnlyList<TranslationLine> blocks)
    {
        var groups = new List<LineGroup>();
        foreach (var block in blocks)
        {
            var centerY = block.Box.Top + block.Box.Height / 2.0;
            LineGroup? target = null;
            foreach (var group in groups)
            {
                var groupCenterY = group.Bounds.Top + group.Bounds.Height / 2.0;
                var tolerance = Math.Max(4, Math.Max(group.Bounds.Height, block.Box.Height) * 0.35);
                if (Math.Abs(centerY - groupCenterY) <= tolerance)
                {
                    target = group;
                    break;
                }
            }

            if (target == null)
            {
                target = new LineGroup();
                groups.Add(target);
            }

            target.Add(block);
        }

        return groups
            .SelectMany(group => SplitLineSegments(group.Items))
            .Where(line => !string.IsNullOrWhiteSpace(line.Text) && line.Box.Width > 4 && line.Box.Height > 4)
            .OrderBy(line => line.Box.Top)
            .ThenBy(line => line.Box.Left)
            .ToList();
    }

    private static IEnumerable<TranslationLine> SplitLineSegments(IEnumerable<TranslationLine> group)
    {
        var ordered = group.OrderBy(item => item.Box.Left).ToList();
        if (ordered.Count == 0)
            yield break;

        var segment = new List<TranslationLine> { ordered[0] };
        var segmentBox = ordered[0].Box;
        for (var i = 1; i < ordered.Count; i++)
        {
            var current = ordered[i];
            var lineHeight = Math.Max(segmentBox.Height, current.Box.Height);
            var maxMergeGap = Math.Max(10, lineHeight * 0.8);

            if (current.Box.Left - segmentBox.Right > maxMergeGap)
            {
                yield return new TranslationLine(JoinLineText(segment), segmentBox);
                segment = new List<TranslationLine>();
                segmentBox = current.Box;
            }
            else
            {
                segmentBox = Rectangle.Union(segmentBox, current.Box);
            }

            segment.Add(current);
        }

        yield return new TranslationLine(JoinLineText(segment), segmentBox);
    }

    /// <summary>
    /// 将行按垂直邻近合并为段落:左缘相近且行距不大的相邻行归为一段,
    /// 整段共用一个文本框和统一字号渲染。
    /// </summary>
    public static List<TranslationParagraph> GroupParagraphs(IReadOnlyList<TranslationLine> lines)
    {
        var ordered = lines
            .Where(line => !string.IsNullOrWhiteSpace(line.Text) && line.Box.Width > 4 && line.Box.Height > 4)
            .OrderBy(line => line.Box.Top)
            .ThenBy(line => line.Box.Left)
            .ToList();

        var paragraphs = new List<ParagraphGroup>();
        foreach (var line in ordered)
        {
            var group = paragraphs.Count > 0 ? paragraphs[^1] : null;
            if (group != null)
            {
                var previous = group.Lines[^1];
                var gap = line.Box.Top - previous.Box.Bottom;
                var maxGap = Math.Max(10, Math.Max(previous.Box.Height, line.Box.Height) * 0.55);
                var similarLeftEdge = Math.Abs(line.Box.Left - group.MinLeft) <= Math.Max(28, line.Box.Height * 1.6);
                if (gap <= maxGap && similarLeftEdge)
                {
                    group.Add(line);
                    continue;
                }
            }

            var created = new ParagraphGroup();
            created.Add(line);
            paragraphs.Add(created);
        }

        return paragraphs
            .Select(group => new TranslationParagraph(
                JoinParagraphText(group.Lines),
                group.Bounds,
                group.Lines.Select(l => l.Box.Height).OrderBy(h => h).ElementAt(group.Lines.Count / 2)))
            .Where(p => !string.IsNullOrWhiteSpace(p.Text))
            .ToList();
    }

    internal static string JoinParagraphText(IReadOnlyList<TranslationLine> lines)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                var prevLast = lines[i - 1].Text.Length > 0 ? lines[i - 1].Text[^1] : '\0';
                var currFirst = lines[i].Text.Length > 0 ? lines[i].Text[0] : '\0';
                // 中文跨行直接接续;西文跨行补空格。
                if (!TextScript.IsCjk(prevLast) && !TextScript.IsCjk(currFirst))
                    sb.Append(' ');
            }

            sb.Append(lines[i].Text);
        }

        return sb.ToString();
    }

    internal static string JoinLineText(IReadOnlyList<TranslationLine> words)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            if (i > 0 && ShouldInsertSpace(words[i - 1], words[i]))
                sb.Append(' ');

            sb.Append(words[i].Text);
        }

        return sb.ToString();
    }

    private static bool ShouldInsertSpace(TranslationLine previous, TranslationLine current)
    {
        var gap = current.Box.Left - previous.Box.Right;
        if (gap <= 1)
            return false;

        var prevLast = previous.Text.Length > 0 ? previous.Text[^1] : '\0';
        var currentFirst = current.Text.Length > 0 ? current.Text[0] : '\0';
        return !TextScript.IsCjk(prevLast) && !TextScript.IsCjk(currentFirst);
    }

    private static bool IsLikelyIconOcrArtifact(TranslationLine line)
    {
        var text = line.Text.Trim();
        if (text.Length != 1)
            return false;

        var ch = text[0];
        // 单字母/数字/CJK 不算 artifact,只过滤标点和符号
        if (char.IsLetterOrDigit(ch) || TextScript.IsCjk(ch))
            return false;

        return line.Box.Width <= line.Box.Height * 1.2;
    }

    public static Rectangle ClampBox(Rectangle box, int maxWidth, int maxHeight)
    {
        var left = Math.Clamp(box.Left, 0, maxWidth);
        var top = Math.Clamp(box.Top, 0, maxHeight);
        var right = Math.Clamp(box.Right, 0, maxWidth);
        var bottom = Math.Clamp(box.Bottom, 0, maxHeight);
        return new Rectangle(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>在 [minSize, preferredSize] 内按 0.5 的步长二分查找能放下文本的最大字号。</summary>
    public static double FitFontSize(double preferredSize, double minSize, Func<double, bool> fits)
    {
        var low = (int)Math.Ceiling(minSize * 2);
        var high = Math.Max(low, (int)Math.Floor(preferredSize * 2));
        if (fits(high / 2.0))
            return high / 2.0;
        if (!fits(low / 2.0))
            return low / 2.0;

        // 不变式：low 放得下，high 放不下。
        while (high - low > 1)
        {
            var mid = (low + high) / 2;
            if (fits(mid / 2.0))
                low = mid;
            else
                high = mid;
        }

        return low / 2.0;
    }

    private sealed class LineGroup
    {
        public List<TranslationLine> Items { get; } = new();
        public Rectangle Bounds { get; private set; }

        public void Add(TranslationLine line)
        {
            Bounds = Items.Count == 0 ? line.Box : Rectangle.Union(Bounds, line.Box);
            Items.Add(line);
        }
    }

    private sealed class ParagraphGroup
    {
        public List<TranslationLine> Lines { get; } = new();
        public Rectangle Bounds { get; private set; }
        public int MinLeft { get; private set; }

        public void Add(TranslationLine line)
        {
            Bounds = Lines.Count == 0 ? line.Box : Rectangle.Union(Bounds, line.Box);
            MinLeft = Lines.Count == 0 ? line.Box.Left : Math.Min(MinLeft, line.Box.Left);
            Lines.Add(line);
        }
    }
}
