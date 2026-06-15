using System;
using System.IO;
using System.Text.Json;
using STool.Models;

namespace STool.Core;

public class ConfigManager
{
    private readonly string _configPath;
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
                    Save(_config);
                }
            }
            catch
            {
                _config = new AppConfig();
            }
        }
        else
        {
            _config = new AppConfig();
            Save(_config);
        }

        return _config;
    }

    public void Save(AppConfig config)
    {
        _config = config;
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(_configPath, json);
    }

    public void Reload()
    {
        _config = null;
        Get();
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
