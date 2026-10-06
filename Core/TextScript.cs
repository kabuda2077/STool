namespace STool.Core;

/// <summary>字符所属文字系统的判断，供语言检测、断行和截图翻译过滤共用。</summary>
internal static class TextScript
{
    /// <summary>中文汉字：CJK 统一表意文字（U+4E00–U+9FFF）及扩展 A（U+3400–U+4DBF）。</summary>
    public static bool IsHan(char ch) =>
        (ch >= 0x4E00 && ch <= 0x9FFF) || (ch >= 0x3400 && ch <= 0x4DBF);

    /// <summary>日文假名：平假名/片假名（U+3040–U+30FF）及片假名音标扩展（U+31F0–U+31FF）。</summary>
    public static bool IsKana(char ch) =>
        (ch >= 0x3040 && ch <= 0x30FF) || (ch >= 0x31F0 && ch <= 0x31FF);

    /// <summary>韩文音节（U+AC00–U+D7AF）。</summary>
    public static bool IsHangul(char ch) => ch >= 0xAC00 && ch <= 0xD7AF;

    /// <summary>中日韩文字：汉字、平假名/片假名、韩文音节。</summary>
    public static bool IsCjk(char ch) =>
        IsHan(ch) || (ch >= 0x3040 && ch <= 0x30FF) || IsHangul(ch);

    public static bool IsLatinLetter(char ch) => (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');
}
