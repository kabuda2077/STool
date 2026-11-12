using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Serilog;

namespace STool.Modules.Screenshot;

/// <summary>
/// CaptureOverlay 原位翻译：流水线在 <see cref="ScreenshotTranslationPipeline"/>，
/// 擦除与样式分析在 <see cref="ScreenshotTranslationRenderer"/>，这里只负责把结果画到选区上。
/// </summary>
public partial class CaptureOverlay
{
    /// <summary>放不下时最多缩到原字号的比例。</summary>
    private const double MinFontScale = 0.7;
    private const double MinTranslationFontSize = 9;

    private TranslationRenderPlan? _translationPlan;

    /// <summary>把原位翻译结果渲染到选区：底图换成擦除原文后的图，译文按原样式排版。</summary>
    private void ShowTranslationParagraphs(ScreenshotTranslationResult result, TranslationRenderPlan plan)
    {
        ClearBlockTranslation();
        translationOverlay.Visibility = Visibility.Collapsed;
        translationOverlayText.Text = string.Empty;
        _translationPlan = plan;

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var fontFamily = TranslationFontFamily(result.TargetLanguage);

        var backgroundImage = new System.Windows.Controls.Image
        {
            Source = BitmapInterop.ToBitmapSource(plan.Background),
            Width = _selection.Width,
            Height = _selection.Height,
            Stretch = Stretch.Fill
        };
        RenderOptions.SetBitmapScalingMode(backgroundImage, BitmapScalingMode.NearestNeighbor);
        translationBlockCanvas.Children.Add(backgroundImage);

        var truncated = 0;
        for (var i = 0; i < plan.Blocks.Count && i < result.Translations.Count; i++)
        {
            var style = plan.Blocks[i];
            var translation = result.Translations[i];
            var weight = style.Bold ? FontWeights.Bold : FontWeights.Normal;
            var typeface = new Typeface(fontFamily, FontStyles.Normal, weight, FontStretches.Normal);
            var lineRatio = Math.Clamp(style.LinePitchPx / Math.Max(1, style.FontPx), 1.15, 1.8);
            var baseSize = Math.Max(MinTranslationFontSize, style.FontPx / BitmapScaleY);

            // 依次尝试：原区域原字号 → 扩展区域原字号 → 扩展区域缩小字号（下限原字号的 70%）。
            var box = PhysicalLineToDipRect(style.Box);
            var maxBox = PhysicalLineToDipRect(style.MaxBox);
            var fontSize = baseSize;
            var area = box;
            if (!Fits(translation, typeface, baseSize, lineRatio, box, pixelsPerDip))
            {
                if (Fits(translation, typeface, baseSize, lineRatio, maxBox, pixelsPerDip))
                {
                    // 扩展区域放得下时，高度只取实际需要的部分。
                    var needed = MeasureTextHeight(translation, typeface, baseSize, lineRatio, maxBox.Width, pixelsPerDip);
                    area = new Rect(maxBox.X, maxBox.Y, maxBox.Width, Math.Max(box.Height, Math.Min(maxBox.Height, needed)));
                }
                else
                {
                    area = maxBox;
                    var minSize = Math.Max(MinTranslationFontSize, baseSize * MinFontScale);
                    fontSize = ScreenshotTextLayout.FitFontSize(
                        baseSize,
                        minSize,
                        size => Fits(translation, typeface, size, lineRatio, maxBox, pixelsPerDip));
                    if (!Fits(translation, typeface, fontSize, lineRatio, maxBox, pixelsPerDip))
                        truncated++;
                }
            }

            var text = new TextBlock
            {
                Text = translation,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontFamily = fontFamily,
                FontWeight = weight,
                FontSize = fontSize,
                LineHeight = Math.Ceiling(fontSize * lineRatio),
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(style.TextColor.R, style.TextColor.G, style.TextColor.B)),
                TextAlignment = style.Alignment switch
                {
                    TranslationTextAlignment.Center => TextAlignment.Center,
                    TranslationTextAlignment.Right => TextAlignment.Right,
                    _ => TextAlignment.Left
                },
                Width = area.Width,
                MaxHeight = area.Height,
                ClipToBounds = true
            };

            // 译文比原文矮时（如单行），在原文区域内垂直居中。
            var measured = Math.Min(area.Height, MeasureTextHeight(translation, typeface, fontSize, lineRatio, area.Width, pixelsPerDip));
            var top = measured < box.Height ? box.Y + (box.Height - measured) / 2 : area.Y;
            Canvas.SetLeft(text, area.X);
            Canvas.SetTop(text, top);
            translationBlockCanvas.Children.Add(text);
        }

