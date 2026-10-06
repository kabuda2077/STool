using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;
using STool.Modules.Screenshot.Annotations;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace STool.Modules.Screenshot;

/// <summary>
/// 微信式一体化截图取景窗:冻结屏幕 → 默认全屏(工作区)选区 → 框内清晰框外淡蒙版 →
/// 8 手柄缩放/拖动移动/空白重框 → 内联标注 → 内嵌工具条(随选区定位且夹在屏内) →
/// 回车/✓ 复制到剪贴板。Esc 取消。
/// </summary>
public partial class CaptureOverlay : Window
{
    private enum DragMode { None, NewSelection, Move, Resize }

    private readonly CaptureOverlayServices? _services;
    private readonly bool _diagnosticsEnabled;
    private CapturedScreen? _capture;
    private readonly System.Drawing.Rectangle _virtualScreenBounds;
    private VirtualDesktopCoordinateMapper? _coordinateMapper;
    private Rect _selection;
    private DragMode _dragMode = DragMode.None;
    private string _activeHandle = "";
    private System.Windows.Point _dragStart;
    private Rect _selectionAtStart;
    private bool _closing;
    private bool _handlesReady;
    private bool _interactionReady;
    private bool _suppressStartupAltRelease;
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
    private IntPtr _selfHwnd;
    private HwndSource? _hwndSource;
    private System.Threading.CancellationTokenSource? _translationCts;
    private System.Threading.CancellationTokenSource? _ocrCts;
    private EventHandler? _firstRenderingHandler;

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    public CaptureOverlay(CaptureOverlayServices services, Stopwatch? startupTimer)
        : this(services, startupTimer, prewarm: false)
    {
    }

    /// <summary>
    /// 预热构造:仅触发 InitializeComponent(BAML 解析 + 模板/JIT 一次性成本),
    /// 跳过抓屏与 Loaded 逻辑,实例随即丢弃。用于消除首次截图的冷启动延迟。
    /// </summary>
    public static CaptureOverlay CreateForWarmUp() => new(null, null, prewarm: true);

    private CaptureOverlay(CaptureOverlayServices? services, Stopwatch? startupTimer, bool prewarm)
    {
        _services = services;
        _diagnosticsEnabled = services?.DiagnosticsEnabled == true;
        _startupTimer = startupTimer;
        InitializeComponent();
        LogStartupStep("InitializeComponent");

        if (prewarm)
        {
            // 不抓屏、不挂 Loaded、不显示;只为把 WPF 一次性初始化成本提前付掉
            return;
        }

        _virtualScreenBounds = ScreenCapture.GetVirtualScreenBounds();

        Mouse.OverrideCursor = Cursors.Cross;
        Closed += (_, _) =>
        {
            Mouse.OverrideCursor = null;
            if (_firstRenderingHandler != null)
            {
                CompositionTarget.Rendering -= _firstRenderingHandler;
                _firstRenderingHandler = null;
            }
        };

        // 覆盖整个虚拟屏幕(DIP)
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        LogStartupStep("Window bounds prepared");

        // 冻结屏幕:像素放在共享内存段,GDI+ 与 WPF 共用
        _capture = ScreenCapture.Capture(_virtualScreenBounds);
        screenshotImage.Source = _capture.Source;
        LogStartupStep($"Capture {_capture.Width}x{_capture.Height}");

        if (_startupTimer != null)
        {
            ContentRendered += OnContentRendered;
            _firstRenderingHandler = (_, _) =>
            {
                CompositionTarget.Rendering -= _firstRenderingHandler;
                _firstRenderingHandler = null;
                LogStartupStep("CompositionTarget.Rendering first frame");
            };
            CompositionTarget.Rendering += _firstRenderingHandler;
        }

        Loaded += OnLoaded;
    }

    private void LogStartupStep(string step)
    {
        if (_startupTimer == null)
            return;

        var elapsedMs = _startupTimer.ElapsedMilliseconds;
        Log.Information(
            "[CaptureStartup] id={CaptureId} {Step} at {ElapsedMs}ms (+{DeltaMs}ms)",
            _captureId,
            step,
            elapsedMs,
            elapsedMs - _lastStartupMarkMs);
        _lastStartupMarkMs = elapsedMs;
    }

