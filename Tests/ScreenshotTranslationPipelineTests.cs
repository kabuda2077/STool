using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using STool.Models;
using STool.Modules.Ocr;
using STool.Modules.Screenshot;
using STool.Modules.Translation;
using Xunit;

namespace STool.Tests;

public class ScreenshotTranslationPipelineTests
{
    private static OcrResult TwoParagraphOcr() => new()
    {
        Success = true,
        Provider = "fake",
        FullText = "This is the first sentence of the body\nwhich continues here.\nAnother paragraph is here.",
        TextBlocks =
        {
            Block("This is the first sentence of the body", 10, 10, 260, 18),
            Block("which continues here.", 10, 32, 160, 18),
            Block("Another paragraph is here.", 10, 90, 200, 18)
        }
    };

    private static OcrTextBlock Block(string text, int x, int y, int w, int h) =>
        new() { Text = text, Confidence = 1, BoundingBox = new Rectangle(x, y, w, h) };

    [Fact]
    public async Task Fast_TranslatesParagraphsInPlace()
    {
        var services = new FakeServices { Ocr = TwoParagraphOcr() };
        using var crop = new Bitmap(400, 140);

        var result = await new ScreenshotTranslationPipeline(services).RunAsync(crop, default);

        Assert.Equal(ScreenshotTranslationOutcome.InPlace, result.Outcome);
        Assert.Equal(2, result.Paragraphs.Count);
        Assert.Equal(2, result.Paragraphs[0].Lines.Count);
        Assert.Equal(result.Paragraphs.Count, result.Translations.Count);
        Assert.Null(result.Notice);
    }

    [Fact]
    public async Task Smart_UsesAiBlocks()
    {
        var services = new FakeServices
        {
            Mode = ScreenshotTranslationMode.Smart,
            Ocr = TwoParagraphOcr(),
            Smart = (lines, _, _) => Task.FromResult<IReadOnlyList<ScreenTranslationBlock>?>(
                new[] { new ScreenTranslationBlock(new[] { 0, 1 }, "第一段") })
        };
        using var crop = new Bitmap(400, 140);

        var result = await new ScreenshotTranslationPipeline(services).RunAsync(crop, default);

        Assert.Equal(ScreenshotTranslationOutcome.InPlace, result.Outcome);
        var paragraph = Assert.Single(result.Paragraphs);
        Assert.Equal(2, paragraph.Lines.Count);
        Assert.Equal("第一段", result.Translations[0]);
        Assert.Equal(0, services.BatchCalls);
    }

    [Fact]
    public async Task Smart_PassesLocalParagraphHints()
    {
        IReadOnlyList<ScreenContentLine>? seen = null;
        var services = new FakeServices
        {
            Mode = ScreenshotTranslationMode.Smart,
            Ocr = TwoParagraphOcr(),
            Smart = (lines, _, _) =>
            {
                seen = lines;
                return Task.FromResult<IReadOnlyList<ScreenTranslationBlock>?>(new[] { new ScreenTranslationBlock(new[] { 2 }, "x") });
            }
        };
        using var crop = new Bitmap(400, 140);

        await new ScreenshotTranslationPipeline(services).RunAsync(crop, default);

        Assert.NotNull(seen);
        Assert.Equal(seen[0].Paragraph, seen[1].Paragraph);
        Assert.NotEqual(seen[1].Paragraph, seen[2].Paragraph);
    }

    [Fact]
    public async Task Smart_Failure_FallsBackToFastWithNotice()
    {
        var services = new FakeServices
        {
            Mode = ScreenshotTranslationMode.Smart,
            Ocr = TwoParagraphOcr(),
            Smart = (_, _, _) => Task.FromResult<IReadOnlyList<ScreenTranslationBlock>?>(null)
        };
        using var crop = new Bitmap(400, 140);

        var result = await new ScreenshotTranslationPipeline(services).RunAsync(crop, default);

        Assert.Equal(ScreenshotTranslationOutcome.InPlace, result.Outcome);
        Assert.Equal(1, services.BatchCalls);
        Assert.NotNull(result.Notice);
    }

