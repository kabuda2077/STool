using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Serilog;

namespace STool.Core;

/// <summary>
/// 剪贴板单一所有者：所有读写都在同一条专属 STA 线程上串行执行。
/// 这样 STool 内部不会自相争抢系统剪贴板锁，重试也不会阻塞 UI 线程。
/// </summary>
internal sealed class ClipboardService : IDisposable
{
    private const int ClipboardBusyHResult = unchecked((int)0x800401D0);
    private const int WriteAttempts = 20;
    private const int RetryDelayMs = 100;

    /// <summary>STool 自己写入的内容会带上该格式，用于识别并跳过自身的剪贴板通知。</summary>
    internal const string InternalFormat = "STool.Internal.ClipboardWrite";

    private readonly System.Collections.Concurrent.BlockingCollection<Func<Task>> _work = new();
    private readonly Thread _thread;
    private bool _disposed;

    public ClipboardService()
    {
        _thread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = "STool.Clipboard"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void RunLoop()
    {
        foreach (var operation in _work.GetConsumingEnumerable())
        {
            try
            {
                operation().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Clipboard operation failed on owner thread");
            }
        }
    }

    /// <summary>在剪贴板所有者线程上排队执行一个操作。</summary>
    public Task RunAsync(Func<Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_work.TryAdd(async () =>
            {
                try
                {
                    await operation();
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }))
        {
            completion.TrySetException(new ObjectDisposedException(nameof(ClipboardService)));
        }

        return cancellationToken.CanBeCanceled
            ? completion.Task.WaitAsync(cancellationToken)
            : completion.Task;
    }

    public Task SetTextAsync(string text, CancellationToken cancellationToken = default) =>
        RunAsync(() => WriteTaggedAsync(data => data.SetText(text)), cancellationToken);

    public Task SetImageAsync(BitmapSource image, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            var frozen = image.IsFrozen ? image : CloneAndFreeze(image);
            return WriteTaggedAsync(data => data.SetImage(frozen));
        }, cancellationToken);

    public Task SetFileDropListAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            var files = new StringCollection();
            foreach (var path in paths)
                files.Add(path);

            return WriteTaggedAsync(data => data.SetFileDropList(files));
        }, cancellationToken);

    public Task SetDataObjectAsync(DataObject data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        return RunAsync(() => WriteDataObjectAsync(data), cancellationToken);
    }

    /// <summary>写入并附带内部格式标记，监听器据此跳过自身产生的通知。</summary>
    public Task WriteTaggedAsync(Action<DataObject> populate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(populate);
        return RunAsync(() => WriteCoreAsync(() =>
        {
            var data = new DataObject();
            populate(data);
            data.SetData(InternalFormat, true);
            return data;
        }), cancellationToken);
    }

    private static Task WriteTaggedAsync(Action<DataObject> populate) =>
        WriteCoreAsync(() =>
        {
            var data = new DataObject();
            populate(data);
            data.SetData(InternalFormat, true);
            return data;
        });

    private static Task WriteDataObjectAsync(DataObject data) =>
        WriteCoreAsync(() =>
        {
            data.SetData(InternalFormat, true);
            return data;
        });

    private static Task WriteCoreAsync(Func<DataObject> build) =>
        RetryBusyAsync(
            // copy: true 让数据留在剪贴板，写入方随后释放对象也不会丢失内容。
            () => System.Windows.Clipboard.SetDataObject(build(), true),
            WriteAttempts,
            RetryDelayMs,
            CancellationToken.None);

    /// <summary>
    /// 剪贴板被其他程序占用（CLIPBRD_E_CANT_OPEN）时按固定间隔重试；其他异常原样抛出。
    /// 写入与读取共用这一套逻辑。
    /// </summary>
    internal static Task RetryBusyAsync(
        Action operation,
        int attempts,
        int delayMs,
        CancellationToken cancellationToken)
    {
        return RetryBusyAsync<object?>(
            () =>
            {
                operation();
                return null;
            },
            attempts,
            delayMs,
            cancellationToken);
    }

    internal static Task<T> RetryBusyAsync<T>(
        Func<T> operation,
        int attempts,
        int delayMs,
        CancellationToken cancellationToken)
    {
        try
        {
            return Task.FromResult(RetryBusy(operation, attempts, delayMs, cancellationToken));
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }

    private static T RetryBusy<T>(
        Func<T> operation,
        int attempts,
        int delayMs,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return operation();
            }
            catch (COMException ex) when (ex.HResult == ClipboardBusyHResult)
            {
                if (attempt >= attempts)
                {
                    Log.Warning(ex, "Clipboard stayed locked after {Attempts} attempts", attempts);
                    throw new InvalidOperationException("剪贴板正被其他程序占用，请稍后重试。", ex);
                }

                // 只在专属 STA 线程上调用。该线程没有 SynchronizationContext，
                // await Task.Delay 会在线程池（MTA）上继续，重试时访问剪贴板会抛 ThreadStateException。
                // 这里直接阻塞等待，线程本来就是串行专用的。
                if (cancellationToken.WaitHandle.WaitOne(delayMs))
                    cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    /// <summary>默认的写入重试参数，供 <see cref="ClipboardWriter"/> 复用。</summary>
    internal static Task RetryBusyAsync(Action operation, CancellationToken cancellationToken) =>
        RetryBusyAsync(operation, WriteAttempts, RetryDelayMs, cancellationToken);

    internal static bool IsInternalWrite()
    {
        try
        {
            return System.Windows.Clipboard.ContainsData(InternalFormat);
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static BitmapSource CloneAndFreeze(BitmapSource image)
    {
        var copy = image.Clone();
        copy.Freeze();
        return copy;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _work.CompleteAdding();
        try
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Clipboard owner thread did not stop cleanly");
        }
        _work.Dispose();
    }
}
