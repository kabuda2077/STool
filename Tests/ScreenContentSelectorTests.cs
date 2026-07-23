using STool.Modules.Translation;
using STool.Modules.Screenshot;
using Xunit;

namespace STool.Tests;

public class ScreenContentSelectorTests
{
    [Fact]
    public void TryParseIndices_PlainArray_ParsesAll()
    {
        var result = ScreenContentSelector.TryParseIndices("[0, 2, 3]", 5);
        Assert.NotNull(result);
        Assert.Equal(new[] { 0, 2, 3 }, result);
    }

    [Fact]
    public void TryParseIndices_CodeFenceWrapped_StillParses()
    {
        var result = ScreenContentSelector.TryParseIndices("```json\n[1,2]\n```", 5);
        Assert.NotNull(result);
        Assert.Equal(new[] { 1, 2 }, result);
    }

    [Fact]
    public void TryParseIndices_WithSurroundingProse_ExtractsArray()
    {
        var result = ScreenContentSelector.TryParseIndices("Sure, here are the indices: [2, 4]. Done.", 5);
        Assert.NotNull(result);
        Assert.Equal(new[] { 2, 4 }, result);
    }

    [Fact]
    public void TryParseIndices_OutOfRangeAndDuplicates_AreDropped()
    {
        var result = ScreenContentSelector.TryParseIndices("[0, 0, 9, 2, -1]", 3);
        Assert.NotNull(result);
        Assert.Equal(new[] { 0, 2 }, result);
    }

    [Fact]
    public void TryParseIndices_Unsorted_ReturnsAscending()
    {
        var result = ScreenContentSelector.TryParseIndices("[3,1,2]", 5);
        Assert.NotNull(result);
        Assert.Equal(new[] { 1, 2, 3 }, result);
    }

    [Fact]
    public void TryParseIndices_EmptyArray_ReturnsEmpty()
    {
        var result = ScreenContentSelector.TryParseIndices("[]", 5);
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void TryParseIndices_NoArray_ReturnsNull()
    {
        Assert.Null(ScreenContentSelector.TryParseIndices("no indices here", 5));
    }

    [Fact]
    public void TryParseIndices_ZeroLineCount_ReturnsNull()
    {
        Assert.Null(ScreenContentSelector.TryParseIndices("[0,1]", 0));
    }

    [Fact]
    public void BuildPrompt_NumbersLinesAndIncludesText()
    {
        var prompt = ScreenContentSelector.BuildPrompt(new[] { "hello", "world" });
        Assert.Contains("0: hello", prompt);
        Assert.Contains("1: world", prompt);
        Assert.Contains("JSON array", prompt);
    }

    [Fact]
    public void TryParseTranslations_PlainArray_ParsesItems()
    {
        var result = ScreenContentSelector.TryParseTranslations(
            "[{\"i\":1,\"t\":\"你好\"},{\"i\":3,\"t\":\"世界\"}]",
            new HashSet<int> { 1, 2, 3 });

        Assert.NotNull(result);
        Assert.Equal(new[] { 1, 3 }, result.Select(item => item.Index));
        Assert.Equal(new[] { "你好", "世界" }, result.Select(item => item.Translation));
    }

    [Fact]
    public void TryParseTranslations_CodeFenceWrapped_StillParses()
    {
        var result = ScreenContentSelector.TryParseTranslations(
            "```json\n[{\"i\":2,\"t\":\"translated\"}]\n```",
            new HashSet<int> { 2 });

        Assert.NotNull(result);
        var item = Assert.Single(result);
        Assert.Equal(2, item.Index);
        Assert.Equal("translated", item.Translation);
    }

    [Fact]
    public void TryParseTranslations_InvalidItems_AreDropped()
    {
        var result = ScreenContentSelector.TryParseTranslations(
            "[{\"i\":0,\"t\":\"ok\"},{\"i\":0,\"t\":\"duplicate\"},{\"i\":9,\"t\":\"bad\"},{\"i\":1,\"t\":\"\"}]",
            new HashSet<int> { 0, 1 });

        Assert.NotNull(result);
        var item = Assert.Single(result);
        Assert.Equal(0, item.Index);
        Assert.Equal("ok", item.Translation);
    }

    [Fact]
    public void BuildTranslatePrompt_IncludesCoordinatesAndTarget()
    {
        var prompt = ScreenContentSelector.BuildTranslatePrompt(
            new[] { new ScreenContentLine(4, "hello", 10, 20, 30, 40) },
            "zh");

        Assert.Contains("to Chinese", prompt);
        Assert.Contains("[4,10,20,30,40,\"hello\"]", prompt);
        Assert.Contains("\"i\"", prompt);
        Assert.Contains("\"t\"", prompt);
    }

    [Theory]
    [InlineData(1, 10, 384)]
    [InlineData(10, 100, 560)]
    [InlineData(50, 2000, 2200)]
    public void CalculateTranslateMaxTokens_UsesBoundedDynamicBudget(int lineCount, int textLength, int expected)
    {
        var textPerLine = new string('x', textLength / lineCount);
        var lines = Enumerable.Range(0, lineCount)
            .Select(index => new ScreenContentLine(index, textPerLine, 0, 0, 10, 10))
            .ToArray();

        Assert.Equal(expected, ScreenContentSelector.CalculateTranslateMaxTokens(lines));
    }

    [Theory]
    [InlineData("This actress cut her hair and wore a full wig.", "zh", true)]
    [InlineData("Primary-Ad-7788", "zh", false)]
    [InlineData("207 Reply Award Share", "zh", false)]
    [InlineData("回复 奖励 分享", "en", false)]
    [InlineData("这位女演员本季剪了头发。", "zh", false)]
    [InlineData("这位女演员本季剪了头发。", "en", true)]
    public void FastFilter_KeepsSourceContentAndDropsUiMetadata(string text, string targetLanguage, bool expected)
    {
        var line = new CaptureOverlay.TranslationLine(
            text,
            new System.Drawing.Rectangle(100, 100, 500, 30));

        var result = CaptureOverlay.IsLikelyTranslatableContent(line, 1200, 800, targetLanguage);

        Assert.Equal(expected, result);
    }
}
