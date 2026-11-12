using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using STool.Models;
using STool.Modules.Ocr;
using STool.Modules.Translation;

namespace STool.Modules.Screenshot;

/// <summary>截图翻译依赖的服务，便于在测试中替换。</summary>
internal interface IScreenshotTranslationServices
{
    ScreenshotTranslationMode ScreenshotMode { get; }
    string TranslationMode { get; }
    Task<OcrResult> RecognizeForLayoutAsync(Bitmap image, CancellationToken cancellationToken);

    /// <summary>智能模式的「挑选 + 翻译」；AI 文本模型未配置时返回 null。</summary>
    Func<IReadOnlyList<ScreenContentLine>, string, CancellationToken, Task<IReadOnlyList<ScreenTranslationBlock>?>>? CreateSmartSelector();

    Task<BlockTranslationResult> TranslateBlocksAsync(IReadOnlyList<string> blocks, string targetLanguage, CancellationToken cancellationToken);
    Task<TranslationResult> TranslateAsync(string text, CancellationToken cancellationToken);
}

internal sealed class ScreenshotTranslationServices : IScreenshotTranslationServices
{
    private readonly OcrManager _ocr;
    private readonly TranslationManager _translation;

    public ScreenshotTranslationServices(OcrManager ocr, TranslationManager translation)
    {
        _ocr = ocr;
        _translation = translation;
    }

    public ScreenshotTranslationMode ScreenshotMode => _translation.GetConfiguredScreenshotMode();
    public string TranslationMode => _translation.GetConfiguredTranslationMode();

    public Task<OcrResult> RecognizeForLayoutAsync(Bitmap image, CancellationToken cancellationToken) =>
        _ocr.RecognizeForLayoutAsync(image, cancellationToken);

    public Func<IReadOnlyList<ScreenContentLine>, string, CancellationToken, Task<IReadOnlyList<ScreenTranslationBlock>?>>? CreateSmartSelector()
    {
        var selector = _translation.TryCreateContentSelector();
        return selector == null ? null : selector.SelectAndTranslateAsync;
    }

    public Task<BlockTranslationResult> TranslateBlocksAsync(IReadOnlyList<string> blocks, string targetLanguage, CancellationToken cancellationToken) =>
        _translation.TranslateBlocksAsync(blocks, targetLanguage: targetLanguage, cancellationToken: cancellationToken);

    public Task<TranslationResult> TranslateAsync(string text, CancellationToken cancellationToken) =>
        _translation.TranslateAsync(text, cancellationToken: cancellationToken);
}

internal enum ScreenshotTranslationStage { Recognizing, SelectingAndTranslating, Translating }

internal enum ScreenshotTranslationOutcome
{
    /// <summary>原位翻译：译文按段落贴回原位置。</summary>
    InPlace,

    /// <summary>整段翻译：译文显示在覆盖选区的浮层里。</summary>
    WholeBlock,

    Failed
}

internal sealed class ScreenshotTranslationResult
{
    public ScreenshotTranslationOutcome Outcome { get; init; }

    /// <summary>原位翻译的段落与对应译文。</summary>
    public IReadOnlyList<TranslationParagraph> Paragraphs { get; init; } = Array.Empty<TranslationParagraph>();
    public IReadOnlyList<string> Translations { get; init; } = Array.Empty<string>();

    /// <summary>识别出的全部行（包括未翻译的），渲染时用来避免译文扩展到其他文字上。</summary>
    public IReadOnlyList<TranslationLine> AllLines { get; init; } = Array.Empty<TranslationLine>();

