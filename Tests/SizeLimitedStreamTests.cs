using System.IO;
using STool.Core;
using Xunit;

namespace STool.Tests;

public class SizeLimitedStreamTests
{
    [Fact]
    public void ExactLimitAndHeaderRewrite_AreAllowed()
    {
        using var inner = new MemoryStream();
        using var limited = new SizeLimitedStream(inner, 4, leaveOpen: true);
        limited.Write(new byte[] { 1, 2, 3, 4 }, 0, 4);
        limited.Seek(0, SeekOrigin.Begin);
        limited.Write(new byte[] { 5, 6 }.AsSpan());
        limited.Position = 4;
        limited.Write(ReadOnlySpan<byte>.Empty);

        Assert.Equal(new byte[] { 5, 6, 3, 4 }, inner.ToArray());
        Assert.False(limited.LimitExceeded);
    }

    [Fact]
    public void OversizedWrite_IsRejectedBeforeChangingTheUnderlyingStream()
    {
        using var inner = new MemoryStream();
        using var limited = new SizeLimitedStream(inner, 4);
        limited.WriteByte(1);

        Assert.Throws<OutputLimitExceededException>(() => limited.Write(new byte[4], 0, 4));
        Assert.Equal(1, inner.Length);
        Assert.Equal(1, inner.Position);
        Assert.True(limited.LimitExceeded);
    }

    [Fact]
    public void SeekPositionAndLength_CannotBypassLimit()
    {
        using var inner = new MemoryStream();
        using var limited = new SizeLimitedStream(inner, 4);
        Assert.Throws<OutputLimitExceededException>(() => limited.Position = 5);
        Assert.Throws<OutputLimitExceededException>(() => limited.Seek(5, SeekOrigin.Begin));
        Assert.Throws<OutputLimitExceededException>(() => limited.SetLength(5));
        Assert.Equal(0, inner.Length);
        Assert.Equal(0, inner.Position);
    }

    [Fact]
    public void InvalidNegativePositions_AreNotReportedAsLimitFailures()
    {
        using var inner = new MemoryStream();
        using var limited = new SizeLimitedStream(inner, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => limited.Position = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => limited.SetLength(-1));
        Assert.Throws<IOException>(() => limited.Seek(-1, SeekOrigin.Begin));
        Assert.False(limited.LimitExceeded);
    }

    [Fact]
    public void OverflowingSeek_IsRejectedWithoutWrapping()
    {
        using var inner = new MemoryStream();
        using var limited = new SizeLimitedStream(inner, long.MaxValue);
        limited.WriteByte(1);

        Assert.Throws<OutputLimitExceededException>(() => limited.Seek(long.MaxValue, SeekOrigin.Current));
        Assert.Equal(1, limited.Position);
    }

    [Fact]
    public void TruncateThenWrite_UsesFinalLengthRatherThanCumulativeBytes()
    {
        using var inner = new MemoryStream();
        using var limited = new SizeLimitedStream(inner, 4);
        limited.SetLength(4);
        limited.SetLength(1);
        limited.Seek(0, SeekOrigin.End);
        limited.Write(new byte[] { 1, 2, 3 });

        Assert.Equal(4, limited.Length);
        Assert.False(limited.LimitExceeded);
    }

    [Fact]
    public async Task AsyncWrites_RespectTheSameLimit()
    {
        using var inner = new MemoryStream();
        using var limited = new SizeLimitedStream(inner, 2);
        await limited.WriteAsync(new byte[] { 1 }.AsMemory());
        await limited.WriteAsync(new byte[] { 2 }, 0, 1, CancellationToken.None);

        await Assert.ThrowsAsync<OutputLimitExceededException>(async () =>
            await limited.WriteAsync(new byte[] { 3 }.AsMemory()));
        Assert.Equal(new byte[] { 1, 2 }, inner.ToArray());
    }

    [Fact]
    public void LimitFlag_IsStickyAfterFailureAndCanIdentifyWrappedExceptions()
    {
        using var inner = new MemoryStream();
        using var limited = new SizeLimitedStream(inner, 0);
        var original = Assert.Throws<OutputLimitExceededException>(() => limited.WriteByte(1));
        var wrapped = new IOException("encoder failed", original);

        Assert.True(limited.LimitExceeded);
        Assert.Same(original, wrapped.InnerException);
        limited.Position = 0;
        Assert.True(limited.LimitExceeded);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Dispose_RespectsLeaveOpenAndIsIdempotent(bool leaveOpen)
    {
        using var inner = new MemoryStream();
        var limited = new SizeLimitedStream(inner, 4, leaveOpen);
        limited.Dispose();
        limited.Dispose();

        Assert.Equal(leaveOpen, inner.CanWrite);
        Assert.False(limited.CanWrite);
        Assert.Throws<ObjectDisposedException>(() => limited.WriteByte(1));
    }
}
