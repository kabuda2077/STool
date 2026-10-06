using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace STool.Core;

/// <summary>
/// 统一的剪贴板写入：剪贴板被其他进程短暂占用（CLIPBRD_E_CANT_OPEN）时自动重试，
/// 其余异常直接抛出。所有写剪贴板的功能都应走这里。
/// </summary>
internal static class ClipboardWriter
{
    private const int ClipboardBusyHResult = unchecked((int)0x800401D0);
    private const int WriteAttempts = 6;
    private const int RetryDelayMs = 50;

    public static Task SetTextAsync(string text, CancellationToken cancellationToken = default) =>
        RunAsync(() => System.Windows.Clipboard.SetText(text), cancellationToken);

    public static Task SetImageAsync(BitmapSource image, CancellationToken cancellationToken = default) =>
        RunAsync(() => System.Windows.Clipboard.SetImage(image), cancellationToken);

    public static async Task RunAsync(Action setClipboard, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setClipboard);

        for (var attempt = 1; attempt <= WriteAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                setClipboard();
                return;
            }
            catch (COMException ex) when (ex.HResult == ClipboardBusyHResult && attempt < WriteAttempts)
            {
                await Task.Delay(RetryDelayMs, cancellationToken);
            }
        }
    }
}
