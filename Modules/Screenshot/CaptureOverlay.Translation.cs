using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Serilog;
using STool.Modules.Ocr;
using STool.Modules.Translation;
using Brush = System.Windows.Media.Brush;

namespace STool.Modules.Screenshot;

/// <summary>
/// CaptureOverlay 原位翻译：调用版面分析与翻译服务，把译文渲染回截图原位置。
/// </summary>
public partial class CaptureOverlay
{
    private const double MinTranslationFontSize = 9;

    private async Task<bool> TryShowBlockTranslationAsync(
        OcrResult ocrResult,
        TranslationManager translationManager,
        System.Drawing.Bitmap crop,
        CancellationToken cancellationToken)
    {
        try
        {
            var rawLines = ScreenshotTextLayout.BuildLines(ocrResult, crop.Width, crop.Height);
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
                    return true;

                // 智能模式不可用或失败,提示用户而不是静默回退
                Log.Warning("[ScreenshotTranslate] Smart mode unavailable or failed");
                Core.ToastNotification.Show("智能翻译不可用", "AI 未配置或调用失败,请检查设置", Core.ToastNotification.ToastType.Warning);
                return false;
            }

            return await TryShowFastTranslationAsync(rawLines, translationManager, crop, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ScreenshotTranslate] Block translation overlay failed; falling back");
            return false;
        }
    }

    private async Task<bool> TryShowSmartTranslationAsync(
        IReadOnlyList<TranslationLine> rawLines,
        TranslationManager translationManager,
        System.Drawing.Bitmap crop,
        CancellationToken cancellationToken)
    {
        var selector = translationManager.TryCreateContentSelector();
        if (selector == null)
        {
            Log.Information("[ScreenshotTranslate] Smart mode unavailable; falling back to whole-block translation");
            return false;
        }

        var targetLanguage = TranslationManager.ResolveTargetLanguage(
            string.Join("\n", rawLines.Select(line => line.Text)),
            translationManager.GetConfiguredTranslationMode());

        var contentLines = rawLines
            .Select((line, index) => new ScreenContentLine(
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
            Log.Information("[ScreenshotTranslate] Smart selection empty/failed; falling back to whole-block translation");
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

    private async Task<bool> TryShowFastTranslationAsync(
        IReadOnlyList<TranslationLine> rawLines,
        TranslationManager translationManager,
        System.Drawing.Bitmap crop,
        CancellationToken cancellationToken)
    {
        var targetLanguage = TranslationManager.ResolveTargetLanguage(
            string.Join("\n", rawLines.Select(line => line.Text)),
            translationManager.GetConfiguredTranslationMode());
        var selectedLines = rawLines
            .Where(line => ScreenshotTextLayout.IsLikelyTranslatableContent(line, crop.Width, crop.Height, targetLanguage))
            .ToList();

        if (selectedLines.Count == 0)
        {
            Log.Information("[ScreenshotTranslate] Fast selection empty; falling back to whole-block");
            return false;
        }

        if (selectedLines.Count < rawLines.Count)
        {
            Log.Debug(
                "[ScreenshotTranslate] Fast mode filtered lines={Count}",
                rawLines.Count - selectedLines.Count);
        }

        var paragraphs = ScreenshotTextLayout.GroupParagraphs(selectedLines);
        if (paragraphs.Count == 0)
            return false;

        Log.Information(
            "[ScreenshotTranslate] Fast selected lines={Selected}/{Total} paragraphs={Paragraphs}",
            selectedLines.Count,
            rawLines.Count,
            paragraphs.Count);

        var translated = await translationManager.TranslateBlocksAsync(
            paragraphs.Select(p => p.Text).ToArray(),
            targetLanguage: targetLanguage,
            cancellationToken: cancellationToken);
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

    private void ShowTranslationParagraphs(
        IReadOnlyList<TranslationParagraph> paragraphs,
        IReadOnlyList<string> translations,
        System.Drawing.Bitmap crop)
    {
        translationBlockCanvas.Children.Clear();
        translationOverlay.Visibility = Visibility.Collapsed;
        translationOverlayText.Text = string.Empty;
        var typeface = new Typeface(System.Windows.SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (var i = 0; i < paragraphs.Count; i++)
        {
            var paragraph = paragraphs[i];
            var rect = ExpandRect(PhysicalLineToDipRect(paragraph.Box), 2, 1.5, _selection.Width, _selection.Height);
            if (rect.Width < 6 || rect.Height < 6)
                continue;

            var (sampled, uniform) = ScreenshotTextLayout.SampleBackground(crop, paragraph.Box, paragraph.MedianLineHeight);
            var background = System.Windows.Media.Color.FromRgb(sampled.R, sampled.G, sampled.B);
            var foreground = ContrastBrush(background);

            // 背景非单色(渐变/图片)时,纯色矩形会很违和 —— 回退到半透明蒙版,降低突兀感。
            if (!uniform)
                background = System.Windows.Media.Color.FromArgb(232, background.R, background.G, background.B);

            const double paddingX = 3.0;
            const double paddingY = 2.0;
            var contentWidth = Math.Max(1, rect.Width - paddingX * 2);
            var contentHeight = Math.Max(1, rect.Height - paddingY * 2);

            // 整段统一字号:以段内行高中位数为基准,再按译文是否塞得下整体缩放一次。
            var translation = translations[i];
            var baseSize = Clamp(paragraph.MedianLineHeight / BitmapScaleY * 0.82, MinTranslationFontSize, 26);
            var fontSize = ScreenshotTextLayout.FitFontSize(
                baseSize,
                MinTranslationFontSize,
                size => MeasureTextHeight(translation, typeface, size, contentWidth, pixelsPerDip) <= contentHeight);

            var text = new TextBlock
            {
                Text = translation,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontFamily = System.Windows.SystemFonts.MessageFontFamily,
                FontSize = fontSize,
                LineHeight = Math.Ceiling(fontSize * 1.25),
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
        }

        if (translationBlockCanvas.Children.Count == 0)
            throw new InvalidOperationException("没有可渲染的翻译块");

        translationBlockCanvas.Visibility = Visibility.Visible;
        UpdateVisuals();
    }

    private static double MeasureTextHeight(string text, Typeface typeface, double size, double width, double pixelsPerDip)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            typeface,
            size,
            System.Windows.Media.Brushes.Black,
            pixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, width),
            LineHeight = Math.Ceiling(size * 1.25)
        };
        return formatted.Height;
    }

    private Rect PhysicalLineToDipRect(System.Drawing.Rectangle box)
    {
        return new Rect(
            box.X / BitmapScaleX,
            box.Y / BitmapScaleY,
            box.Width / BitmapScaleX,
            box.Height / BitmapScaleY);
    }

    private Brush ContrastBrush(System.Windows.Media.Color background)
    {
        var luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255.0;
        return luminance > 0.55
            ? ResourceBrush("ScreenshotTranslationDarkTextBrush")
            : ResourceBrush("OnPrimaryBrush");
    }

    private static Rect ExpandRect(Rect rect, double x, double y, double maxWidth, double maxHeight)
    {
        var left = Clamp(rect.Left - x, 0, maxWidth);
        var top = Clamp(rect.Top - y, 0, maxHeight);
        var right = Clamp(rect.Right + x, 0, maxWidth);
        var bottom = Clamp(rect.Bottom + y, 0, maxHeight);
        return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private void ShowTranslationOverlay(string text)
    {
        translationBlockCanvas.Visibility = Visibility.Collapsed;
        translationBlockCanvas.Children.Clear();
        translationLoadingIndicator.Visibility = Visibility.Collapsed;
        translationOverlayScroll.Visibility = Visibility.Visible;
        translationOverlayText.Text = text;
        translationOverlay.Visibility = Visibility.Visible;
        UpdateVisuals();
    }

    private void ShowTranslationLoading()
    {
        translationBlockCanvas.Visibility = Visibility.Collapsed;
        translationBlockCanvas.Children.Clear();
        translationOverlayText.Text = string.Empty;
        translationOverlayScroll.Visibility = Visibility.Collapsed;
        translationLoadingIndicator.Visibility = Visibility.Visible;
        translationOverlay.Visibility = Visibility.Visible;
        UpdateVisuals();
    }

    private void HideTranslationOverlay()
    {
        translationBlockCanvas.Visibility = Visibility.Collapsed;
        translationBlockCanvas.Children.Clear();
        translationOverlay.Visibility = Visibility.Collapsed;
        translationLoadingIndicator.Visibility = Visibility.Collapsed;
        translationOverlayScroll.Visibility = Visibility.Visible;
        translationOverlayText.Text = string.Empty;
    }

    private bool IsTranslationOverlayVisible()
    {
        return translationOverlay.Visibility == Visibility.Visible ||
               translationBlockCanvas.Visibility == Visibility.Visible;
    }

    private void CancelCurrentTranslation()
    {
        var old = _translationCts;
        _translationCts = null;
        if (!_closing)
            UpdateTranslationToolTip();
        try
        {
            old?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 请求已经结束并释放了令牌。
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
}
