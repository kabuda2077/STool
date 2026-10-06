using System.Drawing;
using STool.Modules.Ocr;
using STool.Modules.Screenshot;
using Xunit;

namespace STool.Tests;

public class ScreenshotTextLayoutTests
{
    [Theory]
    [InlineData("This actress cut her hair and wore a full wig.", "zh", true)]
    [InlineData("Primary-Ad-7788", "zh", false)]
    [InlineData("207 Reply Award Share", "zh", false)]
    [InlineData("回复 奖励 分享", "en", false)]
    [InlineData("这位女演员本季剪了头发。", "zh", false)]
    [InlineData("这位女演员本季剪了头发。", "en", true)]
    [InlineData("https://example.com/page", "zh", false)]
    [InlineData("12:45", "zh", false)]
    [InlineData("3 hours ago", "zh", false)]
    public void FastFilter_KeepsSourceContentAndDropsUiMetadata(string text, string targetLanguage, bool expected)
    {
        var line = new TranslationLine(text, new Rectangle(100, 100, 500, 30));

        var result = ScreenshotTextLayout.IsLikelyTranslatableContent(line, 1200, 800, targetLanguage);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void BuildLines_MergesWordsOnSameRowAndSplitsDistantColumns()
    {
        var ocr = new OcrResult
        {
            Success = true,
            TextBlocks =
            {
                Block("Hello", 10, 10, 50, 20),
                Block("world", 65, 11, 50, 20),
                Block("Sidebar", 600, 10, 80, 20),
                Block("Next", 10, 50, 40, 20),
                Block("line", 55, 50, 40, 20)
            }
        };

        var lines = ScreenshotTextLayout.BuildLines(ocr, 800, 200);

        Assert.Equal(new[] { "Hello world", "Sidebar", "Next line" }, lines.Select(line => line.Text));
        Assert.Equal(new Rectangle(10, 10, 105, 21), lines[0].Box);
    }

    [Fact]
    public void BuildLines_JoinsCjkWithoutSpaces()
    {
        var ocr = new OcrResult
        {
            Success = true,
            TextBlocks =
            {
                Block("你好", 10, 10, 40, 20),
                Block("世界", 55, 10, 40, 20),
                Block("另一行", 10, 60, 60, 20)
            }
        };

        var lines = ScreenshotTextLayout.BuildLines(ocr, 400, 200);

        Assert.Equal("你好世界", lines[0].Text);
    }

    [Fact]
    public void GroupParagraphs_MergesCloseAlignedLinesOnly()
    {
        var lines = new[]
        {
            new TranslationLine("First sentence of the", new Rectangle(20, 10, 300, 20)),
            new TranslationLine("paragraph continues here.", new Rectangle(22, 34, 280, 20)),
            new TranslationLine("A separate block far below.", new Rectangle(20, 200, 300, 20)),
            new TranslationLine("Indented far away", new Rectangle(400, 224, 200, 20))
        };

        var paragraphs = ScreenshotTextLayout.GroupParagraphs(lines);

        Assert.Equal(3, paragraphs.Count);
        Assert.Equal("First sentence of the paragraph continues here.", paragraphs[0].Text);
        Assert.Equal(new Rectangle(20, 10, 300, 44), paragraphs[0].Box);
        Assert.Equal(20, paragraphs[0].MedianLineHeight);
    }

    [Theory]
    [InlineData(13.7, 13.5)]
    [InlineData(30, 26)]
    [InlineData(5, 9)]
    public void FitFontSize_FindsLargestHalfPointSizeThatFits(double largestFitting, double expected)
    {
        var size = ScreenshotTextLayout.FitFontSize(26, 9, candidate => candidate <= largestFitting);

        Assert.Equal(expected, size);
    }

    [Fact]
    public void AnalyzeBackground_DetectsUniformLightBackground()
    {
        var samples = Enumerable.Repeat(0xF0F0F0, 90).Concat(Enumerable.Repeat(0x202020, 10)).ToList();

        var (color, uniform) = ScreenshotTextLayout.AnalyzeBackground(samples);

        Assert.True(uniform);
        Assert.Equal(0xF0, color.R);
    }

    [Fact]
    public void AnalyzeBackground_DetectsGradientAsNonUniform()
    {
        var samples = Enumerable.Range(0, 200).Select(index => (index << 16) | (index << 8) | index).ToList();

        var (_, uniform) = ScreenshotTextLayout.AnalyzeBackground(samples);

        Assert.False(uniform);
    }

    [Fact]
    public void SampleBackground_ReadsRingAroundTextBox()
    {
        using var bitmap = new Bitmap(200, 100);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(250, 250, 250));
            graphics.FillRectangle(Brushes.Black, 60, 40, 80, 20);
        }

        var (color, uniform) = ScreenshotTextLayout.SampleBackground(bitmap, new Rectangle(60, 40, 80, 20), 20);

        Assert.True(uniform);
        Assert.Equal(250, color.R);
    }

    private static OcrTextBlock Block(string text, int x, int y, int width, int height) => new()
    {
        Text = text,
        Confidence = 1,
        BoundingBox = new Rectangle(x, y, width, height)
    };
}
