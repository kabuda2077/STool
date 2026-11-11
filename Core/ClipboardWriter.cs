using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace STool.Core;

/// <summary>
/// 统一的剪贴板写入入口。实际写入由 <see cref="ClipboardService"/> 在专属 STA 线程上串行执行，
/// 避免 STool 内部自相争抢剪贴板，以及重试阻塞 UI 线程。
/// </summary>
internal static class ClipboardWriter
{
    private static readonly Lazy<ClipboardService> Shared = new(() => new ClipboardService());

    internal static ClipboardService Service => Shared.Value;

    public static Task SetTextAsync(string text, CancellationToken cancellationToken = default) =>
        Service.SetTextAsync(text, cancellationToken);

    public static Task SetImageAsync(BitmapSource image, CancellationToken cancellationToken = default) =>
        Service.SetImageAsync(image, cancellationToken);

    public static Task SetFileDropListAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        Service.SetFileDropListAsync(paths, cancellationToken);

    public static Task SetDataObjectAsync(System.Windows.DataObject data, CancellationToken cancellationToken = default) =>
        Service.SetDataObjectAsync(data, cancellationToken);

    /// <summary>写入内容并附带内部格式标记，监听器会跳过该次通知。</summary>
    public static Task WriteTaggedAsync(Action<System.Windows.DataObject> populate, CancellationToken cancellationToken = default) =>
        Service.WriteTaggedAsync(populate, cancellationToken);

    /// <summary>兼容旧调用点：在剪贴板所有者线程上执行任意剪贴板操作。</summary>
    public static Task RunAsync(Action setClipboard, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setClipboard);
        return Service.RunAsync(() => ClipboardService.RetryBusyAsync(setClipboard, cancellationToken), cancellationToken);
    }
}
