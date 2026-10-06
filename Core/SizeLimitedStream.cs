using System.IO;

namespace STool.Core;

/// <summary>
/// 限制可寻址输出流的最终长度，允许编码器回写文件头。限制不是累计写入量，
/// 也不代表底层流容量或原生编码器的总内存上限。
/// </summary>
internal sealed class SizeLimitedStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maximumLength;
    private readonly bool _leaveOpen;
    private bool _disposed;

    public SizeLimitedStream(Stream inner, long maximumLength, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumLength);
        if (!inner.CanSeek || !inner.CanWrite)
            throw new ArgumentException("输出流必须可写且可寻址。", nameof(inner));
        if (inner.Length > maximumLength || inner.Position > maximumLength)
            throw new ArgumentException("输出流已超出长度上限。", nameof(inner));

        _inner = inner;
        _maximumLength = maximumLength;
        _leaveOpen = leaveOpen;
    }

    // WIC 可能包装原始异常，调用方用该标记区分超限与其他编码失败。
    public bool LimitExceeded { get; private set; }
    public override bool CanRead => !_disposed && _inner.CanRead;
    public override bool CanSeek => !_disposed && _inner.CanSeek;
    public override bool CanWrite => !_disposed && _inner.CanWrite;

    public override long Length
    {
        get { ThrowIfDisposed(); return _inner.Length; }
    }

    public override long Position
    {
        get { ThrowIfDisposed(); return _inner.Position; }
        set
        {
            ThrowIfDisposed();
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            CheckLength(value);
            _inner.Position = value;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        var basis = origin switch
        {
            SeekOrigin.Begin => 0,
            SeekOrigin.Current => _inner.Position,
            SeekOrigin.End => _inner.Length,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (offset > 0 && basis > long.MaxValue - offset)
            throw Exceeded();
        var target = basis + offset;
        if (target < 0)
            throw new IOException("不能定位到流起点之前。");
        CheckLength(target);
        return _inner.Seek(target, SeekOrigin.Begin);
    }

    public override void SetLength(long value)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        CheckLength(value);
        _inner.SetLength(value);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        CheckWrite(buffer.Length);
        _inner.Write(buffer);
    }

    public override void WriteByte(byte value)
    {
        CheckWrite(1);
        _inner.WriteByte(value);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckWrite(buffer.Length);
        return _inner.WriteAsync(buffer, cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ThrowIfDisposed();
        return _inner.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        return _inner.Read(buffer);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _inner.ReadAsync(buffer, cancellationToken);
    }

    public override void Flush()
    {
        ThrowIfDisposed();
        _inner.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _inner.FlushAsync(cancellationToken);
    }

    private void CheckWrite(int count)
    {
        ThrowIfDisposed();
        // 避免 Position + count 溢出；超限前拒绝，不扩容、不部分写入。
        if (_inner.Position > _maximumLength || count > _maximumLength - _inner.Position)
            throw Exceeded();
    }

    private void CheckLength(long value)
    {
        if (value > _maximumLength)
            throw Exceeded();
    }

    private OutputLimitExceededException Exceeded()
    {
        LimitExceeded = true;
        return new OutputLimitExceededException(_maximumLength);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing && !_leaveOpen)
                _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class OutputLimitExceededException(long limit)
    : IOException($"编码输出超过 {limit} 字节上限。")
{
    public long Limit { get; } = limit;
}
