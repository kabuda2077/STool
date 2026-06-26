using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Panel = System.Windows.Controls.Panel;

namespace STool.Modules.Screenshot;

/// <summary>
/// CaptureOverlay 原位翻译功能
/// </summary>
public partial class CaptureOverlay
{
    private async System.Threading.Tasks.Task<bool> TryShowBlockTranslationAsync(
        STool.Modules.Ocr.OcrResult ocrResult,
        STool.Modules.Translation.TranslationManager translationManager,
        System.Drawing.Bitmap crop,
        System.Threading.CancellationToken cancellationToken)
    {
        try
        {
            var rawLines = BuildRawTranslationLines(ocrResult, crop.Width, crop.Height);
            Log.Information(
                "[ScreenshotTranslate] OCR blocks={BlockCount} mergedLines={LineCount} provider={Provider}",
                ocrResult.TextBlocks.Count,
                rawLines.Count,
                ocrResult.Provider);

            if (rawLines.Count < 2)
                return false;

            if (translationManager.GetConfiguredScreenshotMode() == STool.Models.ScreenshotTranslationMode.Smart)
            {
                if (await TryShowSmartTranslationAsync(rawLines, translationManager, crop, cancellationToken))
                {
                    return true;
                }

                // 智能模式不可用或失败,提示用户而不是静默回退
                Log.Warning("[ScreenshotTranslate] Smart mode unavailable or failed");
                Core.ToastNotification.Show("智能翻译不可用", "AI 未配置或调用失败,请检查设置", Core.ToastNotification.ToastType.Warning);
                return false;
            }

            return await TryShowFastTranslationAsync(rawLines, translationManager, crop, cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ScreenshotTranslate] Block translation overlay failed; falling back");
            return false;
        }
    }

