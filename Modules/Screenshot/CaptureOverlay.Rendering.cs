using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using Serilog;
using STool.Modules.Screenshot.Annotations;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace STool.Modules.Screenshot;

/// <summary>
/// CaptureOverlay 渲染与输出（视觉更新、蒙版、合成最终图片）
/// </summary>
public partial class CaptureOverlay
{
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
        var outer = new RectangleGeometry(new Rect(0, 0, ActualW, ActualH));
        var inner = new RectangleGeometry(_selection);
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(outer);
        group.Children.Add(inner);
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

        // 标注层贴合选区
        Canvas.SetLeft(annotationCanvas, _selection.X);
        Canvas.SetTop(annotationCanvas, _selection.Y);
        annotationCanvas.Width = _selection.Width;
        annotationCanvas.Height = _selection.Height;
        annotationCanvas.Clip = new RectangleGeometry(new Rect(0, 0, _selection.Width, _selection.Height));

        // 原位翻译层贴合选区
        Canvas.SetLeft(translationBlockCanvas, _selection.X);
        Canvas.SetTop(translationBlockCanvas, _selection.Y);
        translationBlockCanvas.Width = _selection.Width;
        translationBlockCanvas.Height = _selection.Height;
        translationBlockCanvas.Clip = new RectangleGeometry(new Rect(0, 0, _selection.Width, _selection.Height));

        Canvas.SetLeft(translationOverlay, _selection.X);
        Canvas.SetTop(translationOverlay, _selection.Y);
        translationOverlay.Width = _selection.Width;
        translationOverlay.Height = _selection.Height;
        translationOverlay.Clip = new RectangleGeometry(new Rect(0, 0, _selection.Width, _selection.Height));
        ApplyTranslationOverlayLayout();

        PositionToolbar();
    }

    private void UpdateMosaicSource(bool force = false)
    {
        if (_annotation == null || _frozen == null)
            return;

        if (!force && _mosaicSourceSelection == _selection)
            return;

        _mosaicSourceSelection = _selection;

        var px = (int)Math.Round(_selection.X * _scaleX);
        var py = (int)Math.Round(_selection.Y * _scaleY);
        var pw = Math.Max(1, (int)Math.Round(_selection.Width * _scaleX));
        var ph = Math.Max(1, (int)Math.Round(_selection.Height * _scaleY));
        px = Math.Clamp(px, 0, Math.Max(0, _frozen.Width - 1));
        py = Math.Clamp(py, 0, Math.Max(0, _frozen.Height - 1));
        pw = Math.Min(pw, _frozen.Width - px);
        ph = Math.Min(ph, _frozen.Height - py);

        _annotation.MosaicSampler = SampleMosaicPreviewColor;
    }

    private System.Windows.Media.Color SampleMosaicPreviewColor(Rect rect)
    {
        if (_frozen == null)
            return System.Windows.Media.Color.FromRgb(160, 160, 166);

        var x = (int)Math.Round((_selection.X + rect.X) * _scaleX);
        var y = (int)Math.Round((_selection.Y + rect.Y) * _scaleY);
        var width = Math.Max(1, (int)Math.Round(rect.Width * _scaleX));
        var height = Math.Max(1, (int)Math.Round(rect.Height * _scaleY));

        var region = new System.Drawing.Rectangle(x, y, width, height);
        region.Intersect(new System.Drawing.Rectangle(0, 0, _frozen.Width, _frozen.Height));
        if (region.Width <= 0 || region.Height <= 0)
            return System.Windows.Media.Color.FromRgb(160, 160, 166);

        var color = AverageColor(_frozen, region.X, region.Y, region.Width, region.Height);
        return System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B);
    }

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

}
