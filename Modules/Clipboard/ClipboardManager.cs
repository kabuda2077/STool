using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;
using STool.Core;
using STool.Models;

namespace STool.Modules.Clipboard;

/// <summary>
/// 剪贴板历史：监听器在 UI 线程取出内容后放进队列，这里在后台线程完成查重、图片编码与入库。
/// </summary>
public class ClipboardManager : IDisposable
{
    private static readonly TimeSpan MaintenanceDelay = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    /// <summary>超过这个原始像素体积的图片直接跳过，避免在后台一次性申请过大的内存。</summary>
    private const long MaxRawImageBytes = 512L * 1024 * 1024;

    private readonly ConfigManager _configManager;
    private readonly ClipboardMonitor _monitor;
    private readonly ClipboardStorage _storage;
    private readonly string _imagesDirectory;
    private readonly Channel<ClipboardCapture> _captures = Channel.CreateBounded<ClipboardCapture>(
        new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _processing;
    private Timer? _cleanupTimer;
    private volatile ClipboardConfig _settings;
    private int _cleanupRunning;
    private int _disposed;

    /// <summary>新增或重新提到最前的记录。在后台线程触发。</summary>
    public event EventHandler<ClipboardItem>? ItemAdded;

    public ClipboardManager(ConfigManager configManager)
        : this(configManager, new ClipboardStorage(), AppPaths.ClipboardImagesDirectory)
    {
    }

    internal ClipboardManager(ConfigManager configManager, ClipboardStorage storage, string imagesDirectory)
    {
        _configManager = configManager;
        _storage = storage;
        _imagesDirectory = imagesDirectory;
        _settings = configManager.Get().Clipboard;
        _monitor = new ClipboardMonitor
        {
            SourceFilter = sourceApp => ClipboardPrivacy.IsExcludedApp(sourceApp, _settings.ExcludedApps)
        };
        _monitor.ContentCaptured += (_, capture) => _captures.Writer.TryWrite(capture);
        _processing = Task.Run(ProcessCapturesAsync);
    }

    public void Start()
    {
        ApplySettings();

        // 文件扫描、元数据回填和过期清理放到启动之后的后台执行，不拖慢启动。
        _ = Task.Delay(MaintenanceDelay, _lifetime.Token).ContinueWith(
            _ => RunMaintenance(),
            _lifetime.Token,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
        _cleanupTimer = new Timer(_ => CleanOldEntries(), null, CleanupInterval, CleanupInterval);
    }

    /// <summary>重新读取剪贴板设置：开关监听、更新排除列表，并按新的保留策略清理一次。</summary>
    public void ApplySettings()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        _settings = _configManager.Get().Clipboard;
        if (_settings.Enabled)
        {
            _monitor.Start();
        }
        else
        {
            _monitor.Stop();
            Log.Information("Clipboard monitoring is disabled");
        }

        _ = Task.Run(CleanOldEntries);
    }

    public List<ClipboardItem> Query(ClipboardQuery query) => _storage.Query(query);

    /// <summary>切换收藏并返回新状态；记录已不存在时返回 null。</summary>
    public bool? ToggleFavorite(string id) => _storage.ToggleFavorite(id);

    public void Delete(string id) => _storage.Delete(id);

    public void ClearAll() => _storage.ClearAll();

    public void ClearByType(ClipboardItemType type) => _storage.ClearByType(type);

    public async Task RestoreToClipboardAsync(ClipboardItem item, CancellationToken cancellationToken = default)
    {
        try
        {
            var restored = false;
            switch (item.Type)
            {
                case ClipboardItemType.Text:
                    // 列表只加载了预览，复制时再读取完整文本。
                    var text = item.TextContent ?? await Task.Run(() => _storage.GetById(item.Id)?.TextContent, cancellationToken);
                    if (!string.IsNullOrEmpty(text))
                    {
                        await SetClipboardWithoutRecaptureAsync(
                            () => System.Windows.Clipboard.SetText(text),
                            cancellationToken);
                        restored = true;
                    }
                    break;

                case ClipboardItemType.Image:
                    if (!string.IsNullOrEmpty(item.ImagePath))
                    {
                        var bitmap = await Task.Run(() => LoadImage(item.ImagePath), cancellationToken);
                        await SetClipboardWithoutRecaptureAsync(
                            () => System.Windows.Clipboard.SetImage(bitmap),
                            cancellationToken);
                        restored = true;
                    }
                    break;

                case ClipboardItemType.File:
                    if (item.FilePaths is { Length: > 0 } paths)
                    {
                        var fileDropList = new System.Collections.Specialized.StringCollection();
                        fileDropList.AddRange(paths);
                        await SetClipboardWithoutRecaptureAsync(
                            () => System.Windows.Clipboard.SetFileDropList(fileDropList),
                            cancellationToken);
                        restored = true;
                    }
                    break;
            }

            if (!restored)
                throw new InvalidOperationException("剪贴板记录内容已不可用。");

            Log.Information("Restored clipboard item: {ItemId}", item.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to restore clipboard item {ItemId}", item.Id);
            throw;
        }
    }

    private Task SetClipboardWithoutRecaptureAsync(Action setClipboard, CancellationToken cancellationToken)
    {
        return ClipboardWriter.RunAsync(() =>
        {
            _monitor.BeginUpdateSuppression();
            try
            {
                setClipboard();
                _monitor.CompleteUpdateSuppression();
            }
            catch
            {
                _monitor.CancelUpdateSuppression();
                throw;
            }
        }, cancellationToken);
    }

    private async Task ProcessCapturesAsync()
    {
        try
        {
            await foreach (var capture in _captures.Reader.ReadAllAsync(_lifetime.Token))
            {
                try
                {
                    var item = ProcessCapture(capture, _settings);
                    if (item == null)
                        continue;

                    Log.Information("Clipboard item captured: {Type}", item.Type);
                    ItemAdded?.Invoke(this, item);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to save clipboard item type={Type}", capture.Type);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal ClipboardItem? ProcessCapture(ClipboardCapture capture, ClipboardConfig settings)
    {
        // 排队期间可能刚刚暂停记录或更新排除列表，保存前再次检查隐私设置。
        if (!settings.Enabled || ClipboardPrivacy.IsExcludedApp(capture.SourceApp, settings.ExcludedApps))
            return null;

        return capture.Type switch
        {
            ClipboardItemType.Text => ProcessText(capture, settings),
            ClipboardItemType.Image => ProcessImage(capture, settings),
            ClipboardItemType.File => ProcessFiles(capture),
            _ => null
        };
    }

    private ClipboardItem? ProcessText(ClipboardCapture capture, ClipboardConfig settings)
    {
        var text = capture.Text;
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (settings.MaxTextLength > 0 && text.Length > settings.MaxTextLength)
        {
            Log.Information(
                "Skipping clipboard text ({Length} chars exceeds limit {Limit})",
                text.Length,
                settings.MaxTextLength);
            return null;
        }

        var hash = ClipboardContentHash.ForText(text);
        return _storage.TryPromoteDuplicate(ClipboardItemType.Text, hash, capture.CopiedAt, capture.SourceApp)
            ?? AddItem(new ClipboardItem
            {
                Type = ClipboardItemType.Text,
                TextContent = text,
                TextPreview = ClipboardItem.CreatePreview(text),
                ContentHash = hash,
                SourceApp = capture.SourceApp,
                CreatedAt = capture.CopiedAt
            });
    }

    private ClipboardItem? ProcessImage(ClipboardCapture capture, ClipboardConfig settings)
    {
        var image = capture.Image;
        if (image == null)
            return null;

        if (ClipboardMonitor.IsVisuallyBlankImage(image))
        {
            Log.Information("Skipping visually blank clipboard image from {SourceApp}", capture.SourceApp ?? "unknown");
            return null;
        }

        var width = image.PixelWidth;
        var height = image.PixelHeight;
        if ((long)width * height * 4 > MaxRawImageBytes)
        {
            Log.Information("Skipping oversized clipboard image {Width}x{Height}", width, height);
            return null;
        }

        var hash = ClipboardContentHash.ForImage(image);

        var promoted = _storage.TryPromoteDuplicate(ClipboardItemType.Image, hash, capture.CopiedAt, capture.SourceApp);
        if (promoted != null && File.Exists(promoted.ImagePath))
            return promoted;
        if (promoted != null)
            _storage.Delete(promoted.Id);

        // 在编码器写入时限制输出长度，超限即终止，不先分配整张 PNG 的缓冲。
        using var png = new MemoryStream();
        var maximumBytes = settings.MaxImageSizeKB > 0 ? settings.MaxImageSizeKB * 1024L : long.MaxValue;
        if (!TryEncodePng(image, png, maximumBytes))
        {
            Log.Information("Skipping clipboard image: encoded output exceeds {LimitKB}KB", settings.MaxImageSizeKB);
            return null;
        }

        Directory.CreateDirectory(_imagesDirectory);
        var path = Path.Combine(_imagesDirectory, $"clipboard_{capture.CopiedAt:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.png");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            png.Position = 0;
            png.CopyTo(file);
        }

        try
        {
            return AddItem(new ClipboardItem
            {
                Type = ClipboardItemType.Image,
                ImagePath = path,
                ImageWidth = width,
                ImageHeight = height,
                ImageBytes = png.Length,
                ContentHash = hash,
                SourceApp = capture.SourceApp,
                CreatedAt = capture.CopiedAt
            });
        }
        catch
        {
            TryDeleteFile(path);
            throw;
        }
    }

    internal static bool TryEncodePng(BitmapSource image, Stream destination, long maximumBytes)
    {
        using var limited = new SizeLimitedStream(destination, maximumBytes, leaveOpen: true);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        try
        {
            encoder.Save(limited);
        }
        catch (Exception) when (limited.LimitExceeded)
        {
            // 原生 WIC 可能包装流抛出的异常。只有确认超限时才转换为“跳过”。
            return false;
        }
        return !limited.LimitExceeded;
    }

    private ClipboardItem? ProcessFiles(ClipboardCapture capture)
    {
        var files = capture.Files;
        if (files == null || files.Length == 0)
            return null;

        var hash = ClipboardContentHash.ForFiles(files);
        return _storage.TryPromoteDuplicate(ClipboardItemType.File, hash, capture.CopiedAt, capture.SourceApp)
            ?? AddItem(new ClipboardItem
            {
                Type = ClipboardItemType.File,
                FilePaths = files,
                ContentHash = hash,
                SourceApp = capture.SourceApp,
                CreatedAt = capture.CopiedAt
            });
    }

    private ClipboardItem AddItem(ClipboardItem item)
    {
        _storage.Add(item);
        // 与列表查询保持一致：只带预览，完整文本在复制时按需读取。
        item.TextContent = null;
        return item;
    }

    private static BitmapImage LoadImage(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void RunMaintenance()
    {
        try
        {
            _storage.RunMaintenance();
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Clipboard maintenance failed");
        }

        CleanOldEntries();
    }

    private void CleanOldEntries()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _cleanupRunning, 1) != 0)
            return;

        try
        {
            var settings = _settings;
            _storage.CleanOldEntries(settings.RetentionDays, settings.MaxEntries);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to clean old clipboard entries");
        }
        finally
        {
            Volatile.Write(ref _cleanupRunning, 0);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Failed to delete clipboard image {Path}", path);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cleanupTimer?.Dispose();
        _monitor.Dispose();
        _captures.Writer.TryComplete();
        try
        {
            // 等队列里已取到的内容保存完，但不无限期阻塞退出。
            _processing.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex)
        {
            Log.Debug(ex, "Clipboard processing ended with an error during disposal");
        }

        _lifetime.Cancel();
        _storage.Dispose();
        _lifetime.Dispose();
    }
}
