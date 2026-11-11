using System.IO;
using System.Text.Json;
using STool.Core;
using STool.Models;
using Xunit;

namespace STool.Tests;

public class ConfigManagerTests
{
    [Fact]
    public void Get_FirstRun_CreatesDefaultConfig()
    {
        using var fixture = new ConfigFixture();

        var config = fixture.Manager.Get();

        Assert.Equal("Alt+1", config.Hotkeys.Screenshot);
        Assert.True(File.Exists(fixture.ConfigPath));
    }

    [Fact]
    public void Get_ReturnsIsolatedSnapshot()
    {
        using var fixture = new ConfigFixture();
        var first = fixture.Manager.Get();

        first.HideTrayIcon = true;

        Assert.False(fixture.Manager.Get().HideTrayIcon);
    }

    [Fact]
    public void Update_AtomicallyChangesAndPersistsConfiguration()
    {
        using var fixture = new ConfigFixture();

        fixture.Manager.Update(config =>
        {
            config.HideTrayIcon = true;
            config.Hotkeys.Screenshot = "Ctrl+Shift+S";
        });
        var reloaded = fixture.CreateManager().Get();

        Assert.True(reloaded.HideTrayIcon);
        Assert.Equal("Ctrl+Shift+S", reloaded.Hotkeys.Screenshot);
    }

    [Fact]
    public void ConcurrentUpdates_ToDifferentSections_DoNotOverwriteEachOther()
    {
        using var fixture = new ConfigFixture();

        Parallel.Invoke(
            () => fixture.Manager.Update(config => config.HideTrayIcon = true),
            () => fixture.Manager.Update(config => config.Hotkeys.Screenshot = "Ctrl+Shift+S"),
            () => fixture.Manager.Update(config => config.LanTransfer.Port = 19001));

        var config = fixture.Manager.Get();
        Assert.True(config.HideTrayIcon);
        Assert.Equal("Ctrl+Shift+S", config.Hotkeys.Screenshot);
        Assert.Equal(19001, config.LanTransfer.Port);
    }

    [Fact]
    public void Save_WhenWriteFails_KeepsCachedAndPersistedConfiguration()
    {
        using var fixture = new ConfigFixture();
        fixture.Manager.Update(config => config.HideTrayIcon = true);
        using var lockedTemp = new FileStream(
            fixture.ConfigPath + ".tmp",
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None);

        Assert.ThrowsAny<IOException>(() =>
            fixture.Manager.Update(config => config.HideTrayIcon = false));

        Assert.True(fixture.Manager.Get().HideTrayIcon);
        Assert.True(fixture.CreateManager().Get().HideTrayIcon);
    }

    [Fact]
    public void Reload_CorruptPrimary_RestoresValidBackup()
    {
        using var fixture = new ConfigFixture();
        fixture.Manager.Update(config => config.HideTrayIcon = true);
        fixture.Manager.Update(config => config.HideTrayIcon = false);
        File.WriteAllText(fixture.ConfigPath, "{broken");

        var config = fixture.CreateManager().Get();

        Assert.True(config.HideTrayIcon);
        Assert.True(File.Exists(fixture.ConfigPath + ".corrupt"));
        Assert.DoesNotContain("broken", File.ReadAllText(fixture.ConfigPath));
    }

    [Fact]
    public void Get_CorruptPrimaryAndBackup_UsesDefaultsWithoutOverwritingPrimary()
    {
        using var fixture = new ConfigFixture();
        File.WriteAllText(fixture.ConfigPath, "broken-primary");
        File.WriteAllText(fixture.ConfigPath + ".bak", "broken-backup");

        var config = fixture.CreateManager().Get();

        Assert.Equal("Alt+1", config.Hotkeys.Screenshot);
        Assert.Equal("broken-primary", File.ReadAllText(fixture.ConfigPath));
        Assert.True(File.Exists(fixture.ConfigPath + ".corrupt"));
    }

    [Fact]
    public void Get_PrimaryLocked_ThrowsWithoutOverwritingPrimaryOrUsingBackup()
    {
        using var fixture = new ConfigFixture();
        fixture.Manager.Update(config => config.HideTrayIcon = true);
        fixture.Manager.Update(config => config.HideTrayIcon = false);
        var before = File.ReadAllText(fixture.ConfigPath);

        using (new FileStream(fixture.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => fixture.CreateManager().Get());
        }

        Assert.Equal(before, File.ReadAllText(fixture.ConfigPath));
        Assert.False(File.Exists(fixture.ConfigPath + ".corrupt"));
    }

