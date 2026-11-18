using System.Drawing;
using System.IO;
using STool.Core;
using STool.Models;
using STool.Modules.Ocr;
using Xunit;

namespace STool.Tests;

public class OcrManagerTests
{
    [Theory]
    [InlineData(OcrProvider.AI, false, OcrProvider.WindowsLocal)]
    [InlineData(OcrProvider.WindowsLocal, false, OcrProvider.WindowsLocal)]
    [InlineData(OcrProvider.Tencent, false, OcrProvider.Tencent)]
    [InlineData(OcrProvider.Tencent, true, OcrProvider.WindowsLocal)]
    public async Task RecognizeForLayoutAsync_FollowsOcrWithoutCallingAi(
        OcrProvider provider, bool migrationPending, OcrProvider expected)
    {
        using var fixture = new OcrFixture();
        fixture.Manager.Update(config => config.Ocr.Provider = provider);
        fixture.Manager.Update(config =>
        {
            config.Translation.ScreenshotOcrMigrationPending = migrationPending;
            config.Ocr.FallbackToLocal = false;
        });
        var calls = new List<OcrProvider>();
        using var manager = new OcrManager(fixture.Manager,
            (selected, _) => new RecordingOcrService(selected, calls, success: true));
        using var image = new Bitmap(1, 1);

        var result = await manager.RecognizeForLayoutAsync(image);

        Assert.True(result.Success);
        Assert.Equal(new[] { expected }, calls);
    }

    [Fact]
    public async Task RecognizeForLayoutAsync_TencentFailureFallsBack_RegardlessOfStandaloneOption()
    {
        using var fixture = new OcrFixture();
        fixture.Manager.Update(config =>
        {
            config.Ocr.Provider = OcrProvider.Tencent;
            config.Ocr.FallbackToLocal = false;
        });
        var calls = new List<OcrProvider>();
        using var manager = new OcrManager(fixture.Manager, (provider, _) =>
            new RecordingOcrService(provider, calls, success: provider == OcrProvider.WindowsLocal));
        using var image = new Bitmap(1, 1);

        var standalone = await manager.RecognizeAsync(image);
        Assert.False(standalone.Success);
        Assert.Equal(new[] { OcrProvider.Tencent }, calls);
        calls.Clear();
        var screenshot = await manager.RecognizeForLayoutAsync(image);

        Assert.True(screenshot.Success);
        Assert.Equal(new[] { OcrProvider.Tencent, OcrProvider.WindowsLocal }, calls);
    }

    [Fact]
    public async Task RecognizeAsync_OcrAccountChangesRebuildCache_TranslationDraftChangesDoNot()
    {
        using var fixture = new OcrFixture();
        fixture.Manager.Update(config =>
        {
            config.Ocr.Provider = OcrProvider.Tencent;
            config.Accounts.TencentOcr.SecretIdEncrypted = "ocr-id";
            config.Accounts.TencentOcr.SecretKeyEncrypted = "ocr-key";
        });
        var created = 0;
        using var manager = new OcrManager(fixture.Manager, (provider, _) =>
        {
            created++;
            return new RecordingOcrService(provider, new List<OcrProvider>(), success: true);
        });
        using var image = new Bitmap(1, 1);

        await manager.RecognizeAsync(image);
        fixture.Manager.Update(config => config.Accounts.TencentTranslation.SecretKeyEncrypted = "unrelated-key");
        await manager.RecognizeAsync(image);
        Assert.Equal(1, created);
        fixture.Manager.Update(config => config.Accounts.TencentOcr.SecretKeyEncrypted = "changed-key");
        await manager.RecognizeAsync(image);
        Assert.Equal(2, created);
    }

    private sealed class RecordingOcrService(OcrProvider provider, List<OcrProvider> calls, bool success) : IOcrService
    {
        public bool IsAvailable() => true;
        public Task<OcrResult> RecognizeAsync(Bitmap image, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls.Add(provider);
            return Task.FromResult(new OcrResult { Success = success, Provider = provider.ToString(), ErrorMessage = "test failure" });
        }
        public void Dispose() { }
    }

    private sealed class OcrFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "STool.Tests", Guid.NewGuid().ToString("N"));
        public OcrFixture()
        {
            Manager = new ConfigManager(Path.Combine(_root, "config.json"), value => value,
                value => value, _ => true);
        }
        public ConfigManager Manager { get; }
        public void Dispose() => Directory.Delete(_root, true);
    }
}
