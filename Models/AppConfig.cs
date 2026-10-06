using System.Text.Json.Serialization;

namespace STool.Models;

public class AppConfig
{
    public HotkeyConfig Hotkeys { get; set; } = new();
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

public class OcrConfig
{
    public OcrProvider Provider { get; set; } = OcrProvider.WindowsLocal;
    public OcrAiPlatform AiPlatform { get; set; } = OcrAiPlatform.OpenAI;
    public string? TencentSecretIdEncrypted { get; set; }
    public string? TencentSecretKeyEncrypted { get; set; }
    public string? AiApiUrlEncrypted { get; set; }
    public string? AiApiKeyEncrypted { get; set; }
    public string? AiModel { get; set; } = "gpt-4o-mini";
    public bool FallbackToLocal { get; set; } = true;
}

public class TranslationConfig
{
    public TranslationProvider Provider { get; set; } = TranslationProvider.OpenAI;
    public TranslationAiPlatform AiPlatform { get; set; } = TranslationAiPlatform.OpenAI;
    public ScreenshotTranslationMode ScreenshotMode { get; set; } = ScreenshotTranslationMode.Fast;
    public string? TencentSecretIdEncrypted { get; set; }
    public string? TencentSecretKeyEncrypted { get; set; }
    public string? AiApiUrlEncrypted { get; set; }
    public string? AiApiKeyEncrypted { get; set; }
    public string? AiModel { get; set; } = "gpt-4o-mini";
    public string TranslationMode { get; set; } = "zh-en";
    public string SourceLanguage { get; set; } = "auto";
    public string TargetLanguage { get; set; } = "zh";
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

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TranslationAiPlatform
{
    OpenAI,
    GoogleAiStudio,
    DeepSeek,
    Custom
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OcrAiPlatform
{
    OpenAI,
    GoogleAiStudio,
    Custom
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScreenshotTranslationMode
{
    Fast,
    Smart
}
