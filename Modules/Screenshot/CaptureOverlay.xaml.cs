using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;
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
    private bool _confirmed;                    // 是否已确认选区(确认后才出工具条 + 手柄)
    private bool _dragMoved;                    // 本次按下后是否明显移动(区分点击与拖拽)
    private readonly List<System.Drawing.Rectangle> _windowRects = new();   // 底层窗口物理矩形(Z 序,顶层在前)

    private readonly Rectangle[] _handles = new Rectangle[8];
    private static readonly string[] HandleRoles = { "TL", "T", "TR", "L", "R", "BL", "B", "BR" };

    private AnnotationCanvas? _annotation;
    private AnnotationTool _currentTool = AnnotationTool.None;
    private Button[] _toolButtons = Array.Empty<Button>();
    private readonly Stopwatch? _startupTimer;
    private long _lastStartupMarkMs;
    private Rect _mosaicSourceSelection = Rect.Empty;

    public CaptureOverlay() : this(false) { }

    public CaptureOverlay(Stopwatch startupTimer) : this(false, startupTimer) { }

    /// <summary>
    /// 预热构造:仅触发 InitializeComponent(BAML 解析 + 模板/JIT 一次性成本),
    /// 跳过抓屏与 Loaded 逻辑,实例随即丢弃。用于消除首次截图的冷启动延迟。
    /// </summary>
    public static CaptureOverlay CreateForWarmUp() => new CaptureOverlay(true, null);

    private CaptureOverlay(bool prewarm) : this(prewarm, null) { }

    private CaptureOverlay(bool prewarm, Stopwatch? startupTimer)
    {
        _startupTimer = startupTimer;
        InitializeComponent();
        LogStartupStep("InitializeComponent");

        if (prewarm)
        {
            // 不抓屏、不挂 Loaded、不显示;只为把 WPF 一次性初始化成本提前付掉
            return;
        }

        Mouse.OverrideCursor = Cursors.Cross;
        Closed += (_, _) => Mouse.OverrideCursor = null;

        // 覆盖整个虚拟屏幕(DIP)
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        LogStartupStep("Window bounds prepared");

        // 冻结屏幕
        _frozen = ScreenCapture.CaptureAllScreens();
        LogStartupStep($"CaptureAllScreens {_frozen.Width}x{_frozen.Height}");
        screenshotImage.Source = BitmapInterop.ToBitmapSource(_frozen);
        LogStartupStep("BitmapInterop.ToBitmapSource");

        Loaded += OnLoaded;
    }

    private void LogStartupStep(string step)
    {
        if (_startupTimer == null)
            return;

        var elapsedMs = _startupTimer.ElapsedMilliseconds;
        Log.Information(
            "[CaptureStartup] {Step} at {ElapsedMs}ms (+{DeltaMs}ms)",
            step,
            elapsedMs,
            elapsedMs - _lastStartupMarkMs);
        _lastStartupMarkMs = elapsedMs;
    }

    private void UpdateCursorState()
    {
        var cursor = _currentTool != AnnotationTool.None || _confirmed ? Cursors.Arrow : Cursors.Cross;

        Mouse.OverrideCursor = _confirmed || _currentTool != AnnotationTool.None ? null : cursor;
        Cursor = cursor;
        overlayCanvas.Cursor = cursor;
    }

    /// <summary>句柄创建完成(在 Loaded 之前)。强制夺取前台键盘焦点(修复 Esc/Enter 失效)。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        LogStartupStep("SourceInitialized");
        ForceForeground();
        LogStartupStep("ForceForeground");
    }

    /// <summary>
    /// 后台进程(托盘+全局热键)弹出的窗口默认抢不到前台,导致 Esc/Enter 被原前台应用
    /// (如 always-on-top 的 dashboard)吃掉。这里用 AttachThreadInput 绕过前台锁定,
    /// 把前台窗口线程的输入队列临时附加到本线程,从而成功 SetForegroundWindow + 取键盘焦点。
    /// </summary>
    private void ForceForeground()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var foreHwnd = GetForegroundWindow();
            uint foreThread = foreHwnd != IntPtr.Zero ? GetWindowThreadProcessId(foreHwnd, IntPtr.Zero) : 0;
            uint thisThread = GetCurrentThreadId();

            bool attached = false;
            if (foreThread != 0 && foreThread != thisThread)
                attached = AttachThreadInput(foreThread, thisThread, true);

            SetForegroundWindow(hwnd);
            Activate();
            Focus();
            Keyboard.Focus(this);

            if (attached)
                AttachThreadInput(foreThread, thisThread, false);

            // 仅在未夺到键盘焦点时告警(Esc/Enter 会失效),正常成功不刷日志
            if (!IsKeyboardFocusWithin)
                Log.Warning("[Capture] ForceForeground did not gain keyboard focus (attached={Attached})", attached);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Capture] ForceForeground failed");
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var t = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        _scaleX = t.M11; _scaleY = t.M22;

        CreateHandles();
        _annotation = new AnnotationCanvas(annotationCanvas);
        _toolButtons = new[] { btnRect, btnEllipse, btnArrow, btnPen, btnMosaic };

        // 枚举底层窗口(用于"识别窗口"悬停吸附)
        EnumerateWindows();

        // 初始:识别光标所在窗口(无则工作区);未确认前不显示工具条
        _selection = DetectSelectionOrDefault();

        selectionBorder.Visibility = Visibility.Visible;
        toolbar.Visibility = Visibility.Collapsed;
        UpdateVisuals();
        UpdateMosaicSource(force: true);
        Activate();
        LogStartupStep($"Loaded complete windows={_windowRects.Count}");

        Dispatcher.BeginInvoke(() => LogStartupStep("First dispatcher frame"));
    }

    /// <summary>默认选区 = 光标所在显示器的工作区(物理像素换算到窗口 DIP 坐标)。</summary>
    private Rect DefaultSelectionRect()
    {
        var wa = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        return new Rect(
            wa.Left / _scaleX - SystemParameters.VirtualScreenLeft,
            wa.Top / _scaleY - SystemParameters.VirtualScreenTop,
            wa.Width / _scaleX,
            wa.Height / _scaleY);
    }

    // ---------- 识别窗口(悬停吸附) ----------
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr p);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out NRECT r);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out NRECT r, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);

    // 强制夺取前台焦点(绕过 Windows 前台锁定:把目标前台线程的输入队列临时附加到本线程)
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [StructLayout(LayoutKind.Sequential)] private struct NRECT { public int Left, Top, Right, Bottom; }
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;

    /// <summary>枚举覆盖层之下所有可见顶层窗口的物理矩形(EnumWindows 返回 Z 序,顶层在前)。</summary>
    private void EnumerateWindows()
    {
        _windowRects.Clear();
        var self = new WindowInteropHelper(this).Handle;
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == self || !IsWindowVisible(hwnd) || IsIconic(hwnd))
                return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;
            NRECT r;
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf<NRECT>()) != 0)
            {
                if (!GetWindowRect(hwnd, out r)) return true;
            }
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w < 24 || h < 24) return true;
            _windowRects.Add(new System.Drawing.Rectangle(r.Left, r.Top, w, h));
            return true;
        }, IntPtr.Zero);
    }

    /// <summary>光标所在的最上层窗口选区;无则回退到工作区。</summary>
    private Rect DetectSelectionOrDefault()
    {
        var c = System.Windows.Forms.Cursor.Position;   // 物理像素
        foreach (var r in _windowRects)
            if (r.Contains(c))
                return ClampSelection(PhysicalToSelection(r));
        return DefaultSelectionRect();
    }

    private Rect PhysicalToSelection(System.Drawing.Rectangle r) => new Rect(
        r.Left / _scaleX - SystemParameters.VirtualScreenLeft,
        r.Top / _scaleY - SystemParameters.VirtualScreenTop,
        r.Width / _scaleX,
        r.Height / _scaleY);

    private Rect ClampSelection(Rect r)
    {
        double x = Clamp(r.X, 0, ActualW);
        double y = Clamp(r.Y, 0, ActualH);
        double w = Clamp(r.Width, 1, ActualW - x);
        double h = Clamp(r.Height, 1, ActualH - y);
        return new Rect(x, y, w, h);
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
                Fill = ResourceBrush("SurfaceBrush"),
                Stroke = ResourceBrush("SuccessBrush"),
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
        toolbar.Visibility = Visibility.Collapsed;   // 调整过程中隐藏工具条
        overlayCanvas.CaptureMouse();
        e.Handled = true;
    }

    // ---------- 选区:新建 / 移动 ----------
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_currentTool != AnnotationTool.None) return;        // 标注模式交给 annotationCanvas
        if (IsOverToolbar(e)) return;

        HideTranslationOverlay();

        var p = e.GetPosition(overlayCanvas);
        _dragStart = p;
        _selectionAtStart = _selection;
        _dragMoved = false;

        // 双击选区内 = 复制并完成
        if (e.ClickCount == 2 && _selection.Contains(p))
        {
            CopyAndClose();
            return;
        }

        // 已确认且点在选区内 → 移动;否则:拖拽=新建选区,仅点击=确认当前(识别到的)选区
        _dragMode = (_confirmed && _selection.Contains(p)) ? DragMode.Move : DragMode.NewSelection;
        if (_dragMode == DragMode.NewSelection)
            Mouse.OverrideCursor = Cursors.Cross;

        toolbar.Visibility = Visibility.Collapsed;   // 选取过程中隐藏工具条
        overlayCanvas.CaptureMouse();
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);

        // 悬停识别窗口(未确认、且未按下拖拽时)
        if (!_confirmed && _dragMode == DragMode.None)
        {
            _selection = DetectSelectionOrDefault();
            UpdateVisuals();
            return;
        }

        if (_dragMode == DragMode.None) return;

        var p = e.GetPosition(overlayCanvas);
        var dx = p.X - _dragStart.X;
        var dy = p.Y - _dragStart.Y;

        // 死区:移动不足则不处理,保留"点击确认"的可能
        if (!_dragMoved && Math.Abs(dx) < 5 && Math.Abs(dy) < 5)
            return;
        _dragMoved = true;

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
        _dragMode = DragMode.None;
        overlayCanvas.ReleaseMouseCapture();

        if (!_confirmed)
        {
            // 首次确认:拖拽=手动选区(过小则回到识别/默认);仅点击=确认识别到的窗口
            if (_dragMoved && (_selection.Width < 8 || _selection.Height < 8))
                _selection = DetectSelectionOrDefault();
            _confirmed = true;
            toolbar.Visibility = Visibility.Visible;
            UpdateCursorState();
            UpdateVisuals();
            UpdateMosaicSource(force: true);
            return;
        }

        // 已确认后的收尾
        if (_selection.Width < 8 || _selection.Height < 8)
        {
            _selection = DefaultSelectionRect();
        }
        toolbar.Visibility = Visibility.Visible;   // 选取完成 → 显示工具条
        UpdateCursorState();
        UpdateVisuals();
        UpdateMosaicSource(force: true);
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

        // 原位翻译层贴合选区
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
            "Mosaic" => AnnotationTool.Mosaic,
            _ => AnnotationTool.None,
        };
        // 再次点击当前工具 → 取消(回到选区模式)
        _currentTool = _currentTool == tool ? AnnotationTool.None : tool;
        if (_annotation != null) _annotation.CurrentTool = _currentTool;

        annotationCanvas.IsHitTestVisible = _currentTool != AnnotationTool.None;
        UpdateCursorState();

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
                      || (b == btnPen && _currentTool == AnnotationTool.Pen)
                      || (b == btnMosaic && _currentTool == AnnotationTool.Mosaic);
            b.Background = active ? ResourceBrush("PrimarySoftBrush") : ResourceBrush("TransparentBrush");
        }
    }

    private Brush ResourceBrush(string key) => (Brush)FindResource(key);

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
        using var bmp = RenderSelectionBitmap();
        CloseOverlay();   // 先关取景窗,保存对话框显示在真实桌面上

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "STool");
        try { Directory.CreateDirectory(dir); } catch { }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存截图",
            Filter = "PNG 图片 (*.png)|*.png",
            DefaultExt = ".png",
            FileName = $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            InitialDirectory = Directory.Exists(dir) ? dir : string.Empty
        };

        if (dlg.ShowDialog() != true)
            return;   // 用户取消

        try
        {
            bmp.Save(dlg.FileName, System.Drawing.Imaging.ImageFormat.Png);
            Core.ToastNotification.Show("截图已保存", dlg.FileName, Core.ToastNotification.ToastType.Success);
        }
        catch (Exception ex)
        {
            Core.ToastNotification.Show("保存失败", ex.Message, Core.ToastNotification.ToastType.Error);
        }
    }

    private void BtnPin_Click(object sender, RoutedEventArgs e)
    {
        var bmp = RenderSelectionBitmap();
        // 选区在屏幕上的 DIP 位置 = 覆盖窗口原点(虚拟屏左上)+ 窗口内 DIP 偏移
        var screenRect = new Rect(Left + _selection.X, Top + _selection.Y, _selection.Width, _selection.Height);
        CloseOverlay();
        new PinWindow(bmp, screenRect).Show();
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
        HideTranslationOverlay();
        var bmp = RenderSelectionBitmap();
        ShowTranslationOverlay("翻译中...");
        var ocr = ((App)System.Windows.Application.Current).GetService<STool.Modules.Ocr.OcrManager>();
        var tr = ((App)System.Windows.Application.Current).GetService<STool.Modules.Translation.TranslationManager>();
        if (ocr == null || tr == null) { Core.ToastNotification.Show("翻译不可用", "服务未初始化", Core.ToastNotification.ToastType.Error); HideTranslationOverlay(); bmp.Dispose(); return; }
        try
        {
            var o = await ocr.RecognizeAsync(bmp);
            if (!o.Success || string.IsNullOrWhiteSpace(o.FullText))
            { Core.ToastNotification.Show("OCR 失败", o.ErrorMessage ?? "未识别到文字", Core.ToastNotification.ToastType.Warning); HideTranslationOverlay(); return; }
            var t = await tr.TranslateAsync(o.FullText);
            if (!t.Success) { Core.ToastNotification.Show("翻译失败", t.ErrorMessage ?? "", Core.ToastNotification.ToastType.Error); HideTranslationOverlay(); return; }
            ShowTranslationOverlay(t.TranslatedText);
        }
        catch (Exception ex) { HideTranslationOverlay(); Core.ToastNotification.Show("翻译失败", ex.Message, Core.ToastNotification.ToastType.Error); }
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

        var hasTranslation = translationOverlay.Visibility == Visibility.Visible;
        var hasAnnotations = annotationCanvas.Children.Count > 0;
        var mosaicAnnotations = annotationCanvas.Children
            .OfType<MosaicAnnotation>()
            .Where(m => m.ActualWidth > 0 && m.ActualHeight > 0)
            .ToList();

        // 无标注/译文则直接返回裁剪图
        if (!hasAnnotations && !hasTranslation)
            return crop;

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
                var translationBrush = new VisualBrush(translationOverlay) { Stretch = Stretch.Fill };
                ctx.DrawRectangle(translationBrush, null, new Rect(0, 0, pw, ph));
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
        crop.Dispose();
        return BitmapSourceToBitmap(rtb);
    }

    private void ApplyMosaicAnnotations(System.Drawing.Bitmap bitmap, IEnumerable<MosaicAnnotation> mosaics)
    {
        foreach (var mosaic in mosaics)
        {
            var brushSize = Math.Max(10, (int)Math.Round(mosaic.BrushSize * _scaleX));
            foreach (var point in mosaic.Points)
            {
                var centerX = (int)Math.Round(point.X * _scaleX);
                var centerY = (int)Math.Round(point.Y * _scaleY);
                ApplyMosaic(bitmap, new System.Drawing.Rectangle(
                    centerX - brushSize / 2,
                    centerY - brushSize / 2,
                    brushSize,
                    brushSize));
            }
        }
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
        => (Math.Max(1, (int)Math.Round(w * _scaleX)), Math.Max(1, (int)Math.Round(h * _scaleY)));

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

    private void ShowTranslationOverlay(string text)
    {
        translationOverlayText.Text = text;
        translationOverlay.Visibility = Visibility.Visible;
        Panel.SetZIndex(translationOverlay, 35);
        Panel.SetZIndex(selectionBorder, 40);
        UpdateVisuals();
        ApplyTranslationOverlayLayout();
    }

    private void HideTranslationOverlay()
    {
        if (translationOverlay.Visibility != Visibility.Visible)
            return;
        translationOverlay.Visibility = Visibility.Collapsed;
        translationOverlayText.Text = string.Empty;
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
        _frozen?.Dispose();
        _frozen = null;
        base.OnClosed(e);
    }
}