    [Fact]
    public void Get_MigratesLegacyDefaultHotkeys()
    {
        using var fixture = new ConfigFixture();
        fixture.Write(new AppConfig
        {
            Hotkeys = new HotkeyConfig { Settings = "Alt+4", LanTransfer = "Alt+5" }
        });

        var config = fixture.CreateManager().Get();

        Assert.Equal("Alt+5", config.Hotkeys.Settings);
        Assert.Equal("Alt+4", config.Hotkeys.LanTransfer);
    }

    [Fact]
    public void Get_MigratesLegacyEncryptedSecrets()
    {
        using var fixture = new ConfigFixture(
            decrypt: value => value == "legacy-secret" ? "plain-secret" : string.Empty,
            encrypt: value => "v2:test:" + value,
            isPortable: value => value.StartsWith("v2:", StringComparison.Ordinal));
        fixture.Write(new AppConfig
        {
            Ocr = new OcrConfig
            {
                LegacyTencentSecretIdEncrypted = "legacy-secret",
                LegacyTencentSecretKeyEncrypted = "legacy-secret"
            }
        });

        var config = fixture.CreateManager().Get();

        Assert.Equal("v2:test:plain-secret", config.Accounts.Tencent.SecretIdEncrypted);
    }

    [Fact]
    public void MigrateToV2_MergesAccountsPreferringTranslation()
    {
        var config = new AppConfig
        {
            Ocr = new OcrConfig
            {
                Provider = OcrProvider.Tencent,
                LegacyTencentSecretIdEncrypted = "ocr-id",
                LegacyTencentSecretKeyEncrypted = "ocr-key",
                LegacyAiApiUrlEncrypted = "url",
                LegacyAiApiKeyEncrypted = "key",
                LegacyAiModel = "vision-model"
            },
            Translation = new TranslationConfig
            {
                LegacyTencentSecretIdEncrypted = "tr-id",
                LegacyTencentSecretKeyEncrypted = "tr-key",
                LegacyAiPlatform = AiPlatform.DeepSeek,
                LegacyAiApiUrlEncrypted = "url",
                LegacyAiApiKeyEncrypted = "key",
                LegacyAiModel = "deepseek-chat"
            }
        };

        ConfigManager.MigrateToV2(config);

        Assert.Equal(2, config.ConfigVersion);
        Assert.Equal("tr-id", config.Accounts.Tencent.SecretIdEncrypted);
        Assert.Equal("tr-key", config.Accounts.Tencent.SecretKeyEncrypted);
        Assert.Equal(AiPlatform.DeepSeek, config.Accounts.AiText.Platform);
        Assert.Equal("deepseek-flash", config.Accounts.AiText.Model);
        Assert.True(config.Accounts.AiVision.SameAccountAsText);
        Assert.Equal("vision-model", config.Accounts.AiVision.Model);
        Assert.Equal(ScreenshotOcrEngine.Tencent, config.Translation.ScreenshotOcr);
        Assert.Null(config.Ocr.LegacyTencentSecretIdEncrypted);
        Assert.Null(config.Translation.LegacyAiApiKeyEncrypted);
    }

    [Fact]
    public void MigrateToV2_UsesOcrTencentWhenTranslationEmpty_AndKeepsSeparateVisionAccount()
    {
        var config = new AppConfig
        {
            Ocr = new OcrConfig
            {
                LegacyTencentSecretIdEncrypted = "ocr-id",
                LegacyTencentSecretKeyEncrypted = "ocr-key",
                LegacyAiPlatform = AiPlatform.GoogleAiStudio,
                LegacyAiApiUrlEncrypted = "vision-url",
                LegacyAiApiKeyEncrypted = "vision-key",
                LegacyAiModel = "gemini-flash-latest"
            },
            Translation = new TranslationConfig
            {
                LegacyAiApiUrlEncrypted = "text-url",
                LegacyAiApiKeyEncrypted = "text-key",
                LegacyAiModel = "my-model"
            }
        };

        ConfigManager.MigrateToV2(config);

        Assert.Equal("ocr-id", config.Accounts.Tencent.SecretIdEncrypted);
        Assert.False(config.Accounts.AiVision.SameAccountAsText);
        Assert.Equal("vision-url", config.Accounts.AiVision.ApiUrlEncrypted);
        Assert.Equal(AiPlatform.GoogleAiStudio, config.Accounts.AiVision.Platform);
        Assert.Equal("my-model", config.Accounts.AiText.Model);
        Assert.Equal(ScreenshotOcrEngine.WindowsLocal, config.Translation.ScreenshotOcr);
    }

