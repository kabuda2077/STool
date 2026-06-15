using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace STool.Core;

public static class SecureStorage
{
    private const string PortablePrefix = "v2:";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        var key = GetOrCreatePortableKey();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var data = Encoding.UTF8.GetBytes(plainText);
        var cipherText = new byte[data.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, data, cipherText, tag);

        var payload = new byte[NonceSize + TagSize + cipherText.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, payload, NonceSize, TagSize);
        Buffer.BlockCopy(cipherText, 0, payload, NonceSize + TagSize, cipherText.Length);
        return PortablePrefix + Convert.ToBase64String(payload);
    }

    public static string Decrypt(string encryptedText)
    {
        if (string.IsNullOrEmpty(encryptedText))
            return string.Empty;

        try
        {
            if (encryptedText.StartsWith(PortablePrefix, StringComparison.Ordinal))
            {
                return DecryptPortable(encryptedText[PortablePrefix.Length..]);
            }

            return DecryptLegacyDpapi(encryptedText);
        }
        catch
        {
            return string.Empty;
        }
    }

    public static bool IsPortableEncrypted(string encryptedText)
    {
        return encryptedText.StartsWith(PortablePrefix, StringComparison.Ordinal);
    }

    private static string DecryptPortable(string payloadText)
    {
        var payload = Convert.FromBase64String(payloadText);
        if (payload.Length < NonceSize + TagSize)
        {
            return string.Empty;
        }

        var key = GetOrCreatePortableKey();
        var nonce = payload[..NonceSize];
        var tag = payload[NonceSize..(NonceSize + TagSize)];
        var cipherText = payload[(NonceSize + TagSize)..];
        var plainText = new byte[cipherText.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipherText, tag, plainText);
        return Encoding.UTF8.GetString(plainText);
    }

    private static string DecryptLegacyDpapi(string encryptedText)
    {
        var data = Convert.FromBase64String(encryptedText);
        var decrypted = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(decrypted);
    }

    private static byte[] GetOrCreatePortableKey()
    {
        AppPaths.EnsureDataDirectory();

        if (File.Exists(AppPaths.SecureKeyPath))
        {
            return Convert.FromBase64String(File.ReadAllText(AppPaths.SecureKeyPath));
        }

        var key = RandomNumberGenerator.GetBytes(KeySize);
        File.WriteAllText(AppPaths.SecureKeyPath, Convert.ToBase64String(key));
        File.SetAttributes(AppPaths.SecureKeyPath, FileAttributes.Hidden);
        return key;
    }
}
