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
        Assert.Equal(1000, config.Clipboard.MaxEntries);
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

        Assert.Equal("v2:test:plain-secret", config.Accounts.TencentOcr.SecretIdEncrypted);
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
        Assert.Equal("tr-id", config.Accounts.LegacyTencent!.SecretIdEncrypted);
        Assert.Equal("tr-key", config.Accounts.LegacyTencent!.SecretKeyEncrypted);
        Assert.Equal(AiPlatform.DeepSeek, config.Accounts.AiText.Platform);
        Assert.Equal("deepseek-flash", config.Accounts.AiText.Model);
        Assert.True(config.Accounts.AiVision.LegacySameAccountAsText);
        Assert.Equal("vision-model", config.Accounts.AiVision.Model);
        Assert.Equal(ScreenshotOcrEngine.Tencent, config.Translation.LegacyScreenshotOcr);
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

        Assert.Equal("ocr-id", config.Accounts.TencentOcr.SecretIdEncrypted);
        Assert.False(config.Accounts.AiVision.LegacySameAccountAsText);
        Assert.Equal("vision-url", config.Accounts.AiVision.ApiUrlEncrypted);
        Assert.Equal(AiPlatform.GoogleAiStudio, config.Accounts.AiVision.Platform);
        Assert.Equal("my-model", config.Accounts.AiText.Model);
        Assert.Equal(ScreenshotOcrEngine.WindowsLocal, config.Translation.LegacyScreenshotOcr);
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
                LegacyAiApiUrlEncrypted = "https://api.deepseek.com/chat/completions",
                LegacyAiApiKeyEncrypted = "key",
                LegacyAiModel = "deepseek-flash"
            }
        };

        ConfigManager.MigrateToV2(config);

        Assert.True(config.Accounts.AiVision.LegacySameAccountAsText);
        Assert.Equal("deepseek-flash", config.Accounts.AiVision.Model);
        ConfigManager.MigrateToV3(config);
        Assert.True(config.Accounts.ResolveVision(value => value).IsConfigured);
    }

    [Theory]
    [InlineData(AiPlatform.Custom, "https://gateway.example/v1")]
    [InlineData(AiPlatform.OpenAI, "https://gateway.example/v1/chat/completions")]
    [InlineData(AiPlatform.Custom, "http://gateway.example/api/openai/chat/completions?channel=one")]
    public void Get_V2SharedProxy_RemainsConfiguredAfterMigrationAndReload(AiPlatform platform, string endpoint)
    {
        using var fixture = new ConfigFixture(decrypt: value => value, isPortable: _ => true);
        fixture.Write(new AppConfig
        {
            ConfigVersion = 2,
            Accounts = new AccountsConfig
            {
                AiText = new AiAccountConfig
                {
                    Platform = platform, ApiUrlEncrypted = endpoint, ApiKeyEncrypted = "source-key", Model = "text-model"
                },
                AiVision = new AiVisionAccountConfig
                {
                    LegacySameAccountAsText = true, ApiUrlEncrypted = "hidden-draft", ApiKeyEncrypted = "hidden-key", Model = "vision-model"
                }
            }
        });

        foreach (var config in new[] { fixture.CreateManager().Get(), fixture.CreateManager().Get() })
        {
            Assert.Equal(AppConfig.CurrentVersion, config.ConfigVersion);
            Assert.Equal(AccountReuseSource.Translation, config.Accounts.AiVision.ReuseFrom);
            var effective = config.Accounts.ResolveVision(value => value);
            Assert.True(effective.IsConfigured);
            Assert.Equal(platform, effective.Platform);
            Assert.Equal(endpoint, effective.ApiUrlEncrypted);
            Assert.Equal("source-key", effective.ApiKeyEncrypted);
            Assert.Equal("vision-model", effective.Model);
            Assert.Equal("text-model", config.Accounts.AiText.Model);
            Assert.Equal("hidden-draft", config.Accounts.AiVision.ApiUrlEncrypted);
            Assert.Equal("hidden-key", config.Accounts.AiVision.ApiKeyEncrypted);
        }
        Assert.True(File.Exists(fixture.ConfigPath + ".v2.bak"));
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
        using var savedDocument = JsonDocument.Parse(saved);

        Assert.True(File.Exists(fixture.ConfigPath + ".v1.bak"));
        Assert.Equal("v2:a", config.Accounts.TencentOcr.SecretIdEncrypted);
        Assert.DoesNotContain("tencentSecretIdEncrypted\":\"v2:a\",\"tencentSecretKey", saved.Replace(" ", ""));
        Assert.Equal(AppConfig.CurrentVersion, savedDocument.RootElement.GetProperty("configVersion").GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public void Get_V3Config_DropsRetentionDaysWithoutResettingLimitsOrAccounts(int retentionDays)
    {
        using var fixture = new ConfigFixture();
        var original = $$$"""
            {"configVersion":3,
             "clipboard":{"retentionDays":{{{retentionDays}}},"maxEntries":237,"maxTextLength":12000,"excludedApps":["Private.exe"]},
             "accounts":{
                "tencentOcr":{"secretIdEncrypted":"v2:ocr-id","secretKeyEncrypted":"v2:ocr-key"},
                "tencentTranslation":{"secretIdEncrypted":"v2:text-id","secretKeyEncrypted":"v2:text-key"},
                "aiText":{"apiUrlEncrypted":"v2:text-url","apiKeyEncrypted":"v2:text-api-key","model":"Text-ID"},
                "aiVision":{"reuseFrom":"Translation","apiUrlEncrypted":"v2:vision-draft","apiKeyEncrypted":"v2:vision-key","model":"Vision-ID"}},
             "translation":{"screenshotOcrMigrationPending":true}}
            """;
        File.WriteAllText(fixture.ConfigPath, original);

        foreach (var config in new[] { fixture.CreateManager().Get(), fixture.CreateManager().Get() })
        {
            Assert.Equal(AppConfig.CurrentVersion, config.ConfigVersion);
            Assert.Equal(237, config.Clipboard.MaxEntries);
            Assert.Equal(12000, config.Clipboard.MaxTextLength);
            Assert.Equal(new[] { "Private.exe" }, config.Clipboard.ExcludedApps);
            Assert.Equal("v2:ocr-id", config.Accounts.TencentOcr.SecretIdEncrypted);
            Assert.Equal("v2:ocr-key", config.Accounts.TencentOcr.SecretKeyEncrypted);
            Assert.Equal("v2:text-id", config.Accounts.TencentTranslation.SecretIdEncrypted);
            Assert.Equal("v2:text-key", config.Accounts.TencentTranslation.SecretKeyEncrypted);
            Assert.Equal("v2:text-url", config.Accounts.AiText.ApiUrlEncrypted);
            Assert.Equal("v2:text-api-key", config.Accounts.AiText.ApiKeyEncrypted);
            Assert.Equal("Text-ID", config.Accounts.AiText.Model);
            Assert.Equal(AccountReuseSource.Translation, config.Accounts.AiVision.ReuseFrom);
            Assert.Equal("v2:vision-draft", config.Accounts.AiVision.ApiUrlEncrypted);
            Assert.Equal("v2:vision-key", config.Accounts.AiVision.ApiKeyEncrypted);
            Assert.Equal("Vision-ID", config.Accounts.AiVision.Model);
            Assert.True(config.Translation.ScreenshotOcrMigrationPending);
        }

        Assert.Equal(original, File.ReadAllText(fixture.ConfigPath + ".v3.bak"));
        Assert.DoesNotContain("retentionDays", File.ReadAllText(fixture.ConfigPath));
        fixture.CreateManager().Update(config => config.HideTrayIcon = true);
        Assert.DoesNotContain("retentionDays", File.ReadAllText(fixture.ConfigPath));
    }

    [Fact]
    public void Get_FirstRun_AccountsAreIndependent()
    {
        using var fixture = new ConfigFixture();
        var accounts = fixture.Manager.Get().Accounts;

        Assert.Equal(AccountReuseSource.None, accounts.AiText.ReuseFrom);
        Assert.Equal(AccountReuseSource.None, accounts.AiVision.ReuseFrom);
        Assert.Equal(AccountReuseSource.None, accounts.TencentOcr.ReuseFrom);
        Assert.Equal(AccountReuseSource.None, accounts.TencentTranslation.ReuseFrom);
    }

    [Fact]
    public void Get_V2Config_PreservesSharingAndDrafts_AndLocalScreenshotPrivacy()
    {
        using var fixture = new ConfigFixture();
        File.WriteAllText(fixture.ConfigPath, """
            {"configVersion":2,"accounts":{
                "tencent":{"secretIdEncrypted":"v2:id","secretKeyEncrypted":"v2:key"},
                "aiText":{"platform":"DeepSeek","apiUrlEncrypted":"https://api.deepseek.com/chat/completions","apiKeyEncrypted":"text-key","model":"Text-ID"},
                "aiVision":{"sameAccountAsText":true,"platform":"OpenAI","apiUrlEncrypted":"old-draft","apiKeyEncrypted":"old-key","model":"Vision-ID"}},
             "ocr":{"provider":"Tencent"},"translation":{"screenshotOcr":"WindowsLocal"},
             "clipboard":{"maxImageSizeKB":1}}
            """);

        var config = fixture.CreateManager().Get();
        var effective = config.Accounts.ResolveVision(value => value);
        var saved = File.ReadAllText(fixture.ConfigPath);

        Assert.Equal(AppConfig.CurrentVersion, config.ConfigVersion);
        Assert.True(File.Exists(fixture.ConfigPath + ".v2.bak"));
        Assert.Equal("v2:id", config.Accounts.TencentOcr.SecretIdEncrypted);
        Assert.Equal("v2:key", config.Accounts.TencentTranslation.SecretKeyEncrypted);
        Assert.Equal(AccountReuseSource.None, config.Accounts.TencentOcr.ReuseFrom);
        Assert.Equal(AccountReuseSource.Translation, config.Accounts.AiVision.ReuseFrom);
        Assert.Equal(AiPlatform.DeepSeek, effective.Platform);
        Assert.Equal("Vision-ID", effective.Model);
        Assert.Equal("old-draft", config.Accounts.AiVision.ApiUrlEncrypted);
        Assert.Equal("old-key", config.Accounts.AiVision.ApiKeyEncrypted);
        Assert.True(config.Translation.ScreenshotOcrMigrationPending);
        Assert.DoesNotContain("sameAccountAsText", saved);
        Assert.DoesNotContain("\"tencent\":", saved);
        Assert.DoesNotContain("\"screenshotOcr\":", saved);
        Assert.DoesNotContain("maxImageSizeKB", saved);

        var manager = fixture.CreateManager();
        manager.Update(current => current.Translation.ScreenshotOcrMigrationPending = false);
        Assert.False(fixture.CreateManager().Get().Translation.ScreenshotOcrMigrationPending);
    }

    [Theory]
    [InlineData(OcrProvider.Tencent, ScreenshotOcrEngine.Tencent, false)]
    [InlineData(OcrProvider.Tencent, ScreenshotOcrEngine.WindowsLocal, true)]
    [InlineData(OcrProvider.WindowsLocal, ScreenshotOcrEngine.Tencent, false)]
    [InlineData(OcrProvider.AI, ScreenshotOcrEngine.WindowsLocal, false)]
    public void MigrateToV3_OnlyHoldsBackPreviouslyLocalTencentScreenshots(
        OcrProvider provider, ScreenshotOcrEngine previous, bool pending)
    {
        var config = new AppConfig
        {
            ConfigVersion = 2,
            Ocr = new OcrConfig { Provider = provider },
            Translation = new TranslationConfig { LegacyScreenshotOcr = previous },
            Accounts = new AccountsConfig { AiVision = new AiVisionAccountConfig { LegacySameAccountAsText = false } }
        };

        ConfigManager.MigrateToV3(config);

        Assert.Equal(pending, config.Translation.ScreenshotOcrMigrationPending);
        Assert.Equal(AccountReuseSource.None, config.Accounts.AiVision.ReuseFrom);
        Assert.Null(config.Translation.LegacyScreenshotOcr);
    }

    [Fact]
    public void Update_ChangingOcrProvider_ClearsMigrationHoldback()
    {
        using var fixture = new ConfigFixture();
        fixture.Manager.Update(config =>
        {
            config.Ocr.Provider = OcrProvider.Tencent;
        });
        fixture.Manager.Update(config => config.Translation.ScreenshotOcrMigrationPending = true);
        fixture.Manager.Update(config => config.HideTrayIcon = true);
        Assert.True(fixture.Manager.Get().Translation.ScreenshotOcrMigrationPending);

        fixture.Manager.Update(config => config.Ocr.Provider = OcrProvider.AI);

        Assert.False(fixture.CreateManager().Get().Translation.ScreenshotOcrMigrationPending);
    }

    [Fact]
    public void TencentReuse_IsExplicitBidirectionalAndKeepsDraftsWhenSourceClears()
    {
        var accounts = new AccountsConfig
        {
            TencentOcr = new TencentAccountConfig { SecretIdEncrypted = "ocr-id", SecretKeyEncrypted = "ocr-key" },
            TencentTranslation = new TencentAccountConfig { SecretIdEncrypted = "text-id", SecretKeyEncrypted = "text-key" }
        };
        Assert.Equal("ocr-id", accounts.ResolveTencentOcr().SecretIdEncrypted);
        Assert.True(accounts.TrySetTencentReuse(forOcr: true, reuse: true));
        Assert.Equal("text-id", accounts.ResolveTencentOcr().SecretIdEncrypted);
        Assert.False(accounts.TrySetTencentReuse(forOcr: false, reuse: true));

        accounts.TencentTranslation.SecretKeyEncrypted = null;
        Assert.False(accounts.ResolveTencentOcr().IsConfigured);
        Assert.Equal(AccountReuseSource.Translation, accounts.TencentOcr.ReuseFrom);
        Assert.Equal("ocr-key", accounts.TencentOcr.SecretKeyEncrypted);

        Assert.True(accounts.TrySetTencentReuse(forOcr: true, reuse: false));
        Assert.Equal("ocr-key", accounts.ResolveTencentOcr().SecretKeyEncrypted);
        Assert.True(accounts.TrySetTencentReuse(forOcr: false, reuse: true));
        Assert.Equal("ocr-id", accounts.ResolveTencentTranslation().SecretIdEncrypted);
    }

    [Fact]
    public void AiReuse_FollowsSourceAndKeepsIndependentModelAndHiddenDraft()
    {
        var accounts = CompatibleAiAccounts();
        accounts.AiText.Model = null; // A connection can be reused without borrowing its model.
        Assert.True(accounts.TrySetAiReuse(forOcr: true, reuse: true, value => value));
        Assert.Equal("vision-model", accounts.ResolveVision(value => value).Model);
        Assert.False(accounts.TrySetAiReuse(forOcr: false, reuse: true, value => value));

        accounts.AiText.Platform = AiPlatform.DeepSeek;
        accounts.AiText.ApiUrlEncrypted = "https://api.deepseek.com/chat/completions";
        accounts.AiText.ApiKeyEncrypted = "changed-source-key";
        var effective = accounts.ResolveVision(value => value);
        Assert.True(effective.IsConfigured);
        Assert.Equal(AiPlatform.DeepSeek, effective.Platform);
        Assert.Equal("changed-source-key", effective.ApiKeyEncrypted);
        Assert.Equal("vision-model", effective.Model);
        Assert.Equal("vision-key", accounts.AiVision.ApiKeyEncrypted);

        accounts.AiText.ApiUrlEncrypted = "https://api.deepseek.com/responses";
        Assert.False(accounts.ResolveVision(value => value).IsConfigured);
        Assert.Equal(AccountReuseSource.Translation, accounts.AiVision.ReuseFrom);
        accounts.AiText.ApiUrlEncrypted = "http://gateway.example/v1/chat/completions?channel=one";
        Assert.True(accounts.ResolveVision(value => value).IsConfigured);
        Assert.Equal("changed-source-key", accounts.ResolveVision(value => value).ApiKeyEncrypted);
        Assert.True(accounts.TrySetAiReuse(forOcr: true, reuse: false, value => value));
        Assert.Equal("vision-key", accounts.ResolveVision(value => value).ApiKeyEncrypted);
    }

    [Theory]
    [InlineData(AiPlatform.OpenAI, "https://api.openai.com/v1/chat/completions", true)]
    [InlineData(AiPlatform.OpenAI, "https://api.openai.com/v1", true)]
    [InlineData(AiPlatform.OpenAI, "https://api.openai.com", true)]
    [InlineData(AiPlatform.DeepSeek, "https://api.deepseek.com/chat/completions", false)]
    [InlineData(AiPlatform.OpenAI, "https://unknown.example/v1/chat/completions", false)]
    [InlineData(AiPlatform.OpenAI, "https://api.openai.com/other", false)]
    public void AiReuse_RequiresCompatiblePlatformAndProtocol(AiPlatform sourcePlatform, string sourceUrl, bool compatible)
    {
        var accounts = CompatibleAiAccounts();
        accounts.AiText.Platform = sourcePlatform;
        accounts.AiText.ApiUrlEncrypted = sourceUrl;

        Assert.Equal(compatible, accounts.CanReuseAi(forOcr: true, value => value));
    }

    [Fact]
    public void AiReuse_CustomRequiresExactEndpoint_AndSupportsReverseDirection()
    {
        var accounts = CompatibleAiAccounts();
        accounts.AiText.Platform = accounts.AiVision.Platform = AiPlatform.Custom;
        accounts.AiText.ApiUrlEncrypted = "https://gateway.example/v1/chat/completions";
        accounts.AiVision.ApiUrlEncrypted = "https://other.example/v1/chat/completions";
        Assert.False(accounts.CanReuseAi(forOcr: false, value => value));

        accounts.AiVision.ApiUrlEncrypted = "https://gateway.example/v1/chat/completions/";
        Assert.True(accounts.TrySetAiReuse(forOcr: false, reuse: true, value => value));
        Assert.Equal("vision-key", accounts.ResolveText(value => value).ApiKeyEncrypted);
        Assert.Equal("text-model", accounts.ResolveText(value => value).Model);

        accounts.AiVision.ApiKeyEncrypted = null;
        Assert.False(accounts.ResolveText(value => value).IsConfigured);
        Assert.Equal(AccountReuseSource.Ocr, accounts.AiText.ReuseFrom);
    }

    [Fact]
    public void Resolve_RawCyclesAndWrongDirections_AreUnavailable()
    {
        var accounts = CompatibleAiAccounts();
        accounts.AiVision.ReuseFrom = AccountReuseSource.Translation;
        accounts.AiText.ReuseFrom = AccountReuseSource.Ocr;
        accounts.TencentOcr.ReuseFrom = AccountReuseSource.Translation;
        accounts.TencentTranslation.ReuseFrom = AccountReuseSource.Ocr;

        Assert.False(accounts.ResolveVision(value => value).IsConfigured);
        Assert.False(accounts.ResolveText(value => value).IsConfigured);
        Assert.False(accounts.ResolveTencentOcr().IsConfigured);
        Assert.False(accounts.ResolveTencentTranslation().IsConfigured);
        accounts.AiText.ReuseFrom = AccountReuseSource.None;
        accounts.AiVision.ReuseFrom = AccountReuseSource.Ocr;
        Assert.False(accounts.ResolveVision(value => value).IsConfigured);
    }

    private static AccountsConfig CompatibleAiAccounts() => new()
    {
        AiText = new AiAccountConfig
        {
            ApiUrlEncrypted = "https://api.openai.com/v1/chat/completions",
            ApiKeyEncrypted = "text-key", Model = "text-model"
        },
        AiVision = new AiVisionAccountConfig
        {
            ApiUrlEncrypted = "https://api.openai.com/v1/chat/completions",
            ApiKeyEncrypted = "vision-key", Model = "vision-model"
        }
    };

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