    [Fact]
    public async Task Smart_NotConfigured_FallsBackToFastWithNotice()
    {
        var services = new FakeServices { Mode = ScreenshotTranslationMode.Smart, Ocr = TwoParagraphOcr(), Smart = null };
        using var crop = new Bitmap(400, 140);

        var result = await new ScreenshotTranslationPipeline(services).RunAsync(crop, default);

        Assert.Equal(ScreenshotTranslationOutcome.InPlace, result.Outcome);
        Assert.Contains("服务账号", result.Notice);
    }

    [Fact]
    public async Task Fast_BatchFailure_FallsBackToWholeBlock()
    {
        var services = new FakeServices { Ocr = TwoParagraphOcr(), BatchFails = true };
        using var crop = new Bitmap(400, 140);

        var result = await new ScreenshotTranslationPipeline(services).RunAsync(crop, default);

        Assert.Equal(ScreenshotTranslationOutcome.WholeBlock, result.Outcome);
        Assert.Equal(1, services.WholeCalls);
    }

    [Fact]
    public async Task SingleLine_GoesStraightToWholeBlock()
    {
        var services = new FakeServices
        {
            Ocr = new OcrResult
            {
                Success = true,
                FullText = "Just one line of text",
                TextBlocks = { Block("Just one line of text", 0, 0, 100, 18) }
            }
        };
        using var crop = new Bitmap(200, 30);

        var result = await new ScreenshotTranslationPipeline(services).RunAsync(crop, default);

        Assert.Equal(ScreenshotTranslationOutcome.WholeBlock, result.Outcome);
        Assert.Equal(0, services.BatchCalls);
    }

    [Fact]
    public async Task OcrFailure_ReportsOcrError()
    {
        var services = new FakeServices { Ocr = new OcrResult { Success = false, ErrorMessage = "boom" } };
        using var crop = new Bitmap(10, 10);

        var result = await new ScreenshotTranslationPipeline(services).RunAsync(crop, default);

        Assert.Equal(ScreenshotTranslationOutcome.Failed, result.Outcome);
        Assert.Equal("OCR 失败", result.ErrorTitle);
        Assert.Equal(0, services.WholeCalls);
    }

    [Fact]
    public async Task WholeBlockFailure_ReportsTranslationError()
    {
        var services = new FakeServices { Ocr = TwoParagraphOcr(), BatchFails = true, WholeFails = true };
        using var crop = new Bitmap(400, 140);

        var result = await new ScreenshotTranslationPipeline(services).RunAsync(crop, default);

        Assert.Equal(ScreenshotTranslationOutcome.Failed, result.Outcome);
        Assert.Equal("翻译失败", result.ErrorTitle);
    }

    private sealed class FakeServices : IScreenshotTranslationServices
    {
        public ScreenshotTranslationMode Mode { get; init; } = ScreenshotTranslationMode.Fast;
        public OcrResult Ocr { get; init; } = new();
        public Func<IReadOnlyList<ScreenContentLine>, string, CancellationToken, Task<IReadOnlyList<ScreenTranslationBlock>?>>? Smart { get; init; }
        public bool BatchFails { get; init; }
        public bool WholeFails { get; init; }
        public int BatchCalls { get; private set; }
        public int WholeCalls { get; private set; }

        public ScreenshotTranslationMode ScreenshotMode => Mode;
        public string TranslationMode => "auto-zh";

        public Task<OcrResult> RecognizeForLayoutAsync(Bitmap image, CancellationToken cancellationToken) => Task.FromResult(Ocr);

        public Func<IReadOnlyList<ScreenContentLine>, string, CancellationToken, Task<IReadOnlyList<ScreenTranslationBlock>?>>? CreateSmartSelector() => Smart;

        public Task<BlockTranslationResult> TranslateBlocksAsync(IReadOnlyList<string> blocks, string targetLanguage, CancellationToken cancellationToken)
        {
            BatchCalls++;
            return Task.FromResult(BatchFails
                ? new BlockTranslationResult { Success = false, ErrorMessage = "batch failed" }
                : new BlockTranslationResult { Success = true, TranslatedBlocks = blocks.Select(b => "译:" + b).ToArray() });
        }

        public Task<TranslationResult> TranslateAsync(string text, CancellationToken cancellationToken)
        {
            WholeCalls++;
            return Task.FromResult(WholeFails
                ? new TranslationResult { Success = false, ErrorMessage = "whole failed" }
                : new TranslationResult { Success = true, TranslatedText = "译:" + text });
        }
    }
}
