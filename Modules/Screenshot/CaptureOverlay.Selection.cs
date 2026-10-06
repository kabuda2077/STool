using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using Serilog;
using STool.Modules.Screenshot.Annotations;
using Cursor = System.Windows.Input.Cursor;
using Cursors = System.Windows.Input.Cursors;
using Panel = System.Windows.Controls.Panel;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace STool.Modules.Screenshot;

/// <summary>
/// CaptureOverlay 选区交互（窗口检测、手柄拖拽、新建/移动/缩放选区、键盘微调）
/// </summary>
public partial class CaptureOverlay
{
    // ---------- P/Invoke for window enumeration ----------
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr p);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out NRECT r);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out NRECT r, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
    [StructLayout(LayoutKind.Sequential)] private struct NRECT { public int Left, Top, Right, Bottom; }
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;

    private Rect DefaultSelectionRect()
    {
        var wa = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        return PhysicalToSelection(wa);
    }

    /// <summary>枚举覆盖层之下所有可见顶层窗口的物理矩形(EnumWindows 返回 Z 序,顶层在前)。</summary>
    private static List<System.Drawing.Rectangle> EnumerateWindows(IntPtr self)
    {
        var rects = new List<System.Drawing.Rectangle>();
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
            rects.Add(new System.Drawing.Rectangle(r.Left, r.Top, w, h));
            return true;
        }, IntPtr.Zero);
        return rects;
    }

    private async void StartWindowDetection()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var self = _selfHwnd;
            var rects = await System.Threading.Tasks.Task.Run(() => EnumerateWindows(self));
            if (_closing)
                return;

            _windowRects.Clear();
            _windowRects.AddRange(rects);

            Log.Debug(
                "[CaptureStartup] Window enumeration completed windows={WindowCount} in {ElapsedMs}ms",
                _windowRects.Count,
                sw.ElapsedMilliseconds);

            if (!_confirmed && _dragMode == DragMode.None)
                SetSelectionIfChanged(DetectSelectionOrDefault());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Capture] Window detection failed");
        }
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

    /// <summary>悬停识别时选区没变就不刷新蒙版和工具条。</summary>
    private void SetSelectionIfChanged(Rect selection)
    {
        if (selection == _selection)
            return;

        _selection = selection;
        UpdateVisuals();
    }

    private Rect PhysicalToSelection(System.Drawing.Rectangle r) => CoordinateMapper.PhysicalToCanvas(r);

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
        _handlesReady = true;
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
        CancelCurrentTranslation();
        HideTranslationOverlay();
        _ocrCts?.Cancel();
        _activeHandle = (string)((Rectangle)sender).Tag;
        _dragMode = DragMode.Resize;
        _dragStart = e.GetPosition(overlayCanvas);
        _selectionAtStart = _selection;
        toolbar.Visibility = Visibility.Collapsed;   // 调整过程中隐藏工具条
        overlayCanvas.CaptureMouse();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(System.Windows.Input.MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_closing || _dragMode == DragMode.None)
            return;

        _dragMode = DragMode.None;
        _activeHandle = "";
        toolbar.Visibility = _confirmed ? Visibility.Visible : Visibility.Collapsed;
        if (_confirmed)
            HideMagnifier();
        UpdateCursorState();
        UpdateVisuals();
    }

    // ---------- 选区:新建 / 移动 ----------
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_closing || !_interactionReady) return;
        if (_currentTool != AnnotationTool.None) return;        // 标注模式交给 annotationCanvas
        if (IsOverToolbar(e)) return;

        CancelCurrentTranslation();
        HideTranslationOverlay();
        _ocrCts?.Cancel();

        var p = e.GetPosition(overlayCanvas);
        _dragStart = p;
        _selectionAtStart = _selection;
        _dragMoved = false;

        // 双击已确认选区内 = 复制并完成。未确认时禁止直接复制默认选区,
        // 避免截图窗口首帧偶发延迟时,排队的点击被识别成双击后直接退出。
        if (_confirmed && e.ClickCount == 2 && _selection.Contains(p))
        {
            CopyAndClose();
            return;
        }

        // 已确认且点在选区内 → 移动;否则:拖拽=新建选区,仅点击=确认当前(识别到的)选区
        _dragMode = (_confirmed && _selection.Contains(p)) ? DragMode.Move : DragMode.NewSelection;
        if (_dragMode == DragMode.NewSelection)
            Mouse.OverrideCursor = Cursors.Cross;
        else
            HideMagnifier();

        toolbar.Visibility = Visibility.Collapsed;   // 选取过程中隐藏工具条
        overlayCanvas.CaptureMouse();
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_closing || !_interactionReady) return;

        var p = e.GetPosition(overlayCanvas);

        // 悬停识别窗口(未确认、且未按下拖拽时)
        if (!_confirmed && _dragMode == DragMode.None)
        {
            SetSelectionIfChanged(DetectSelectionOrDefault());
            UpdateMagnifier(p);
            return;
        }

        if (_dragMode == DragMode.None) return;

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
                UpdateMagnifier(p);
                break;

            case DragMode.Move:
                var nx = Clamp(_selectionAtStart.X + dx, 0, ActualW - _selectionAtStart.Width);
                var ny = Clamp(_selectionAtStart.Y + dy, 0, ActualH - _selectionAtStart.Height);
                _selection = new Rect(nx, ny, _selectionAtStart.Width, _selectionAtStart.Height);
                break;

            case DragMode.Resize:
                _selection = ResizeRect(_selectionAtStart, _activeHandle, dx, dy);
                UpdateMagnifier(p);
                break;
        }
        UpdateVisuals();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_closing || !_interactionReady) return;
        if (_dragMode == DragMode.None) return;
        _dragMode = DragMode.None;
        overlayCanvas.ReleaseMouseCapture();
        HideMagnifier();

        if (!_confirmed)
        {
            // 首次确认:拖拽=手动选区(过小则回到识别/默认);仅点击=确认识别到的窗口
            if (_dragMoved && (_selection.Width < 8 || _selection.Height < 8))
                _selection = DetectSelectionOrDefault();
            _confirmed = true;
        }
        else if (_selection.Width < 8 || _selection.Height < 8)
        {
            _selection = DefaultSelectionRect();
        }

        toolbar.Visibility = Visibility.Visible;   // 选取完成 → 显示工具条
        UpdateCursorState();
        UpdateVisuals();
    }

    private Rect ResizeRect(Rect s, string role, double dx, double dy)
    {
        double l = s.Left, t = s.Top, r = s.Right, b = s.Bottom;
        if (role.Contains('L')) l += dx;
        if (role.Contains('R')) r += dx;
        if (role.Contains('T')) t += dy;
        if (role.Contains('B')) b += dy;
        // 规整 + 夹紧
        l = Clamp(l, 0, ActualW); r = Clamp(r, 0, ActualW);
        t = Clamp(t, 0, ActualH); b = Clamp(b, 0, ActualH);
        var x = Math.Min(l, r); var y = Math.Min(t, b);
        var w = Math.Max(8, Math.Abs(r - l)); var h = Math.Max(8, Math.Abs(b - t));
        return new Rect(x, y, w, h);
    }

    /// <summary>
    /// 方向键微调。选区确定前逐像素移动光标（配合放大镜定位起点）；确定后移动选区，
    /// Ctrl 调整右下边界。Shift 每次 10 像素。direction:0 左、1 上、2 右、3 下。
    /// </summary>
    private bool NudgeWithKeyboard(int direction, bool ctrl, bool shift)
    {
        if (!_interactionReady || _dragMode != DragMode.None)
            return false;

        var (dx, dy) = direction switch
        {
            0 => (-1, 0),
            1 => (0, -1),
            2 => (1, 0),
            _ => (0, 1)
        };
        var step = shift ? 10 : 1;

        if (!_confirmed)
        {
            var cursor = System.Windows.Forms.Cursor.Position;
            SetCursorPos(cursor.X + dx * step, cursor.Y + dy * step);
            return true;
        }

        CancelCurrentTranslation();
        HideTranslationOverlay();
        _ocrCts?.Cancel();
        var stepX = step / CoordinateMapper.PixelsPerDipX;
        var stepY = step / CoordinateMapper.PixelsPerDipY;
        if (ctrl)
        {
            var width = Math.Max(8, _selection.Width + dx * stepX);
            var height = Math.Max(8, _selection.Height + dy * stepY);
            _selection = ClampSelection(new Rect(_selection.X, _selection.Y, width, height));
        }
        else
        {
            var x = Clamp(_selection.X + dx * stepX, 0, ActualW - _selection.Width);
            var y = Clamp(_selection.Y + dy * stepY, 0, ActualH - _selection.Height);
            _selection = new Rect(x, y, _selection.Width, _selection.Height);
        }

        UpdateVisuals();
        return true;
    }
}
