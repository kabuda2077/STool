using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using STool.Core;
using STool.Models;

namespace STool.Modules.Ocr;

/// <summary>
/// OCR 管理器。两条入口：
/// <list type="bullet">
/// <item><see cref="RecognizeAsync"/>：「OCR 识别」按钮，使用 OCR 页选择的引擎（本地 / 腾讯云 / AI 视觉）。</item>
/// <item><see cref="RecognizeForLayoutAsync"/>：截图翻译，只用能返回文字位置的引擎（腾讯云 / 本地）。</item>
/// </list>
/// 两者失败时都可降级到 Windows 本地 OCR。服务实例无请求间状态，可并发使用；锁只保护实例的创建。
/// </summary>
public class OcrManager : IDisposable
{
    private readonly ConfigManager _configManager;
    private readonly object _serviceGate = new();
    private readonly Dictionary<string, IOcrService?> _services = new();
    private readonly List<IOcrService> _retiredServices = new();

    public OcrManager(ConfigManager configManager)
    {
        _configManager = configManager;
    }

    /// <summary>「OCR 识别」：按 OCR 设置选择引擎，失败按设置降级到本地。</summary>
    public Task<OcrResult> RecognizeAsync(Bitmap image, CancellationToken cancellationToken = default)
    {
        var config = _configManager.Get();
        return RecognizeWithFallbackAsync(
            image,
            config.Ocr.Provider,
            config,
            fallbackToLocal: config.Ocr.FallbackToLocal,
            cancellationToken);
    }

    /// <summary>截图翻译：使用翻译设置里的「截图翻译识别引擎」，结果一定带文字位置；腾讯云失败时总是降级到本地。</summary>
    public Task<OcrResult> RecognizeForLayoutAsync(Bitmap image, CancellationToken cancellationToken = default)
    {
        var config = _configManager.Get();
        var provider = config.Translation.ScreenshotOcr == ScreenshotOcrEngine.Tencent
            ? OcrProvider.Tencent
            : OcrProvider.WindowsLocal;
        return RecognizeWithFallbackAsync(image, provider, config, fallbackToLocal: true, cancellationToken);
    }

    private async Task<OcrResult> RecognizeWithFallbackAsync(
        Bitmap image,
        OcrProvider provider,
        AppConfig config,
        bool fallbackToLocal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OcrResult? primaryFailure = null;

        var primaryService = GetOrCreateService(provider, config);
        if (primaryService != null && primaryService.IsAvailable())
        {
            Log.Information("Trying OCR with provider {Provider}", provider);
            var result = await primaryService.RecognizeAsync(image, cancellationToken);
            if (result.Success)
            {
                Log.Information("OCR succeeded with provider {Provider}", provider);
                return result;
            }

            primaryFailure = result;
            Log.Warning("OCR failed with provider {Provider}: {ErrorMessage}", provider, result.ErrorMessage);
        }
        else if (provider != OcrProvider.WindowsLocal)
        {
            primaryFailure = new OcrResult
            {
                Success = false,
                ErrorMessage = provider == OcrProvider.Tencent
                    ? "腾讯云账号未配置，请在「设置 → 服务账号」中填写。"
                    : "AI 视觉模型未配置，请在「设置 → 服务账号」中填写。",
                Provider = provider.ToString()
            };
        }

        // 降级到本地 OCR（用户已取消时不再降级）
        if (fallbackToLocal && provider != OcrProvider.WindowsLocal && !cancellationToken.IsCancellationRequested)
        {
            var localService = GetOrCreateService(OcrProvider.WindowsLocal, config);
            if (localService != null && localService.IsAvailable())
            {
                Log.Information("Falling back to Windows local OCR");
                var result = await localService.RecognizeAsync(image, cancellationToken);
                if (result.Success)
                {
                    Log.Information("OCR succeeded with Windows local OCR");
                    return result;
                }

                Log.Warning("Windows local OCR also failed: {ErrorMessage}", result.ErrorMessage);
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

        return primaryFailure ?? new OcrResult
        {
            Success = false,
            ErrorMessage = "当前 OCR 服务未配置完整或不可用。",
            Provider = "None"
        };
    }

    private IOcrService? GetOrCreateService(OcrProvider provider, AppConfig config)
    {
        var key = provider.ToString();
        var signature = provider switch
        {
            OcrProvider.Tencent => string.Join("|", config.Accounts.Tencent.SecretIdEncrypted, config.Accounts.Tencent.SecretKeyEncrypted),
            OcrProvider.AI => VisionSignature(config.Accounts.ResolveVision()),
            _ => config.Ocr.LocalLanguage
        };
        var cacheKey = key + "#" + signature;

        lock (_serviceGate)
        {
            if (_services.TryGetValue(cacheKey, out var cached))
                return cached;

            // 同一引擎账号变化：旧实例可能仍在使用，延后到 Dispose 时释放。
            foreach (var existingKey in new List<string>(_services.Keys))
            {
                if (!existingKey.StartsWith(key + "#", StringComparison.Ordinal))
                    continue;
                if (_services[existingKey] is { } old)
                    _retiredServices.Add(old);
                _services.Remove(existingKey);
            }

            var service = CreateService(provider, config);
            _services[cacheKey] = service;
            return service;
        }
    }

    private static string VisionSignature(AiAccountConfig account) =>
        string.Join("|", account.ApiUrlEncrypted, account.ApiKeyEncrypted, account.Model);

    private static IOcrService? CreateService(OcrProvider provider, AppConfig config)
    {
        switch (provider)
        {
            case OcrProvider.Tencent when config.Accounts.Tencent.IsConfigured:
                return new TencentOcrService(config.Accounts.Tencent.SecretIdEncrypted!, config.Accounts.Tencent.SecretKeyEncrypted!);
            case OcrProvider.AI:
                var vision = config.Accounts.ResolveVision();
                return vision.IsConfigured
                    ? new AiVisionOcrService(vision.ApiUrlEncrypted!, vision.ApiKeyEncrypted!, vision.Model!)
                    : null;
            case OcrProvider.WindowsLocal:
                return new WindowsOcrService(config.Ocr.LocalLanguage);
            default:
                return null;
        }
    }

    public void Dispose()
    {
        lock (_serviceGate)
        {
            foreach (var service in _services.Values)
                service?.Dispose();
            foreach (var service in _retiredServices)
                service.Dispose();
            _services.Clear();
            _retiredServices.Clear();
        }
    }
}
