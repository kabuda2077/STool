using System.IO;
using System.Runtime.InteropServices;
using STool.Core;
using STool.Models;
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
    public async Task ClipboardWriter_RetriesBusyOperation()
    {
        var attempts = 0;

        await ClipboardWriter.RunAsync(() =>
        {
            attempts++;
            if (attempts < 3)
                throw new COMException("busy", unchecked((int)0x800401D0));
        });

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task ClipboardWriter_DoesNotRetryOtherErrors()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ClipboardWriter.RunAsync(() =>
            {
                attempts++;
                throw new InvalidOperationException("failed");
            }));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ClipboardWriter_CanBeCanceledDuringWait()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ClipboardWriter.RunAsync(() =>
            {
                attempts++;
                cancellation.Cancel();
                throw new COMException("busy", unchecked((int)0x800401D0));
            }, cancellation.Token));

        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(true, false, null, true)]
    [InlineData(false, true, null, true)]
    [InlineData(false, false, 0, true)]
    [InlineData(false, false, 1, false)]
    [InlineData(false, false, null, false)]
    public void Privacy_SkipsContentMarkedByPasswordManagers(bool exclude, bool viewerIgnore, int? canInclude, bool expected)
    {
        Assert.Equal(expected, ClipboardPrivacy.ShouldSkip(exclude, viewerIgnore, canInclude));
    }

    [Theory]
    [InlineData("KeePass.exe", true)]
    [InlineData("keepass.exe", true)]
    [InlineData("KeePass", true)]
    [InlineData("Code.exe", false)]
    [InlineData(null, false)]
    public void Privacy_MatchesExcludedAppsWithOrWithoutExtension(string? sourceApp, bool expected)
    {
        Assert.Equal(expected, ClipboardPrivacy.IsExcludedApp(sourceApp, new[] { "KeePass" }));
    }

    [Fact]
    public void Privacy_ParseAppList_NormalizesAndDeduplicates()
    {
        var apps = ClipboardPrivacy.ParseAppList("KeePass.exe, keepass；1Password\nBitwarden.EXE, ");

        Assert.Equal(new[] { "KeePass.exe", "1Password.exe", "Bitwarden.exe" }, apps);
    }

    [Fact]
    public void ContentHash_DistinguishesImagesBySizeAndPixels()
    {
        var pixels = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        var first = ClipboardContentHash.ForImage(2, 1, pixels);
        Assert.Equal(first, ClipboardContentHash.ForImage(2, 1, pixels));
        Assert.NotEqual(first, ClipboardContentHash.ForImage(1, 2, pixels));
        Assert.NotEqual(first, ClipboardContentHash.ForImage(2, 1, new byte[] { 1, 2, 3, 4, 5, 6, 7, 9 }));
    }

    [Fact]
    public void ContentHash_RowBlocksMatchWholeImageHash()
    {
        const int width = 160;
        const int height = 300;
        var pixels = new byte[width * height * 4];
        new Random(7).NextBytes(pixels);
        var source = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
        source.Freeze();

        Assert.Equal(ClipboardContentHash.ForImage(width, height, pixels), ClipboardContentHash.ForImage(source));
    }

    [Fact]
    public void ProcessCapture_RepeatedTextIsMovedToTopInsteadOfDuplicated()
    {
        using var fixture = new ManagerFixture();
        var first = fixture.Manager.ProcessCapture(Text("same", new DateTime(2026, 1, 1, 8, 0, 0)), fixture.Settings);
        fixture.Manager.ProcessCapture(Text("other", new DateTime(2026, 1, 1, 9, 0, 0)), fixture.Settings);

        var promoted = fixture.Manager.ProcessCapture(Text("same", new DateTime(2026, 1, 1, 10, 0, 0)), fixture.Settings);

        Assert.NotNull(first);
        Assert.NotNull(promoted);
        Assert.Equal(first.Id, promoted.Id);
        var items = fixture.Manager.Query(new ClipboardQuery());
        Assert.Equal(new[] { "same", "other" }, items.Select(item => item.TextPreview));
    }

    [Fact]
    public void ProcessCapture_SkipsQueuedContentAfterRecordingIsDisabledOrAppExcluded()
    {
        using var fixture = new ManagerFixture();
        fixture.Settings.Enabled = false;
        Assert.Null(fixture.Manager.ProcessCapture(Text("private", DateTime.Now), fixture.Settings));

        fixture.Settings.Enabled = true;
        fixture.Settings.ExcludedApps.Add("Test.exe");
        Assert.Null(fixture.Manager.ProcessCapture(Text("private", DateTime.Now), fixture.Settings));
        Assert.Empty(fixture.Manager.Query(new ClipboardQuery()));
    }

    [Fact]
    public void ProcessCapture_SkipsTextLongerThanLimit()
    {
        using var fixture = new ManagerFixture();
        fixture.Settings.MaxTextLength = 5;

        Assert.Null(fixture.Manager.ProcessCapture(Text("123456", DateTime.Now), fixture.Settings));
        Assert.NotNull(fixture.Manager.ProcessCapture(Text("12345", DateTime.Now), fixture.Settings));
    }

    [Fact]
    public void ProcessCapture_RepeatedFileListIsDeduplicated()
    {
        using var fixture = new ManagerFixture();
        var files = new[] { @"C:\a.txt", @"C:\b.txt" };

        var first = fixture.Manager.ProcessCapture(Files(files), fixture.Settings);
        var second = fixture.Manager.ProcessCapture(Files(files), fixture.Settings);

        Assert.Equal(first!.Id, second!.Id);
        Assert.Single(fixture.Manager.Query(new ClipboardQuery(ClipboardItemType.File)));
    }

    [Fact]
    public void ProcessCapture_StopsOversizedPngWithoutWritingAFileOrRecord()
    {
        using var fixture = new ManagerFixture();
        fixture.Settings.MaxImageSizeKB = 1;

        var item = fixture.Manager.ProcessCapture(NoiseImage(), fixture.Settings);

        Assert.Null(item);
        Assert.Empty(fixture.Manager.Query(new ClipboardQuery()));
        Assert.Empty(Directory.GetFiles(fixture.ImagesDirectory));
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(0)]
    [InlineData(-1)]
    public void ProcessCapture_SavesPngWithinLimitOrWithLimitDisabled(int limitKB)
    {
        using var fixture = new ManagerFixture();
        fixture.Settings.MaxImageSizeKB = limitKB;

        var item = fixture.Manager.ProcessCapture(NoiseImage(), fixture.Settings);

        Assert.NotNull(item);
        var path = Assert.Single(Directory.GetFiles(fixture.ImagesDirectory));
        Assert.Equal(new FileInfo(path).Length, item.ImageBytes);
        Assert.True(item.ImageBytes > 1024);
        Assert.Single(fixture.Manager.Query(new ClipboardQuery(ClipboardItemType.Image)));
    }

    [Fact]
    public void PngEncoding_DoesNotSwallowOtherWriteFailures()
    {
        using var destination = new FailingWriteStream();
        Assert.ThrowsAny<Exception>(() => ClipboardManager.TryEncodePng(NoiseImage().Image!, destination, long.MaxValue));
    }

    private static ClipboardCapture NoiseImage()
    {
        const int width = 192;
        const int height = 192;
        var pixels = new byte[width * height * 4];
        new Random(421).NextBytes(pixels);
        for (var index = 3; index < pixels.Length; index += 4)
            pixels[index] = 255;
        var image = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return new ClipboardCapture(ClipboardItemType.Image, null, image, null, "Test.exe", DateTime.Now);
    }

    private sealed class FailingWriteStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("simulated I/O failure");
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("simulated I/O failure");
        public override void WriteByte(byte value) => throw new IOException("simulated I/O failure");
    }

    private static ClipboardCapture Text(string text, DateTime copiedAt) =>
        new(ClipboardItemType.Text, text, null, null, "Test.exe", copiedAt);

    private static ClipboardCapture Files(string[] files) =>
        new(ClipboardItemType.File, null, null, files, "Explorer.exe", DateTime.Now);

    private sealed class ManagerFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "STool.Tests", Guid.NewGuid().ToString("N"));

        public ManagerFixture()
        {
            Directory.CreateDirectory(_root);
            var config = new ConfigManager(
                Path.Combine(_root, "config.json"),
                _ => string.Empty,
                value => "v2:" + value,
                value => value.StartsWith("v2:", StringComparison.Ordinal));
            var storage = new ClipboardStorage(
                Path.Combine(_root, "clipboard.db"),
                Path.Combine(_root, "images"),
                Path.Combine(_root, "thumbnails"));
            Manager = new ClipboardManager(config, storage, Path.Combine(_root, "images"));
        }

        public ClipboardManager Manager { get; }
        public string ImagesDirectory => Path.Combine(_root, "images");
        public ClipboardConfig Settings { get; } = new();

        public void Dispose()
        {
            Manager.Dispose();
            try { Directory.Delete(_root, true); } catch { }
        }
    }
}
