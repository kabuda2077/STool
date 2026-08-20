using System;
using System.Drawing;
using System.Threading;
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
    private OcrServiceOptions? _primaryServiceOptions;
    private WindowsOcrService? _fallbackLocalService;

    public OcrManager(ConfigManager configManager)
    {
        _configManager = configManager;
    }

    /// <summary>
    /// 识别图片中的文字（自动降级）
    /// </summary>
    public async Task<OcrResult> RecognizeAsync(Bitmap image, CancellationToken cancellationToken = default)
    {
        var config = _configManager.Get().Ocr;
        OcrResult? primaryFailure = null;

        var primaryService = GetOrCreatePrimaryService(config);

        // 尝试主要服务
        if (primaryService != null && primaryService.IsAvailable())
        {
            Log.Information($"Trying OCR with provider: {config.Provider}");
            var result = await primaryService.RecognizeAsync(image, cancellationToken);

            if (result.Success)
            {
                Log.Information($"OCR succeeded with provider: {config.Provider}");
                return result;
            }

            primaryFailure = result;
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
                var result = await localService.RecognizeAsync(image, cancellationToken);

                if (result.Success)
                {
                    Log.Information("OCR succeeded with Windows local OCR");
                    return result;
                }

                Log.Warning($"Windows local OCR also failed: {result.ErrorMessage}");
                return new OcrResult
                {
                    Success = false,
                    ErrorMessage = primaryFailure == null
                        ? result.ErrorMessage
                        : $"{primaryFailure.ErrorMessage} Windows 本地 OCR 也未能识别：{result.ErrorMessage}",
                    Provider = primaryFailure == null
                        ? result.Provider
                        : $"{primaryFailure.Provider} / {result.Provider}"
                };
            }
        }

        if (primaryFailure != null)
        {
            return primaryFailure;
        }

        return new OcrResult
        {
            Success = false,
            ErrorMessage = "当前 OCR 服务未配置完整或不可用。",
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
        var options = OcrServiceOptions.From(config);
        if (_primaryService != null && _primaryServiceOptions == options)
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
        _primaryServiceOptions = options;

        return _primaryService;
    }

    private sealed record OcrServiceOptions(
        OcrProvider Provider,
        string? TencentSecretId,
        string? TencentSecretKey,
        string? AiApiUrl,
        string? AiApiKey,
        string? AiModel)
    {
        public static OcrServiceOptions From(OcrConfig config) => new(
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
