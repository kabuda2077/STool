using STool.Modules.Ocr;
using STool.Modules.Translation;

namespace STool.Modules.Screenshot;

/// <summary>截图窗口依赖的服务，由应用外壳创建时传入。</summary>
public sealed record CaptureOverlayServices(
    OcrManager Ocr,
    TranslationManager Translation,
    bool DiagnosticsEnabled);
