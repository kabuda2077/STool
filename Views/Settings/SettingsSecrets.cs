using STool.Core;

namespace STool.Views.Settings;

internal delegate bool TryDecryptSecret(string? encrypted, out string plainText);

/// <summary>
/// 设置页里一个加密保存的字段（API Key、Secret 等）。区分"本来为空"和"已保存但无法解密"：
/// 后者说明 secure.key 缺失或被替换，此时在用户重新填写之前保留原密文，不会被一次无关的自动保存清掉。
/// </summary>
internal sealed class EncryptedSetting
{
    private readonly Func<string, string> _encrypt;

    public EncryptedSetting(string? encrypted)
        : this(encrypted, SecureStorage.Encrypt, SecureStorage.TryDecrypt)
    {
    }

    internal EncryptedSetting(string? encrypted, Func<string, string> encrypt, TryDecryptSecret decrypt)
    {
        _encrypt = encrypt;
        Encrypted = string.IsNullOrWhiteSpace(encrypted) ? null : encrypted;
        Readable = decrypt(Encrypted, out var plain);
        Plain = Readable ? plain : string.Empty;
    }

    public string? Encrypted { get; private set; }

    /// <summary>解密后的明文，用于填入输入框。</summary>
    public string Plain { get; private set; }

    public bool Readable { get; private set; }

    /// <summary>已保存了值，但用当前密钥解不开。</summary>
    public bool IsUnreadable => Encrypted != null && !Readable;

    /// <summary>输入框内容与已保存的值是否一致（无法解密的值视为"空输入即未修改"）。</summary>
    public bool Matches(string input)
    {
        var normalized = input.Trim();
        return IsUnreadable ? normalized.Length == 0 : normalized == Plain;
    }

    /// <summary>计算应写入配置的密文，不改变自身状态；保存成功后调用 <see cref="MarkSaved"/>。</summary>
    public string? Resolve(string input)
    {
        if (Matches(input))
            return Encrypted;

        var normalized = input.Trim();
        return normalized.Length == 0 ? null : _encrypt(normalized);
    }

    public void MarkSaved(string input, string? encrypted)
    {
        // 无关字段的保存保留了原密文，并不意味着这段密文现在能够解密。
        if (IsUnreadable && string.IsNullOrWhiteSpace(input) && encrypted == Encrypted)
            return;

        Encrypted = encrypted;
        Plain = encrypted == null ? string.Empty : input.Trim();
        Readable = true;
    }
}
