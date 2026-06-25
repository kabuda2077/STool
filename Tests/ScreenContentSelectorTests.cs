using STool.Modules.Translation;
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

        Assert.Contains("Translate selected content to Chinese", prompt);
        Assert.Contains("4: [x=10, y=20, w=30, h=40] hello", prompt);
        Assert.Contains("\"i\"", prompt);
        Assert.Contains("\"t\"", prompt);
    }
}
