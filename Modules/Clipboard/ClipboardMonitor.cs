using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;

namespace STool.Modules.Clipboard;

/// <summary>一次剪贴板变化时取到的原始内容。图片已冻结，可以交给后台线程处理。</summary>
internal sealed record ClipboardCapture(
    ClipboardItemType Type,
    string? Text,
    BitmapSource? Image,
    string[]? Files,
    string? SourceApp,
    DateTime CopiedAt);

/// <summary>
/// 剪贴板监听器。消息循环里只做必须在 UI 线程完成的事：检查隐私标记、读取内容并冻结图片；
/// 编码、查重和入库由 <see cref="ClipboardManager"/> 在后台完成。
/// </summary>
internal sealed class ClipboardMonitor : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr memory);

    private static readonly uint ExcludeFromMonitorFormat = RegisterClipboardFormat(ClipboardPrivacy.ExcludeFromMonitorFormat);
    private static readonly uint CanIncludeInHistoryFormat = RegisterClipboardFormat(ClipboardPrivacy.CanIncludeInHistoryFormat);
    private static readonly uint ClipboardViewerIgnoreFormat = RegisterClipboardFormat(ClipboardPrivacy.ClipboardViewerIgnoreFormat);

    private HwndSource? _hwndSource;
    private bool _isMonitoring;
    private int _suppressingInternalWrite;
    private long _suppressedSequenceNumber = -1;
    private bool _disposed;

    public event EventHandler<ClipboardCapture>? ContentCaptured;

    /// <summary>返回 true 时忽略该来源进程（进程名，如 KeePass.exe）复制的内容。</summary>
    public Func<string?, bool>? SourceFilter { get; set; }

    public bool IsMonitoring => _isMonitoring;

    public void Start()
    {
        RunOnDispatcher(StartCore);
    }

    private void StartCore()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isMonitoring)
            return;

        var parameters = new HwndSourceParameters("SToolClipboardListener")
        {
            Width = 0,
            Height = 0,
            WindowStyle = WS_POPUP,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);

        if (!AddClipboardFormatListener(_hwndSource.Handle))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            _hwndSource.RemoveHook(WndProc);
            _hwndSource.Dispose();
            _hwndSource = null;
            Log.Error(error, "Failed to register clipboard listener");
            return;
        }

        _isMonitoring = true;
        Log.Information("Clipboard monitoring started");
    }

    public void Stop()
    {
        RunOnDispatcher(StopCore);
    }

    private void StopCore()
    {
        if (_hwndSource == null)
            return;

        if (_isMonitoring)
        {
            RemoveClipboardFormatListener(_hwndSource.Handle);
            _isMonitoring = false;
            Log.Information("Clipboard monitoring stopped");
        }

        _hwndSource.RemoveHook(WndProc);
        _hwndSource.Dispose();
        _hwndSource = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            OnClipboardUpdate();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void OnClipboardUpdate()
    {
        try
        {
            var sequenceNumber = GetClipboardSequenceNumber();
            if (ShouldSuppressUpdate(sequenceNumber))
                return;

            if (IsMarkedPrivate())
            {
                // 不记录内容本身，只留下发生过跳过的痕迹。
                Log.Information("Skipping clipboard content marked as private by its source application");
                return;
            }

            // 复制发生时,前台窗口通常就是来源应用
            var sourceApp = GetForegroundApp();
            if (SourceFilter?.Invoke(sourceApp) == true)
            {
                Log.Information("Skipping clipboard content from excluded application {SourceApp}", sourceApp);
                return;
            }

            var capture = CaptureClipboardContent(sourceApp);
            // 标记检查和取内容之间若发生了另一次复制，不能保存未经隐私检查的新内容。
            if (capture != null && sequenceNumber == GetClipboardSequenceNumber())
                ContentCaptured?.Invoke(this, capture);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to capture clipboard content");
        }
    }

    public void BeginUpdateSuppression() =>
        Volatile.Write(ref _suppressingInternalWrite, 1);

    public void CompleteUpdateSuppression() =>
        CompleteUpdateSuppression(GetClipboardSequenceNumber());

    internal void CompleteUpdateSuppression(uint sequenceNumber)
    {
        if (sequenceNumber != 0)
            Interlocked.Exchange(ref _suppressedSequenceNumber, sequenceNumber);
        Volatile.Write(ref _suppressingInternalWrite, 0);
    }

    public void CancelUpdateSuppression() =>
        Volatile.Write(ref _suppressingInternalWrite, 0);

    internal bool ShouldSuppressUpdate(uint sequenceNumber) =>
        Volatile.Read(ref _suppressingInternalWrite) != 0 ||
        sequenceNumber != 0 && Interlocked.Read(ref _suppressedSequenceNumber) == sequenceNumber;

    private bool IsMarkedPrivate()
    {
        var hasExclude = ExcludeFromMonitorFormat != 0 && IsClipboardFormatAvailable(ExcludeFromMonitorFormat);
        var hasViewerIgnore = ClipboardViewerIgnoreFormat != 0 && IsClipboardFormatAvailable(ClipboardViewerIgnoreFormat);
        int? canInclude = null;
        if (CanIncludeInHistoryFormat != 0 && IsClipboardFormatAvailable(CanIncludeInHistoryFormat))
        {
            // 读不出值时按"不允许记录"处理，宁可漏记也不误记密码。
            canInclude = ReadClipboardDword(CanIncludeInHistoryFormat) ?? 0;
        }

        return ClipboardPrivacy.ShouldSkip(hasExclude, hasViewerIgnore, canInclude);
    }

    private int? ReadClipboardDword(uint format)
    {
        if (_hwndSource == null || !OpenClipboard(_hwndSource.Handle))
            return null;

        try
        {
            var handle = GetClipboardData(format);
            if (handle == IntPtr.Zero || (ulong)GlobalSize(handle) < sizeof(int))
                return null;

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
                return null;

            try
            {
                return Marshal.ReadInt32(pointer);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static ClipboardCapture? CaptureClipboardContent(string? sourceApp)
    {
        var copiedAt = DateTime.Now;

        if (System.Windows.Clipboard.ContainsText())
        {
            var text = System.Windows.Clipboard.GetText();
            return string.IsNullOrWhiteSpace(text)
                ? null
                : new ClipboardCapture(ClipboardItemType.Text, text, null, null, sourceApp, copiedAt);
        }

        if (System.Windows.Clipboard.ContainsImage())
        {
            var image = System.Windows.Clipboard.GetImage();
            if (image == null)
                return null;

            return new ClipboardCapture(ClipboardItemType.Image, null, FreezeForBackground(image), null, sourceApp, copiedAt);
        }

        if (System.Windows.Clipboard.ContainsFileDropList())
        {
            var files = System.Windows.Clipboard.GetFileDropList();
            if (files == null || files.Count == 0)
                return null;

            return new ClipboardCapture(ClipboardItemType.File, null, null, files.Cast<string>().ToArray(), sourceApp, copiedAt);
        }

        return null;
    }

    private static BitmapSource FreezeForBackground(BitmapSource image)
    {
        if (image.IsFrozen)
            return image;

        if (image.CanFreeze)
        {
            image.Freeze();
            return image;
        }

        var copy = new WriteableBitmap(image);
        copy.Freeze();
        return copy;
    }

    /// <summary>抓取当前前台窗口所属进程名(如 Code.exe),失败返回 null。</summary>
    private static string? GetForegroundApp()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return null;

            GetWindowThreadProcessId(hwnd, out int pid);
            if (pid == 0)
                return null;

            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            var name = proc.ProcessName;
            return string.IsNullOrEmpty(name) ? null : name + ".exe";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>判断图片是否近乎全白或全透明（微信等应用会写入这类占位图）。可在后台线程调用。</summary>
    internal static bool IsVisuallyBlankImage(BitmapSource image)
    {
        try
        {
            if (image.PixelWidth <= 0 || image.PixelHeight <= 0)
                return true;

            const int maxSampleSize = 64;
            var scale = Math.Min(1.0, (double)maxSampleSize / Math.Max(image.PixelWidth, image.PixelHeight));
            BitmapSource sample = image;

            if (scale < 1.0)
            {
                sample = new TransformedBitmap(image, new ScaleTransform(scale, scale));
                sample.Freeze();
            }

            var converted = new FormatConvertedBitmap(sample, PixelFormats.Bgra32, null, 0);
            converted.Freeze();

            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            if (width <= 0 || height <= 0)
                return true;

            var stride = width * 4;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            var opaquePixels = 0;
            var nonWhitePixels = 0;

            for (var i = 0; i < pixels.Length; i += 4)
            {
                var b = pixels[i];
                var g = pixels[i + 1];
                var r = pixels[i + 2];
                var a = pixels[i + 3];

                if (a <= 8)
                    continue;

                opaquePixels++;
                if (r < 244 || g < 244 || b < 244)
                    nonWhitePixels++;
            }

            if (opaquePixels == 0)
                return true;

            return nonWhitePixels <= 3 || (double)nonWhitePixels / opaquePixels < 0.001;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        RunOnDispatcher(() =>
        {
            StopCore();
            _disposed = true;
        });
    }

    private static void RunOnDispatcher(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(action);
            return;
        }

        action();
    }
}
