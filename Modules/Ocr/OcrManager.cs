using System;
using System.Drawing;
using System.Threading.Tasks;
using Serilog;
using STool.Core;
using STool.Models;

namespace STool.Modules.Ocr;

/// <summary>
/// OCR 管理器（带降级策略）
/// </summary>
public class OcrManager : IDisposable
{
    private readonly ConfigManager _configManager;
    private IOcrService? _primaryService;
    private string? _primaryServiceSignature;
    private WindowsOcrService? _fallbackLocalService;

    public OcrManager(ConfigManager configManager)
    {
        _configManager = configManager;
    }

    /// <summary>
    /// 识别图片中的文字（自动降级）
    /// </summary>
    public async Task<OcrResult> RecognizeAsync(Bitmap image)
    {
        var config = _configManager.Get().Ocr;

        var primaryService = GetOrCreatePrimaryService(config);

        // 尝试主要服务
        if (primaryService != null && primaryService.IsAvailable())
        {
            Log.Information($"Trying OCR with provider: {config.Provider}");
            var result = await primaryService.RecognizeAsync(image);

            if (result.Success)
            {
                Log.Information($"OCR succeeded with provider: {config.Provider}");
                return result;
            }

            Log.Warning($"OCR failed with provider {config.Provider}: {result.ErrorMessage}");
        }

        // 降级到本地 OCR
        if (config.FallbackToLocal && config.Provider != OcrProvider.WindowsLocal)
        {
            Log.Information("Falling back to Windows local OCR");
            _fallbackLocalService ??= new WindowsOcrService();
            var localService = _fallbackLocalService;

            if (localService.IsAvailable())
            {
                var result = await localService.RecognizeAsync(image);

                if (result.Success)
                {
                    Log.Information("OCR succeeded with Windows local OCR");
                    return result;
                }

                Log.Warning($"Windows local OCR also failed: {result.ErrorMessage}");
            }
        }

        // 所有服务都失败
        return new OcrResult
        {
            Success = false,
            ErrorMessage = "All OCR services failed",
            Provider = "None"
        };
    }

    private IOcrService? CreateTencentService(OcrConfig config)
    {
        if (string.IsNullOrEmpty(config.TencentSecretIdEncrypted) ||
            string.IsNullOrEmpty(config.TencentSecretKeyEncrypted))
        {
            return null;
        }

        return new TencentOcrService(
            config.TencentSecretIdEncrypted,
            config.TencentSecretKeyEncrypted
        );
    }

    private IOcrService? CreateAiService(OcrConfig config)
    {
        if (string.IsNullOrEmpty(config.AiApiUrlEncrypted) ||
            string.IsNullOrEmpty(config.AiApiKeyEncrypted) ||
            string.IsNullOrEmpty(config.AiModel))
        {
            return null;
        }

        return new AiVisionOcrService(
            config.AiApiUrlEncrypted,
            config.AiApiKeyEncrypted,
            config.AiModel
        );
    }

    private IOcrService? GetOrCreatePrimaryService(OcrConfig config)
    {
        var signature = CreateSignature(config);
        if (_primaryService != null && _primaryServiceSignature == signature)
        {
            return _primaryService;
        }

        _primaryService?.Dispose();
        _primaryService = config.Provider switch
        {
            OcrProvider.Tencent => CreateTencentService(config),
            OcrProvider.AI => CreateAiService(config),
            OcrProvider.WindowsLocal => new WindowsOcrService(),
            _ => null
        };
        _primaryServiceSignature = signature;

        return _primaryService;
    }

    private static string CreateSignature(OcrConfig config)
    {
        return string.Join("|",
            config.Provider,
            config.TencentSecretIdEncrypted,
            config.TencentSecretKeyEncrypted,
            config.AiApiUrlEncrypted,
            config.AiApiKeyEncrypted,
            config.AiModel);
    }

    public void Dispose()
    {
        _primaryService?.Dispose();
        _fallbackLocalService?.Dispose();
    }
}
