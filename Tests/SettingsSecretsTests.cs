using System.IO;
using STool.Views.Settings;
using Xunit;

namespace STool.Tests;

public class SettingsSecretsTests : IDisposable
{
    private readonly SecretStoreFixture _fixture = new();

    private EncryptedSetting CreateSetting(string? encrypted) =>
        new(encrypted, _fixture.Store.Encrypt, _fixture.Store.TryDecrypt);

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void UnreadableSecret_SurvivesRepeatedUnrelatedSaves()
    {
        const string encrypted = "v2:not-valid-base64!";
        var setting = CreateSetting(encrypted);

        for (var save = 0; save < 3; save++)
        {
            Assert.True(setting.IsUnreadable);
            Assert.True(setting.Matches(string.Empty));
            var preserved = setting.Resolve(string.Empty);
            Assert.Equal(encrypted, preserved);
            setting.MarkSaved(string.Empty, preserved);
        }
        Assert.True(setting.IsUnreadable);
    }

    [Fact]
    public void ReadableSecret_CanBeExplicitlyCleared()
    {
        var setting = CreateSetting(_fixture.Store.Encrypt("original"));

        Assert.False(setting.Matches(string.Empty));
        Assert.Null(setting.Resolve(string.Empty));
        setting.MarkSaved(string.Empty, null);

        Assert.False(setting.IsUnreadable);
        Assert.Null(setting.Encrypted);
    }

    [Fact]
    public void UnreadableSecret_CanBeReplacedByUserInput()
    {
        var setting = CreateSetting("v2:broken!");
        var replacement = setting.Resolve("replacement");
        setting.MarkSaved("replacement", replacement);

        Assert.False(setting.IsUnreadable);
        Assert.Equal("replacement", _fixture.Store.Decrypt(replacement!));
        Assert.True(setting.Matches("replacement"));
    }

    [Fact]
    public void DeletedKey_UnrelatedSavesKeepCiphertextAndDoNotCreateAKey()
    {
        var encrypted = _fixture.Store.Encrypt("saved secret");
        File.Delete(_fixture.KeyPath);
        var setting = CreateSetting(encrypted);

        for (var save = 0; save < 3; save++)
        {
            var preserved = setting.Resolve(string.Empty);
            setting.MarkSaved(string.Empty, preserved);
            Assert.Equal(encrypted, preserved);
            Assert.True(setting.IsUnreadable);
        }
        Assert.False(File.Exists(_fixture.KeyPath));
    }

    [Fact]
    public void ReplacedKey_UnrelatedSavePreservesOriginalCiphertext()
    {
        var encrypted = _fixture.Store.Encrypt("saved secret");
        using var replacement = new SecretStoreFixture();
        replacement.Store.Encrypt("other key");
        File.SetAttributes(_fixture.KeyPath, FileAttributes.Normal);
        File.Copy(replacement.KeyPath, _fixture.KeyPath, overwrite: true);
        var setting = CreateSetting(encrypted);

        Assert.True(setting.IsUnreadable);
        Assert.Equal(encrypted, setting.Resolve(string.Empty));
    }
}
