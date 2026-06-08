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
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "STool"
        );
        Directory.CreateDirectory(appDataPath);
        _configPath = Path.Combine(appDataPath, "config.json");
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
}
