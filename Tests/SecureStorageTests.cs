using STool.Core;
using Xunit;

namespace STool.Tests;

public class SecureStorageTests
{
    [Fact]
    public void EncryptThenDecrypt_RoundTripsOriginalText()
    {
        const string secret = "my-api-key-12345";

        var encrypted = SecureStorage.Encrypt(secret);
        var decrypted = SecureStorage.Decrypt(encrypted);

        Assert.Equal(secret, decrypted);
    }

    [Fact]
    public void EncryptThenDecrypt_UnicodeText_RoundTrips()
    {
        const string secret = "密钥-🔑-テスト";
        Assert.Equal(secret, SecureStorage.Decrypt(SecureStorage.Encrypt(secret)));
    }

    [Fact]
    public void Encrypt_ProducesPortablePrefixedCipher()
    {
        var encrypted = SecureStorage.Encrypt("value");

        Assert.StartsWith("v2:", encrypted);
        Assert.True(SecureStorage.IsPortableEncrypted(encrypted));
    }

    [Fact]
    public void Encrypt_SameInputTwice_ProducesDifferentCipher_ButSameDecryption()
    {
        const string secret = "repeatable";

        var a = SecureStorage.Encrypt(secret);
        var b = SecureStorage.Encrypt(secret);

        Assert.NotEqual(a, b); // 随机 nonce
        Assert.Equal(secret, SecureStorage.Decrypt(a));
        Assert.Equal(secret, SecureStorage.Decrypt(b));
    }

    [Fact]
    public void Encrypt_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, SecureStorage.Encrypt(string.Empty));
    }

    [Fact]
    public void Decrypt_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, SecureStorage.Decrypt(string.Empty));
    }

    [Fact]
    public void Decrypt_CorruptInput_ReturnsEmpty_DoesNotThrow()
    {
        Assert.Equal(string.Empty, SecureStorage.Decrypt("v2:not-valid-base64!!!"));
        Assert.Equal(string.Empty, SecureStorage.Decrypt("totally-garbage"));
    }

    [Fact]
    public void IsPortableEncrypted_PlainText_ReturnsFalse()
    {
        Assert.False(SecureStorage.IsPortableEncrypted("plain"));
    }
}
