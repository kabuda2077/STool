using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using STool.Modules.Clipboard;
using STool.Modules.Ocr;
using STool.Modules.Screenshot;
using Xunit;
using Xunit.Abstractions;

namespace STool.Tests;

/// <summary>
/// 固定样本、无 UI 的测量入口。输出实际样本，不断言耗时阈值；托管分配不包含 WIC/GDI 原生内存。
/// 可单独运行 dotnet test --filter Category=Performance 并保留 TRX 中的 STOOL_PERF 行。
/// </summary>
public class PerformanceMeasurementsTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public void MeasureFrozenImageHashAndPngEncoding()
    {
        const int width = 1024;
        const int height = 768;
        var pixels = new byte[width * height * 4];
        new Random(721).NextBytes(pixels);
        for (var index = 3; index < pixels.Length; index += 4)
            pixels[index] = 255;
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();

        string? digest = null;
        Measure("bgra-row-hash-1024x768", () => digest = ClipboardContentHash.ForImage(image));
        Assert.NotNull(digest);
        Assert.Equal(64, digest.Length);

        long encodedBytes = 0;
        Measure("png-encode-1024x768", () =>
        {
            using var stream = new MemoryStream();
            if (!ClipboardManager.TryEncodePng(image, stream, 4L * 1024 * 1024))
                throw new InvalidOperationException("Benchmark image unexpectedly exceeded its limit.");
            encodedBytes = stream.Length;
        });
        Assert.True(encodedBytes > 0);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void MeasureOcrLayoutAndBackgroundSampling()
    {
        var ocr = new OcrResult { Success = true };
        for (var index = 0; index < 1000; index++)
        {
            ocr.TextBlocks.Add(new OcrTextBlock
            {
                Text = "This is a complete sentence for layout analysis.",
                BoundingBox = new Rectangle(10, index * 24 + 10, 600, 20),
                Confidence = 1
            });
        }
        List<TranslationLine>? lines = null;
        Measure("merge-1000-ocr-lines", () => lines = ScreenshotTextLayout.BuildLines(ocr, 1000, 25000));
        Assert.NotNull(lines);
        Assert.Equal(1000, lines.Count);
        List<TranslationParagraph>? paragraphs = null;
        Measure("group-1000-lines", () => paragraphs = ScreenshotTextLayout.GroupParagraphs(lines));
        Assert.NotEmpty(paragraphs!);

        using var bitmap = new Bitmap(1200, 900);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.Clear(System.Drawing.Color.White);
        Measure("sample-paragraph-background", () =>
            ScreenshotTextLayout.SampleBackground(bitmap, new Rectangle(100, 100, 800, 600), 24));
    }

    private void Measure(string operation, Action action)
    {
        const int count = 5;
        action(); // 预热；不把首次 JIT 当作稳定操作耗时。
        var samples = new List<Measurement>();
        for (var index = 0; index < count; index++)
        {
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            action();
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            samples.Add(new Measurement(elapsed, GC.GetAllocatedBytesForCurrentThread() - bytes));
        }
        output.WriteLine("STOOL_PERF:" + JsonSerializer.Serialize(new
        {
            operation,
            sampleCount = count,
            runtime = Environment.Version.ToString(),
            medianMilliseconds = samples.Select(sample => sample.Milliseconds).Order().ElementAt(count / 2),
            medianManagedAllocatedBytes = samples.Select(sample => sample.ManagedAllocatedBytes).Order().ElementAt(count / 2),
            samples,
            scope = "No UI; native allocations and application startup are not measured."
        }));
    }

    private sealed record Measurement(double Milliseconds, long ManagedAllocatedBytes);
}