    public string TargetLanguage { get; init; } = "zh";
    public string WholeText { get; init; } = string.Empty;
    public string? ErrorTitle { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>降级提示（例如智能模式未配置），由 UI 决定是否提示用户。</summary>
    public string? Notice { get; init; }

    /// <summary>走过的步骤，写日志用。</summary>
    public IReadOnlyList<string> Trace { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 截图翻译流水线：① 识别（带位置）→ ② 挑选（智能：AI / 快速：本地规则）→ ③ 翻译。
/// 降级顺序：智能失败 → 快速；快速选不出内容或翻译结果不可用 → 整段翻译；整段翻译失败 → 报错。
/// 不依赖 UI，结果交给 CaptureOverlay 渲染。
/// </summary>
internal sealed class ScreenshotTranslationPipeline
{
    private readonly IScreenshotTranslationServices _services;

    public ScreenshotTranslationPipeline(IScreenshotTranslationServices services)
    {
        _services = services;
    }

    public async Task<ScreenshotTranslationResult> RunAsync(Bitmap crop, CancellationToken cancellationToken,
        IProgress<ScreenshotTranslationStage>? progress = null)
    {
        var trace = new List<string>();
        string? notice = null;

        // ① 识别
        progress?.Report(ScreenshotTranslationStage.Recognizing);
        var ocr = await _services.RecognizeForLayoutAsync(crop, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        trace.Add($"ocr provider={ocr.Provider} success={ocr.Success} blocks={ocr.TextBlocks.Count}");
        if (!ocr.Success || string.IsNullOrWhiteSpace(ocr.FullText))
        {
            return Log(new ScreenshotTranslationResult
            {
                Outcome = ScreenshotTranslationOutcome.Failed,
                ErrorTitle = "OCR 失败",
                ErrorMessage = ocr.ErrorMessage ?? "未识别到文字",
                Trace = trace
            });
        }

        var lines = ScreenshotTextLayout.OrderForGrouping(ScreenshotTextLayout.BuildLines(ocr, crop.Width, crop.Height));
        var targetLanguage = TranslationManager.ResolveTargetLanguage(
            string.Join("\n", lines.Select(line => line.Text)),
            _services.TranslationMode);
        trace.Add($"lines={lines.Count} target={targetLanguage}");

        if (lines.Count >= 2 && IsMostlyVertical(lines))
        {
            trace.Add("vertical text: in-place layout unsupported");
        }
        else if (lines.Count >= 2)
        {
            // ② 挑选：智能
            if (_services.ScreenshotMode == ScreenshotTranslationMode.Smart)
            {
                progress?.Report(ScreenshotTranslationStage.SelectingAndTranslating);
                var smart = await TrySmartAsync(lines, targetLanguage, trace, cancellationToken);
                if (smart.Result != null)
                    return Log(smart.Result);
                notice = smart.Notice;
            }

            // ② 挑选：快速
            progress?.Report(ScreenshotTranslationStage.Translating);
            var fast = await TryFastAsync(lines, crop, targetLanguage, trace, cancellationToken);
            if (fast != null)
                return Log(WithNotice(fast, notice));
        }
        else
        {
            trace.Add("too few lines for in-place");
        }

        // ③ 整段翻译
        progress?.Report(ScreenshotTranslationStage.Translating);
        var whole = await _services.TranslateAsync(ocr.FullText, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        trace.Add($"whole-block success={whole.Success} provider={whole.Provider}");
        return Log(whole.Success
            ? new ScreenshotTranslationResult
            {
                Outcome = ScreenshotTranslationOutcome.WholeBlock,
                WholeText = whole.TranslatedText,
                TargetLanguage = targetLanguage,
                Notice = notice,
                Trace = trace
            }
            : new ScreenshotTranslationResult
            {
                Outcome = ScreenshotTranslationOutcome.Failed,
                ErrorTitle = "翻译失败",
                ErrorMessage = whole.ErrorMessage ?? string.Empty,
                Trace = trace
            });
    }

    private async Task<(ScreenshotTranslationResult? Result, string? Notice)> TrySmartAsync(
        IReadOnlyList<TranslationLine> lines,
        string targetLanguage,
        List<string> trace,
        CancellationToken cancellationToken)
    {
        var selector = _services.CreateSmartSelector();
        if (selector == null)
        {
            trace.Add("smart unavailable: AI text model not configured");
            return (null, "智能模式需要在「设置 → 服务账号」配置 AI 文本模型，本次已改用快速模式。");
        }

        // 本地先分段，作为 AI 分块的参考。
        var paragraphOf = new Dictionary<TranslationLine, int>(ReferenceEqualityComparer.Instance);
        var localParagraphs = ScreenshotTextLayout.GroupParagraphs(lines);
        for (var p = 0; p < localParagraphs.Count; p++)
        {
            foreach (var line in localParagraphs[p].Lines)
                paragraphOf[line] = p;
        }

        var contentLines = lines
            .Select((line, index) => new ScreenContentLine(
                index,
                line.Text,
                line.Box.X,
                line.Box.Y,
                line.Box.Width,
                line.Box.Height,
                paragraphOf.TryGetValue(line, out var p) ? p : -1))
            .ToArray();

        var blocks = await selector(contentLines, targetLanguage, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (blocks == null)
        {
            trace.Add("smart failed");
            return (null, "智能翻译调用失败，本次已改用快速模式。详情见日志。");
        }

        if (blocks.Count == 0)
        {
            trace.Add("smart selected nothing");
            return (null, null);
        }

        var paragraphs = new List<TranslationParagraph>();
        var translations = new List<string>();
        foreach (var block in blocks)
        {
            var blockLines = block.LineIndices.Select(index => lines[index]).ToArray();
            paragraphs.Add(ToParagraph(blockLines));
            translations.Add(block.Translation);
        }

        trace.Add($"smart blocks={paragraphs.Count} lines={blocks.Sum(b => b.LineIndices.Count)}/{lines.Count}");
        return (new ScreenshotTranslationResult
        {
            Outcome = ScreenshotTranslationOutcome.InPlace,
            Paragraphs = paragraphs,
            Translations = translations,
            AllLines = lines,
            TargetLanguage = targetLanguage,
            Trace = trace
        }, null);
    }

    private async Task<ScreenshotTranslationResult?> TryFastAsync(
        IReadOnlyList<TranslationLine> lines,
        Bitmap crop,
        string targetLanguage,
        List<string> trace,
        CancellationToken cancellationToken)
    {
        var selected = lines
            .Where(line => ScreenshotTextLayout.IsLikelyTranslatableContent(line, crop.Width, crop.Height, targetLanguage))
            .ToList();
        if (selected.Count == 0)
        {
            trace.Add("fast selected nothing");
            return null;
        }

        var paragraphs = ScreenshotTextLayout.GroupParagraphs(selected);
        if (paragraphs.Count == 0)
        {
            trace.Add("fast produced no paragraphs");
            return null;
        }

        var translated = await _services.TranslateBlocksAsync(
            paragraphs.Select(p => p.Text).ToArray(),
            targetLanguage,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!translated.Success || translated.TranslatedBlocks.Count != paragraphs.Count)
        {
            trace.Add($"fast translation failed: {translated.ErrorMessage}");
            return null;
        }

        trace.Add($"fast paragraphs={paragraphs.Count} lines={selected.Count}/{lines.Count} provider={translated.Provider}");
        return new ScreenshotTranslationResult
        {
            Outcome = ScreenshotTranslationOutcome.InPlace,
            Paragraphs = paragraphs,
            Translations = translated.TranslatedBlocks,
            AllLines = lines,
            TargetLanguage = targetLanguage,
            Trace = trace
        };
    }

    /// <summary>竖排文字（多数行高明显大于宽）无法按横排原位排版，直接整段翻译。</summary>
    internal static bool IsMostlyVertical(IReadOnlyList<TranslationLine> lines)
    {
        var vertical = lines.Count(line => line.Text.Trim().Length >= 2 && line.Box.Height > line.Box.Width * 1.5);
        return vertical * 2 > lines.Count;
    }

    internal static TranslationParagraph ToParagraph(IReadOnlyList<TranslationLine> blockLines)
    {
        var box = blockLines.Select(line => line.Box).Aggregate(Rectangle.Union);
        var median = blockLines.Select(line => line.Box.Height).OrderBy(h => h).ElementAt(blockLines.Count / 2);
        return new TranslationParagraph(ScreenshotTextLayout.JoinParagraphText(blockLines), box, median)
        {
            Lines = blockLines
        };
    }

    private static ScreenshotTranslationResult WithNotice(ScreenshotTranslationResult result, string? notice) =>
        notice == null
            ? result
            : new ScreenshotTranslationResult
            {
                Outcome = result.Outcome,
                Paragraphs = result.Paragraphs,
                Translations = result.Translations,
                AllLines = result.AllLines,
                TargetLanguage = result.TargetLanguage,
                WholeText = result.WholeText,
                ErrorTitle = result.ErrorTitle,
                ErrorMessage = result.ErrorMessage,
                Notice = notice,
                Trace = result.Trace
            };

    private static ScreenshotTranslationResult Log(ScreenshotTranslationResult result)
    {
        Serilog.Log.Information(
            "[ScreenshotTranslate] outcome={Outcome} trace={Trace}",
            result.Outcome,
            string.Join(" | ", result.Trace));
        return result;
    }
}
