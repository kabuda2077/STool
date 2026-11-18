using System;
using System.IO;
using System.Text.Json;
using Serilog;
using STool.Models;

namespace STool.Core;

public class ConfigManager
{
    private readonly string _configPath;
    private readonly Func<string, string> _decrypt;
    private readonly Func<string, string> _encrypt;
    private readonly Func<string, bool> _isPortableEncrypted;
    private readonly object _gate = new();
    private AppConfig? _config;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Get() 每次都返回深拷贝，快照不需要缩进格式。
    private static readonly JsonSerializerOptions CloneOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ConfigManager()
        : this(AppPaths.ConfigPath, SecureStorage.Decrypt, SecureStorage.Encrypt, SecureStorage.IsPortableEncrypted)
    {
        AppPaths.EnsureStandardDirectories();
    }

    internal ConfigManager(
        string configPath,
        Func<string, string>? decrypt = null,
        Func<string, string>? encrypt = null,
        Func<string, bool>? isPortableEncrypted = null)
    {
        _configPath = configPath;
        _decrypt = decrypt ?? SecureStorage.Decrypt;
        _encrypt = encrypt ?? SecureStorage.Encrypt;
        _isPortableEncrypted = isPortableEncrypted ?? SecureStorage.IsPortableEncrypted;
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath) ?? ".");
    }

    /// <summary>Returns an isolated snapshot. Mutating it never changes the cached configuration.</summary>
    public AppConfig Get()
    {
        lock (_gate)
            return Clone(GetOrLoadInternal());
    }

    public AppConfig Update(Action<AppConfig> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            var next = Clone(GetOrLoadInternal());
            var previousOcrProvider = next.Ocr.Provider;
            update(next);
            if (next.Ocr.Provider != previousOcrProvider)
                next.Translation.ScreenshotOcrMigrationPending = false;
            SaveInternal(next);
            _config = next;
            return Clone(next);
        }
    }

    internal string DecryptSecret(string encrypted) => _decrypt(encrypted);

    public void Reload()
    {
        lock (_gate)
        {
            _config = null;
            _ = GetOrLoadInternal();
        }
    }

    private AppConfig GetOrLoadInternal()
    {
        if (_config != null)
            return _config;

        if (!File.Exists(_configPath))
        {
            _config = new AppConfig { ConfigVersion = AppConfig.CurrentVersion };
            SaveInternal(_config);
            return _config;
        }

        try
        {
            _config = ReadConfigWithRetry(_configPath);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // 只有内容无法解析才算损坏。文件被占用、无权限等 IO 错误会抛给调用方，
            // 绝不能拿备份或默认值覆盖一份完好的配置。
            Log.Error(ex, "Failed to read config, backing up corrupt file and trying backup");
            BackupCorruptConfig();
            if (TryReadBackup(out var backup))
            {
                _config = backup;
                SaveInternal(_config);
                Log.Information("Configuration restored from backup");
            }
            else
            {
                _config = new AppConfig { ConfigVersion = AppConfig.CurrentVersion };
                return _config;
            }
        }

        var changed = false;
        if (_config.ConfigVersion < AppConfig.CurrentVersion)
            BackupBeforeMigration(_config.ConfigVersion);
        if (_config.ConfigVersion < 2)
        {
            MigrateToV2(_config);
            changed = true;
        }
        if (_config.ConfigVersion < 3)
        {
            MigrateToV3(_config);
            changed = true;
        }
        if (_config.ConfigVersion < 4)
        {
            MigrateToV4(_config);
            changed = true;
        }

        changed |= MigrateEncryptedSecrets(_config);
        if (changed)
            SaveInternal(_config);
        return _config;
    }

    /// <summary>读取时遇到瞬时占用（杀毒软件、同步盘）重试几次。</summary>
    private AppConfig ReadConfigWithRetry(string path)
    {
        const int attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return ReadConfig(path);
            }
            catch (IOException ex) when (attempt < attempts && ex is not FileNotFoundException and not DirectoryNotFoundException)
            {
                Log.Warning(ex, "Config file busy, retrying read attempt={Attempt}", attempt);
                System.Threading.Thread.Sleep(100 * attempt);
            }
        }
    }

    private static AppConfig ReadConfig(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions)
            ?? throw new JsonException("配置文件内容为空。");
    }

    private bool TryReadBackup(out AppConfig config)
    {
        config = null!;
        var backupPath = _configPath + ".bak";
        if (!File.Exists(backupPath))
            return false;

        try
        {
            config = ReadConfig(backupPath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read configuration backup");
            return false;
        }
    }

    private void SaveInternal(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        var tempPath = _configPath + ".tmp";
        File.WriteAllText(tempPath, json);

        if (File.Exists(_configPath))
            File.Replace(tempPath, _configPath, _configPath + ".bak");
        else
            File.Move(tempPath, _configPath);
    }

    private void BackupCorruptConfig()
    {
        try
        {
            if (File.Exists(_configPath))
                File.Copy(_configPath, _configPath + ".corrupt", overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to back up corrupt config");
        }
    }

    private void BackupBeforeMigration(int fromVersion)
    {
        try
        {
            var backupPath = $"{_configPath}.v{Math.Max(1, fromVersion)}.bak";
            if (File.Exists(_configPath) && !File.Exists(backupPath))
                File.Copy(_configPath, backupPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to back up config before migration");
        }
    }

    /// <summary>
    /// v1 → v2：OCR 页与翻译页各存一份的腾讯云/AI 账号合并到 accounts；新增截图翻译识别引擎；
    /// 过时的模型名替换；一次性的快捷键调整。只执行一次。
    /// </summary>
    internal static void MigrateToV2(AppConfig config)
    {
        var ocr = config.Ocr;
        var translation = config.Translation;
        var accounts = config.Accounts;
        var legacyTencent = accounts.LegacyTencent ??= new TencentAccountConfig();

        // 保留 v1 各用途的原账号，避免合并后遗失不同的凭据。
        accounts.TencentOcr.SecretIdEncrypted = ocr.LegacyTencentSecretIdEncrypted;
        accounts.TencentOcr.SecretKeyEncrypted = ocr.LegacyTencentSecretKeyEncrypted;
        accounts.TencentTranslation.SecretIdEncrypted = translation.LegacyTencentSecretIdEncrypted;
        accounts.TencentTranslation.SecretKeyEncrypted = translation.LegacyTencentSecretKeyEncrypted;

        // 腾讯云：翻译页优先，为空时取 OCR 页。
        var useTranslationTencent = !string.IsNullOrEmpty(translation.LegacyTencentSecretIdEncrypted) &&
                                    !string.IsNullOrEmpty(translation.LegacyTencentSecretKeyEncrypted);
        var useOcrTencent = !useTranslationTencent &&
                            !string.IsNullOrEmpty(ocr.LegacyTencentSecretIdEncrypted) &&
                            !string.IsNullOrEmpty(ocr.LegacyTencentSecretKeyEncrypted);
        if (useTranslationTencent)
        {
            legacyTencent.SecretIdEncrypted = translation.LegacyTencentSecretIdEncrypted;
            legacyTencent.SecretKeyEncrypted = translation.LegacyTencentSecretKeyEncrypted;
            if (!string.IsNullOrEmpty(ocr.LegacyTencentSecretIdEncrypted) &&
                ocr.LegacyTencentSecretIdEncrypted != translation.LegacyTencentSecretIdEncrypted)
            {
                Log.Information("Config migration: preserved separate OCR and translation Tencent credentials");
            }
        }
        else if (useOcrTencent)
        {
            legacyTencent.SecretIdEncrypted = ocr.LegacyTencentSecretIdEncrypted;
            legacyTencent.SecretKeyEncrypted = ocr.LegacyTencentSecretKeyEncrypted;
        }

        // AI 文本模型 ← 翻译页；AI 视觉模型 ← OCR 页。
        accounts.AiText.Platform = translation.LegacyAiPlatform ?? accounts.AiText.Platform;
        accounts.AiText.ApiUrlEncrypted = translation.LegacyAiApiUrlEncrypted;
        accounts.AiText.ApiKeyEncrypted = translation.LegacyAiApiKeyEncrypted;
        accounts.AiText.Model = MigrateModelName(translation.LegacyAiModel, translation.LegacyAiApiKeyEncrypted) ?? accounts.AiText.Model;

        var vision = accounts.AiVision;
        // 没填 Key 的 AI OCR 配置视为未使用：视觉模型沿用文本模型账号，模型名也跟随文本模型。
        var ocrHasOwnAccount = !string.IsNullOrEmpty(ocr.LegacyAiApiKeyEncrypted);
        var visionModel = MigrateModelName(ocr.LegacyAiModel, ocr.LegacyAiApiKeyEncrypted)
                          ?? (ocrHasOwnAccount ? vision.Model : accounts.AiText.Model);
        var sameAsText = !ocrHasOwnAccount ||
                         (ocr.LegacyAiApiUrlEncrypted == translation.LegacyAiApiUrlEncrypted &&
                          ocr.LegacyAiApiKeyEncrypted == translation.LegacyAiApiKeyEncrypted);
        vision.LegacySameAccountAsText = sameAsText;
        vision.Model = visionModel;
        vision.Platform = ocr.LegacyAiPlatform ?? vision.Platform;
        vision.ApiUrlEncrypted = ocr.LegacyAiApiUrlEncrypted;
        vision.ApiKeyEncrypted = ocr.LegacyAiApiKeyEncrypted;

        // 截图翻译识别引擎：只有腾讯云和本地能返回文字位置。
        translation.LegacyScreenshotOcr = ocr.Provider == OcrProvider.Tencent
            ? ScreenshotOcrEngine.Tencent
            : ScreenshotOcrEngine.WindowsLocal;

        // 一次性快捷键调整（旧版默认 Alt+4 设置 / Alt+5 传输 → 互换）。
        if (string.Equals(config.Hotkeys.Settings, "Alt+4", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(config.Hotkeys.LanTransfer, "Alt+5", StringComparison.OrdinalIgnoreCase))
        {
            config.Hotkeys.Settings = "Alt+5";
            config.Hotkeys.LanTransfer = "Alt+4";
        }

        ClearLegacy(ocr);
        ClearLegacy(translation);
        config.ConfigVersion = 2;
    }

    /// <summary>v2 → v3：账号按用途拆分，保留显式 AI 关联；旧本地截图识别等待用户明确改为跟随。</summary>
    internal static void MigrateToV3(AppConfig config)
    {
        var accounts = config.Accounts;
        if (accounts.LegacyTencent is { } legacy)
        {
            CopyTencentIfEmpty(legacy, accounts.TencentOcr);
            CopyTencentIfEmpty(legacy, accounts.TencentTranslation);
        }
        accounts.LegacyTencent = null;

        // v2 缺省为 true；只有明确 false 才是独立视觉账号。
        if (accounts.AiVision.LegacySameAccountAsText != false)
            accounts.AiVision.ReuseFrom = AccountReuseSource.Translation;
        accounts.AiVision.LegacySameAccountAsText = null;

        config.Translation.ScreenshotOcrMigrationPending =
            config.Ocr.Provider == OcrProvider.Tencent &&
            config.Translation.LegacyScreenshotOcr != ScreenshotOcrEngine.Tencent;
        config.Translation.LegacyScreenshotOcr = null;
        // MaxImageSizeKB 已从模型移除，旧 JSON 字段在读取时忽略，不影响内部图片上限。
        config.ConfigVersion = 3;
    }

    /// <summary>v3 → v4：只按条数保留剪贴板记录；旧 retentionDays 字段在读取时忽略，重写后移除。</summary>
    internal static void MigrateToV4(AppConfig config)
    {
        config.ConfigVersion = 4;
    }

    private static void CopyTencentIfEmpty(TencentAccountConfig source, TencentAccountConfig target)
    {
        if (string.IsNullOrEmpty(target.SecretIdEncrypted) && string.IsNullOrEmpty(target.SecretKeyEncrypted))
        {
            target.SecretIdEncrypted = source.SecretIdEncrypted;
            target.SecretKeyEncrypted = source.SecretKeyEncrypted;
        }
    }

    /// <summary>已下线的模型名换成当前名称；旧版默认 gpt-4o-mini 只在用户没填 Key 时替换。</summary>
    internal static string? MigrateModelName(string? model, string? apiKeyEncrypted)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;

        var trimmed = model.Trim();
        if (trimmed.Equals("deepseek-chat", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("deepseek-reasoner", StringComparison.OrdinalIgnoreCase))
            return "deepseek-flash";
        if (trimmed.Equals("gpt-4o-mini", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(apiKeyEncrypted))
            return null;
        return trimmed;
    }

    private static void ClearLegacy(OcrConfig ocr)
    {
        ocr.LegacyAiPlatform = null;
        ocr.LegacyTencentSecretIdEncrypted = null;
        ocr.LegacyTencentSecretKeyEncrypted = null;
        ocr.LegacyAiApiUrlEncrypted = null;
        ocr.LegacyAiApiKeyEncrypted = null;
        ocr.LegacyAiModel = null;
    }

    private static void ClearLegacy(TranslationConfig translation)
    {
        translation.LegacyAiPlatform = null;
        translation.LegacyTencentSecretIdEncrypted = null;
        translation.LegacyTencentSecretKeyEncrypted = null;
        translation.LegacyAiApiUrlEncrypted = null;
        translation.LegacyAiApiKeyEncrypted = null;
        translation.LegacyAiModel = null;
    }

    private bool MigrateEncryptedSecrets(AppConfig config)
    {
        var accounts = config.Accounts;
        var changed = false;
        changed |= ReencryptIfLegacy(accounts.TencentOcr.SecretIdEncrypted, value => accounts.TencentOcr.SecretIdEncrypted = value);
        changed |= ReencryptIfLegacy(accounts.TencentOcr.SecretKeyEncrypted, value => accounts.TencentOcr.SecretKeyEncrypted = value);
        changed |= ReencryptIfLegacy(accounts.TencentTranslation.SecretIdEncrypted, value => accounts.TencentTranslation.SecretIdEncrypted = value);
        changed |= ReencryptIfLegacy(accounts.TencentTranslation.SecretKeyEncrypted, value => accounts.TencentTranslation.SecretKeyEncrypted = value);
        changed |= ReencryptIfLegacy(accounts.AiText.ApiUrlEncrypted, value => accounts.AiText.ApiUrlEncrypted = value);
        changed |= ReencryptIfLegacy(accounts.AiText.ApiKeyEncrypted, value => accounts.AiText.ApiKeyEncrypted = value);
        changed |= ReencryptIfLegacy(accounts.AiVision.ApiUrlEncrypted, value => accounts.AiVision.ApiUrlEncrypted = value);
        changed |= ReencryptIfLegacy(accounts.AiVision.ApiKeyEncrypted, value => accounts.AiVision.ApiKeyEncrypted = value);
        return changed;
    }

    private bool ReencryptIfLegacy(string? encryptedText, Action<string> setValue)
    {
        if (string.IsNullOrWhiteSpace(encryptedText) || _isPortableEncrypted(encryptedText))
            return false;

        var plainText = _decrypt(encryptedText);
        if (string.IsNullOrEmpty(plainText))
            return false;

        setValue(_encrypt(plainText));
        return true;
    }

    private static AppConfig Clone(AppConfig config)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(config, CloneOptions);
        return JsonSerializer.Deserialize<AppConfig>(json, CloneOptions) ?? new AppConfig();
    }
}
