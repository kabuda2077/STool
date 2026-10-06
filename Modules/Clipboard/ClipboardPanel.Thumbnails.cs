using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using STool.Core;

namespace STool.Modules.Clipboard;

/// <summary>剪贴板面板的缩略图、图片预览与视图模型构造。</summary>
public partial class ClipboardPanel
{
    // 缩略图后台解码并发限流(最多 3 个同时解码)
    private static readonly SemaphoreSlim ThumbThrottle = new(3);
    private const long LargeImageBytes = 2 * 1024 * 1024;
    private readonly ThumbnailCache _thumbnailCache = new();
    private readonly CancellationTokenSource _thumbnailLifetime = new();
    private CancellationTokenSource _thumbnailGeneration = new();

    /// <summary>构造列表项。只使用数据库里已有的元数据，不在 UI 线程访问文件系统。</summary>
    private ClipboardItemViewModel ToViewModel(ClipboardItem item)
    {
        var vm = new ClipboardItemViewModel
        {
            Id = item.Id,
            RelativeTime = RelativeTimeFormatter.Format(item.CreatedAt),
            FullTimestamp = RelativeTimeFormatter.GetFullTimestamp(item.CreatedAt),
            SourceApp = item.SourceApp ?? string.Empty
        };
        vm.SetFavorite(item.IsFavorite);

        if (item.Type == ClipboardItemType.Image && !string.IsNullOrEmpty(item.ImagePath))
        {
            vm.IsImage = true;
            vm.ImagePath = item.ImagePath;
            vm.ThumbnailCacheKey = BuildThumbnailCacheKey(item.Id, item.ImagePath);
            vm.DisplayText = Path.GetFileName(item.ImagePath);
            vm.ImageInfoText = FormatImageInfo(item.ImageWidth, item.ImageHeight, item.ImageBytes);
            vm.SizeText = vm.ImageInfoText;
            vm.IsLargeImage = item.ImageBytes >= LargeImageBytes || Math.Max(item.ImageWidth, item.ImageHeight) >= 3000;

            var thumbWidth = 150d;
            var thumbHeight = 96d;
            if (item.ImageWidth > 0 && item.ImageHeight > 0)
            {
                var ratio = (double)item.ImageWidth / item.ImageHeight;
                if (ratio >= 1)
                {
                    thumbWidth = Math.Min(180, Math.Max(118, 96 * ratio));
                    thumbHeight = 96;
                }
                else
                {
                    thumbWidth = 96;
                    thumbHeight = Math.Min(130, Math.Max(92, 96 / ratio));
                }
            }

            vm.ThumbnailBoxWidth = thumbWidth;
            vm.ThumbnailBoxHeight = thumbHeight;
        }
        else if (item.Type == ClipboardItemType.File)
        {
            vm.IsText = true;
            vm.DisplayText = item.GetDisplayText(200);
            vm.SizeText = $"{item.FilePaths?.Length ?? 0} 个文件";
        }
        else
        {
            vm.IsText = true;
            vm.DisplayText = item.GetDisplayText(200);
            vm.SizeText = "文本";
        }

        return vm;
    }

