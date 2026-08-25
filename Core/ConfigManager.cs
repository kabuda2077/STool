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

    public AppConfig GetSnapshot() => Get();

    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_gate)
        {
            var next = Clone(config);
            SaveInternal(next);
            _config = next;
        }
    }

    public AppConfig Update(Action<AppConfig> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            var next = Clone(GetOrLoadInternal());
            update(next);
            SaveInternal(next);
            _config = next;
            return Clone(next);
        }
    }

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
            _config = new AppConfig();
            SaveInternal(_config);
            return _config;
        }

        try
        {
            _config = ReadConfig(_configPath);
        }
        catch (Exception ex)
        {
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
                _config = new AppConfig();
                return _config;
            }
        }

        var changed = MigrateEncryptedSecrets(_config);
        changed |= MigrateDefaultHotkeys(_config);
        if (changed)
            SaveInternal(_config);
        return _config;
    }

    private AppConfig ReadConfig(string path)
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

    private bool MigrateEncryptedSecrets(AppConfig config)
    {
        var changed = false;
        changed |= ReencryptIfLegacy(config.Ocr.TencentSecretIdEncrypted, value => config.Ocr.TencentSecretIdEncrypted = value);
        changed |= ReencryptIfLegacy(config.Ocr.TencentSecretKeyEncrypted, value => config.Ocr.TencentSecretKeyEncrypted = value);
        changed |= ReencryptIfLegacy(config.Ocr.AiApiUrlEncrypted, value => config.Ocr.AiApiUrlEncrypted = value);
        changed |= ReencryptIfLegacy(config.Ocr.AiApiKeyEncrypted, value => config.Ocr.AiApiKeyEncrypted = value);
        changed |= ReencryptIfLegacy(config.Translation.TencentSecretIdEncrypted, value => config.Translation.TencentSecretIdEncrypted = value);
        changed |= ReencryptIfLegacy(config.Translation.TencentSecretKeyEncrypted, value => config.Translation.TencentSecretKeyEncrypted = value);
        changed |= ReencryptIfLegacy(config.Translation.AiApiUrlEncrypted, value => config.Translation.AiApiUrlEncrypted = value);
        changed |= ReencryptIfLegacy(config.Translation.AiApiKeyEncrypted, value => config.Translation.AiApiKeyEncrypted = value);
        return changed;
    }

    internal static bool MigrateDefaultHotkeys(AppConfig config)
    {
        if (!string.Equals(config.Hotkeys.Settings, "Alt+4", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(config.Hotkeys.LanTransfer, "Alt+5", StringComparison.OrdinalIgnoreCase))
            return false;

        config.Hotkeys.Settings = "Alt+5";
        config.Hotkeys.LanTransfer = "Alt+4";
        return true;
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
        var json = JsonSerializer.Serialize(config, JsonOptions);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
    }
}
