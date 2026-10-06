using STool.Modules.Translation;
using Xunit;

namespace STool.Tests;

public class ScreenContentSelectorTests
{
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
    public void TryParseTranslations_NoArray_ReturnsNull()
    {
        Assert.Null(ScreenContentSelector.TryParseTranslations("no json here", new HashSet<int> { 0 }));
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
}