    // 行进入可视区(虚拟化实例化)时才触发解码;后台线程解码,限流,完成后回 UI 线程赋值
    private void ThumbImage_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Image img || img.DataContext is not ClipboardItemViewModel vm)
            return;
        if (_closing || !vm.IsImage || vm.ImageSource != null || vm.ThumbRequested ||
            string.IsNullOrEmpty(vm.ImagePath) || string.IsNullOrEmpty(vm.ThumbnailCacheKey))
            return;

        vm.ThumbRequested = true;
        var path = vm.ImagePath;
        var cacheKey = vm.ThumbnailCacheKey;
        if (_thumbnailCache.TryGet(cacheKey, out var cached))
        {
            vm.ImageSource = cached;
            return;
        }

        var decodeHeight = GetThumbnailDecodeHeight();
        var cancellationToken = _thumbnailGeneration.Token;

        _ = Task.Run(async () =>
        {
            var acquired = false;
            try
            {
                await ThumbThrottle.WaitAsync(cancellationToken);
                acquired = true;
                cancellationToken.ThrowIfCancellationRequested();
                var bmp = LoadThumbnail(path, decodeHeight);
                if (bmp == null)
                {
                    PostThumbnailFailure(vm, cancellationToken);
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (_closing)
                    return;

                _thumbnailCache.Set(cacheKey, bmp, EstimateImageBytes(bmp));
                if (_closing || cancellationToken.IsCancellationRequested)
                {
                    _thumbnailCache.Remove(cacheKey);
                    return;
                }

                PostThumbnailResult(vm, bmp, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 窗口关闭或列表刷新后不再回 UI 线程写入 ViewModel。
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Clipboard thumbnail load failed: {ex.Message}");
                PostThumbnailFailure(vm, cancellationToken);
            }
            finally
            {
                if (acquired)
                    ThumbThrottle.Release();
            }
        });
    }

    private void PostThumbnailResult(ClipboardItemViewModel vm, ImageSource image, CancellationToken cancellationToken)
    {
        if (_closing || cancellationToken.IsCancellationRequested || Dispatcher.HasShutdownStarted)
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_closing ||
                cancellationToken.IsCancellationRequested ||
                !_viewModelsById.TryGetValue(vm.Id, out var current) ||
                !ReferenceEquals(current, vm))
            {
                return;
            }

            vm.ImageSource = image;
            var count = _thumbnailCache.Count;
            if (count == 1 || count % 16 == 0)
            {
                MemoryDiagnostics.LogCheckpoint(
                    "ClipboardThumbnailsLoaded",
                    count,
                    _thumbnailCache.EstimatedBytes);
            }
        }));
    }

    private void PostThumbnailFailure(ClipboardItemViewModel vm, CancellationToken cancellationToken)
    {
        if (_closing || cancellationToken.IsCancellationRequested || Dispatcher.HasShutdownStarted)
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_closing && !cancellationToken.IsCancellationRequested)
                vm.ThumbRequested = false;
        }));
    }

    private int GetThumbnailDecodeHeight()
    {
        var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        return Math.Clamp((int)Math.Round(160 * dpiScale), 160, 220);
    }

    private static long EstimateImageBytes(ImageSource image)
    {
        return image is BitmapSource bitmap
            ? Math.Max(1, (long)bitmap.PixelWidth * bitmap.PixelHeight * 4)
            : 0;
    }

    private static ImageSource? LoadThumbnail(string path, int decodeHeight)
    {
        try
        {
            var thumbPath = GetThumbnailPath(path);
            EnsureThumbnail(path, thumbPath);

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;   // 立即加载,不锁文件
            bmp.DecodePixelHeight = decodeHeight;         // 按 DPI 适配,避免过度解码
            bmp.UriSource = new Uri(File.Exists(thumbPath) ? thumbPath : path);
            bmp.EndInit();
            bmp.Freeze();                                 // 跨线程:冻结后可在 UI 线程使用
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static void EnsureThumbnail(string sourcePath, string thumbPath)
    {
        try
        {
            var sourceInfo = new FileInfo(sourcePath);
            var thumbInfo = new FileInfo(thumbPath);
            if (thumbInfo.Exists && thumbInfo.LastWriteTimeUtc >= sourceInfo.LastWriteTimeUtc)
                return;

            Directory.CreateDirectory(AppPaths.ClipboardThumbnailsDirectory);

            var source = new BitmapImage();
            source.BeginInit();
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.DecodePixelHeight = 240;
            source.UriSource = new Uri(sourcePath);
            source.EndInit();
            source.Freeze();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new FileStream(thumbPath, FileMode.Create, FileAccess.Write, FileShare.None);
            encoder.Save(stream);
            File.SetLastWriteTimeUtc(thumbPath, sourceInfo.LastWriteTimeUtc);
        }
        catch
        {
            // 缩略图缓存失败时回退到源图加载,不影响主流程
        }
    }

    private static string GetThumbnailPath(string imagePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(imagePath);
        return Path.Combine(AppPaths.ClipboardThumbnailsDirectory, fileName + ".thumb.png");
    }

    /// <summary>剪贴板图片文件名唯一且写入后不再修改，记录 Id + 路径即可唯一标识缩略图。</summary>
    internal static string BuildThumbnailCacheKey(string id, string path) => $"{id}|{path}";

    private void OnThumbnailEvicted(string cacheKey)
    {
        if (_closing)
            return;

        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => OnThumbnailEvicted(cacheKey)));
            return;
        }

        foreach (var vm in _items)
        {
            if (string.Equals(vm.ThumbnailCacheKey, cacheKey, StringComparison.Ordinal))
            {
                vm.ImageSource = null;
                vm.ThumbRequested = false;
                break;
            }
        }
    }

    private void ResetThumbnailGeneration()
    {
        var previous = _thumbnailGeneration;
        _thumbnailGeneration = CancellationTokenSource.CreateLinkedTokenSource(_thumbnailLifetime.Token);
        previous.Cancel();
        previous.Dispose();

        foreach (var vm in _items)
        {
            if (vm.ImageSource == null)
                vm.ThumbRequested = false;
        }
    }

    private static string FormatImageInfo(int width, int height, long bytes)
    {
        var dimensions = width > 0 && height > 0 ? $"{width} × {height}" : "图片";
        return bytes > 0 ? $"{dimensions} · {FormatBytes(bytes)}" : dimensions;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024)
            return $"{bytes / 1024d / 1024d:0.#} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024d:0.#} KB";
        return $"{bytes} B";
    }

    // ---------- 图片预览 ----------

    private void ThumbPreview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: ClipboardItemViewModel vm })
            ShowImagePreview(vm);
    }

    private void ShowImagePreview(ClipboardItemViewModel vm)
    {
        if (string.IsNullOrEmpty(vm.ImagePath) || !File.Exists(vm.ImagePath))
        {
            ToastNotification.Show("图片不存在", type: ToastNotification.ToastType.Error);
            return;
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 1200;
            bmp.UriSource = new Uri(vm.ImagePath);
            bmp.EndInit();
            bmp.Freeze();

            imagePreview.Source = bmp;
            imagePreviewTitle.Text = vm.ImageInfoText;
            imagePreviewOverlay.Visibility = Visibility.Visible;
            MemoryDiagnostics.LogCheckpoint(
                "ClipboardPreviewOpened",
                _thumbnailCache.Count,
                _thumbnailCache.EstimatedBytes);
        }
        catch
        {
            ToastNotification.Show("预览失败", type: ToastNotification.ToastType.Error);
        }
    }

    private void ClosePreview_Click(object sender, RoutedEventArgs e)
    {
        HideImagePreview();
    }

    private void ImagePreviewOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        HideImagePreview();
    }

    private void ImagePreviewCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void HideImagePreview()
    {
        var wasVisible = imagePreviewOverlay.Visibility == Visibility.Visible;
        imagePreview.Source = null;
        imagePreviewOverlay.Visibility = Visibility.Collapsed;
        if (wasVisible)
        {
            MemoryDiagnostics.LogCheckpoint(
                "ClipboardPreviewClosed",
                _thumbnailCache.Count,
                _thumbnailCache.EstimatedBytes);
        }
    }
}

