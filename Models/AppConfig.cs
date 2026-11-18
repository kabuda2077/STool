using System.Text.Json.Serialization;
using STool.Core;

namespace STool.Models;

public class AppConfig
{
    /// <summary>当前配置结构版本。缺省（旧文件）为 0，由 ConfigManager 按版本依次迁移。</summary>
    public const int CurrentVersion = 4;

    public int ConfigVersion { get; set; }
    public HotkeyConfig Hotkeys { get; set; } = new();
    public AccountsConfig Accounts { get; set; } = new();
    public OcrConfig Ocr { get; set; } = new();
    public TranslationConfig Translation { get; set; } = new();
    public ClipboardConfig Clipboard { get; set; } = new();
    public LanTransferConfig LanTransfer { get; set; } = new();
    public DiagnosticsConfig Diagnostics { get; set; } = new();
    public bool AutoStart { get; set; }
    public bool HideTrayIcon { get; set; }
}

public class HotkeyConfig
{
    public string Screenshot { get; set; } = "Alt+1";
    public string Translation { get; set; } = "Alt+2";
    public string Clipboard { get; set; } = "Alt+3";
    public string Settings { get; set; } = "Alt+5";
    public string LanTransfer { get; set; } = "Alt+4";
}

public class LanTransferConfig
{
    public int Port { get; set; } = 17654;
    public string ReceiveDirectory { get; set; } = "";
    public string? ConfiguredExecutablePath { get; set; }
    public int MaxConcurrentTransfers { get; set; } = 2;
}

/// <summary>各用途独立保存账号草稿；显式关联只复用凭据，模型始终属于本用途。</summary>
public class AccountsConfig
{
    public TencentAccountConfig TencentOcr { get; set; } = new();
    public TencentAccountConfig TencentTranslation { get; set; } = new();

