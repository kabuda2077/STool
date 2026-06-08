using System.Text.Json.Serialization;

namespace STool.Models;

public class AppConfig
{
    public HotkeyConfig Hotkeys { get; set; } = new();
    public OcrConfig Ocr { get; set; } = new();
    public TranslationConfig Translation { get; set; } = new();
    public ClipboardConfig Clipboard { get; set; } = new();
    public bool AutoStart { get; set; }
}

public class HotkeyConfig
{
    public string Screenshot { get; set; } = "Ctrl+Alt+A";
    public string Translation { get; set; } = "Ctrl+Alt+T";
    public string Clipboard { get; set; } = "Ctrl+Alt+V";
}

public class OcrConfig
{
    public OcrProvider Provider { get; set; } = OcrProvider.WindowsLocal;
    public string? TencentSecretIdEncrypted { get; set; }
    public string? TencentSecretKeyEncrypted { get; set; }
    public string? AiApiUrlEncrypted { get; set; }
    public string? AiApiKeyEncrypted { get; set; }
    public string? AiModel { get; set; } = "gpt-4o";
    public bool FallbackToLocal { get; set; } = true;
}

public class TranslationConfig
{
    public TranslationProvider Provider { get; set; } = TranslationProvider.OpenAI;
    public string? TencentSecretIdEncrypted { get; set; }
    public string? TencentSecretKeyEncrypted { get; set; }
    public string? AiApiUrlEncrypted { get; set; }
    public string? AiApiKeyEncrypted { get; set; }
    public string? AiModel { get; set; } = "gpt-4o-mini";
    public string SourceLanguage { get; set; } = "auto";
    public string TargetLanguage { get; set; } = "zh";
}

public class ClipboardConfig
{
    public bool Enabled { get; set; } = true;
    public int MaxEntries { get; set; } = 1000;
    public int RetentionDays { get; set; } = 30;
    public int MaxImageSizeKB { get; set; } = 5120;
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
    OpenAI
}
