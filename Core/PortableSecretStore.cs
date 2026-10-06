using System.IO;
using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace STool.Core;

/// <summary>
/// 每次操作读取当前密钥快照，避免删除或同时间戳替换后继续使用失效缓存。
/// 新密钥先写临时文件再以不覆盖方式发布；不同实例或进程竞争创建时使用胜出的密钥。
/// </summary>
internal sealed class PortableSecretStore
{
    internal const string PortablePrefix = "v2:";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int MaxKeyFileBytes = 128;
    private readonly string _keyPath;

    public PortableSecretStore(string keyPath)
    {
        _keyPath = Path.GetFullPath(keyPath);
    }

    public string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        var key = GetOrCreateKey();
        var data = Encoding.UTF8.GetBytes(plainText);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var cipherText = new byte[data.Length];
            var tag = new byte[TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, data, cipherText, tag);

            var payload = new byte[NonceSize + TagSize + cipherText.Length];
            nonce.CopyTo(payload, 0);
            tag.CopyTo(payload, NonceSize);
            cipherText.CopyTo(payload, NonceSize + TagSize);
            return PortablePrefix + Convert.ToBase64String(payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(data);
        }
    }

    public string Decrypt(string encryptedText) =>
        TryDecrypt(encryptedText, out var plainText) ? plainText : string.Empty;

    public bool TryDecrypt(string? encryptedText, out string plainText)
    {
        plainText = string.Empty;
        if (string.IsNullOrEmpty(encryptedText))
            return true;

        try
        {
            plainText = encryptedText.StartsWith(PortablePrefix, StringComparison.Ordinal)
                ? DecryptPortable(encryptedText[PortablePrefix.Length..])
                : DecryptLegacyDpapi(encryptedText);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private string DecryptPortable(string payloadText)
    {
        var payload = Convert.FromBase64String(payloadText);
        if (payload.Length < NonceSize + TagSize)
            throw new CryptographicException("密文长度无效。");

        // 解密不创建目录或密钥；不存在与无法访问都应向调用方报告失败。
        var key = ReadKey();
        var plainText = new byte[payload.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(
                payload.AsSpan(0, NonceSize),
                payload.AsSpan(NonceSize + TagSize),
                payload.AsSpan(NonceSize, TagSize),
                plainText);
            return Encoding.UTF8.GetString(plainText);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plainText);
        }
    }

    private static string DecryptLegacyDpapi(string encryptedText)
    {
        var data = Convert.FromBase64String(encryptedText);
        var plainText = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(plainText);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainText);
        }
    }

    private byte[] ReadKey()
    {
        // 不共享写入权限，避免读到就地修改的半个文件；允许外部以原子替换方式发布新文件。
        using var stream = new FileStream(_keyPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length is <= 0 or > MaxKeyFileBytes)
            throw new CryptographicException("密钥文件无效。");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var key = Convert.FromBase64String(reader.ReadToEnd().Trim());
        if (key.Length == KeySize)
            return key;

        CryptographicOperations.ZeroMemory(key);
        throw new CryptographicException("密钥文件无效。");
    }

    private byte[] GetOrCreateKey()
    {
        try
        {
            return ReadKey();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // 只有明确不存在时才创建；不能将权限错误或损坏文件当成缺失。
            return CreateKey();
        }
    }

    private byte[] CreateKey()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
        var tempPath = _keyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var candidate = RandomNumberGenerator.GetBytes(KeySize);
        var encoded = Encoding.UTF8.GetBytes(Convert.ToBase64String(candidate));
        var createdTemp = false;
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                createdTemp = true;
                stream.Write(encoded);
                stream.Flush(flushToDisk: true);
            }
            try
            {
                File.SetAttributes(tempPath, File.GetAttributes(tempPath) | FileAttributes.Hidden);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug(ex, "Failed to hide portable key temporary file");
            }

            try
            {
                File.Move(tempPath, _keyPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(_keyPath))
            {
                // 另一创建者已经发布，不覆盖，也不使用本地未发布的随机密钥。
            }
            return ReadKey();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
            CryptographicOperations.ZeroMemory(encoded);
            if (createdTemp)
            {
                try { File.Delete(tempPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Debug(ex, "Failed to clean portable key temporary file");
                }
            }
        }
    }
}
