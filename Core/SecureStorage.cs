namespace STool.Core;

/// <summary>敏感配置的便携式加密入口，密钥随 Data 目录迁移。</summary>
public static class SecureStorage
{
    private static readonly PortableSecretStore Store = new(AppPaths.SecureKeyPath);

    public static string Encrypt(string plainText) => Store.Encrypt(plainText);

    /// <summary>需要区分“无法解密”和“本来为空”时使用 TryDecrypt。</summary>
    public static string Decrypt(string encryptedText) => Store.Decrypt(encryptedText);

    public static bool TryDecrypt(string? encryptedText, out string plainText) =>
        Store.TryDecrypt(encryptedText, out plainText);

    public static bool IsPortableEncrypted(string encryptedText) =>
        encryptedText.StartsWith(PortableSecretStore.PortablePrefix, StringComparison.Ordinal);
}
