using System.Text.Json.Serialization;

namespace STool.Models;

public class AppConfig
{
    /// <summary>当前配置结构版本。缺省（旧文件）为 0，由 ConfigManager 按版本依次迁移。</summary>
    public const int CurrentVersion = 2;

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

/// <summary>服务账号：每个账号只保存一份，OCR、翻译、截图翻译按需引用。</summary>
public class AccountsConfig
{
    public TencentAccountConfig Tencent { get; set; } = new();

    /// <summary>AI 文本模型：AI 翻译、截图翻译的智能模式。</summary>
    public AiAccountConfig AiText { get; set; } = new();

    /// <summary>AI 视觉模型：AI OCR。</summary>
    public AiVisionAccountConfig AiVision { get; set; } = new();

    /// <summary>视觉模型实际使用的账号：勾选「与文本模型同一账号」时沿用文本模型的平台、地址和密钥。</summary>
    public AiAccountConfig ResolveVision() => AiVision.SameAccountAsText
        ? new AiAccountConfig
        {
            Platform = AiText.Platform,
            ApiUrlEncrypted = AiText.ApiUrlEncrypted,
            ApiKeyEncrypted = AiText.ApiKeyEncrypted,
            Model = AiVision.Model
        }
        : AiVision;
}

public class TencentAccountConfig
{
    public string? SecretIdEncrypted { get; set; }
    public string? SecretKeyEncrypted { get; set; }

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrEmpty(SecretIdEncrypted) && !string.IsNullOrEmpty(SecretKeyEncrypted);
}

public class AiAccountConfig
{
    /// <summary>平台预设，只影响设置页预填与思考控制参数的识别，不参与其他服务逻辑。</summary>
    public AiPlatform Platform { get; set; } = AiPlatform.OpenAI;
    public string? ApiUrlEncrypted { get; set; }
    public string? ApiKeyEncrypted { get; set; }
    public string? Model { get; set; } = "gpt-6-luna";

    [JsonIgnore]
    public bool IsConfigured =>
        !string.IsNullOrEmpty(ApiUrlEncrypted) &&
        !string.IsNullOrEmpty(ApiKeyEncrypted) &&
        !string.IsNullOrWhiteSpace(Model);
}

public class AiVisionAccountConfig : AiAccountConfig
{
    /// <summary>为 true 时平台、地址、密钥沿用文本模型，只单独保存模型名。</summary>
    public bool SameAccountAsText { get; set; } = true;
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

    /// <summary>截图翻译的识别引擎：只提供能返回文字位置的引擎，与「OCR 识别」按钮的引擎无关。</summary>
    public ScreenshotOcrEngine ScreenshotOcr { get; set; } = ScreenshotOcrEngine.WindowsLocal;

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
    public int MaxEntries { get; set; } = 1000;

    /// <summary>非收藏记录的保留天数；0 表示不按时间清理。</summary>
    public int RetentionDays { get; set; } = 30;

    public int MaxImageSizeKB { get; set; } = 5120;

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
