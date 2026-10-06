using System.IO;
using System.Security.Cryptography;
using STool.Core;
using Xunit;

namespace STool.Tests;

public class SecureStorageTests : IDisposable
{
    private readonly SecretStoreFixture _fixture = new();
    private PortableSecretStore Store => _fixture.Store;

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void EncryptThenDecrypt_RoundTripsOriginalText()
    {
        const string secret = "my-api-key-12345";

        var encrypted = Store.Encrypt(secret);
        var decrypted = Store.Decrypt(encrypted);

        Assert.Equal(secret, decrypted);
    }

    [Fact]
    public void EncryptThenDecrypt_UnicodeText_RoundTrips()
    {
        const string secret = "密钥-🔑-テスト";
        Assert.Equal(secret, Store.Decrypt(Store.Encrypt(secret)));
    }

    [Fact]
    public void Encrypt_ProducesPortablePrefixedCipher()
    {
        var encrypted = Store.Encrypt("value");

        Assert.StartsWith("v2:", encrypted);
        Assert.True(SecureStorage.IsPortableEncrypted(encrypted));
    }

    [Fact]
    public void Encrypt_SameInputTwice_ProducesDifferentCipher_ButSameDecryption()
    {
        const string secret = "repeatable";

        var a = Store.Encrypt(secret);
        var b = Store.Encrypt(secret);

        Assert.NotEqual(a, b); // 随机 nonce
        Assert.Equal(secret, Store.Decrypt(a));
        Assert.Equal(secret, Store.Decrypt(b));
    }

    [Fact]
    public void Encrypt_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, Store.Encrypt(string.Empty));
    }

    [Fact]
    public void Decrypt_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, Store.Decrypt(string.Empty));
    }

    [Fact]
    public void Decrypt_CorruptInput_ReturnsEmpty_DoesNotThrow()
    {
        Assert.Equal(string.Empty, Store.Decrypt("v2:not-valid-base64!!!"));
        Assert.Equal(string.Empty, Store.Decrypt("totally-garbage"));
    }

    [Fact]
    public void IsPortableEncrypted_PlainText_ReturnsFalse()
    {
        Assert.False(SecureStorage.IsPortableEncrypted("plain"));
    }

    [Fact]
    public void DeletedKey_InvalidatesSubsequentReadsWithoutRecreatingIt()
    {
        var encrypted = Store.Encrypt("before deletion");
        Assert.True(Store.TryDecrypt(encrypted, out _));
        File.Delete(_fixture.KeyPath);

        Assert.False(Store.TryDecrypt(encrypted, out _));
        Assert.False(File.Exists(_fixture.KeyPath));
    }

    [Fact]
    public void ReplacedKey_WithSameSizeAndTimestampIsNotCached()
    {
        var encrypted = Store.Encrypt("before replacement");
        var timestamp = File.GetLastWriteTimeUtc(_fixture.KeyPath);
        var length = new FileInfo(_fixture.KeyPath).Length;
        File.SetAttributes(_fixture.KeyPath, FileAttributes.Normal);
        File.WriteAllText(_fixture.KeyPath, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        File.SetLastWriteTimeUtc(_fixture.KeyPath, timestamp);
        Assert.Equal(length, new FileInfo(_fixture.KeyPath).Length);

        Assert.False(Store.TryDecrypt(encrypted, out _));
        Assert.Equal("after replacement", Store.Decrypt(Store.Encrypt("after replacement")));
    }

    [Fact]
    public void MissingKey_DecryptionDoesNotCreateItsDirectory()
    {
        using var other = new SecretStoreFixture();
        var encrypted = other.Store.Encrypt("secret");

        Assert.False(Store.TryDecrypt(encrypted, out _));
        Assert.False(Directory.Exists(_fixture.Root));
    }

    [Fact]
    public void InvalidExistingKey_IsNeverOverwritten()
    {
        Directory.CreateDirectory(_fixture.Root);
        var invalid = Convert.ToBase64String(new byte[8]);
        File.WriteAllText(_fixture.KeyPath, invalid);

        Assert.Throws<CryptographicException>(() => Store.Encrypt("secret"));
        Assert.Equal(invalid, File.ReadAllText(_fixture.KeyPath));
        Assert.Empty(Directory.GetFiles(_fixture.Root, "*.tmp"));
    }

    [Fact]
    public void DirectoryAtKeyPath_IsNotTreatedAsAMissingKey()
    {
        Directory.CreateDirectory(_fixture.KeyPath);

        Assert.Throws<UnauthorizedAccessException>(() => Store.Encrypt("secret"));
        Assert.True(Directory.Exists(_fixture.KeyPath));
        Assert.Empty(Directory.GetFiles(_fixture.Root));
    }

    [Fact]
    public void IndependentCreators_UseThePublishedKey()
    {
        var ciphertexts = new string[24];
        Parallel.For(0, ciphertexts.Length, index =>
        {
            var independentStore = new PortableSecretStore(_fixture.KeyPath);
            ciphertexts[index] = independentStore.Encrypt($"secret-{index}");
        });

        for (var index = 0; index < ciphertexts.Length; index++)
            Assert.Equal($"secret-{index}", Store.Decrypt(ciphertexts[index]));
        Assert.Empty(Directory.GetFiles(_fixture.Root, "*.tmp"));
    }

    [Fact]
    public void DifferentDirectories_DoNotShareKeys()
    {
        using var other = new SecretStoreFixture();
        var encrypted = Store.Encrypt("first directory");
        var otherEncrypted = other.Store.Encrypt("second directory");

        Assert.False(other.Store.TryDecrypt(encrypted, out _));
        Assert.False(Store.TryDecrypt(otherEncrypted, out _));
        Assert.Equal("first directory", Store.Decrypt(encrypted));
    }

    [Fact]
    public void TryDecrypt_DistinguishesEmptyFromUnreadable()
    {
        Assert.True(Store.TryDecrypt(null, out var empty));
        Assert.Equal(string.Empty, empty);

        Assert.True(Store.TryDecrypt(Store.Encrypt("secret"), out var plain));
        Assert.Equal("secret", plain);

        Assert.False(Store.TryDecrypt("v2:not-valid-base64!!!", out var corrupt));
        Assert.Equal(string.Empty, corrupt);
    }
}
