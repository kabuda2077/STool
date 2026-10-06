using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;
using STool.Core;

namespace STool.Modules.Screenshot;

/// <summary>
/// CaptureOverlay 辅助方法（坐标转换、合成最终图片）
/// </summary>
public partial class CaptureOverlay
{
    private VirtualDesktopCoordinateMapper CoordinateMapper =>
        _coordinateMapper ?? throw new InvalidOperationException("Screenshot coordinate mapper is not initialized.");

    private double BitmapScaleX => _capture == null ? 1 : _capture.Width / CoordinateMapper.CanvasSize.Width;
    private double BitmapScaleY => _capture == null ? 1 : _capture.Height / CoordinateMapper.CanvasSize.Height;

    /// <summary>
    /// 输出选区图片:冻结画面裁剪 + 译文层 + 标注层。各层以 VisualBrush 按物理像素尺寸直接绘制,
    /// 文字清晰,马赛克与屏幕上看到的完全一致。
    /// </summary>
    private System.Drawing.Bitmap RenderSelectionBitmap()
    {
        _annotation?.CommitText();
        MemoryDiagnostics.LogCheckpoint("ScreenshotRenderStarted");
        var frozen = _capture?.Bitmap ?? throw new InvalidOperationException("Screenshot bitmap is not available.");
        var pixelRect = CoordinateMapper.CanvasToBitmap(_selection, frozen.Width, frozen.Height);

        // 裁剪成独立的 ARGB 位图,与共享内存中的冻结画面脱钩。
        var crop = frozen.Clone(pixelRect, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        var hasTranslation = translationOverlay.Visibility == Visibility.Visible;
        var hasBlockTranslation = translationBlockCanvas.Visibility == Visibility.Visible;
        var hasAnnotations = annotationCanvas.Children.Count > 0;

        if (hasTranslation || hasBlockTranslation)
        {
            Log.Information(
                "[ScreenshotRender] Compose translation overlay={Overlay} block={Block} size={Width}x{Height}",
                hasTranslation,
                hasBlockTranslation,
                pixelRect.Width,
                pixelRect.Height);
        }

        // 无标注/译文则直接返回裁剪图
        if (!hasAnnotations && !hasTranslation && !hasBlockTranslation)
        {
            MemoryDiagnostics.LogCheckpoint("ScreenshotRenderCompleted");
            return crop;
        }

        try
        {
            var target = new Rect(0, 0, pixelRect.Width, pixelRect.Height);
            var rtb = new RenderTargetBitmap(pixelRect.Width, pixelRect.Height, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var ctx = visual.RenderOpen())
            {
                ctx.DrawImage(BitmapInterop.ToBitmapSource(crop), target);
                if (hasBlockTranslation)
                    DrawSelectionLayer(ctx, translationBlockCanvas, target);
                if (hasTranslation)
                    DrawSelectionLayer(ctx, translationOverlay, target);
                if (hasAnnotations)
                    DrawSelectionLayer(ctx, annotationCanvas, target);
            }

            rtb.Render(visual);
            var result = BitmapInterop.ToBitmap(rtb);
            MemoryDiagnostics.LogCheckpoint("ScreenshotRenderCompleted");
            return result;
        }
        finally
        {
            crop.Dispose();
        }
    }

    /// <summary>把贴合选区的图层按选区大小(DIP)映射到输出像素区域。</summary>
    private void DrawSelectionLayer(DrawingContext ctx, FrameworkElement layer, Rect target)
    {
        layer.UpdateLayout();
        var brush = new VisualBrush(layer)
        {
            Stretch = Stretch.Fill,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, Math.Max(1, _selection.Width), Math.Max(1, _selection.Height))
        };
        ctx.DrawRectangle(brush, null, target);
    }

    private (int, int) ToPhysicalSize(double w, double h)
        => CoordinateMapper.CanvasToBitmapSize(w, h, _capture!.Width, _capture.Height);

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
}