    [JsonPropertyName("tencent"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TencentAccountConfig? LegacyTencent { get; set; }

    /// <summary>AI 文本模型：AI 翻译、截图翻译的智能模式。</summary>
    public AiAccountConfig AiText { get; set; } = new();

    /// <summary>AI 视觉模型：AI OCR。</summary>
    public AiVisionAccountConfig AiVision { get; set; } = new();

    public TencentAccountConfig ResolveTencentOcr() => ResolveTencent(forOcr: true);
    public TencentAccountConfig ResolveTencentTranslation() => ResolveTencent(forOcr: false);
    public AiAccountConfig ResolveVision(Func<string, string>? decrypt = null) => ResolveAi(forOcr: true, decrypt);
    public AiAccountConfig ResolveText(Func<string, string>? decrypt = null) => ResolveAi(forOcr: false, decrypt);

    public bool CanReuseTencent(bool forOcr)
    {
        var source = forOcr ? TencentTranslation : TencentOcr;
        return source.ReuseFrom == AccountReuseSource.None && source.IsConfigured;
    }

    public bool CanReuseAi(bool forOcr, Func<string, string>? decrypt = null)
    {
        AiAccountConfig target = forOcr ? AiVision : AiText;
        AiAccountConfig source = forOcr ? AiText : AiVision;
        return source.ReuseFrom == AccountReuseSource.None && source.IsConnectionConfigured &&
               AccountConnectionRules.AreCompatible(target, source, decrypt ?? SecureStorage.Decrypt);
    }

    public bool TrySetTencentReuse(bool forOcr, bool reuse)
    {
        if (reuse && !CanReuseTencent(forOcr))
            return false;
        (forOcr ? TencentOcr : TencentTranslation).ReuseFrom = reuse
            ? (forOcr ? AccountReuseSource.Translation : AccountReuseSource.Ocr)
            : AccountReuseSource.None;
        return true;
    }

    public bool TrySetAiReuse(bool forOcr, bool reuse, Func<string, string>? decrypt = null)
    {
        if (reuse && !CanReuseAi(forOcr, decrypt))
            return false;
        (forOcr ? (AiAccountConfig)AiVision : AiText).ReuseFrom = reuse
            ? (forOcr ? AccountReuseSource.Translation : AccountReuseSource.Ocr)
            : AccountReuseSource.None;
        return true;
    }

    private TencentAccountConfig ResolveTencent(bool forOcr)
    {
        var target = forOcr ? TencentOcr : TencentTranslation;
        if (target.ReuseFrom == AccountReuseSource.None)
            return target;
        var expected = forOcr ? AccountReuseSource.Translation : AccountReuseSource.Ocr;
        return target.ReuseFrom == expected && CanReuseTencent(forOcr)
            ? (forOcr ? TencentTranslation : TencentOcr)
            : new TencentAccountConfig();
    }

    private AiAccountConfig ResolveAi(bool forOcr, Func<string, string>? decrypt)
    {
        AiAccountConfig target = forOcr ? AiVision : AiText;
        if (target.ReuseFrom == AccountReuseSource.None)
            return target;
        var expected = forOcr ? AccountReuseSource.Translation : AccountReuseSource.Ocr;
        AiAccountConfig source = forOcr ? AiText : AiVision;
        if (target.ReuseFrom != expected || source.ReuseFrom != AccountReuseSource.None ||
            !source.IsConnectionConfigured ||
            !AccountConnectionRules.IsSupportedConnection(source, decrypt ?? SecureStorage.Decrypt))
            return new AiAccountConfig { Platform = target.Platform, Model = target.Model };
        return new AiAccountConfig
        {
            Platform = source.Platform,
            ApiUrlEncrypted = source.ApiUrlEncrypted,
            ApiKeyEncrypted = source.ApiKeyEncrypted,
            Model = target.Model
        };
    }
}

public class TencentAccountConfig
{
    public AccountReuseSource ReuseFrom { get; set; }
    public string? SecretIdEncrypted { get; set; }
    public string? SecretKeyEncrypted { get; set; }

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(SecretIdEncrypted) && !string.IsNullOrWhiteSpace(SecretKeyEncrypted);
}

public class AiAccountConfig
{
    public AccountReuseSource ReuseFrom { get; set; }
    /// <summary>平台预设用于设置预填与凭据复用的协议兼容检查；请求仍使用实际接口地址。</summary>
    public AiPlatform Platform { get; set; } = AiPlatform.OpenAI;
    public string? ApiUrlEncrypted { get; set; }
    public string? ApiKeyEncrypted { get; set; }
    public string? Model { get; set; } = "gpt-6-luna";

    [JsonIgnore]
    public bool IsConnectionConfigured => !string.IsNullOrWhiteSpace(ApiUrlEncrypted) && !string.IsNullOrWhiteSpace(ApiKeyEncrypted);

    [JsonIgnore]
    public bool IsConfigured => IsConnectionConfigured && !string.IsNullOrWhiteSpace(Model);
}

public class AiVisionAccountConfig : AiAccountConfig
{
    [JsonPropertyName("sameAccountAsText"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacySameAccountAsText { get; set; }
}

/// <summary>目前接入的 AI 服务使用 Bearer 认证的 OpenAI Chat Completions 协议。</summary>
internal static class AccountConnectionRules
{
    public static bool IsSupportedConnection(AiAccountConfig account, Func<string, string> decrypt)
    {
        if (string.IsNullOrWhiteSpace(account.ApiUrlEncrypted)) return false;
        var endpoint = decrypt(account.ApiUrlEncrypted);
        // 已有关系沿用请求解析器的短地址与代理支持；初次关联仍由 AreCompatible 检查。
        try
        {
            AiApiEndpointResolver.ResolvePrimaryChatCompletionUrl(endpoint);
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    public static bool AreCompatible(AiAccountConfig target, AiAccountConfig source, Func<string, string> decrypt)
    {
        if (target.Platform != source.Platform || string.IsNullOrWhiteSpace(target.ApiUrlEncrypted))
            return false;
        if (!TryEndpoint(decrypt(target.ApiUrlEncrypted), target.Platform, out var targetUri) ||
            !TryEndpoint(decrypt(source.ApiUrlEncrypted!), source.Platform, out var sourceUri))
            return false;
        if (target.Platform == AiPlatform.Custom)
            return Uri.Compare(targetUri, sourceUri, UriComponents.HttpRequestUrl, UriFormat.SafeUnescaped,
                StringComparison.Ordinal) == 0;
        return IsPlatformEndpoint(target.Platform, targetUri) && IsPlatformEndpoint(source.Platform, sourceUri);
    }

    private static bool TryEndpoint(string value, AiPlatform platform, out Uri uri)
    {
        if (!Uri.TryCreate(value.Trim().TrimEnd('/'), UriKind.Absolute, out uri!) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        if (platform == AiPlatform.Custom)
            return uri.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal);
        if (uri.AbsolutePath.EndsWith("/responses", StringComparison.OrdinalIgnoreCase))
            return false;
        uri = new Uri(AiApiEndpointResolver.ResolvePrimaryChatCompletionUrl(uri.AbsoluteUri));
        return true;
    }

    private static bool IsPlatformEndpoint(AiPlatform platform, Uri uri) => uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort && (platform switch
        {
            AiPlatform.OpenAI => uri.Host == "api.openai.com" && uri.AbsolutePath == "/v1/chat/completions",
            AiPlatform.GoogleAiStudio => uri.Host == "generativelanguage.googleapis.com" &&
                uri.AbsolutePath == "/v1beta/openai/chat/completions",
            AiPlatform.DeepSeek => uri.Host == "api.deepseek.com" &&
                uri.AbsolutePath is "/chat/completions" or "/v1/chat/completions",
            _ => false
        });
}

public class OcrConfig
{
    public OcrProvider Provider { get; set; } = OcrProvider.WindowsLocal;
    public bool FallbackToLocal { get; set; } = true;

    /// <summary>Windows 本地 OCR 的识别语言（BCP-47，如 zh-Hans、en-US）；auto 表示自动选择。</summary>
    public string LocalLanguage { get; set; } = "auto";

    // ---- v1 遗留字段：只用于读取旧配置并迁移，迁移后清空，不再写出 ----
    [JsonPropertyName("aiPlatform"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AiPlatform? LegacyAiPlatform { get; set; }
    [JsonPropertyName("tencentSecretIdEncrypted"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyTencentSecretIdEncrypted { get; set; }
    [JsonPropertyName("tencentSecretKeyEncrypted"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyTencentSecretKeyEncrypted { get; set; }
    [JsonPropertyName("aiApiUrlEncrypted"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyAiApiUrlEncrypted { get; set; }
    [JsonPropertyName("aiApiKeyEncrypted"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyAiApiKeyEncrypted { get; set; }
    [JsonPropertyName("aiModel"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyAiModel { get; set; }
}

public class TranslationConfig
{
    public TranslationProvider Provider { get; set; } = TranslationProvider.Google;
    public ScreenshotTranslationMode ScreenshotMode { get; set; } = ScreenshotTranslationMode.Fast;

    /// <summary>迁移时保留原本的本地截图识别；用户明确跟随或更改识别服务后清除。</summary>
    public bool ScreenshotOcrMigrationPending { get; set; }

    [JsonPropertyName("screenshotOcr"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScreenshotOcrEngine? LegacyScreenshotOcr { get; set; }

    public string TranslationMode { get; set; } = "zh-en";
    public string SourceLanguage { get; set; } = "auto";
    public string TargetLanguage { get; set; } = "zh";

    // ---- v1 遗留字段：只用于读取旧配置并迁移，迁移后清空，不再写出 ----
    [JsonPropertyName("aiPlatform"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AiPlatform? LegacyAiPlatform { get; set; }
    [JsonPropertyName("tencentSecretIdEncrypted"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyTencentSecretIdEncrypted { get; set; }
    [JsonPropertyName("tencentSecretKeyEncrypted"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyTencentSecretKeyEncrypted { get; set; }
    [JsonPropertyName("aiApiUrlEncrypted"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyAiApiUrlEncrypted { get; set; }
    [JsonPropertyName("aiApiKeyEncrypted"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyAiApiKeyEncrypted { get; set; }
    [JsonPropertyName("aiModel"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyAiModel { get; set; }
}

public class ClipboardConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>非收藏记录的条数上限；收藏记录不计入。</summary>
    public int MaxEntries { get; set; } = 1000;

    /// <summary>单条文本的最大字符数，超出的复制内容不记录。</summary>
    public int MaxTextLength { get; set; } = 500_000;

    /// <summary>
    /// 不记录这些进程复制的内容（进程名，如 KeePass.exe）。多数密码管理器会给复制的密码打上隐私标记，
    /// 这里再按进程兜底；用户可在剪贴板设置中增删。
    /// </summary>
    public List<string> ExcludedApps { get; set; } = new()
    {
        "KeePass.exe",
        "KeePassXC.exe",
        "1Password.exe",
        "Bitwarden.exe"
    };
}

public class DiagnosticsConfig
{
    /// <summary>开启内存检查点、截图启动耗时与 UI 响应探测等诊断日志。</summary>
    public bool Enabled { get; set; }

    /// <summary>日志级别：Debug / Information / Warning / Error。</summary>
    public string LogLevel { get; set; } = "Information";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AccountReuseSource
{
    None,
    Ocr,
    Translation
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OcrProvider
{
    Tencent,
    AI,
    WindowsLocal
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TranslationProvider
{
    Tencent,
    OpenAI,
    Google
}

/// <summary>AI 接口的平台预设标识。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiPlatform
{
    OpenAI,
    GoogleAiStudio,
    DeepSeek,
    Custom
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScreenshotTranslationMode
{
    Fast,
    Smart
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScreenshotOcrEngine
{
    Tencent,
    WindowsLocal
}
