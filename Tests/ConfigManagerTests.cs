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
        fixture.Manager.Save(new AppConfig { HideTrayIcon = true });
        using var lockedTemp = new FileStream(
            fixture.ConfigPath + ".tmp",
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None);

        Assert.ThrowsAny<IOException>(() =>
            fixture.Manager.Save(new AppConfig { HideTrayIcon = false }));

        Assert.True(fixture.Manager.Get().HideTrayIcon);
        Assert.True(fixture.CreateManager().Get().HideTrayIcon);
    }

    [Fact]
    public void Reload_CorruptPrimary_RestoresValidBackup()
    {
        using var fixture = new ConfigFixture();
        fixture.Manager.Save(new AppConfig { HideTrayIcon = true });
        fixture.Manager.Save(new AppConfig { HideTrayIcon = false });
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
            Ocr = new OcrConfig { TencentSecretIdEncrypted = "legacy-secret" }
        });

        var config = fixture.CreateManager().Get();

        Assert.Equal("v2:test:plain-secret", config.Ocr.TencentSecretIdEncrypted);
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
