using System.Collections.Generic;
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
        Assert.Contains("<<<STOOL_001>>>", packed);
        Assert.Contains("Translate each marked item", packed);
    }

    [Fact]
    public void PackBlocks_Tencent_OmitsInstructionLine()
    {
        var packed = TranslationManager.PackBlocks(new[] { "x" }, TranslationProvider.Tencent);
        Assert.Contains("<<<STOOL_001>>>", packed);
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
}
