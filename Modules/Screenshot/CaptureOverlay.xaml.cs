using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using STool.Modules.Screenshot.Annotations;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using Rectangle = System.Windows.Shapes.Rectangle;
using Cursors = System.Windows.Input.Cursors;
using Cursor = System.Windows.Input.Cursor;
using Button = System.Windows.Controls.Button;
using Panel = System.Windows.Controls.Panel;

namespace STool.Modules.Screenshot;

/// <summary>
/// 微信式一体化截图取景窗:冻结屏幕 → 默认全屏(工作区)选区 → 框内清晰框外淡蒙版 →
/// 8 手柄缩放/拖动移动/空白重框 → 内联标注 → 内嵌工具条(随选区定位且夹在屏内) →
/// 回车/✓ 复制到剪贴板。Esc 取消。
/// </summary>
public partial class CaptureOverlay : Window
{
    private enum DragMode { None, NewSelection, Move, Resize }

    private System.Drawing.Bitmap? _frozen;
    private double _scaleX = 1, _scaleY = 1;          // DIP → 物理像素
    private Rect _selection;
    private DragMode _dragMode = DragMode.None;
    private string _activeHandle = "";
    private Point _dragStart;
    private Rect _selectionAtStart;
    private bool _closing;
    private bool _isDefaultSelection = true;   // 仍为默认整屏选区:任意拖拽 = 新建选区

    private readonly Rectangle[] _handles = new Rectangle[8];
    private static readonly string[] HandleRoles = { "TL", "T", "TR", "L", "R", "BL", "B", "BR" };

    private AnnotationCanvas? _annotation;
    private AnnotationTool _currentTool = AnnotationTool.None;
    private Button[] _toolButtons = Array.Empty<Button>();

    public CaptureOverlay()
    {
        InitializeComponent();

        // 覆盖整个虚拟屏幕(DIP)
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        // 冻结屏幕
        _frozen = ScreenCapture.CaptureAllScreens();
        screenshotImage.Source = ToBitmapSource(_frozen);

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var t = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        _scaleX = t.M11; _scaleY = t.M22;

        CreateHandles();
        _annotation = new AnnotationCanvas(annotationCanvas);
        _toolButtons = new[] { btnRect, btnEllipse, btnArrow, btnPen };

        // 默认选区 = 主显示器工作区(排除任务栏),换算到窗口坐标
        var wa = SystemParameters.WorkArea;
        _selection = new Rect(
            wa.Left - SystemParameters.VirtualScreenLeft,
            wa.Top - SystemParameters.VirtualScreenTop,
            wa.Width, wa.Height);

        selectionBorder.Visibility = Visibility.Visible;
        toolbar.Visibility = Visibility.Visible;
        UpdateVisuals();
        Activate();
    }

