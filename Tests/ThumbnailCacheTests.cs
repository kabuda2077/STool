using System.IO;
using System.Windows.Media;
using STool.Modules.Clipboard;
using Xunit;

namespace STool.Tests;

public class ThumbnailCacheTests
{
    [Fact]
    public void Set_UnderLimits_KeepsEntries()
    {
        var cache = new ThumbnailCache(maxEntries: 3, maxBytes: 300);

        cache.Set("a", CreateImage(), 100);
        cache.Set("b", CreateImage(), 100);

        Assert.Equal(2, cache.Count);
        Assert.Equal(200, cache.EstimatedBytes);
        Assert.True(cache.TryGet("a", out var image));
        Assert.NotNull(image);
    }

    [Fact]
    public void Set_OverEntryLimit_EvictsLeastRecentlyUsed()
    {
        var cache = new ThumbnailCache(maxEntries: 2, maxBytes: 1_000);
        var evicted = new List<string>();
        cache.ItemEvicted += evicted.Add;

        cache.Set("a", CreateImage(), 100);
        cache.Set("b", CreateImage(), 100);
        cache.Set("c", CreateImage(), 100);

        Assert.False(cache.Contains("a"));
        Assert.True(cache.Contains("b"));
        Assert.True(cache.Contains("c"));
        Assert.Equal(new[] { "a" }, evicted);
    }

    [Fact]
    public void Set_OverByteLimit_EvictsUntilWithinBudget()
    {
        var cache = new ThumbnailCache(maxEntries: 10, maxBytes: 250);

        cache.Set("a", CreateImage(), 100);
        cache.Set("b", CreateImage(), 100);
        cache.Set("c", CreateImage(), 100);

        Assert.Equal(2, cache.Count);
        Assert.Equal(200, cache.EstimatedBytes);
        Assert.False(cache.Contains("a"));
    }

    [Fact]
    public void TryGet_PromotesEntryInLruOrder()
    {
        var cache = new ThumbnailCache(maxEntries: 2, maxBytes: 1_000);
        cache.Set("a", CreateImage(), 100);
        cache.Set("b", CreateImage(), 100);

        Assert.True(cache.TryGet("a", out _));
        cache.Set("c", CreateImage(), 100);

        Assert.True(cache.Contains("a"));
        Assert.False(cache.Contains("b"));
        Assert.True(cache.Contains("c"));
    }

    [Fact]
    public void Set_SameKey_ReplacesWithoutGrowingCount()
    {
        var cache = new ThumbnailCache(maxEntries: 2, maxBytes: 1_000);
        var replacement = CreateImage();
        cache.Set("a", CreateImage(), 100);

        cache.Set("a", replacement, 150);

        Assert.Equal(1, cache.Count);
        Assert.Equal(150, cache.EstimatedBytes);
        Assert.True(cache.TryGet("a", out var cached));
        Assert.Same(replacement, cached);
    }

    [Fact]
    public void Clear_RemovesAllEntriesAndNotifiesOwners()
    {
        var cache = new ThumbnailCache(maxEntries: 3, maxBytes: 1_000);
        var evicted = new HashSet<string>();
        cache.ItemEvicted += key => evicted.Add(key);
        cache.Set("a", CreateImage(), 100);
        cache.Set("b", CreateImage(), 100);

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.EstimatedBytes);
        Assert.Equal(new HashSet<string> { "a", "b" }, evicted);
    }

    [Fact]
    public void BuildThumbnailCacheKey_FileTimestampChanges_InvalidatesKey()
    {
        var path = Path.GetTempFileName();
        try
        {
            var firstTimestamp = DateTime.UtcNow.AddMinutes(-2);
            File.SetLastWriteTimeUtc(path, firstTimestamp);
            var first = ClipboardPanel.BuildThumbnailCacheKey("id", path);

            File.SetLastWriteTimeUtc(path, firstTimestamp.AddMinutes(1));
            var second = ClipboardPanel.BuildThumbnailCacheKey("id", path);

            Assert.NotEqual(first, second);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ImageSource CreateImage() => new DrawingImage();
}