/// <summary>
/// 剪贴板条目视图模型
/// </summary>
public class ClipboardItemViewModel : INotifyPropertyChanged
{
    private ImageSource? _imageSource;
    private bool _isFavorite;

    public string Id { get; set; } = string.Empty;
    public bool IsImage { get; set; }
    public bool IsText { get; set; }
    public string DisplayText { get; set; } = string.Empty;

    // 图片路径与延迟解码标记
    public string? ImagePath { get; set; }
    public string? ThumbnailCacheKey { get; set; }
    public bool ThumbRequested { get; set; }

    public ImageSource? ImageSource
    {
        get => _imageSource;
        set
        {
            if (ReferenceEquals(_imageSource, value)) return;
            _imageSource = value;
            OnPropertyChanged();
        }
    }

    public string RelativeTime { get; set; } = string.Empty;
    public string FullTimestamp { get; set; } = string.Empty;
    public string SourceApp { get; set; } = string.Empty;
    public bool HasSource => !string.IsNullOrEmpty(SourceApp);
    public string SizeText { get; set; } = string.Empty;
    public string ImageInfoText { get; set; } = string.Empty;
    public bool IsLargeImage { get; set; }
    public double ThumbnailBoxWidth { get; set; } = 150;
    public double ThumbnailBoxHeight { get; set; } = 96;

    public bool IsFavorite => _isFavorite;
    public string FavoriteGlyph => _isFavorite ? "★" : "☆";
    public string FavoriteTooltip => _isFavorite ? "取消收藏" : "收藏";
    public string? FavoriteState => _isFavorite ? "on" : null;

    /// <summary>更新收藏状态并通知收藏按钮和右键菜单刷新。</summary>
    public void SetFavorite(bool favorite)
    {
        if (_isFavorite == favorite)
            return;

        _isFavorite = favorite;
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(FavoriteGlyph));
        OnPropertyChanged(nameof(FavoriteTooltip));
        OnPropertyChanged(nameof(FavoriteState));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