    public void SchedulePostShowDiagnostics()
    {
        QueueStartupCheckpoint(System.Windows.Threading.DispatcherPriority.Render, "Post-show dispatcher Render");
        QueueStartupCheckpoint(System.Windows.Threading.DispatcherPriority.Input, "Post-show dispatcher Input");
        QueueStartupCheckpoint(System.Windows.Threading.DispatcherPriority.Loaded, "Post-show dispatcher Loaded");
        QueueStartupCheckpoint(System.Windows.Threading.DispatcherPriority.Background, "Post-show dispatcher Background");
    }

    private void QueueStartupCheckpoint(System.Windows.Threading.DispatcherPriority priority, string step)
    {
        Dispatcher.BeginInvoke(priority, new Action(() => LogStartupStep(step)));
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        LogStartupStep("ContentRendered");
    }

    private void UpdateCursorState()
    {
        var cursor = _currentTool switch
        {
            AnnotationTool.Text => Cursors.IBeam,
            AnnotationTool.None when !_confirmed => Cursors.Cross,
            _ => Cursors.Arrow
        };

        Mouse.OverrideCursor = _confirmed || _currentTool != AnnotationTool.None ? null : cursor;
        Cursor = cursor;
        overlayCanvas.Cursor = cursor;
    }

    /// <summary>句柄创建完成(在 Loaded 之前)。这里只缓存句柄,避免显示首帧前抢输入队列。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (_capture == null)
            return;

