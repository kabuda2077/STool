using System.Collections.Generic;
using System.IO;
using STool.Core;
using STool.Models;
using STool.Modules.Translation;
using Xunit;

namespace STool.Tests;

public class TranslationManagerTests
{
    // ---------- ResolveTargetLanguage ----------

    [Theory]
    [InlineData("auto-en", "en")]
    [InlineData("auto-ja", "ja")]
    [InlineData("auto-ko", "ko")]
    [InlineData("en", "en")]
    [InlineData("ja", "ja")]
    [InlineData("ko", "ko")]
    [InlineData("zh", "zh")]
    [InlineData(null, "zh")]
    [InlineData("unknown-mode", "zh")]
    public void ResolveTargetLanguage_FixedModes_ReturnsExpected(string? mode, string expected)
    {
        Assert.Equal(expected, TranslationManager.ResolveTargetLanguage("任意文本", mode));
    }

    [Fact]
    public void ResolveTargetLanguage_ZhEn_ChineseDominant_TranslatesToEnglish()
    {
        Assert.Equal("en", TranslationManager.ResolveTargetLanguage("这是一段中文文本", "zh-en"));
    }

    [Fact]
    public void ResolveTargetLanguage_ZhEn_EnglishDominant_TranslatesToChinese()
    {
        Assert.Equal("zh", TranslationManager.ResolveTargetLanguage("This is an English sentence", "zh-en"));
    }

    [Fact]
    public void ResolveTargetLanguage_ZhEn_NoLetters_DefaultsToChinese()
    {
        Assert.Equal("zh", TranslationManager.ResolveTargetLanguage("12345 !!!", "zh-en"));
    }

    [Fact]
    public async Task TranslateAsync_SerializesProviderSwitchAndDisposesAfterCurrentRequest()
    {
        using var fixture = new TranslationFixture();
        var firstService = new BlockingTranslationService();
        var secondService = new BlockingTranslationService(completeImmediately: true);
        using var manager = new TranslationManager(
            fixture.Manager,
            (provider, _) => provider == TranslationProvider.Google ? firstService : secondService);

        var first = manager.TranslateAsync("first", "auto", "zh", TranslationProvider.Google);
        await firstService.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = manager.TranslateAsync("second", "auto", "zh", TranslationProvider.OpenAI);

        await Task.Delay(30);
        Assert.False(secondService.Started.Task.IsCompleted);
        Assert.False(firstService.Disposed);

        firstService.Complete();
        await first;
        await second;

        Assert.True(firstService.Disposed);
        Assert.True(secondService.Started.Task.IsCompleted);
    }

