using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace STool.Modules.Ocr;

/// <summary>
/// OCR 服务接口
/// </summary>
public interface IOcrService : IDisposable
{
    /// <summary>
    /// 识别图片中的文字
    /// </summary>
    Task<OcrResult> RecognizeAsync(Bitmap image, CancellationToken cancellationToken = default);

    /// <summary>
    /// 服务是否可用
    /// </summary>
    bool IsAvailable();
}