        if (truncated > 0)
            Log.Information("[ScreenshotTranslate] truncated blocks={Count}", truncated);

        translationBlockCanvas.Visibility = Visibility.Visible;
        UpdateVisuals();
    }

    private System.Windows.Media.FontFamily TranslationFontFamily(string targetLanguage)
    {
        var key = targetLanguage switch
        {
            "zh" or "zh-CHS" or "zh-Hans" or "zh-TW" => "ScreenshotTranslationFontZh",
            "ja" => "ScreenshotTranslationFontJa",
            "ko" => "ScreenshotTranslationFontKo",
            _ => "ScreenshotTranslationFontLatin"
        };
        return (System.Windows.Media.FontFamily)FindResource(key);
    }

    private static bool Fits(string text, Typeface typeface, double size, double lineRatio, Rect area, double pixelsPerDip) =>
        MeasureTextHeight(text, typeface, size, lineRatio, area.Width, pixelsPerDip) <= area.Height + 0.5;

    private static double MeasureTextHeight(string text, Typeface typeface, double size, double lineRatio, double width, double pixelsPerDip)
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
            LineHeight = Math.Ceiling(size * lineRatio)
        };
        return formatted.Height;
    }

    /// <summary>按住 Ctrl 临时显示原图，松开恢复译文。</summary>
    private void SetTranslationPeek(bool showOriginal)
    {
        if (translationBlockCanvas.Visibility != Visibility.Visible)
            return;
        translationBlockCanvas.Opacity = showOriginal ? 0 : 1;
    }

    private void ClearBlockTranslation()
    {
        translationBlockCanvas.Children.Clear();
        translationBlockCanvas.Opacity = 1;
        _translationPlan?.Dispose();
        _translationPlan = null;
    }

    private Rect PhysicalLineToDipRect(System.Drawing.Rectangle box)
    {
        return new Rect(
            box.X / BitmapScaleX,
            box.Y / BitmapScaleY,
            box.Width / BitmapScaleX,
            box.Height / BitmapScaleY);
    }

    private void ShowTranslationOverlay(string text)
    {
        translationBlockCanvas.Visibility = Visibility.Collapsed;
        ClearBlockTranslation();
        translationLoadingIndicator.Visibility = Visibility.Collapsed;
        translationOverlayScroll.Visibility = Visibility.Visible;
        translationOverlayText.Text = text;
        translationOverlay.Visibility = Visibility.Visible;
        UpdateVisuals();
    }

    private void ShowTranslationLoading()
    {
        translationStageText.Text = "正在识别文字… 再次点击翻译可取消";
        translationBlockCanvas.Visibility = Visibility.Collapsed;
        ClearBlockTranslation();
        translationOverlayText.Text = string.Empty;
        translationOverlayScroll.Visibility = Visibility.Collapsed;
        translationLoadingIndicator.Visibility = Visibility.Visible;
        translationOverlay.Visibility = Visibility.Visible;
        UpdateVisuals();
    }

    private void HideTranslationOverlay()
    {
        translationBlockCanvas.Visibility = Visibility.Collapsed;
        ClearBlockTranslation();
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
