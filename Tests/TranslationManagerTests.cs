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
    public async Task TranslateAsync_DifferentProviders_RunConcurrentlyWithoutDisposing()
    {
        using var fixture = new TranslationFixture();
        var firstService = new BlockingTranslationService();
        var secondService = new BlockingTranslationService(completeImmediately: true);
        using var manager = new TranslationManager(
            fixture.Manager,
            (provider, _) => provider == TranslationProvider.Google ? firstService : secondService);

        var first = manager.TranslateAsync("first", "auto", "zh", TranslationProvider.Google);
        await firstService.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = await manager.TranslateAsync("second", "auto", "zh", TranslationProvider.OpenAI);

        Assert.True(second.Success);
        Assert.False(first.IsCompleted);
        Assert.False(firstService.Disposed);

        firstService.Complete();
        await first;
    }

    [Fact]
    public async Task TranslateAsync_SameProvider_ReusesServiceInstance()
    {
        using var fixture = new TranslationFixture();
        var created = 0;
        using var manager = new TranslationManager(
            fixture.Manager,
            (_, _) =>
            {
                created++;
                return new BlockingTranslationService(completeImmediately: true);
            });

        await manager.TranslateAsync("a", "auto", "zh", TranslationProvider.Google);
        await manager.TranslateAsync("b", "auto", "zh", TranslationProvider.OpenAI);
        await manager.TranslateAsync("c", "auto", "zh", TranslationProvider.Google);

        Assert.Equal(2, created);
    }

    [Fact]
    public async Task TranslateBlocksAsync_UsesServiceBatch()
    {
        using var fixture = new TranslationFixture();
        var service = new BlockingTranslationService(completeImmediately: true);
        using var manager = new TranslationManager(fixture.Manager, (_, _) => service);

        var result = await manager.TranslateBlocksAsync(new[] { "one", "two" }, "auto", "zh");

        Assert.True(result.Success);
        Assert.Equal(new[] { "one", "two" }, result.TranslatedBlocks);
    }

    [Fact]
    public async Task TranslateAsync_TencentUsesItsOwnAccount_AndSharedSourceChangesRebuildCache()
    {
        using var fixture = new TranslationFixture();
        fixture.Manager.Update(config =>
        {
            config.Accounts.TencentOcr.SecretIdEncrypted = "ocr-id";
            config.Accounts.TencentOcr.SecretKeyEncrypted = "ocr-key";
            config.Accounts.TencentTranslation.SecretIdEncrypted = "text-id";
            config.Accounts.TencentTranslation.SecretKeyEncrypted = "text-key";
        });
        var keys = new List<string?>();
        using var manager = new TranslationManager(fixture.Manager, (_, accounts) =>
        {
            keys.Add(accounts.ResolveTencentTranslation().SecretKeyEncrypted);
            return new BlockingTranslationService(completeImmediately: true);
        });

        await manager.TranslateAsync("one", "auto", "zh", TranslationProvider.Tencent);
        fixture.Manager.Update(config => config.Accounts.TencentOcr.SecretKeyEncrypted = "unrelated-key");
        await manager.TranslateAsync("two", "auto", "zh", TranslationProvider.Tencent);
        Assert.Equal(new[] { "text-key" }, keys);

        fixture.Manager.Update(config => Assert.True(config.Accounts.TrySetTencentReuse(forOcr: false, reuse: true)));
        await manager.TranslateAsync("three", "auto", "zh", TranslationProvider.Tencent);
        fixture.Manager.Update(config => config.Accounts.TencentOcr.SecretKeyEncrypted = "source-key");
        await manager.TranslateAsync("four", "auto", "zh", TranslationProvider.Tencent);
        Assert.Equal(new[] { "text-key", "unrelated-key", "source-key" }, keys);
    }

    [Fact]
    public async Task TranslateAsync_ReusedVisionCredentialsFollowSource_AndInvalidSourceCannotUseDraft()
    {
        using var fixture = new TranslationFixture();
        fixture.Manager.Update(config =>
        {
            config.Accounts.AiText.ApiUrlEncrypted = "https://api.openai.com/v1/chat/completions";
            config.Accounts.AiText.ApiKeyEncrypted = "text-draft-key";
            config.Accounts.AiText.Model = "text-model";
            config.Accounts.AiVision.ApiUrlEncrypted = "https://api.openai.com/v1/chat/completions";
            config.Accounts.AiVision.ApiKeyEncrypted = "vision-source-key";
            config.Accounts.AiVision.Model = "vision-model";
            Assert.True(config.Accounts.TrySetAiReuse(forOcr: false, reuse: true, value => value));
        });
        var accountsUsed = new List<AiAccountConfig>();
        using var manager = new TranslationManager(fixture.Manager, (_, accounts) =>
        {
            var account = accounts.ResolveText(value => value);
            accountsUsed.Add(account);
            return account.IsConfigured ? new BlockingTranslationService(completeImmediately: true) : null;
        });

        Assert.True((await manager.TranslateAsync("one", "auto", "zh", TranslationProvider.OpenAI)).Success);
        fixture.Manager.Update(config => config.Accounts.AiVision.ApiKeyEncrypted = "new-source-key");
        Assert.True((await manager.TranslateAsync("two", "auto", "zh", TranslationProvider.OpenAI)).Success);
        fixture.Manager.Update(config => config.Accounts.AiVision.ApiKeyEncrypted = null);
        Assert.False((await manager.TranslateAsync("three", "auto", "zh", TranslationProvider.OpenAI)).Success);
        Assert.Equal(new[] { "vision-source-key", "new-source-key", null }, accountsUsed.Select(account => account.ApiKeyEncrypted));
        Assert.All(accountsUsed, account => Assert.Equal("text-model", account.Model));
        Assert.Equal(AccountReuseSource.Ocr, fixture.Manager.Get().Accounts.AiText.ReuseFrom);
        Assert.Equal("text-draft-key", fixture.Manager.Get().Accounts.AiText.ApiKeyEncrypted);
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
                value => value,
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
