using System.Runtime.InteropServices;
using STool.Modules.Clipboard;
using Xunit;

namespace STool.Tests;

public class ClipboardManagerTests
{
    [Fact]
    public void ClipboardMonitor_Suppression_CoversAllMessagesForOneSequence()
    {
        using var monitor = new ClipboardMonitor();

        monitor.BeginUpdateSuppression();
        Assert.True(monitor.ShouldSuppressUpdate(41));

        monitor.CompleteUpdateSuppression(42);
        Assert.True(monitor.ShouldSuppressUpdate(42));
        Assert.True(monitor.ShouldSuppressUpdate(42));
        Assert.False(monitor.ShouldSuppressUpdate(43));

        monitor.BeginUpdateSuppression();
        monitor.CancelUpdateSuppression();
        Assert.False(monitor.ShouldSuppressUpdate(43));
    }

    [Fact]
    public async Task SetClipboardWithRetryAsync_RetriesBusyOperation()
    {
        var attempts = 0;

        await ClipboardManager.SetClipboardWithRetryAsync(() =>
        {
            attempts++;
            if (attempts < 3)
                throw new COMException("busy", unchecked((int)0x800401D0));
        });

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task SetClipboardWithRetryAsync_DoesNotRetryOtherErrors()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClipboardManager.SetClipboardWithRetryAsync(() =>
            {
                attempts++;
                throw new InvalidOperationException("failed");
            }));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task SetClipboardWithRetryAsync_CanBeCanceledDuringWait()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ClipboardManager.SetClipboardWithRetryAsync(() =>
            {
                attempts++;
                cancellation.Cancel();
                throw new COMException("busy", unchecked((int)0x800401D0));
            }, cancellation.Token));

        Assert.Equal(1, attempts);
    }
}
