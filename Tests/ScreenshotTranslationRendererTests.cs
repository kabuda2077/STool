using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using STool.Modules.Ocr;
using STool.Modules.Screenshot;
using Xunit;

namespace STool.Tests;

public class ScreenshotTranslationRendererTests
{
    [Fact]
    public void Prepare_UniformBackground_ErasesTextCompletely()
    {
        using var crop = new Bitmap(220, 60);
        using (var g = Graphics.FromImage(crop))
        {
            g.Clear(Color.FromArgb(250, 250, 245));
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var font = new Font("Segoe UI", 16, FontStyle.Regular, GraphicsUnit.Pixel);
            g.DrawString("Hello world", font, Brushes.Black, 12, 18);
        }

        var line = new TranslationLine("Hello world", new Rectangle(10, 16, 110, 24));
        var paragraph = new TranslationParagraph(line.Text, line.Box, line.Box.Height) { Lines = new[] { line } };

        using var plan = ScreenshotTranslationRenderer.Prepare(crop, new[] { paragraph }, new[] { line });

        for (var y = line.Box.Top; y < line.Box.Bottom; y++)
        {
            for (var x = line.Box.Left; x < line.Box.Right; x++)
            {
                var pixel = plan.Background.GetPixel(x, y);
                Assert.True(
                    ScreenshotTranslationRenderer.Distance(pixel.ToArgb() & 0xFFFFFF, 0xFAFAF5) <= 12,
                    $"pixel ({x},{y}) = {pixel} not erased");
            }
        }

        var style = Assert.Single(plan.Blocks);
        Assert.True(style.TextColor.R < 80 && style.TextColor.G < 80 && style.TextColor.B < 80, $"text color {style.TextColor}");
    }

    [Fact]
    public void Prepare_GradientBackground_RestoresGradient()
    {
        using var crop = new Bitmap(200, 60);
        using (var g = Graphics.FromImage(crop))
        {
            using var brush = new LinearGradientBrush(new Rectangle(0, 0, 200, 60), Color.FromArgb(40, 90, 200), Color.FromArgb(200, 120, 60), 0f);
            g.FillRectangle(brush, 0, 0, 200, 60);
            using var font = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
            g.DrawString("Gradient", font, Brushes.White, 40, 18);
        }

        using var reference = new Bitmap(200, 60);
        using (var g = Graphics.FromImage(reference))
        {
            using var brush = new LinearGradientBrush(new Rectangle(0, 0, 200, 60), Color.FromArgb(40, 90, 200), Color.FromArgb(200, 120, 60), 0f);
            g.FillRectangle(brush, 0, 0, 200, 60);
        }

        var line = new TranslationLine("Gradient", new Rectangle(38, 16, 90, 24));
        var paragraph = new TranslationParagraph(line.Text, line.Box, line.Box.Height) { Lines = new[] { line } };

        using var plan = ScreenshotTranslationRenderer.Prepare(crop, new[] { paragraph }, new[] { line });

        var maxError = 0;
        for (var y = line.Box.Top; y < line.Box.Bottom; y++)
        {
            for (var x = line.Box.Left; x < line.Box.Right; x++)
            {
                var actual = plan.Background.GetPixel(x, y).ToArgb() & 0xFFFFFF;
                var expected = reference.GetPixel(x, y).ToArgb() & 0xFFFFFF;
                maxError = Math.Max(maxError, ScreenshotTranslationRenderer.Distance(actual, expected));
            }
        }

        Assert.True(maxError <= 30, $"max gradient error {maxError}");
        var style = Assert.Single(plan.Blocks);
        Assert.True(style.TextColor.R > 200 && style.TextColor.G > 200 && style.TextColor.B > 200, $"text color {style.TextColor}");
    }

    [Fact]
    public void DetectAlignment_CenteredLines_ReturnsCenter()
    {
        var lines = new[]
        {
            new TranslationLine("a", new Rectangle(100, 0, 200, 20)),
            new TranslationLine("b", new Rectangle(150, 24, 100, 20)),
            new TranslationLine("c", new Rectangle(125, 48, 150, 20))
        };

        Assert.Equal(TranslationTextAlignment.Center, ScreenshotTranslationRenderer.DetectAlignment(lines, 400));
    }

    [Fact]
    public void DetectAlignment_LeftAlignedParagraph_ReturnsLeft()
    {
        var lines = new[]
        {
            new TranslationLine("a", new Rectangle(10, 0, 300, 20)),
            new TranslationLine("b", new Rectangle(11, 24, 120, 20))
        };

        Assert.Equal(TranslationTextAlignment.Left, ScreenshotTranslationRenderer.DetectAlignment(lines, 400));
    }

