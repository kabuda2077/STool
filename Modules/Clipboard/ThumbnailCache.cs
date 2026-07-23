using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace STool.Modules.Clipboard;

/// <summary>
/// 有界的冻结缩略图缓存。缓存本身不持有 ViewModel,由调用方处理淘汰后的引用清理。
/// </summary>
internal sealed class ThumbnailCache
{
    public const int DefaultMaxEntries = 48;
    public const long DefaultMaxBytes = 24 * 1024 * 1024;

    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private readonly object _gate = new();
    private long _estimatedBytes;

    public ThumbnailCache(int maxEntries = DefaultMaxEntries, long maxBytes = DefaultMaxBytes)
    {
        if (maxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));

        _maxEntries = maxEntries;
        _maxBytes = maxBytes;
    }

    public event Action<string>? ItemEvicted;

    public int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    public long EstimatedBytes
    {
        get
        {
            lock (_gate)
                return _estimatedBytes;
        }
    }

    public bool TryGet(string key, out ImageSource? image)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                image = null;
                return false;
            }

            Touch(entry.Node);
            image = entry.Image;
            return true;
        }
    }

    public bool Contains(string key)
    {
        lock (_gate)
            return _entries.ContainsKey(key);
    }

    public void Set(string key, ImageSource image, long estimatedBytes)
    {
        ArgumentNullException.ThrowIfNull(image);

        var bytes = estimatedBytes > 0 ? estimatedBytes : EstimateBytes(image);
        if (bytes > _maxBytes)
        {
            Remove(key);
            return;
        }

        List<string>? evicted = null;
        lock (_gate)
        {
            if (_entries.Remove(key, out var previous))
            {
                _lru.Remove(previous.Node);
                _estimatedBytes -= previous.EstimatedBytes;
            }

            var node = _lru.AddFirst(key);
            _entries[key] = new CacheEntry(image, bytes, node);
            _estimatedBytes += bytes;

            while (_entries.Count > _maxEntries || _estimatedBytes > _maxBytes)
            {
                var last = _lru.Last;
                if (last == null)
                    break;

                var removedKey = last.Value;
                _lru.RemoveLast();
                if (_entries.Remove(removedKey, out var removed))
                {
                    _estimatedBytes -= removed.EstimatedBytes;
                    (evicted ??= new List<string>()).Add(removedKey);
                }
            }
        }

        NotifyEvicted(evicted);
    }

    public void Remove(string key)
    {
        var removed = false;
        lock (_gate)
        {
            if (!_entries.Remove(key, out var entry))
                return;

            _lru.Remove(entry.Node);
            _estimatedBytes -= entry.EstimatedBytes;
            removed = true;
        }

        if (removed)
            ItemEvicted?.Invoke(key);
    }

    public void Clear()
    {
        List<string> keys;
        lock (_gate)
        {
            keys = new List<string>(_entries.Keys);
            _entries.Clear();
            _lru.Clear();
            _estimatedBytes = 0;
        }

        NotifyEvicted(keys);
    }

    private void Touch(LinkedListNode<string> node)
    {
        if (node.List == _lru && node != _lru.First)
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
        }
    }

    private void NotifyEvicted(IEnumerable<string>? keys)
    {
        if (keys == null || ItemEvicted == null)
            return;

        foreach (var key in keys)
            ItemEvicted(key);
    }

    private static long EstimateBytes(ImageSource image)
    {
        if (image is BitmapSource bitmap)
            return Math.Max(1, (long)bitmap.PixelWidth * bitmap.PixelHeight * 4);

        return 0;
    }

    private sealed record CacheEntry(ImageSource Image, long EstimatedBytes, LinkedListNode<string> Node);
}
