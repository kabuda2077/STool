using System;
using System.IO;
using System.Text.Json;
using Serilog;
using STool.Models;

namespace STool.Core;

public class ConfigManager
{
    private readonly string _configPath;
    private readonly object _gate = new();
    private AppConfig? _config;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ConfigManager()
    {
        AppPaths.EnsureStandardDirectories();
        _configPath = AppPaths.ConfigPath;
    }

    public AppConfig Get()
    {
        lock (_gate)
        {
            if (_config != null)
                return _config;

            if (File.Exists(_configPath))
            {
                try
                {
                    var json = File.ReadAllText(_configPath);
                    _config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
                    if (MigrateEncryptedSecrets(_config))
                    {
                        SaveInternal(_config);
                    }
                }
                catch (Exception ex)
                {
                    // 不能静默丢弃:损坏的配置一旦被默认值覆盖保存,会丢失用户密钥。
                    // 先备份损坏文件留待排查,再回退默认值,且不主动覆盖原文件。
                    Log.Error(ex, "Failed to read config, backing up corrupt file and using defaults");
                    BackupCorruptConfig();
                    _config = new AppConfig();
                }
            }
            else
            {
                _config = new AppConfig();
                SaveInternal(_config);
            }

            return _config;
        }
    }

    public void Save(AppConfig config)
    {
        lock (_gate)
        {
            _config = config;
            SaveInternal(config);
        }
    }

    public void Reload()
    {
        lock (_gate)
        {
            _config = null;
        }
        Get();
    }

    /// <summary>
    /// 原子写入:先写临时文件再替换,避免写一半崩溃导致 config.json 损坏、丢失加密密钥。
    /// 调用方需持有 _gate 锁。
    /// </summary>
    private void SaveInternal(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        var tempPath = _configPath + ".tmp";

        File.WriteAllText(tempPath, json);

        if (File.Exists(_configPath))
        {
            // File.Replace 原子替换,并保留一个 .bak 以便回滚
            File.Replace(tempPath, _configPath, _configPath + ".bak");
        }
        else
        {
            File.Move(tempPath, _configPath);
        }
    }

    private void BackupCorruptConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                File.Copy(_configPath, _configPath + ".corrupt", overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to back up corrupt config");
        }
    }

    private static bool MigrateEncryptedSecrets(AppConfig config)
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

    private static bool ReencryptIfLegacy(string? encryptedText, Action<string> setValue)
    {
        if (string.IsNullOrWhiteSpace(encryptedText) || SecureStorage.IsPortableEncrypted(encryptedText))
        {
            return false;
        }

        var plainText = SecureStorage.Decrypt(encryptedText);
        if (string.IsNullOrEmpty(plainText))
        {
            return false;
        }

        setValue(SecureStorage.Encrypt(plainText));
        return true;
    }
}
