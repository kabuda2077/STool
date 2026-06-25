using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace STool.Modules.Ocr;

/// <summary>
/// Windows 内置 OCR 服务
/// </summary>
public class WindowsOcrService : IOcrService
{
    private readonly OcrEngine? _ocrEngine;

    public WindowsOcrService()
    {
        try
        {
            // 尝试使用中文简体
            var language = new Language("zh-Hans");
            if (OcrEngine.IsLanguageSupported(language))
            {
                _ocrEngine = OcrEngine.TryCreateFromLanguage(language);
            }
            else
            {
                // 降级到英文
                _ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to initialize Windows OCR engine");
            _ocrEngine = null;
        }
    }

    public bool IsAvailable()
    {
        return _ocrEngine != null;
    }

    public async Task<OcrResult> RecognizeAsync(Bitmap image, CancellationToken cancellationToken = default)
    {
        if (_ocrEngine == null)
        {
            return new OcrResult
            {
                Success = false,
                ErrorMessage = "Windows OCR engine not available",
                Provider = "Windows Local"
            };
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 转换 Bitmap 到 SoftwareBitmap
            using var memoryStream = new MemoryStream();
            image.Save(memoryStream, System.Drawing.Imaging.ImageFormat.Bmp);
            memoryStream.Position = 0;

            var randomAccessStream = new InMemoryRandomAccessStream();
            await memoryStream.CopyToAsync(randomAccessStream.AsStreamForWrite(), cancellationToken);
            randomAccessStream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(randomAccessStream).AsTask(cancellationToken);
            var softwareBitmap = await decoder.GetSoftwareBitmapAsync().AsTask(cancellationToken);

            // 执行 OCR
            var ocrResult = await _ocrEngine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);

            // 转换结果
            var result = new OcrResult
            {
                Success = true,
                Provider = "Windows Local",
                FullText = string.Join("\n", ocrResult.Lines.Select(line => line.Text))
            };

            foreach (var line in ocrResult.Lines)
            {
                foreach (var word in line.Words)
                {
                    result.TextBlocks.Add(new OcrTextBlock
                    {
                        Text = word.Text,
                        Confidence = 1.0f, // Windows OCR 不提供置信度
                        BoundingBox = new System.Drawing.Rectangle(
                            (int)word.BoundingRect.X,
                            (int)word.BoundingRect.Y,
                            (int)word.BoundingRect.Width,
                            (int)word.BoundingRect.Height
                        )
                    });
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            return new OcrResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Provider = "Windows Local"
            };
        }
    }

    public void Dispose()
    {
    }
}
