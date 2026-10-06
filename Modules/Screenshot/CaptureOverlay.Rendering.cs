using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using STool.Core;
using STool.Modules.Screenshot.Annotations;

namespace STool.Modules.Screenshot;

/// <summary>
/// CaptureOverlay 渲染（视觉更新、蒙版、马赛克图层、放大镜）
/// </summary>
public partial class CaptureOverlay
{
    private const int MagnifierColumns = 15;
    private const int MagnifierRows = 11;
    private const double MagnifierOffset = 18;

    private BitmapSource? _mosaicLayer;
    private ImageBrush? _magnifierBrush;
    private string _magnifierColorHex = string.Empty;

    private double ActualW => overlayCanvas.ActualWidth > 0 ? overlayCanvas.ActualWidth : Width;
    private double ActualH => overlayCanvas.ActualHeight > 0 ? overlayCanvas.ActualHeight : Height;

    private void UpdateVisuals()
    {
        if (_closing || !_interactionReady)
            return;

        // 边框
        Canvas.SetLeft(selectionBorder, _selection.X);
        Canvas.SetTop(selectionBorder, _selection.Y);
        selectionBorder.Width = _selection.Width;
        selectionBorder.Height = _selection.Height;

        // 挖洞蒙版
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(new RectangleGeometry(new Rect(0, 0, ActualW, ActualH)));
        group.Children.Add(new RectangleGeometry(_selection));
        maskPath.Data = group;

        // 手柄位置
        PositionHandles();

        // 尺寸标签
        var size = ToPhysicalSize(_selection.Width, _selection.Height);
        sizeText.Text = $"{size.Item1} × {size.Item2}";
        sizeLabel.Visibility = Visibility.Visible;
        double labelTop = _selection.Y - 28;
        if (labelTop < 2) labelTop = _selection.Y + 4;
        Canvas.SetLeft(sizeLabel, Math.Max(2, _selection.X));
        Canvas.SetTop(sizeLabel, labelTop);

        // 标注层与原位翻译层贴合选区
        PlaceOnSelection(annotationCanvas);
        PlaceOnSelection(translationBlockCanvas);
        PlaceOnSelection(translationOverlay);
        ApplyTranslationOverlayLayout();

        // 选区移动后马赛克要显示新位置下方的内容
        if (_mosaicLayer != null)
            _annotation?.SetMosaicSource(_mosaicLayer, MosaicSourceRect());

        PositionToolbar();
    }

    private void PlaceOnSelection(FrameworkElement element)
    {
        Canvas.SetLeft(element, _selection.X);
        Canvas.SetTop(element, _selection.Y);
        element.Width = _selection.Width;
        element.Height = _selection.Height;
        element.Clip = new RectangleGeometry(new Rect(0, 0, _selection.Width, _selection.Height));
    }

    /// <summary>
    /// 首次使用马赛克时生成整屏像素化图层:按块大小缩小后以最近邻放大显示,
    /// 预览与导出共用,块大小不随 DPI 变化。
    /// </summary>
    private void EnsureMosaicLayer()
    {
        if (_mosaicLayer != null || _capture == null || _annotation == null)
            return;

        var blockPixels = Math.Max(4, (int)Math.Round(MosaicAnnotation.BlockSize * CoordinateMapper.PixelsPerDipX));
        var scale = 1.0 / blockPixels;
        var reduced = new TransformedBitmap(_capture.Source, new ScaleTransform(scale, scale));
        var layer = new WriteableBitmap(reduced);
        layer.Freeze();
        _mosaicLayer = layer;
        _annotation.SetMosaicSource(layer, MosaicSourceRect());
    }

    /// <summary>整屏马赛克图层在标注画布(选区)局部坐标中的位置。</summary>
    private Rect MosaicSourceRect() => new(
        -_selection.X,
        -_selection.Y,
        CoordinateMapper.CanvasSize.Width,
        CoordinateMapper.CanvasSize.Height);

    private void PositionHandles()
    {
        bool show = _confirmed && _currentTool == AnnotationTool.None;
        if (!_handlesReady)
            return;

        double x = _selection.X, y = _selection.Y, w = _selection.Width, h = _selection.Height;
        var pts = new (double, double)[]
        {
            (x, y), (x + w/2, y), (x + w, y),
            (x, y + h/2), (x + w, y + h/2),
            (x, y + h), (x + w/2, y + h), (x + w, y + h),
        };
        for (int i = 0; i < 8; i++)
        {
            if (_handles[i] == null)
                continue;

            _handles[i].Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            Canvas.SetLeft(_handles[i], pts[i].Item1 - 5);
            Canvas.SetTop(_handles[i], pts[i].Item2 - 5);
        }
    }

    // ---------- 放大镜与取色 ----------

    private void UpdateMagnifier(System.Windows.Point canvasPoint)
    {
        if (_magnifierBrush == null || _capture == null)
            return;

        var px = Math.Clamp((int)Math.Floor(canvasPoint.X * BitmapScaleX), 0, _capture.Width - 1);
        var py = Math.Clamp((int)Math.Floor(canvasPoint.Y * BitmapScaleY), 0, _capture.Height - 1);
        _magnifierBrush.Viewbox = new Rect(px - MagnifierColumns / 2, py - MagnifierRows / 2, MagnifierColumns, MagnifierRows);

        var color = _capture.Bitmap.GetPixel(px, py);
        _magnifierColorHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        magnifierColor.Text = _magnifierColorHex;
        magnifierSwatch.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(color.R, color.G, color.B));
        magnifierPosition.Text = $"{px + _virtualScreenBounds.Left}, {py + _virtualScreenBounds.Top}";

        magnifier.Visibility = Visibility.Visible;
        magnifier.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = magnifier.DesiredSize.Width;
        var height = magnifier.DesiredSize.Height;
        var left = canvasPoint.X + MagnifierOffset;
        var top = canvasPoint.Y + MagnifierOffset;
        if (left + width > ActualW - 2)
            left = canvasPoint.X - MagnifierOffset - width;
        if (top + height > ActualH - 2)
            top = canvasPoint.Y - MagnifierOffset - height;
        Canvas.SetLeft(magnifier, Math.Max(2, left));
        Canvas.SetTop(magnifier, Math.Max(2, top));
    }

    private void HideMagnifier()
    {
        magnifier.Visibility = Visibility.Collapsed;
    }

    private async void CopyPixelColor()
    {
        var hex = _magnifierColorHex;
        if (string.IsNullOrEmpty(hex))
            return;

        try
        {
            await ClipboardWriter.SetTextAsync(hex);
            ToastNotification.Show("已复制颜色", hex, ToastNotification.ToastType.Success, duration: 1600);
        }
        catch (Exception ex)
        {
            ToastNotification.Show("复制失败", ex.Message, ToastNotification.ToastType.Error);
        }
    }
}