    [Fact]
    public void MigrateToV2_OcrAiWithoutKey_FollowsTextModel()
    {
        var config = new AppConfig
        {
            Ocr = new OcrConfig { LegacyAiApiUrlEncrypted = "unused-url", LegacyAiModel = "gpt-4o-mini" },
            Translation = new TranslationConfig
            {
                LegacyAiPlatform = AiPlatform.DeepSeek,
                LegacyAiApiUrlEncrypted = "url",
                LegacyAiApiKeyEncrypted = "key",
                LegacyAiModel = "deepseek-flash"
            }
        };

        ConfigManager.MigrateToV2(config);

        Assert.True(config.Accounts.AiVision.SameAccountAsText);
        Assert.Equal("deepseek-flash", config.Accounts.AiVision.Model);
        Assert.True(config.Accounts.ResolveVision().IsConfigured);
    }

    [Theory]
    [InlineData("deepseek-reasoner", "k", "deepseek-flash")]
    [InlineData("gpt-4o-mini", null, null)]
    [InlineData("gpt-4o-mini", "k", "gpt-4o-mini")]
    [InlineData("custom", null, "custom")]
    [InlineData("  ", null, null)]
    public void MigrateModelName_ReplacesRetiredModels(string model, string? key, string? expected)
    {
        Assert.Equal(expected, ConfigManager.MigrateModelName(model, key));
    }

    [Fact]
    public void Get_V2Config_DoesNotRerunHotkeyMigration()
    {
        using var fixture = new ConfigFixture();
        fixture.Write(new AppConfig
        {
            ConfigVersion = AppConfig.CurrentVersion,
            Hotkeys = new HotkeyConfig { Settings = "Alt+4", LanTransfer = "Alt+5" }
        });

        var config = fixture.CreateManager().Get();

        Assert.Equal("Alt+4", config.Hotkeys.Settings);
        Assert.Equal("Alt+5", config.Hotkeys.LanTransfer);
    }

    [Fact]
    public void Get_V1Config_WritesBackupAndDropsLegacyFields()
    {
        using var fixture = new ConfigFixture();
        File.WriteAllText(fixture.ConfigPath, """
            {"ocr":{"provider":"Tencent","tencentSecretIdEncrypted":"v2:a","tencentSecretKeyEncrypted":"v2:b"},
             "translation":{"provider":"Google"}}
            """);

        var config = fixture.CreateManager().Get();
        var saved = File.ReadAllText(fixture.ConfigPath);

        Assert.True(File.Exists(fixture.ConfigPath + ".v1.bak"));
        Assert.Equal("v2:a", config.Accounts.Tencent.SecretIdEncrypted);
        Assert.DoesNotContain("tencentSecretIdEncrypted\":\"v2:a\",\"tencentSecretKey", saved.Replace(" ", ""));
        Assert.Contains("\"configVersion\": 2", saved);
    }

    private sealed class ConfigFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "SToolTests", Guid.NewGuid().ToString("N"));
        private readonly Func<string, string> _decrypt;
        private readonly Func<string, string> _encrypt;
        private readonly Func<string, bool> _isPortable;

        public ConfigFixture(
            Func<string, string>? decrypt = null,
            Func<string, string>? encrypt = null,
            Func<string, bool>? isPortable = null)
        {
            Directory.CreateDirectory(_root);
            ConfigPath = Path.Combine(_root, "config.json");
            _decrypt = decrypt ?? (_ => string.Empty);
            _encrypt = encrypt ?? (value => "v2:" + value);
            _isPortable = isPortable ?? (value => value.StartsWith("v2:", StringComparison.Ordinal));
            Manager = CreateManager();
        }

        public string ConfigPath { get; }
        public ConfigManager Manager { get; }

        public ConfigManager CreateManager() => new(ConfigPath, _decrypt, _encrypt, _isPortable);

        public void Write(AppConfig config)
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }
    }
}