    [Fact]
    public async Task TranslateAsync_CanceledWhileWaiting_DoesNotInvokeService()
    {
        using var fixture = new TranslationFixture();
        var activeService = new BlockingTranslationService();
        var waitingService = new BlockingTranslationService(completeImmediately: true);
        using var manager = new TranslationManager(
            fixture.Manager,
            (provider, _) => provider == TranslationProvider.Google ? activeService : waitingService);

        var active = manager.TranslateAsync("active", "auto", "zh", TranslationProvider.Google);
        await activeService.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var waiting = manager.TranslateAsync(
            "waiting", "auto", "zh", TranslationProvider.OpenAI, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(waitingService.Started.Task.IsCompleted);

        activeService.Complete();
        await active;
    }

    // ---------- PackBlocks / TryUnpackBlocks round-trip ----------

    [Fact]
    public void PackThenUnpack_OpenAi_RoundTripsAllBlocks()
    {
        var blocks = new List<string> { "Hello", "World", "Foo" };

        var packed = TranslationManager.PackBlocks(blocks, TranslationProvider.OpenAI);
        var unpacked = TranslationManager.TryUnpackBlocks(packed, blocks.Count);

        Assert.NotNull(unpacked);
        Assert.Equal(blocks, unpacked);
    }

    [Fact]
    public void PackBlocks_OpenAi_IncludesInstructionLine()
    {
        var packed = TranslationManager.PackBlocks(new[] { "x" }, TranslationProvider.OpenAI);
        Assert.Contains("[[STOOL-001]]", packed);
        Assert.Contains("Translate each marked item", packed);
    }

    [Fact]
    public void PackBlocks_Tencent_OmitsInstructionLine()
    {
        var packed = TranslationManager.PackBlocks(new[] { "x" }, TranslationProvider.Tencent);
        Assert.Contains("[[STOOL-001]]", packed);
        Assert.DoesNotContain("Translate each marked item", packed);
    }

    [Fact]
    public void TryUnpackBlocks_MarkedOutput_ParsesInOrder()
    {
        var text = "<<<STOOL_001>>> 你好\n<<<STOOL_002>>> 世界";
        var unpacked = TranslationManager.TryUnpackBlocks(text, 2);

        Assert.NotNull(unpacked);
        Assert.Equal(new[] { "你好", "世界" }, unpacked);
    }

    [Fact]
    public void TryUnpackBlocks_AlternativeMarkerSyntax_IsAccepted()
    {
        var text = "[[STOOL-001]] A\n[[STOOL-002]] B";
        var unpacked = TranslationManager.TryUnpackBlocks(text, 2);

        Assert.NotNull(unpacked);
        Assert.Equal(new[] { "A", "B" }, unpacked);
    }

    [Fact]
    public void TryUnpackBlocks_NoMarkers_FallsBackToLineSplit()
    {
        var text = "第一行\n第二行";
        var unpacked = TranslationManager.TryUnpackBlocks(text, 2);

        Assert.NotNull(unpacked);
        Assert.Equal(new[] { "第一行", "第二行" }, unpacked);
    }

    [Fact]
    public void TryUnpackBlocks_CountMismatch_ReturnsNull()
    {
        var text = "<<<STOOL_001>>> only one";
        Assert.Null(TranslationManager.TryUnpackBlocks(text, 3));
    }

    [Fact]
    public void TryUnpackBlocks_DuplicateMarker_ReturnsNull()
    {
        var text = "<<<STOOL_001>>> A\n<<<STOOL_001>>> B";
        Assert.Null(TranslationManager.TryUnpackBlocks(text, 2));
    }

    [Fact]
    public void TryUnpackBlocks_EmptyBlockValue_ReturnsNull()
    {
        var text = "<<<STOOL_001>>> A\n<<<STOOL_002>>>   ";
        Assert.Null(TranslationManager.TryUnpackBlocks(text, 2));
    }

    [Fact]
    public void TryUnpackBlocks_HtmlEncodedLegacyMarkers_AreDecoded()
    {
        var text = "&lt;&lt;&lt;STOOL_001&gt;&gt;&gt; A\n&amp;lt;&amp;lt;&amp;lt;STOOL_002&amp;gt;&amp;gt;&amp;gt; B";

        var unpacked = TranslationManager.TryUnpackBlocks(text, 2);

        Assert.NotNull(unpacked);
        Assert.Equal(new[] { "A", "B" }, unpacked);
    }

    [Fact]
    public void TryUnpackBlocks_LooseLines_StripsMarkers()
    {
        var text = "[[STOOL-001]] A\n[[STOOL-002]] B";

        var unpacked = TranslationManager.TryUnpackBlocks(text, 2);

        Assert.Equal(new[] { "A", "B" }, unpacked);
    }

    private sealed class BlockingTranslationService(bool completeImmediately = false) : ITranslationService
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public bool IsAvailable() => true;

        public async Task<TranslationResult> TranslateAsync(
            string text,
            string sourceLanguage,
            string targetLanguage,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            if (!completeImmediately)
                await _completion.Task.WaitAsync(cancellationToken);
            return new TranslationResult { Success = true, TranslatedText = text };
        }

        public void Complete() => _completion.TrySetResult();
        public void Dispose() => Disposed = true;
    }

    private sealed class TranslationFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "STool.Tests", Guid.NewGuid().ToString("N"));

        public TranslationFixture()
        {
            Directory.CreateDirectory(_root);
            Manager = new ConfigManager(
                Path.Combine(_root, "config.json"),
                _ => string.Empty,
                value => "v2:" + value,
                value => value.StartsWith("v2:", StringComparison.Ordinal));
        }

        public ConfigManager Manager { get; }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }
    }
}
