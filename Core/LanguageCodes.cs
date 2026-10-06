namespace STool.Core;

/// <summary>应用内语言代码（zh / en / ja / ko ...）与各翻译服务、提示词语言名之间的映射。</summary>
internal static class LanguageCodes
{
    /// <summary>用于 AI 提示词的英文语言名。</summary>
    public static string ToEnglishName(string code) => code.ToLowerInvariant() switch
    {
        "zh" or "zh-cn" or "chinese" => "Chinese",
        "en" or "english" => "English",
        "ja" or "japanese" => "Japanese",
        "ko" or "korean" => "Korean",
        "fr" or "french" => "French",
        "de" or "german" => "German",
        "es" or "spanish" => "Spanish",
        "ru" or "russian" => "Russian",
        _ => code
    };

    public static string ToGoogle(string code) => code.ToLowerInvariant() switch
    {
        "auto" => "auto",
        "zh" or "zh-cn" or "chinese" => "zh-CN",
        _ => ToCommonCode(code)
    };

    public static string ToTencent(string code) => code.ToLowerInvariant() switch
    {
        "auto" => "auto",
        "zh" or "zh-cn" or "chinese" => "zh",
        _ => ToCommonCode(code)
    };

    private static string ToCommonCode(string code) => code.ToLowerInvariant() switch
    {
        "en" or "english" => "en",
        "ja" or "japanese" => "ja",
        "ko" or "korean" => "ko",
        "fr" or "french" => "fr",
        "de" or "german" => "de",
        "es" or "spanish" => "es",
        "ru" or "russian" => "ru",
        _ => code
    };
}
