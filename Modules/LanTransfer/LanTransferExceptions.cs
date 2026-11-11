using System.IO;

namespace STool.Modules.LanTransfer;

/// <summary>未认证（缺少或失效的会话）。映射为 401，网页据此跳回登录页。</summary>
internal sealed class LanAuthException(string message) : Exception(message);

/// <summary>请求来源无效（缺少防跨站头或 Origin 不符）。映射为 403，不应让网页退出登录。</summary>
internal sealed class LanRequestForbiddenException(string message) : Exception(message);

/// <summary>任务属于其他设备。映射为 403。</summary>
internal sealed class TransferOwnershipException(string message) : Exception(message);

/// <summary>传输名额在等待时间内没有空出。映射为 503 并带 Retry-After。</summary>
internal sealed class TransferBusyException(string message, int retryAfterSeconds) : Exception(message)
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>
/// 包装网络流：单次读/写在超时时间内没有任何进展就抛出 <see cref="TimeoutException"/>。
/// 只对单次 IO 计时，任务暂停期间（不在读写中）不会超时。
/// </summary>
internal sealed class IdleTimeoutStream(Stream inner, TimeSpan idleTimeout, Action? onTimeout = null) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return await inner.ReadAsync(buffer, cancellationToken).AsTask().WaitAsync(idleTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            onTimeout?.Invoke();
            throw;
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            await inner.WriteAsync(buffer, cancellationToken).AsTask().WaitAsync(idleTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            onTimeout?.Invoke();
            throw;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
