using System.IO;
using STool.Modules.Clipboard;
using Xunit;

namespace STool.Tests;

public class ClipboardStorageTests
{
    [Fact]
    public void AddAndQuery_PreservesContentAndConvertsTimeToLocal()
    {
        using var fixture = new StorageFixture();
        var createdUtc = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        fixture.Storage.Add(new ClipboardItem
        {
            Id = "text-1",
            Type = ClipboardItemType.Text,
            TextContent = "hello",
            SourceApp = "Code.exe",
            CreatedAt = createdUtc
        });

        var item = Assert.Single(fixture.Storage.GetRecent());

        Assert.Equal("hello", item.TextContent);
        Assert.Equal("Code.exe", item.SourceApp);
        Assert.Equal(createdUtc, item.CreatedAt.ToUniversalTime());
        Assert.Equal(DateTimeKind.Local, item.CreatedAt.Kind);
        Assert.Single(fixture.Storage.Search("Code"));
    }

    [Fact]
    public void ClearAll_PreservesFavoritesAndDeletesImageFiles()
    {
        using var fixture = new StorageFixture();
        var imagePath = fixture.CreateImage("clipboard_image.png");
        var thumbPath = fixture.CreateThumbnail("clipboard_image.thumb.png");
        fixture.Storage.Add(new ClipboardItem
        {
            Id = "image-1",
            Type = ClipboardItemType.Image,
            ImagePath = imagePath
        });
        fixture.Storage.Add(new ClipboardItem
        {
            Id = "favorite-1",
            Type = ClipboardItemType.Text,
            TextContent = "keep",
            IsFavorite = true
        });

        fixture.Storage.ClearAll();

        var remaining = Assert.Single(fixture.Storage.GetRecent());
        Assert.Equal("favorite-1", remaining.Id);
        Assert.False(File.Exists(imagePath));
        Assert.False(File.Exists(thumbPath));
    }

    [Fact]
    public void Reopen_RemovesImageRecordWhenSourceFileIsMissing()
    {
        using var fixture = new StorageFixture();
        var imagePath = fixture.CreateImage("missing.png");
        fixture.Storage.Add(new ClipboardItem
        {
            Id = "missing-image",
            Type = ClipboardItemType.Image,
            ImagePath = imagePath
        });
        fixture.DisposeStorage();
        File.Delete(imagePath);

        fixture.ReopenStorage();

        Assert.Empty(fixture.Storage.GetRecent());
    }

    [Fact]
    public void Reopen_RemovesOrphanedImageAndThumbnailFiles()
    {
        using var fixture = new StorageFixture();
        var imagePath = fixture.CreateImage("orphan.png");
        var thumbPath = fixture.CreateThumbnail("orphan.thumb.png");

        fixture.DisposeStorage();
        fixture.ReopenStorage();

        Assert.False(File.Exists(imagePath));
        Assert.False(File.Exists(thumbPath));
    }

    [Fact]
    public void ConcurrentReadsAndWrites_AreSerialized()
    {
        using var fixture = new StorageFixture();

        Parallel.For(0, 40, index =>
        {
            fixture.Storage.Add(new ClipboardItem
            {
                Id = $"item-{index}",
                Type = ClipboardItemType.Text,
                TextContent = $"value-{index}"
            });
            _ = fixture.Storage.GetRecent(10);
        });

        Assert.Equal(40, fixture.Storage.GetRecent(100).Count);
    }

    private sealed class StorageFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "SToolTests", Guid.NewGuid().ToString("N"));
        private readonly string _dbPath;
        private readonly string _imagesPath;
        private readonly string _thumbnailsPath;
        private ClipboardStorage? _storage;

        public StorageFixture()
        {
            _dbPath = Path.Combine(_root, "clipboard.db");
            _imagesPath = Path.Combine(_root, "images");
            _thumbnailsPath = Path.Combine(_root, "thumbnails");
            ReopenStorage();
        }

        public ClipboardStorage Storage => _storage ?? throw new ObjectDisposedException(nameof(StorageFixture));

        public string CreateImage(string name)
        {
            Directory.CreateDirectory(_imagesPath);
            var path = Path.Combine(_imagesPath, name);
            File.WriteAllBytes(path, [1, 2, 3]);
            return path;
        }

        public string CreateThumbnail(string name)
        {
            Directory.CreateDirectory(_thumbnailsPath);
            var path = Path.Combine(_thumbnailsPath, name);
            File.WriteAllBytes(path, [4, 5, 6]);
            return path;
        }

        public void DisposeStorage()
        {
            _storage?.Dispose();
            _storage = null;
        }

        public void ReopenStorage()
        {
            DisposeStorage();
            _storage = new ClipboardStorage(_dbPath, _imagesPath, _thumbnailsPath);
        }

        public void Dispose()
        {
            DisposeStorage();
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, true);
            }
            catch
            {
            }
        }
    }
}
