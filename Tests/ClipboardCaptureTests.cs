using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using STool.Core;
using STool.Modules.Clipboard;
using Xunit;

namespace STool.Tests;

public class ClipboardCaptureTests
{
    private const int BusyHResult = unchecked((int)0x800401D0);

    [Fact]
    public void ClipboardMonitor_IsStale_WhenSequenceChangedDuringRead()
    {
        Assert.False(ClipboardMonitor.IsStale(7, 7));
        Assert.True(ClipboardMonitor.IsStale(7, 8));
    }

    [Fact]
    public async Task ReadRetry_RetriesBusyThenSucceeds()
    {
        var attempts = 0;

        var result = await ClipboardService.RetryBusyAsync(
            () =>
            {
                attempts++;
                if (attempts < 3)
                    throw new COMException("busy", BusyHResult);
                return "text";
            },
            ClipboardMonitor.ReadAttempts,
            delayMs: 1,
            CancellationToken.None);

        Assert.Equal("text", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ReadRetry_GivesUpAfterMaxAttempts()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => ClipboardService.RetryBusyAsync<string?>(
            () =>
            {
                attempts++;
                throw new COMException("busy", BusyHResult);
            },
            attempts: 4,
            delayMs: 1,
            CancellationToken.None));

        Assert.Equal(4, attempts);
    }

    [Fact]
    public void Capture_EstimatedBytes_CountsImagePixels()
    {
        var pixels = new byte[100 * 50 * 4];
        var image = BitmapSource.Create(100, 50, 96, 96, PixelFormats.Bgra32, null, pixels, 100 * 4);
        image.Freeze();

        var capture = new ClipboardCapture(ClipboardItemType.Image, null, image, null, null, DateTime.Now);

        Assert.Equal(100 * 50 * 4, capture.EstimatedBytes);
        Assert.Equal(10, new ClipboardCapture(ClipboardItemType.Text, "12345", null, null, null, DateTime.Now).EstimatedBytes);
    }

    [Fact]
    public void PendingQueue_DropsOldestWhenCountExceeded()
    {
        var dropped = new List<string>();
        var queue = new PendingCaptureQueue<(string Name, long Size)>(2, 1000, item => item.Size, (item, _) => dropped.Add(item.Name));

        queue.TryEnqueue(("a", 1));
        queue.TryEnqueue(("b", 1));
        queue.TryEnqueue(("c", 1));

        Assert.Equal(2, queue.Count);
        Assert.Equal(new[] { "a" }, dropped);
    }

    [Fact]
    public void PendingQueue_DropsOldestImagesWhenByteBudgetExceeded()
    {
        var dropped = new List<string>();
        var queue = new PendingCaptureQueue<(string Name, long Size)>(16, 100, item => item.Size, (item, _) => dropped.Add(item.Name));

        queue.TryEnqueue(("img1", 60));
        queue.TryEnqueue(("text", 0));
        queue.TryEnqueue(("img2", 60));
        queue.TryEnqueue(("img3", 60));

        // 小文本不会因字节预算被丢弃，最旧的图片先丢。
        Assert.Equal(new[] { "img1", "img2" }, dropped);
        Assert.Equal(2, queue.Count);
        Assert.Equal(60, queue.TotalBytes);
    }

    [Fact]
    public void PendingQueue_KeepsNewestEvenWhenItAloneExceedsBudget()
    {
        var queue = new PendingCaptureQueue<(string Name, long Size)>(16, 100, item => item.Size);

        queue.TryEnqueue(("small", 10));
        queue.TryEnqueue(("huge", 500));

        Assert.Equal(1, queue.Count);
        Assert.Equal(500, queue.TotalBytes);
    }

    [Fact]
    public async Task PendingQueue_ReadsInOrderAndCompletes()
    {
        var queue = new PendingCaptureQueue<int>(2, 1000, _ => 1);
        queue.TryEnqueue(1);
        queue.TryEnqueue(2);
        queue.TryEnqueue(3); // 1 被丢弃，留下多余信号
        queue.Complete();

        var read = new List<int>();
        await foreach (var item in queue.ReadAllAsync())
            read.Add(item);

        Assert.Equal(new[] { 2, 3 }, read);
        Assert.False(queue.TryEnqueue(4));
    }

    [Fact]
    public async Task PendingQueue_WaitsForItemsAfterDrops()
    {
        var queue = new PendingCaptureQueue<int>(1, 1000, _ => 1);
        queue.TryEnqueue(1);
        queue.TryEnqueue(2); // 丢弃 1

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reader = Task.Run(async () =>
        {
            var items = new List<int>();
            await foreach (var item in queue.ReadAllAsync(cancellation.Token))
            {
                items.Add(item);
                if (items.Count == 2)
                    break;
            }
            return items;
        });

        await Task.Delay(50);
        queue.TryEnqueue(3);

        Assert.Equal(new[] { 2, 3 }, await reader);
    }
}