    private async System.Threading.Tasks.Task<bool> TryShowSmartTranslationAsync(
        IReadOnlyList<TranslationLine> rawLines,
        STool.Modules.Translation.TranslationManager translationManager,
        System.Drawing.Bitmap crop,
        System.Threading.CancellationToken cancellationToken)
    {
        var selector = translationManager.TryCreateContentSelector();
        if (selector == null)
        {
            Log.Information("[ScreenshotTranslate] Smart mode unavailable; falling back to fast mode");
            return false;
        }

        var targetLanguage = STool.Modules.Translation.TranslationManager.ResolveTargetLanguage(
            string.Join("\n", rawLines.Select(line => line.Text)),
            translationManager.GetConfiguredTranslationMode());

        var contentLines = rawLines
            .Select((line, index) => new STool.Modules.Translation.ScreenContentLine(
                index,
                line.Text,
                line.Box.X,
                line.Box.Y,
                line.Box.Width,
                line.Box.Height))
            .ToArray();

        var translated = await selector.SelectAndTranslateAsync(contentLines, targetLanguage, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (translated == null || translated.Count == 0)
        {
            Log.Information("[ScreenshotTranslate] Smart selection empty/failed; falling back to fast mode");
            return false;
        }

        var paragraphs = new List<TranslationParagraph>();
        var translations = new List<string>();
        foreach (var item in translated)
        {
            if (item.Index < 0 || item.Index >= rawLines.Count)
                continue;

            var line = rawLines[item.Index];
            paragraphs.Add(new TranslationParagraph(line.Text, line.Box, line.Box.Height));
            translations.Add(item.Translation);
        }

        if (paragraphs.Count == 0)
            return false;

        Log.Information(
            "[ScreenshotTranslate] Smart translated lines={Selected}/{Total}",
            paragraphs.Count,
            rawLines.Count);

        ShowTranslationParagraphs(paragraphs, translations, crop);
        return true;
    }

    private async System.Threading.Tasks.Task<bool> TryShowFastTranslationAsync(
        IReadOnlyList<TranslationLine> rawLines,
        STool.Modules.Translation.TranslationManager translationManager,
        System.Drawing.Bitmap crop,
        System.Threading.CancellationToken cancellationToken)
    {
        var selectedLines = rawLines
            .Where(line => IsLikelyTranslatableContent(line, crop.Width, crop.Height))
            .ToList();

        if (selectedLines.Count == 0)
        {
            Log.Information("[ScreenshotTranslate] Fast selection empty; falling back to whole-block");
            return false;
        }

        if (selectedLines.Count < rawLines.Count)
        {
            var filtered = rawLines.Except(selectedLines).ToList();
            Log.Debug(
                "[ScreenshotTranslate] Fast mode filtered lines={Count}: {Lines}",
                filtered.Count,
                string.Join("; ", filtered.Select(l => $"\"{l.Text.Substring(0, Math.Min(l.Text.Length, 20))}\"")));
        }

        var paragraphs = GroupParagraphs(selectedLines, crop.Height);
        if (paragraphs.Count == 0)
            return false;

        Log.Information(
            "[ScreenshotTranslate] Fast selected lines={Selected}/{Total} paragraphs={Paragraphs}",
            selectedLines.Count,
            rawLines.Count,
            paragraphs.Count);

        var translated = await translationManager.TranslateBlocksAsync(paragraphs.Select(p => p.Text).ToArray(), cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!translated.Success || translated.TranslatedBlocks.Count != paragraphs.Count)
        {
            Log.Information(
                "[ScreenshotTranslate] Fast paragraph translation fallback reason={Reason}",
                translated.ErrorMessage ?? "count mismatch");
            return false;
        }

        ShowTranslationParagraphs(paragraphs, translated.TranslatedBlocks, crop);
        Log.Information("[ScreenshotTranslate] Fast block translation overlay shown paragraphs={Count}", paragraphs.Count);
        return true;
    }

    private List<TranslationLine> BuildRawTranslationLines(STool.Modules.Ocr.OcrResult result, int cropWidth, int cropHeight)
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

        if (blocks.Count < 2)
            return blocks;

        var lineGroups = new List<List<TranslationLine>>();
        foreach (var block in blocks)
        {
            var centerY = block.Box.Top + block.Box.Height / 2.0;
            var targetGroup = lineGroups.FirstOrDefault(group =>
            {
                var groupBox = UnionBoxes(group.Select(item => item.Box));
                var groupCenterY = groupBox.Top + groupBox.Height / 2.0;
                var tolerance = Math.Max(4, Math.Max(groupBox.Height, block.Box.Height) * 0.35);
                return Math.Abs(centerY - groupCenterY) <= tolerance;
            });

            if (targetGroup == null)
            {
                targetGroup = new List<TranslationLine>();
                lineGroups.Add(targetGroup);
            }

            targetGroup.Add(block);
        }

        return lineGroups
            .SelectMany(SplitLineSegments)
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
        for (var i = 1; i < ordered.Count; i++)
        {
            var previousBox = UnionBoxes(segment.Select(item => item.Box));
            var current = ordered[i];
            var lineHeight = Math.Max(previousBox.Height, current.Box.Height);
            var maxMergeGap = Math.Max(10, lineHeight * 0.8);

            if (current.Box.Left - previousBox.Right > maxMergeGap)
            {
                yield return CreateLineSegment(segment);
                segment.Clear();
            }

            segment.Add(current);
        }

        if (segment.Count > 0)
            yield return CreateLineSegment(segment);
    }

    private static TranslationLine CreateLineSegment(IReadOnlyList<TranslationLine> items)
    {
        var text = JoinLineText(items);
        var box = UnionBoxes(items.Select(item => item.Box));
        return new TranslationLine(text, box);
    }

