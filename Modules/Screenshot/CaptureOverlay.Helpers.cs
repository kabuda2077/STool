using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;
using STool.Core;
using STool.Modules.Screenshot.Annotations;

namespace STool.Modules.Screenshot;

/// <summary>
/// CaptureOverlay 辅助方法（坐标转换、位图工具）
/// </summary>
public partial class CaptureOverlay
{
    private VirtualDesktopCoordinateMapper CoordinateMapper =>
        _coordinateMapper ?? throw new InvalidOperationException("Screenshot coordinate mapper is not initialized.");

    private double BitmapScaleX => _frozen == null ? 1 : _frozen.Width / CoordinateMapper.CanvasSize.Width;
    private double BitmapScaleY => _frozen == null ? 1 : _frozen.Height / CoordinateMapper.CanvasSize.Height;
    private System.Drawing.Rectangle SelectionBitmapRect =>
        CoordinateMapper.CanvasToBitmap(_selection, _frozen!.Width, _frozen.Height);

    private System.Drawing.Bitmap RenderSelectionBitmap()
    {
        MemoryDiagnostics.LogCheckpoint("ScreenshotRenderStarted");
        var frozen = _frozen ?? throw new InvalidOperationException("Screenshot bitmap is not available.");
        var pixelRect = CoordinateMapper.CanvasToBitmap(_selection, frozen.Width, frozen.Height);
        var px = pixelRect.X;
        var py = pixelRect.Y;
        var pw = pixelRect.Width;
        var ph = pixelRect.Height;

        // 裁剪冻结图
        var crop = frozen.Clone(new System.Drawing.Rectangle(px, py, Math.Max(1, pw), Math.Max(1, ph)), frozen.PixelFormat);

        var hasTranslation = translationOverlay.Visibility == Visibility.Visible;
        var hasBlockTranslation = translationBlockCanvas.Visibility == Visibility.Visible;
        var hasAnnotations = annotationCanvas.Children.Count > 0;
        var mosaicAnnotations = annotationCanvas.Children
            .OfType<MosaicAnnotation>()
            .Where(m => m.ActualWidth > 0 && m.ActualHeight > 0)
            .ToList();

        if (hasTranslation || hasBlockTranslation)
        {
            Log.Information(
                "[ScreenshotRender] Compose translation overlay={Overlay} block={Block} size={Width}x{Height}",
                hasTranslation,
                hasBlockTranslation,
                pw,
                ph);
        }

        // 无标注/译文则直接返回裁剪图
        if (!hasAnnotations && !hasTranslation && !hasBlockTranslation)
        {
            MemoryDiagnostics.LogCheckpoint("ScreenshotRenderCompleted");
            return crop;
        }

        try
        {
            if (mosaicAnnotations.Count > 0)
                ApplyMosaicAnnotations(crop, mosaicAnnotations);

            // 合成标注层
            var baseSource = ToBitmapSource(crop);
            var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var ctx = visual.RenderOpen())
            {
                ctx.DrawImage(baseSource, new Rect(0, 0, pw, ph));
                if (hasTranslation)
                {
                    DrawElementSnapshot(ctx, translationOverlay, pw, ph);
                }
                if (hasBlockTranslation)
                {
                    DrawElementSnapshot(ctx, translationBlockCanvas, pw, ph);
                }
                if (hasAnnotations)
                {
                    var previousVisibility = mosaicAnnotations
                        .Select(mosaic => (Mosaic: mosaic, mosaic.Visibility))
                        .ToList();

                    try
                    {
                        foreach (var mosaic in mosaicAnnotations)
                            mosaic.Visibility = Visibility.Collapsed;

                        var vb = new VisualBrush(annotationCanvas) { Stretch = Stretch.Fill };
                        ctx.DrawRectangle(vb, null, new Rect(0, 0, pw, ph));
                    }
                    finally
                    {
                        foreach (var item in previousVisibility)
                            item.Mosaic.Visibility = item.Visibility;
                    }
                }
            }

            rtb.Render(visual);
            var result = BitmapSourceToBitmap(rtb);
            MemoryDiagnostics.LogCheckpoint("ScreenshotRenderCompleted");
            return result;
        }
        finally
        {
            crop.Dispose();
        }
    }

    private static void DrawElementSnapshot(DrawingContext ctx, FrameworkElement element, int pixelWidth, int pixelHeight)
    {
        element.UpdateLayout();

        var dipWidth = Math.Max(1, element.ActualWidth > 0 ? element.ActualWidth : element.Width);
        var dipHeight = Math.Max(1, element.ActualHeight > 0 ? element.ActualHeight : element.Height);
        var snapshotWidth = Math.Max(1, (int)Math.Ceiling(dipWidth));
        var snapshotHeight = Math.Max(1, (int)Math.Ceiling(dipHeight));
        var snapshot = new RenderTargetBitmap(snapshotWidth, snapshotHeight, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();

        using (var snapshotContext = visual.RenderOpen())
        {
            var brush = new VisualBrush(element)
            {
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, dipWidth, dipHeight),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, dipWidth, dipHeight)
            };

            snapshotContext.DrawRectangle(brush, null, new Rect(0, 0, dipWidth, dipHeight));
        }

        snapshot.Render(visual);
        ctx.DrawImage(snapshot, new Rect(0, 0, pixelWidth, pixelHeight));
    }

    private void ApplyMosaicAnnotations(System.Drawing.Bitmap bitmap, IEnumerable<MosaicAnnotation> mosaics)
    {
        foreach (var mosaic in mosaics)
        {
            var brushSize = Math.Max(10, (int)Math.Round(mosaic.BrushSize * BitmapScaleX));
            foreach (var point in mosaic.Points)
            {
                var centerX = (int)Math.Round(point.X * BitmapScaleX);
                var centerY = (int)Math.Round(point.Y * BitmapScaleY);
                ApplyMosaic(bitmap, new System.Drawing.Rectangle(
                    centerX - brushSize / 2,
                    centerY - brushSize / 2,
                    brushSize,
                    brushSize));
            }
        }
    }

    private void ApplyTranslationRenderBlocks(System.Drawing.Bitmap bitmap, IReadOnlyList<TranslationRenderBlock> blocks)
    {
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var first = blocks.FirstOrDefault();
        if (first != null)
        {
            Log.Information(
                "[ScreenshotRender] Drawing translation blocks count={Count} firstRect={Rect} firstFont={FontSize} firstTextLength={TextLength}",
                blocks.Count,
                first.Rect,
                first.FontSize,
                first.Text.Length);
        }

        foreach (var block in blocks)
        {
            var rect = new System.Drawing.RectangleF(
                (float)(block.Rect.X * BitmapScaleX),
                (float)(block.Rect.Y * BitmapScaleY),
                (float)(block.Rect.Width * BitmapScaleX),
                (float)(block.Rect.Height * BitmapScaleY));

            if (rect.Width <= 1 || rect.Height <= 1)
                continue;

            using var backgroundBrush = new System.Drawing.SolidBrush(ToDrawingColor(block.Background));
            graphics.FillRectangle(backgroundBrush, rect);

            var paddingX = (float)(block.Padding.Left * BitmapScaleX);
            var paddingY = (float)(block.Padding.Top * BitmapScaleY);
            var textRect = new System.Drawing.RectangleF(
                rect.X + paddingX,
                rect.Y + paddingY,
                Math.Max(1, rect.Width - paddingX - (float)(block.Padding.Right * BitmapScaleX)),
                Math.Max(1, rect.Height - paddingY - (float)(block.Padding.Bottom * BitmapScaleY)));

            var fontSizePoints = (float)(block.FontSize * 72.0 / graphics.DpiY);
            using var font = new System.Drawing.Font("Microsoft YaHei UI", fontSizePoints, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            using var textBrush = new System.Drawing.SolidBrush(ToDrawingColor(block.Foreground));
            using var format = new System.Drawing.StringFormat
            {
                Alignment = System.Drawing.StringAlignment.Near,
                LineAlignment = System.Drawing.StringAlignment.Near,
                Trimming = System.Drawing.StringTrimming.EllipsisCharacter,
                FormatFlags = System.Drawing.StringFormatFlags.NoClip
            };

            graphics.DrawString(block.Text, font, textBrush, textRect, format);
        }
    }

    private static System.Drawing.Color ToDrawingColor(System.Windows.Media.Color color)
    {
        return System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
    }

    private static void ApplyMosaic(System.Drawing.Bitmap bitmap, System.Drawing.Rectangle region)
    {
        region.Intersect(new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
        if (region.Width <= 0 || region.Height <= 0)
            return;

        const int block = 12;
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        for (var y = region.Top; y < region.Bottom; y += block)
        {
            for (var x = region.Left; x < region.Right; x += block)
            {
                var w = Math.Min(block, region.Right - x);
                var h = Math.Min(block, region.Bottom - y);
                var color = AverageColor(bitmap, x, y, w, h);
                using var brush = new System.Drawing.SolidBrush(color);
                graphics.FillRectangle(brush, x, y, w, h);
            }
        }
    }

    private static System.Drawing.Color AverageColor(System.Drawing.Bitmap bitmap, int x, int y, int width, int height)
    {
        long a = 0, r = 0, g = 0, b = 0;
        var count = 0;
        for (var py = y; py < y + height; py++)
        {
            for (var px = x; px < x + width; px++)
            {
                var color = bitmap.GetPixel(px, py);
                a += color.A;
                r += color.R;
                g += color.G;
                b += color.B;
                count++;
            }
        }

        count = Math.Max(1, count);
        return System.Drawing.Color.FromArgb((int)(a / count), (int)(r / count), (int)(g / count), (int)(b / count));
    }

    private (int, int) ToPhysicalSize(double w, double h)
        => CoordinateMapper.CanvasToBitmapSize(w, h, _frozen!.Width, _frozen.Height);

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
}
