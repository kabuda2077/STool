using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace STool.Modules.Clipboard;

/// <summary>
/// 待处理队列：同时按条数和估算字节数限制，超出时丢弃最旧的条目。
/// 单个读取者、多个写入者；字节预算主要约束全尺寸位图，避免排队的大图占用数 GB 内存。
/// </summary>
internal sealed class PendingCaptureQueue<T>
{
    private readonly object _gate = new();
    private readonly LinkedList<(T Item, long Bytes)> _items = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly int _maxCount;
    private readonly long _maxBytes;
    private readonly Func<T, long> _sizeOf;
    private readonly Action<T, string>? _onDropped;
    private long _totalBytes;
    private bool _completed;

    public PendingCaptureQueue(int maxCount, long maxBytes, Func<T, long> sizeOf, Action<T, string>? onDropped = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        _maxCount = maxCount;
        _maxBytes = maxBytes;
        _sizeOf = sizeOf;
        _onDropped = onDropped;
    }

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    public long TotalBytes
    {
        get { lock (_gate) return _totalBytes; }
    }

    /// <summary>入队。超出上限时先丢弃最旧条目；最新入队的条目始终保留。队列已完成时返回 false。</summary>
    public bool TryEnqueue(T item)
    {
        var dropped = new List<(T Item, string Reason)>();
        lock (_gate)
        {
            if (_completed)
                return false;

            var bytes = Math.Max(0, _sizeOf(item));
            _items.AddLast((item, bytes));
            _totalBytes += bytes;

            while (_items.Count > 1 && _items.Count > _maxCount)
                DropFirst(dropped, "queue full");

            // 超出字节预算时只丢弃有体积的旧条目（图片），小文本不受影响。
            var node = _items.First;
            while (_totalBytes > _maxBytes && node != null && node != _items.Last)
            {
                var next = node.Next;
                if (node.Value.Bytes > 0)
                {
                    dropped.Add((node.Value.Item, "byte budget exceeded"));
                    _totalBytes -= node.Value.Bytes;
                    _items.Remove(node);
                }
                node = next;
            }
        }

        // 每个入队的条目对应一次信号；被丢弃条目留下的多余信号由读取方忽略。
        _signal.Release();

        foreach (var (droppedItem, reason) in dropped)
        {
            Log.Information("Dropped pending clipboard capture: {Reason}", reason);
            _onDropped?.Invoke(droppedItem, reason);
        }

        return true;
    }

    /// <summary>不再接受新条目；已入队的条目仍可被读完。</summary>
    public void Complete()
    {
        lock (_gate)
        {
            if (_completed)
                return;
            _completed = true;
        }

        _signal.Release();
    }

    public async IAsyncEnumerable<T> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);

            T item;
            lock (_gate)
            {
                if (_items.First == null)
                {
                    if (_completed)
                        yield break;
                    continue;
                }

                var first = _items.First.Value;
                _items.RemoveFirst();
                _totalBytes -= first.Bytes;
                item = first.Item;
            }

            yield return item;
        }
    }

    private void DropFirst(List<(T Item, string Reason)> dropped, string reason)
    {
        var first = _items.First!.Value;
        _items.RemoveFirst();
        _totalBytes -= first.Bytes;
        dropped.Add((first.Item, reason));
    }
}