    [Fact]
    public void TierFontSizes_GroupsSimilarSizes()
    {
        var tiers = ScreenshotTranslationRenderer.TierFontSizes(new[] { 14.0, 14.6, 13.8, 24.0 });

        Assert.Equal(tiers[0], tiers[1]);
        Assert.Equal(tiers[1], tiers[2]);
        Assert.Equal(24.0, tiers[3]);
    }

    [Fact]
    public void ComputeMaxBox_StopsBeforeNeighbourText()
    {
        using var crop = new Bitmap(400, 200);
        using (var g = Graphics.FromImage(crop))
            g.Clear(Color.White);

        var own = new TranslationLine("own", new Rectangle(10, 10, 100, 20));
        var right = new TranslationLine("right", new Rectangle(130, 10, 60, 20));
        var below = new TranslationLine("below", new Rectangle(10, 45, 100, 20));
        var paragraph = new TranslationParagraph("own", own.Box, 20) { Lines = new[] { own } };

        var max = ScreenshotTranslationRenderer.ComputeMaxBox(crop, paragraph, new[] { own, right, below });

        Assert.True(max.Right < right.Box.Left);
        Assert.True(max.Bottom < below.Box.Top);
        Assert.True(max.Right > own.Box.Right);
    }
}

public class WindowsOcrServiceTests
{
    [Fact]
    public void JoinWords_RemovesSpacesBetweenCjkCharacters()
    {
        Assert.Equal("你好世界", WindowsOcrService.JoinWords(new[] { "你", "好", "世", "界" }));
        Assert.Equal("Hello world", WindowsOcrService.JoinWords(new[] { "Hello", "world" }));
        Assert.Equal("使用OCR识别，结果", WindowsOcrService.JoinWords(new[] { "使", "用", "OCR", "识", "别", "，", "结", "果" }));
    }

    [Theory]
    [InlineData(100, 50, 10000, 2.0)]
    [InlineData(1000, 400, 10000, 1.0)]
    [InlineData(20000, 500, 10000, 0.5)]
    [InlineData(6000, 100, 10000, 10000.0 / 6000)]
    public void ComputeScale_ShrinksLargeAndEnlargesSmall(int width, int height, int max, double expected)
    {
        Assert.Equal(expected, WindowsOcrService.ComputeScale(width, height, max), 3);
    }

    [Fact]
    public void ScaleBack_MapsCoordinatesToOriginal()
    {
        var rect = WindowsOcrService.ScaleBack(20, 10, 40, 20, 2.0);
        Assert.Equal(new Rectangle(10, 5, 20, 10), rect);
    }
}

public class TranslationBatchTests
{
    [Fact]
    public void TencentGroupForBatch_SplitsByCharacterBudget()
    {
        var texts = new[] { new string('a', 3000), new string('b', 2000), new string('c', 1000), "d" };

        var groups = STool.Modules.Translation.TencentTranslationService.GroupForBatch(texts, 5500);

        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal(2, groups[1].Count);
    }

    [Fact]
    public void AiParseBatchResponse_RequiresExactCount()
    {
        Assert.Equal(new[] { "一", "二" }, STool.Modules.Translation.AiTranslationService.ParseBatchResponse("{\"t\":[\"一\",\"二\"]}", 2));
        Assert.Null(STool.Modules.Translation.AiTranslationService.ParseBatchResponse("{\"t\":[\"一\"]}", 2));
        Assert.Null(STool.Modules.Translation.AiTranslationService.ParseBatchResponse("{\"t\":[\"一\",\"\"]}", 2));
        Assert.Equal(new[] { "x" }, STool.Modules.Translation.AiTranslationService.ParseBatchResponse("```json\n{\"t\":[\"x\"]}\n```", 1));
    }

    [Fact]
    public async Task DefaultBatch_CallsSingleTranslatePerItem()
    {
        STool.Modules.Translation.ITranslationService service = new EchoService();

        var result = await service.TranslateBatchAsync(new[] { "a", "b" }, "auto", "zh");

        Assert.True(result.Success);
        Assert.Equal(new[] { "A", "B" }, result.Translations);
    }

    private sealed class EchoService : STool.Modules.Translation.ITranslationService
    {
        public Task<STool.Modules.Translation.TranslationResult> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default) =>
            Task.FromResult(new STool.Modules.Translation.TranslationResult { Success = true, TranslatedText = text.ToUpperInvariant() });

        public bool IsAvailable() => true;
        public void Dispose() { }
    }
}
