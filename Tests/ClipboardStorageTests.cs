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
        Assert.Single(fixture.Storage.Query(new ClipboardQuery(Keyword: "Code")));
    }

    [Fact]
    public void Query_LoadsPreviewOnly_FullTextIsReadOnDemand()
    {
        using var fixture = new StorageFixture();
        var longText = new string('x', ClipboardItem.TextPreviewLength + 50);
        fixture.Storage.Add(new ClipboardItem { Id = "long", Type = ClipboardItemType.Text, TextContent = longText });

        var listed = Assert.Single(fixture.Storage.Query(new ClipboardQuery()));
        Assert.Null(listed.TextContent);
        Assert.Equal(ClipboardItem.TextPreviewLength, listed.TextPreview!.Length);
        Assert.Equal(longText, fixture.Storage.GetById("long")!.TextContent);
    }

    [Fact]
    public void Query_FiltersByTypeFavoriteAndKeyword_BeyondFirstPage()
    {
        using var fixture = new StorageFixture();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var index = 0; index < 250; index++)
        {
            fixture.Storage.Add(new ClipboardItem
            {
                Id = $"item-{index}",
                Type = ClipboardItemType.Text,
                TextContent = index == 0 ? "needle in the oldest item" : $"value-{index}",
                IsFavorite = index == 1,
                CreatedAt = start.AddMinutes(index)
            });
        }
        fixture.Storage.Add(new ClipboardItem { Id = "file", Type = ClipboardItemType.File, FilePaths = new[] { @"C:\report.docx" } });

        // 最早的记录也能搜到、收藏也能列出，不再受"最近 200 条"限制。
        Assert.Equal("item-0", Assert.Single(fixture.Storage.Query(new ClipboardQuery(Keyword: "needle"))).Id);
        Assert.Equal("item-1", Assert.Single(fixture.Storage.Query(new ClipboardQuery(FavoritesOnly: true))).Id);
        Assert.Equal("file", Assert.Single(fixture.Storage.Query(new ClipboardQuery(ClipboardItemType.File))).Id);

        var secondPage = fixture.Storage.Query(new ClipboardQuery(ClipboardItemType.Text, Offset: 100, Limit: 100));
        Assert.Equal(100, secondPage.Count);
        Assert.Equal("item-149", secondPage[0].Id);
    }

    [Theory]
    [InlineData("100%")]
    [InlineData("a_b")]
    [InlineData(@"C:\path")]
    public void Query_TreatsLikeWildcardsInKeywordLiterally(string keyword)
    {
        using var fixture = new StorageFixture();
        fixture.Storage.Add(new ClipboardItem { Id = "match", Type = ClipboardItemType.Text, TextContent = $"prefix {keyword} suffix" });
        fixture.Storage.Add(new ClipboardItem { Id = "decoy", Type = ClipboardItemType.Text, TextContent = "100 percent aXb C:Xpath" });

        var result = Assert.Single(fixture.Storage.Query(new ClipboardQuery(Keyword: keyword)));
        Assert.Equal("match", result.Id);
    }

    [Fact]
    public void TryPromoteDuplicate_MovesExistingRecordToTop()
    {
        using var fixture = new StorageFixture();
        fixture.Storage.Add(new ClipboardItem { Id = "old", Type = ClipboardItemType.Text, TextContent = "a", ContentHash = "hash-a", CreatedAt = DateTime.Now.AddMinutes(-10) });
        fixture.Storage.Add(new ClipboardItem { Id = "new", Type = ClipboardItemType.Text, TextContent = "b", ContentHash = "hash-b", CreatedAt = DateTime.Now.AddMinutes(-5) });

        var promoted = fixture.Storage.TryPromoteDuplicate(ClipboardItemType.Text, "hash-a", DateTime.Now, "Code.exe");

        Assert.Equal("old", promoted!.Id);
        Assert.Equal("Code.exe", promoted.SourceApp);
        Assert.Equal(new[] { "old", "new" }, fixture.Storage.Query(new ClipboardQuery()).Select(item => item.Id));
        Assert.Null(fixture.Storage.TryPromoteDuplicate(ClipboardItemType.Text, "missing", DateTime.Now, null));
    }

    [Fact]
    public void ToggleFavorite_ReturnsNewState()
    {
        using var fixture = new StorageFixture();
        fixture.Storage.Add(new ClipboardItem { Id = "item", Type = ClipboardItemType.Text, TextContent = "x" });

        Assert.True(fixture.Storage.ToggleFavorite("item"));
        Assert.False(fixture.Storage.ToggleFavorite("item"));
        Assert.Null(fixture.Storage.ToggleFavorite("missing"));
    }

    [Fact]
    public void CleanOldEntries_ZeroRetentionKeepsOldRecords()
    {
        using var fixture = new StorageFixture();
        fixture.Storage.Add(new ClipboardItem { Id = "old", Type = ClipboardItemType.Text, TextContent = "x", CreatedAt = DateTime.Now.AddYears(-2) });

        fixture.Storage.CleanOldEntries(retentionDays: 0, maxEntries: 10);
        Assert.Single(fixture.Storage.GetRecent());

        fixture.Storage.CleanOldEntries(retentionDays: 30, maxEntries: 10);
        Assert.Empty(fixture.Storage.GetRecent());
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
    public void Maintenance_RemovesImageRecordWhenSourceFileIsMissing()
    {
        using var fixture = new StorageFixture();
        var imagePath = fixture.CreateImage("missing.png");
        fixture.Storage.Add(new ClipboardItem
        {
            Id = "missing-image",
            Type = ClipboardItemType.Image,
            ImagePath = imagePath
        });
        File.Delete(imagePath);

        fixture.Storage.RunMaintenance();

        Assert.Empty(fixture.Storage.GetRecent());
    }

    [Fact]
    public void Maintenance_RemovesOldOrphanedFilesButKeepsRecentOnes()
    {
        using var fixture = new StorageFixture();
        var oldImage = fixture.CreateImage("orphan.png", DateTime.UtcNow.AddHours(-1));
        var oldThumb = fixture.CreateThumbnail("orphan.thumb.png", DateTime.UtcNow.AddHours(-1));
        var recentImage = fixture.CreateImage("just-written.png");

        fixture.Storage.RunMaintenance();

        Assert.False(File.Exists(oldImage));
        Assert.False(File.Exists(oldThumb));
        Assert.True(File.Exists(recentImage));
    }

    [Fact]
    public void Maintenance_BackfillsImageSizeFromPngHeader()
    {
        using var fixture = new StorageFixture();
        var imagePath = fixture.CreatePng("sized.png", 640, 480);
        fixture.Storage.Add(new ClipboardItem { Id = "png", Type = ClipboardItemType.Image, ImagePath = imagePath });

        fixture.Storage.RunMaintenance();

        var item = Assert.Single(fixture.Storage.Query(new ClipboardQuery(ClipboardItemType.Image)));
        Assert.Equal(640, item.ImageWidth);
        Assert.Equal(480, item.ImageHeight);
        Assert.True(item.ImageBytes > 0);
    }

    [Fact]
    public void Reopen_KeepsExistingRecords()
    {
        using var fixture = new StorageFixture();
        fixture.Storage.Add(new ClipboardItem { Id = "persisted", Type = ClipboardItemType.Text, TextContent = "x" });

        fixture.ReopenStorage();

        Assert.Equal("persisted", Assert.Single(fixture.Storage.GetRecent()).Id);
    }

    [Fact]
    public void CorruptDatabase_IsPreservedInsteadOfReplacedByAnEmptyDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), "STool.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "clipboard.db");
            var original = new byte[] { 1, 2, 3, 4, 5, 6 };
            File.WriteAllBytes(path, original);
            var images = Path.Combine(root, "images");
            Directory.CreateDirectory(images);
            var image = Path.Combine(images, "history.png");
            File.WriteAllBytes(image, original);

            Assert.Throws<InvalidDataException>(() => new ClipboardStorage(path, images, Path.Combine(root, "thumbnails")));

            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal(original, File.ReadAllBytes(image));
            Assert.Single(Directory.GetFiles(root, "clipboard.db.corrupt-*"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
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
            _ = fixture.Storage.Query(new ClipboardQuery(Limit: 10));
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

        public string CreateImage(string name, DateTime? lastWriteUtc = null) =>
            CreateFile(_imagesPath, name, [1, 2, 3], lastWriteUtc);

        public string CreateThumbnail(string name, DateTime? lastWriteUtc = null) =>
            CreateFile(_thumbnailsPath, name, [4, 5, 6], lastWriteUtc);

        public string CreatePng(string name, int width, int height)
        {
            var header = new byte[24];
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(header, 0);
            WriteBigEndian(header, 16, width);
            WriteBigEndian(header, 20, height);
            return CreateFile(_imagesPath, name, header, null);
        }

        private static void WriteBigEndian(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static string CreateFile(string directory, string name, byte[] content, DateTime? lastWriteUtc)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name);
            File.WriteAllBytes(path, content);
            if (lastWriteUtc != null)
                File.SetLastWriteTimeUtc(path, lastWriteUtc.Value);
            return path;
        }

        public void ReopenStorage()
        {
            _storage?.Dispose();
            _storage = new ClipboardStorage(_dbPath, _imagesPath, _thumbnailsPath);
        }

        public void Dispose()
        {
            _storage?.Dispose();
            _storage = null;
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