    /// <summary>
    /// 将选中的行按垂直邻近合并为段落:左缘相近且行距不大的相邻行归为一段,
    /// 整段共用一个文本框和统一字号渲染。
    /// </summary>
    private static List<TranslationParagraph> GroupParagraphs(IReadOnlyList<TranslationLine> lines, int cropHeight)
    {
        var ordered = lines
            .Where(line => !string.IsNullOrWhiteSpace(line.Text) && line.Box.Width > 4 && line.Box.Height > 4)
            .OrderBy(line => line.Box.Top)
            .ThenBy(line => line.Box.Left)
            .ToList();

        var paragraphs = new List<List<TranslationLine>>();
        foreach (var line in ordered)
        {
            var group = paragraphs.LastOrDefault();
            if (group == null)
            {
                paragraphs.Add(new List<TranslationLine> { line });
                continue;
            }

            var previous = group[^1];
            var gap = line.Box.Top - previous.Box.Bottom;
            var maxGap = Math.Max(10, Math.Max(previous.Box.Height, line.Box.Height) * 0.55);
            var similarLeftEdge = Math.Abs(line.Box.Left - group.Min(l => l.Box.Left)) <= Math.Max(28, line.Box.Height * 1.6);

            if (gap <= maxGap && similarLeftEdge)
                group.Add(line);
            else
                paragraphs.Add(new List<TranslationLine> { line });
        }

        return paragraphs
            .Select(group => new TranslationParagraph(
                JoinParagraphText(group),
                UnionBoxes(group.Select(l => l.Box)),
                group.Select(l => l.Box.Height).OrderBy(h => h).ElementAt(group.Count / 2)))
            .Where(p => !string.IsNullOrWhiteSpace(p.Text))
            .ToList();
    }