    // ---------- 手柄 ----------
    private void CreateHandles()
    {
        for (int i = 0; i < 8; i++)
        {
            var h = new Rectangle
            {
                Width = 10,
                Height = 10,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(0x2D, 0xC4, 0x72)),
                StrokeThickness = 1.5,
                Tag = HandleRoles[i],
                Cursor = HandleCursor(HandleRoles[i])
            };
            h.MouseLeftButtonDown += Handle_MouseLeftButtonDown;
            _handles[i] = h;
            overlayCanvas.Children.Add(h);
            Panel.SetZIndex(h, 50);
        }
        Panel.SetZIndex(toolbar, 100);
    }

    private static Cursor HandleCursor(string role) => role switch
    {
        "TL" or "BR" => Cursors.SizeNWSE,
        "TR" or "BL" => Cursors.SizeNESW,
        "T" or "B" => Cursors.SizeNS,
        _ => Cursors.SizeWE,
    };

    private void Handle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_currentTool != AnnotationTool.None) return;
        _activeHandle = (string)((Rectangle)sender).Tag;
        _dragMode = DragMode.Resize;
        _dragStart = e.GetPosition(overlayCanvas);
        _selectionAtStart = _selection;
        overlayCanvas.CaptureMouse();
        e.Handled = true;
    }

    // ---------- 选区:新建 / 移动 ----------
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_currentTool != AnnotationTool.None) return;        // 标注模式交给 annotationCanvas
        if (IsOverToolbar(e)) return;

        var p = e.GetPosition(overlayCanvas);
        _dragStart = p;
        _selectionAtStart = _selection;

        // 双击选区内 = 复制并完成
        if (e.ClickCount == 2 && _selection.Contains(p))
        {
            CopyAndClose();
            return;
        }

        if (!_isDefaultSelection && _selection.Contains(p))
        {
            _dragMode = DragMode.Move;
        }
        else
        {
            // 默认整屏选区下、或点在选区外 → 拖出新选区
            _dragMode = DragMode.NewSelection;
            _selection = new Rect(p.X, p.Y, 0, 0);
            UpdateVisuals();
        }
        overlayCanvas.CaptureMouse();
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragMode == DragMode.None) return;

        var p = e.GetPosition(overlayCanvas);
        var dx = p.X - _dragStart.X;
        var dy = p.Y - _dragStart.Y;

        switch (_dragMode)
        {
            case DragMode.NewSelection:
                _selection = new Rect(
                    Math.Min(_dragStart.X, p.X),
                    Math.Min(_dragStart.Y, p.Y),
                    Math.Abs(p.X - _dragStart.X),
                    Math.Abs(p.Y - _dragStart.Y));
                break;

            case DragMode.Move:
                var nx = Clamp(_selectionAtStart.X + dx, 0, ActualW - _selectionAtStart.Width);
                var ny = Clamp(_selectionAtStart.Y + dy, 0, ActualH - _selectionAtStart.Height);
                _selection = new Rect(nx, ny, _selectionAtStart.Width, _selectionAtStart.Height);
                break;

            case DragMode.Resize:
                _selection = ResizeRect(_selectionAtStart, _activeHandle, dx, dy);
                break;
        }
        UpdateVisuals();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragMode == DragMode.None) return;
        var mode = _dragMode;
        _dragMode = DragMode.None;
        overlayCanvas.ReleaseMouseCapture();

        if (_selection.Width < 8 || _selection.Height < 8)
        {
            // 选区过小 → 恢复整屏工作区默认
            var wa = SystemParameters.WorkArea;
            _selection = new Rect(wa.Left - SystemParameters.VirtualScreenLeft,
                                  wa.Top - SystemParameters.VirtualScreenTop, wa.Width, wa.Height);
            _isDefaultSelection = true;
        }
        else if (mode == DragMode.NewSelection)
        {
            _isDefaultSelection = false;   // 用户已手动框选,之后框内拖=移动
        }
        UpdateVisuals();
    }

    private Rect ResizeRect(Rect s, string role, double dx, double dy)
    {
        double l = s.Left, t = s.Top, r = s.Right, b = s.Bottom;
        if (role.Contains('L')) l += dx;
        if (role.Contains('R')) r += dx;
        if (role.Contains('T')) t += dy;
        if (role.Contains('B')) b += dy;
        if (role == "T" || role == "B") { /* 仅纵向 */ }
        // 规整 + 夹紧
        l = Clamp(l, 0, ActualW); r = Clamp(r, 0, ActualW);
        t = Clamp(t, 0, ActualH); b = Clamp(b, 0, ActualH);
        var x = Math.Min(l, r); var y = Math.Min(t, b);
        var w = Math.Max(8, Math.Abs(r - l)); var h = Math.Max(8, Math.Abs(b - t));
        return new Rect(x, y, w, h);
    }

    // ---------- 视觉更新 ----------
    private double ActualW => overlayCanvas.ActualWidth > 0 ? overlayCanvas.ActualWidth : Width;
    private double ActualH => overlayCanvas.ActualHeight > 0 ? overlayCanvas.ActualHeight : Height;

    private void UpdateVisuals()
    {
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

        PositionToolbar();
    }

    private void PositionHandles()
    {
        bool show = _currentTool == AnnotationTool.None;
        double x = _selection.X, y = _selection.Y, w = _selection.Width, h = _selection.Height;
        var pts = new (double, double)[]
        {
            (x, y), (x + w/2, y), (x + w, y),
            (x, y + h/2), (x + w, y + h/2),
            (x, y + h), (x + w/2, y + h), (x + w, y + h),
        };
        for (int i = 0; i < 8; i++)
        {
            _handles[i].Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            Canvas.SetLeft(_handles[i], pts[i].Item1 - 5);
            Canvas.SetTop(_handles[i], pts[i].Item2 - 5);
        }
    }

    private void PositionToolbar()
    {
        toolbar.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        double tbW = toolbar.DesiredSize.Width, tbH = toolbar.DesiredSize.Height;

        double tx = _selection.X + (_selection.Width - tbW) / 2;
        double ty = _selection.Bottom + 8;
        if (ty + tbH > ActualH - 2)            // 下方放不下 → 翻到上方
            ty = _selection.Y - tbH - 8;
        if (ty < 2)                             // 上方也放不下(选区贴顶/占满)→ 压在选区内底部
            ty = Math.Max(2, _selection.Bottom - tbH - 8);

        tx = Clamp(tx, 4, ActualW - tbW - 4);
        Canvas.SetLeft(toolbar, tx);
        Canvas.SetTop(toolbar, ty);
    }

    private bool IsOverToolbar(MouseButtonEventArgs e)
    {
        if (toolbar.Visibility != Visibility.Visible) return false;
        var p = e.GetPosition(toolbar);
        return p.X >= 0 && p.Y >= 0 && p.X <= toolbar.ActualWidth && p.Y <= toolbar.ActualHeight;
    }

    // ---------- 工具条:标注工具 ----------
    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        var tag = (string)((Button)sender).Tag;
        var tool = tag switch
        {
            "Rectangle" => AnnotationTool.Rectangle,
            "Ellipse" => AnnotationTool.Ellipse,
            "Arrow" => AnnotationTool.Arrow,
            "Pen" => AnnotationTool.Pen,
            _ => AnnotationTool.None,
        };
        // 再次点击当前工具 → 取消(回到选区模式)
        _currentTool = _currentTool == tool ? AnnotationTool.None : tool;
        if (_annotation != null) _annotation.CurrentTool = _currentTool;

        annotationCanvas.IsHitTestVisible = _currentTool != AnnotationTool.None;
        Cursor = _currentTool != AnnotationTool.None ? Cursors.Arrow : Cursors.Cross;

        // 高亮当前工具按钮
        foreach (var b in _toolButtons)
            b.Tag = b.Tag; // 无操作占位
        HighlightTools();
        PositionHandles();
    }

    private void HighlightTools()
    {
        foreach (var b in _toolButtons)
        {
            var active = (b == btnRect && _currentTool == AnnotationTool.Rectangle)
                      || (b == btnEllipse && _currentTool == AnnotationTool.Ellipse)
                      || (b == btnArrow && _currentTool == AnnotationTool.Arrow)
                      || (b == btnPen && _currentTool == AnnotationTool.Pen);
            b.Background = active ? new SolidColorBrush(Color.FromArgb(0x33, 0x25, 0x63, 0xEB)) : Brushes.Transparent;
        }
    }

    private void BtnUndo_Click(object sender, RoutedEventArgs e) => _annotation?.Undo();
    private void BtnRedo_Click(object sender, RoutedEventArgs e) => _annotation?.Redo();

    // ---------- 动作 ----------
    private void BtnConfirm_Click(object sender, RoutedEventArgs e) => CopyAndClose();

    private void CopyAndClose()
    {
        try
        {
            using var bmp = RenderSelectionBitmap();
            System.Windows.Clipboard.SetImage(ToBitmapSource(bmp));
        }
        catch { /* 忽略,直接关闭 */ }
        CloseOverlay();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var bmp = RenderSelectionBitmap();
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "STool");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            CloseOverlay();
            Core.ToastNotification.Show("截图已保存", file, Core.ToastNotification.ToastType.Success);
        }
        catch (Exception ex)
        {
            Core.ToastNotification.Show("保存失败", ex.Message, Core.ToastNotification.ToastType.Error);
        }
    }

    private void BtnPin_Click(object sender, RoutedEventArgs e)
    {
        var bmp = RenderSelectionBitmap();
        CloseOverlay();
        new PinWindow(bmp).Show();
    }

    private async void BtnOcr_Click(object sender, RoutedEventArgs e)
    {
        var bmp = RenderSelectionBitmap();
        CloseOverlay();
        var ocr = ((App)System.Windows.Application.Current).GetService<STool.Modules.Ocr.OcrManager>();
        if (ocr == null) { Core.ToastNotification.Show("OCR 不可用", "服务未初始化", Core.ToastNotification.ToastType.Error); bmp.Dispose(); return; }
        try
        {
            var result = await ocr.RecognizeAsync(bmp);
            new STool.Modules.Ocr.OcrResultWindow(result.FullText, result.Provider).Show();
        }
        catch (Exception ex) { Core.ToastNotification.Show("OCR 失败", ex.Message, Core.ToastNotification.ToastType.Error); }
        finally { bmp.Dispose(); }
    }

    private async void BtnTranslate_Click(object sender, RoutedEventArgs e)
    {
        var bmp = RenderSelectionBitmap();
        CloseOverlay();
        var ocr = ((App)System.Windows.Application.Current).GetService<STool.Modules.Ocr.OcrManager>();
        var tr = ((App)System.Windows.Application.Current).GetService<STool.Modules.Translation.TranslationManager>();
        if (ocr == null || tr == null) { Core.ToastNotification.Show("翻译不可用", "服务未初始化", Core.ToastNotification.ToastType.Error); bmp.Dispose(); return; }
        try
        {
            var o = await ocr.RecognizeAsync(bmp);
            if (!o.Success || string.IsNullOrWhiteSpace(o.FullText))
            { Core.ToastNotification.Show("OCR 失败", o.ErrorMessage ?? "未识别到文字", Core.ToastNotification.ToastType.Warning); return; }
            var t = await tr.TranslateAsync(o.FullText);
            if (!t.Success) { Core.ToastNotification.Show("翻译失败", t.ErrorMessage ?? "", Core.ToastNotification.ToastType.Error); return; }
            new STool.Modules.Translation.InPlaceTranslationWindow(o.FullText, t.TranslatedText, t.Provider).Show();
        }
        catch (Exception ex) { Core.ToastNotification.Show("翻译失败", ex.Message, Core.ToastNotification.ToastType.Error); }
        finally { bmp.Dispose(); }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => CloseOverlay();

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseOverlay(); return; }
        if (e.Key == Key.Enter) { CopyAndClose(); return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) { _annotation?.Undo(); return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y) { _annotation?.Redo(); return; }
    }

    // ---------- 合成输出 ----------
    private System.Drawing.Bitmap RenderSelectionBitmap()
    {
        int px = (int)Math.Round(_selection.X * _scaleX);
        int py = (int)Math.Round(_selection.Y * _scaleY);
        int pw = Math.Max(1, (int)Math.Round(_selection.Width * _scaleX));
        int ph = Math.Max(1, (int)Math.Round(_selection.Height * _scaleY));

        // 裁剪冻结图
        pw = Math.Min(pw, _frozen!.Width - px);
        ph = Math.Min(ph, _frozen.Height - py);
        var crop = _frozen.Clone(new System.Drawing.Rectangle(px, py, Math.Max(1, pw), Math.Max(1, ph)), _frozen.PixelFormat);

        // 无标注则直接返回裁剪图
        if (annotationCanvas.Children.Count == 0)
            return crop;

        // 合成标注层
        var baseSource = ToBitmapSource(crop);
        var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            ctx.DrawImage(baseSource, new Rect(0, 0, pw, ph));
            var vb = new VisualBrush(annotationCanvas) { Stretch = Stretch.Fill };
            ctx.DrawRectangle(vb, null, new Rect(0, 0, pw, ph));
        }
        rtb.Render(visual);
        crop.Dispose();
        return BitmapSourceToBitmap(rtb);
    }

    private (int, int) ToPhysicalSize(double w, double h)
        => (Math.Max(1, (int)Math.Round(w * _scaleX)), Math.Max(1, (int)Math.Round(h * _scaleY)));

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

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
        _frozen?.Dispose();
        _frozen = null;
        base.OnClosed(e);
    }
}
