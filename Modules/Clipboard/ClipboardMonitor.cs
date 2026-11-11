using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;
using STool.Core;

namespace STool.Modules.Clipboard;

/// <summary>一次剪贴板变化时取到的原始内容。图片已冻结，可以交给后台线程处理。</summary>
internal sealed record ClipboardCapture(
    ClipboardItemType Type,
    string? Text,
    BitmapSource? Image,
    string[]? Files,
    string? SourceApp,
    DateTime CopiedAt)
{
    /// <summary>估算该内容排队时占用的内存字节数，用于待处理队列的字节预算。</summary>
    public long EstimatedBytes
    {
        get
        {
            long total = 0;
            if (Text != null)
                total += (long)Text.Length * sizeof(char);
            if (Image != null)
                total += (long)Image.PixelWidth * Image.PixelHeight * Math.Max(1, Image.Format.BitsPerPixel / 8);
            if (Files != null)
            {
                foreach (var file in Files)
                    total += (long)file.Length * sizeof(char);
            }

            return total;
        }
    }
}

/// <summary>
/// 剪贴板监听器。UI 线程的消息循环里只做轻量检查（序列号、自身写入抑制、前台进程 ID），
/// 随后把隐私格式检查和内容读取投递到专属 STA 线程，避免大图或无响应的源应用卡住界面；
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
    private volatile bool _disposed;

    /// <summary>读取剪贴板的专属 STA 线程，与写入线程分开，读取卡住时不影响 STool 自己的写入。</summary>
    private readonly ClipboardService _reader = new();

    /// <summary>读取线程上单次读取的最大等待重试次数与间隔（WPF 自身还会内部重试）。</summary>
    internal const int ReadAttempts = 5;
    internal const int ReadRetryDelayMs = 100;

    /// <summary>读取超过该时长仍未完成时记录警告（线程本身无法被强行中断）。</summary>
    private static readonly TimeSpan SlowReadWarning = TimeSpan.FromSeconds(10);

    /// <summary>在读取线程（非 UI 线程）上触发。</summary>
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

            // 复制发生时，前台窗口通常就是来源应用；这类信息只能在事件当时取，进程名解析留给读取线程。
            var sourceProcessId = GetForegroundProcessId();
            var copiedAt = DateTime.Now;

            _ = ReadAndPublishAsync(sequenceNumber, sourceProcessId, copiedAt);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to schedule clipboard read");
        }
    }

    private async Task ReadAndPublishAsync(uint sequenceNumber, int sourceProcessId, DateTime copiedAt)
    {
        try
        {
            var read = _reader.RunAsync(() => ReadAndPublishCoreAsync(sequenceNumber, sourceProcessId, copiedAt));
            var finished = await Task.WhenAny(read, Task.Delay(SlowReadWarning));
            if (finished != read)
            {
                Log.Warning("Clipboard read is taking longer than {Seconds}s; the source application may be unresponsive", SlowReadWarning.TotalSeconds);
            }

            await read;
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to capture clipboard content");
        }
    }

    /// <summary>在读取线程上执行：隐私检查、读取内容、序列号复核，最后发布。</summary>
    private async Task ReadAndPublishCoreAsync(uint sequenceNumber, int sourceProcessId, DateTime copiedAt)
    {
        if (_disposed || IsStale(sequenceNumber, GetClipboardSequenceNumber()))
            return;

        // STool 自己写入的内容带内部格式标记，直接跳过，不依赖序列号窗口。
        if (ClipboardService.IsInternalWrite())
            return;

        if (IsMarkedPrivate())
        {
            // 不记录内容本身，只留下发生过跳过的痕迹。
            Log.Information("Skipping clipboard content marked as private by its source application");
            return;
        }

        var sourceApp = ResolveProcessName(sourceProcessId);
        if (SourceFilter?.Invoke(sourceApp) == true)
        {
            Log.Information("Skipping clipboard content from excluded application {SourceApp}", sourceApp);
            return;
        }

        var capture = await ClipboardService.RetryBusyAsync(
            () => CaptureClipboardContent(sourceApp, copiedAt),
            ReadAttempts,
            ReadRetryDelayMs,
            CancellationToken.None);

        // 隐私检查、取内容期间若发生了另一次复制，丢弃本次结果（新内容有自己的通知）。
        if (capture != null && !IsStale(sequenceNumber, GetClipboardSequenceNumber()))
            ContentCaptured?.Invoke(this, capture);
    }

    /// <summary>读取期间剪贴板序列号已变化，说明手里的内容可能混杂了新旧数据，不能保存。</summary>
    internal static bool IsStale(uint capturedSequence, uint currentSequence) =>
        capturedSequence != currentSequence;

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

    private static bool IsMarkedPrivate()
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

    private static int? ReadClipboardDword(uint format)
    {
        if (!OpenClipboard(IntPtr.Zero))
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

    /// <summary>必须在 STA 读取线程上调用。被占用时抛出 COMException，由调用方重试。</summary>
    private static ClipboardCapture? CaptureClipboardContent(string? sourceApp, DateTime copiedAt)
    {
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

    /// <summary>取当前前台窗口所属进程 ID，失败返回 0。</summary>
    private static int GetForegroundProcessId()
    {
        try
        {
            var hwnd = NativeWindows.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return 0;

            GetWindowThreadProcessId(hwnd, out int pid);
            return pid;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>把进程 ID 解析为进程名（如 Code.exe），失败返回 null。</summary>
    private static string? ResolveProcessName(int pid)
    {
        if (pid == 0)
            return null;

        try
        {
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
        _reader.Dispose();
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