        StartCaptureDiagnostics();
        _selfHwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_selfHwnd);
        _hwndSource?.AddHook(WndProc);
        _suppressStartupAltRelease = (GetAsyncKeyState(0x12) & 0x8000) != 0;
        if (!SetWindowPos(
            _selfHwnd,
            IntPtr.Zero,
            _virtualScreenBounds.Left,
            _virtualScreenBounds.Top,
            _virtualScreenBounds.Width,
            _virtualScreenBounds.Height,
            SWP_NOZORDER | SWP_NOACTIVATE))
        {
            Log.Warning("[Capture] Failed to size overlay to the physical virtual desktop error={Error}", Marshal.GetLastWin32Error());
        }
        LogStartupStep("SourceInitialized");
        EnsureInteractionReady("SourceInitialized");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_KEYDOWN = 0x0100;
        const int WM_KEYUP = 0x0101;
        const int WM_SYSKEYDOWN = 0x0104;
        const int WM_SYSKEYUP = 0x0105;
        const int WM_SYSCOMMAND = 0x0112;
        const int SC_KEYMENU = 0xF100;
        const int VK_ESCAPE = 0x1B;
        const int VK_RETURN = 0x0D;
        const int VK_MENU = 0x12;
        const int VK_LEFT = 0x25;
        const int VK_DOWN = 0x28;
        const int VK_C = 0x43;
        const int VK_S = 0x53;
        const int VK_Y = 0x59;
        const int VK_Z = 0x5A;

        // Alt+数字启动截图时,覆盖层可能在 Alt 松开前取得焦点。吞掉这次残留的
        // Alt 释放和系统菜单命令,避免 WPF 进入菜单模式后暂停鼠标悬停识别。
        if (msg == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_KEYMENU)
        {
            handled = true;
            return IntPtr.Zero;
        }

        if (_suppressStartupAltRelease &&
            (msg == WM_KEYUP || msg == WM_SYSKEYUP) &&
            wParam.ToInt32() == VK_MENU)
        {
            _suppressStartupAltRelease = false;
            handled = true;
            return IntPtr.Zero;
        }

        if (msg != WM_KEYDOWN && msg != WM_SYSKEYDOWN)
            return IntPtr.Zero;

        var key = wParam.ToInt32();
        if (_annotation?.IsEditingText == true)
        {
            // 文字输入中:Esc 结束输入,其余按键交给输入框处理。
            if (key == VK_ESCAPE)
            {
                _annotation.CommitText();
                handled = true;
            }
            return IntPtr.Zero;
        }

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        switch (key)
        {
            case VK_ESCAPE:
                LogCaptureState("Native ESC received");
                CloseOverlay();
                handled = true;
                break;
            case VK_RETURN:
                CopyAndClose();
                handled = true;
                break;
            case VK_Z when ctrl:
                _annotation?.Undo();
                handled = true;
                break;
            case VK_Y when ctrl:
                _annotation?.Redo();
                handled = true;
                break;
            case VK_S when ctrl:
                SaveSelection();
                handled = true;
                break;
            case VK_C when !ctrl && magnifier.Visibility == Visibility.Visible:
                CopyPixelColor();
                handled = true;
                break;
            case >= VK_LEFT and <= VK_DOWN:
                handled = NudgeWithKeyboard(key - VK_LEFT, ctrl, shift);
                break;
        }

        return IntPtr.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LogStartupStep("Loaded begin");
        Core.MemoryDiagnostics.LogCheckpoint("ScreenshotShown");

        EnsureInteractionReady("Loaded");
        LogStartupStep("Loaded interactive");

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            LogStartupStep("Post-interactive dispatcher frame");
            StartWindowDetection();
        }));
    }

    private void EnsureInteractionReady(string caller)
    {
        if (_interactionReady || _capture == null)
            return;

        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var canvasSize = new System.Windows.Size(
            _virtualScreenBounds.Width / Math.Max(0.01, transform.M11),
            _virtualScreenBounds.Height / Math.Max(0.01, transform.M22));
        _coordinateMapper = new VirtualDesktopCoordinateMapper(_virtualScreenBounds, canvasSize);
        Width = canvasSize.Width;
        Height = canvasSize.Height;
        Log.Information(
            "[Capture] virtualDesktop={PhysicalBounds} canvas={CanvasWidth:F1}x{CanvasHeight:F1} scale={ScaleX:F3}x{ScaleY:F3}",
            _virtualScreenBounds,
            canvasSize.Width,
            canvasSize.Height,
            _coordinateMapper.PixelsPerDipX,
            _coordinateMapper.PixelsPerDipY);
        LogStartupStep($"{caller} DPI scale ready");

        CreateHandles();
        _annotation = new AnnotationCanvas(annotationCanvas);
        _toolButtons = new[] { btnRect, btnEllipse, btnArrow, btnPen, btnText, btnMosaic };
        _magnifierBrush = new ImageBrush(_capture.Source)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill
        };
        magnifierView.Fill = _magnifierBrush;

        selectionBorder.Visibility = Visibility.Visible;
        toolbar.Visibility = Visibility.Collapsed;

        // 初始选区先用当前显示器工作区，避免窗口枚举拖住首帧和鼠标输入。
        _selection = DefaultSelectionRect();
        _interactionReady = true;
        UpdateTranslationToolTip();
        HighlightAnnotationOptions();
        UpdateVisuals();
        LogStartupStep($"{caller} interaction ready");
    }

    private void CloseOverlay([CallerMemberName] string caller = "")
    {
        LogCaptureState($"Close requested by {caller}");
        if (_closing) return;
        _closing = true;
        Close();
        LogCaptureState("Close returned");
    }

    protected override void OnClosed(EventArgs e)
    {
        LogCaptureState("OnClosed begin");
        _closing = true;
        _ocrCts?.Cancel();
        CancelCurrentTranslation();
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;

        ContentRendered -= OnContentRendered;
        Loaded -= OnLoaded;
        if (_firstRenderingHandler != null)
        {
            CompositionTarget.Rendering -= _firstRenderingHandler;
            _firstRenderingHandler = null;
        }

        _annotation?.Clear();
        _annotation = null;
        translationBlockCanvas.Children.Clear();
        translationOverlayText.Text = string.Empty;
        screenshotImage.Source = null;
        magnifierView.Fill = null;
        _magnifierBrush = null;
        _mosaicLayer = null;

        _capture?.Dispose();
        _capture = null;
        Core.MemoryDiagnostics.LogCheckpoint("ScreenshotClosed");
        base.OnClosed(e);
        StopCaptureDiagnostics();
        LogCaptureState("OnClosed completed");
    }
}