    private static string JoinParagraphText(IReadOnlyList<TranslationLine> lines)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                var prevLast = lines[i - 1].Text.Length > 0 ? lines[i - 1].Text[^1] : '\0';
                var currFirst = lines[i].Text.Length > 0 ? lines[i].Text[0] : '\0';
                // 中文跨行直接接续;西文跨行补空格。
                if (!IsCjk(prevLast) && !IsCjk(currFirst))
                    sb.Append(' ');
            }

            sb.Append(lines[i].Text);
        }

        return sb.ToString();
    }

    private void ShowTranslationParagraphs(
        IReadOnlyList<TranslationParagraph> paragraphs,
        IReadOnlyList<string> translations,
        System.Drawing.Bitmap crop)
    {
        translationBlockCanvas.Children.Clear();
        translationOverlay.Visibility = Visibility.Collapsed;
        translationOverlayText.Text = string.Empty;
        _translationRenderBlocks.Clear();

        for (var i = 0; i < paragraphs.Count; i++)
        {
            var paragraph = paragraphs[i];
            var rect = PhysicalLineToDipRect(paragraph.Box);
            rect = ExpandRect(rect, 2, 1.5, _selection.Width, _selection.Height);
            if (rect.Width < 6 || rect.Height < 6)
                continue;

            var (background, uniform) = SampleParagraphBackground(crop, paragraph.Box);
            var foreground = ContrastBrush(background);
            var foregroundColor = BrushToColor(foreground, System.Windows.Media.Color.FromRgb(78, 78, 88));

            // 背景非单色(渐变/图片)时,纯色矩形会很违和 —— 回退到半透明蒙版,降低突兀感。
            if (!uniform)
                background = System.Windows.Media.Color.FromArgb(232, background.R, background.G, background.B);

            var paddingX = 3.0;
            var paddingY = 2.0;
            var contentWidth = Math.Max(1, rect.Width - paddingX * 2);
            var contentHeight = Math.Max(1, rect.Height - paddingY * 2);

            // 整段统一字号:以段内行高中位数为基准,再按译文是否塞得下整体缩放一次。
            var baseSize = Clamp(paragraph.MedianLineHeight / _scaleY * 0.82, 9, 26);
            var fontSize = FitParagraphFontSize(translations[i], baseSize, contentWidth, contentHeight);

            var lineHeight = Math.Ceiling(fontSize * 1.25);

            var text = new TextBlock
            {
                Text = translations[i],
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontFamily = System.Windows.SystemFonts.MessageFontFamily,
                FontSize = fontSize,
                LineHeight = lineHeight,
                Foreground = foreground,
                MaxHeight = contentHeight,
                MaxWidth = contentWidth,
                ClipToBounds = true
            };

            var block = new Border
            {
                Background = new SolidColorBrush(background),
                CornerRadius = new CornerRadius(Math.Min(3, rect.Height / 8)),
                Padding = new Thickness(paddingX, paddingY, paddingX, paddingY),
                Width = rect.Width,
                Height = rect.Height,
                ClipToBounds = true,
                Child = text
            };

            Canvas.SetLeft(block, rect.X);
            Canvas.SetTop(block, rect.Y);
            translationBlockCanvas.Children.Add(block);

            _translationRenderBlocks.Add(new TranslationRenderBlock(
                translations[i],
                rect,
                background,
                foregroundColor,
                fontSize,
                new Thickness(paddingX, paddingY, paddingX, paddingY)));
        }

        if (translationBlockCanvas.Children.Count == 0)
            throw new InvalidOperationException("没有可渲染的翻译块");

        translationBlockCanvas.Visibility = Visibility.Visible;
        Panel.SetZIndex(translationBlockCanvas, 34);
        Panel.SetZIndex(translationOverlay, 35);
        Panel.SetZIndex(selectionBorder, 40);
        UpdateVisuals();
    }

    private Rect PhysicalLineToDipRect(System.Drawing.Rectangle box)
    {
        return new Rect(
            box.X / _scaleX,
            box.Y / _scaleY,
            box.Width / _scaleX,
            box.Height / _scaleY);
    }

    private (System.Windows.Media.Color Color, bool Uniform) SampleParagraphBackground(System.Drawing.Bitmap crop, System.Drawing.Rectangle box)
    {
        var marginX = Math.Max(8, box.Height);
        var marginY = Math.Max(5, box.Height / 2);
        var outer = new System.Drawing.Rectangle(
            Math.Max(0, box.Left - marginX),
            Math.Max(0, box.Top - marginY),
            Math.Min(crop.Width - Math.Max(0, box.Left - marginX), box.Width + marginX * 2),
            Math.Min(crop.Height - Math.Max(0, box.Top - marginY), box.Height + marginY * 2));

        if (outer.Width <= 0 || outer.Height <= 0)
            return (System.Windows.Media.Color.FromRgb(255, 255, 255), true);

        var center = new System.Drawing.Rectangle(
            Math.Max(0, box.Left - 1),
            Math.Max(0, box.Top - 1),
            Math.Min(crop.Width - Math.Max(0, box.Left - 1), box.Width + 2),
            Math.Min(crop.Height - Math.Max(0, box.Top - 1), box.Height + 2));

        var color = DominantBackgroundColor(crop, outer, center);
        var uniform = IsBackgroundUniform(crop, outer, center, color);
        return (System.Windows.Media.Color.FromRgb(color.R, color.G, color.B), uniform);
    }

    /// <summary>
    /// 判断文字框周边背景是否接近单色:统计环形采样区里与主色相近的像素占比。
    /// 占比高 → 单色(可贴纯色矩形);占比低 → 渐变/图片背景(应回退半透明蒙版)。
    /// </summary>
    private static bool IsBackgroundUniform(
        System.Drawing.Bitmap bitmap,
        System.Drawing.Rectangle outer,
        System.Drawing.Rectangle excludedCenter,
        System.Drawing.Color dominant)
    {
        var total = 0;
        var near = 0;
        // 大区域时隔点采样,避免逐像素遍历过慢。
        var stepX = Math.Max(1, outer.Width / 64);
        var stepY = Math.Max(1, outer.Height / 64);

        var data = bitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        try
        {
            unsafe
            {
                var ptr = (byte*)data.Scan0;
                var stride = data.Stride;

                for (var y = outer.Top; y < outer.Bottom; y += stepY)
                {
                    for (var x = outer.Left; x < outer.Right; x += stepX)
                    {
                        if (excludedCenter.Contains(x, y))
                            continue;

                        var offset = y * stride + x * 4;
                        var b = ptr[offset];
                        var g = ptr[offset + 1];
                        var r = ptr[offset + 2];

                        total++;
                        var distance = Math.Abs(r - dominant.R) + Math.Abs(g - dominant.G) + Math.Abs(b - dominant.B);
                        if (distance <= 36)
                            near++;
                    }
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        if (total == 0)
            return true;

        return (double)near / total >= 0.62;
    }

    /// <summary>
    /// 整段统一字号:从基准字号起逐档缩小,直到译文按 availableWidth 自动换行后总高度塞进段落框。
    /// </summary>
    private static double FitParagraphFontSize(string text, double preferredSize, double availableWidth, double availableHeight)
    {
        var baseSize = Clamp(preferredSize, 9, 26);
        const double minSize = 9;
        var typeface = new Typeface(System.Windows.SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        for (var size = baseSize; size >= minSize; size -= 0.5)
        {
            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                System.Windows.FlowDirection.LeftToRight,
                typeface,
                size,
                Brushes.Black,
                1.0)
            {
                MaxTextWidth = Math.Max(1, availableWidth),
                LineHeight = Math.Ceiling(size * 1.25)
            };

            if (formatted.Height <= availableHeight)
                return size;
        }

        return minSize;
    }

    private static Brush ContrastBrush(System.Windows.Media.Color background)
    {
        var luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255.0;
        return luminance > 0.55
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 78, 88))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255));
    }

    private static System.Windows.Media.Color BrushToColor(Brush brush, System.Windows.Media.Color fallback)
    {
        return brush is SolidColorBrush solid ? solid.Color : fallback;
    }

    private static System.Drawing.Color DominantBackgroundColor(
        System.Drawing.Bitmap bitmap,
        System.Drawing.Rectangle outer,
        System.Drawing.Rectangle excludedCenter)
    {
        var all = new List<System.Drawing.Color>();
        var light = new List<System.Drawing.Color>();

        var data = bitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        try
        {
            unsafe
            {
                var ptr = (byte*)data.Scan0;
                var stride = data.Stride;

                for (var y = outer.Top; y < outer.Bottom; y++)
                {
                    for (var x = outer.Left; x < outer.Right; x++)
                    {
                        if (excludedCenter.Contains(x, y))
                            continue;

                        var offset = y * stride + x * 4;
                        var b = ptr[offset];
                        var g = ptr[offset + 1];
                        var r = ptr[offset + 2];
                        var a = ptr[offset + 3];

                        var color = System.Drawing.Color.FromArgb(a, r, g, b);
                        all.Add(color);

                        if (Luminance(color) >= 145)
                            light.Add(color);
                    }
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        var candidates = light.Count >= Math.Max(24, all.Count / 5) ? light : all;
        if (candidates.Count == 0)
            return AverageColor(bitmap, outer.X, outer.Y, outer.Width, outer.Height);

        return DominantQuantizedColor(candidates);
    }

    private static System.Drawing.Color DominantQuantizedColor(IReadOnlyList<System.Drawing.Color> colors)
    {
        const int step = 10;
        var buckets = new Dictionary<int, (int Count, long R, long G, long B)>();

        foreach (var color in colors)
        {
            var r = color.R / step;
            var g = color.G / step;
            var b = color.B / step;
            var key = (r << 16) | (g << 8) | b;

            if (!buckets.TryGetValue(key, out var bucket))
                bucket = (0, 0, 0, 0);

            bucket.Count++;
            bucket.R += color.R;
            bucket.G += color.G;
            bucket.B += color.B;
            buckets[key] = bucket;
        }

        var best = buckets.Values
            .OrderByDescending(bucket => bucket.Count)
            .ThenByDescending(bucket => bucket.R + bucket.G + bucket.B)
            .First();

        return System.Drawing.Color.FromArgb(
            (int)(best.R / best.Count),
            (int)(best.G / best.Count),
            (int)(best.B / best.Count));
    }

    private static double Luminance(System.Drawing.Color color)
    {
        return 0.299 * color.R + 0.587 * color.G + 0.114 * color.B;
    }

    private static Rect ExpandRect(Rect rect, double x, double y, double maxWidth, double maxHeight)
    {
        var left = Clamp(rect.Left - x, 0, maxWidth);
        var top = Clamp(rect.Top - y, 0, maxHeight);
        var right = Clamp(rect.Right + x, 0, maxWidth);
        var bottom = Clamp(rect.Bottom + y, 0, maxHeight);
        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static System.Drawing.Rectangle ClampBox(System.Drawing.Rectangle box, int maxWidth, int maxHeight)
    {
        var left = Math.Clamp(box.Left, 0, maxWidth);
        var top = Math.Clamp(box.Top, 0, maxHeight);
        var right = Math.Clamp(box.Right, 0, maxWidth);
        var bottom = Math.Clamp(box.Bottom, 0, maxHeight);
        return new System.Drawing.Rectangle(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static System.Drawing.Rectangle UnionBoxes(IEnumerable<System.Drawing.Rectangle> boxes)
    {
        var list = boxes.ToList();
        if (list.Count == 0)
            return System.Drawing.Rectangle.Empty;

        var left = list.Min(box => box.Left);
        var top = list.Min(box => box.Top);
        var right = list.Max(box => box.Right);
        var bottom = list.Max(box => box.Bottom);
        return new System.Drawing.Rectangle(left, top, right - left, bottom - top);
    }

    private static string JoinLineText(IReadOnlyList<TranslationLine> words)
    {
        var sb = new System.Text.StringBuilder();
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
        if (IsCjk(prevLast) || IsCjk(currentFirst))
            return false;

        return true;
    }

    private static bool IsCjk(char ch)
    {
        return (ch >= '\u4e00' && ch <= '\u9fff')
            || (ch >= '\u3400' && ch <= '\u4dbf')
            || (ch >= '\u3040' && ch <= '\u30ff')
            || (ch >= '\uac00' && ch <= '\ud7af');
    }

    private static bool IsLikelyIconOcrArtifact(TranslationLine line)
    {
        var text = line.Text.Trim();
        if (text.Length != 1)
            return false;

        var ch = text[0];
        // 单字母/数字/CJK 不算 artifact,只过滤标点和符号
        if (char.IsLetterOrDigit(ch) || IsCjk(ch))
            return false;

        return line.Box.Width <= line.Box.Height * 1.2;
    }

    private static bool IsLikelyTranslatableContent(TranslationLine line, int cropWidth, int cropHeight)
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
            IsPureNumberLike(text))
        {
            return false;
        }

        var topRatio = line.Box.Top / Math.Max(1.0, cropHeight);
        var bottomRatio = line.Box.Bottom / Math.Max(1.0, cropHeight);
        if ((topRatio < 0.04 || bottomRatio > 0.96) && visualChars <= 12)
            return false;

        if (!HasNaturalLanguageSignal(text))
            return false;

        if (visualChars <= 5 && !LooksLikeSentenceText(text))
            return false;

        if (IsLikelyShortControlLabel(text, line, cropWidth, cropHeight))
            return false;

        return true;
    }

    private static int CountVisualCharacters(string text)
    {
        var count = 0;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
                continue;

            count += IsCjk(ch) ? 2 : 1;
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
            if (IsCjk(ch))
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
               normalized.Any(IsCjk) ||
               normalized.Any(ch => ch is '.' or ',' or '，' or '。' or '!' or '?' or '！' or '？' or ':' or '：' or ';' or '；');
    }

    private static bool IsUrlLike(string text)
    {
        return text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(text, @"\b[a-z0-9-]+\.(com|net|org|io|dev|app|cn)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool IsTimestampLike(string text)
    {
        var normalized = text.Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(normalized, @"^\d{1,2}[:：]\d{2}$")
            || System.Text.RegularExpressions.Regex.IsMatch(normalized, @"\b\d+\s*(秒|分钟|小时|天|周|月|年)前\b")
            || System.Text.RegularExpressions.Regex.IsMatch(normalized, @"\b\d+\s*(s|sec|secs|min|mins|h|hr|hrs|hour|hours|d|day|days|w|week|weeks|mo|month|months|y|year|years)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool IsPureNumberLike(string text)
    {
        var normalized = text.Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(normalized, @"^[\d\s.,:%+\-]+$");
    }

    private static bool IsMostlySymbols(string text)
    {
        var meaningful = text.Count(ch => char.IsLetterOrDigit(ch) || IsCjk(ch));
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

    private void ShowTranslationOverlay(string text)
    {
        translationBlockCanvas.Visibility = Visibility.Collapsed;
        translationBlockCanvas.Children.Clear();
        _translationRenderBlocks.Clear();
        translationOverlayText.Text = text;
        translationOverlay.Visibility = Visibility.Visible;
        Panel.SetZIndex(translationOverlay, 35);
        Panel.SetZIndex(selectionBorder, 40);
        UpdateVisuals();
        ApplyTranslationOverlayLayout();
    }

    private void HideTranslationOverlay()
    {
        translationBlockCanvas.Visibility = Visibility.Collapsed;
        translationBlockCanvas.Children.Clear();
        _translationRenderBlocks.Clear();
        translationOverlay.Visibility = Visibility.Collapsed;
        translationOverlayText.Text = string.Empty;
    }

    private bool IsTranslationOverlayVisible()
    {
        return translationOverlay.Visibility == Visibility.Visible ||
               translationBlockCanvas.Visibility == Visibility.Visible;
    }

    private void CancelCurrentTranslation()
    {
        try
        {
            var old = _translationCts;
            _translationCts = null;
            old?.Cancel();
            old?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ApplyTranslationOverlayLayout()
    {
        if (translationOverlay.Visibility != Visibility.Visible)
            return;

        var h = Math.Max(1, _selection.Height);
        var w = Math.Max(1, _selection.Width);
        var padding = h switch
        {
            < 42 => 4,
            < 64 => 6,
            < 96 => 8,
            _ => 12
        };

        var fontSize = h switch
        {
            < 36 => 11,
            < 52 => 12,
            < 72 => 13,
            _ => 14
        };

        translationOverlay.Padding = new Thickness(padding, Math.Max(2, padding - 1), padding, Math.Max(2, padding - 1));
        translationOverlayText.FontSize = fontSize;
        translationOverlayText.LineHeight = Math.Ceiling(fontSize * 1.35);
        translationOverlayText.MaxWidth = Math.Max(1, w - padding * 2);

        // 高度很小的单行选区里,滚动条本身会吃掉空间;先隐藏滚动条保证文字完整露出。
        translationOverlayScroll.VerticalScrollBarVisibility = h < 72
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
    }

    private static BitmapSource ToBitmapSource(System.Drawing.Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var img = new BitmapImage();
        img.BeginInit();
        img.StreamSource = ms;
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        return img;
    }

    private static System.Drawing.Bitmap BitmapSourceToBitmap(BitmapSource src)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(src));
        var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;
        return new System.Drawing.Bitmap(ms);
    }

    private void CloseOverlay()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelCurrentTranslation();
        _translationCts?.Dispose();
        _translationCts = null;
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
        _frozen?.Dispose();
        _frozen = null;
        base.OnClosed(e);
    }

    private sealed record TranslationLine(string Text, System.Drawing.Rectangle Box);

    private sealed record TranslationParagraph(string Text, System.Drawing.Rectangle Box, int MedianLineHeight);

    private sealed record TranslationRenderBlock(
        string Text,
        Rect Rect,
        System.Windows.Media.Color Background,
        System.Windows.Media.Color Foreground,
        double FontSize,
        Thickness Padding);
}
