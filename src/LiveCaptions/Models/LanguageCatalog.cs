namespace LiveCaptions.Models;

public sealed record LanguageOption(string Code, string EnglishName, string DisplayName);

public static class LanguageCatalog
{
    /// <summary>Source languages for speech recognition ("auto" enables language identification).</summary>
    public static readonly IReadOnlyList<LanguageOption> Source =
    [
        new("auto", "Auto detect", "自动检测"),
        new("zh", "Chinese", "中文"),
        new("en", "English", "英语"),
        new("ja", "Japanese", "日语"),
        new("ko", "Korean", "韩语"),
        new("yue", "Cantonese", "粤语"),
        new("de", "German", "德语"),
        new("fr", "French", "法语"),
        new("es", "Spanish", "西班牙语"),
        new("pt", "Portuguese", "葡萄牙语"),
        new("it", "Italian", "意大利语"),
        new("ru", "Russian", "俄语"),
        new("uk", "Ukrainian", "乌克兰语"),
        new("ar", "Arabic", "阿拉伯语"),
        new("hi", "Hindi", "印地语"),
        new("th", "Thai", "泰语"),
        new("vi", "Vietnamese", "越南语"),
        new("tr", "Turkish", "土耳其语"),
        new("id", "Indonesian", "印尼语"),
        new("ms", "Malay", "马来语"),
        new("nl", "Dutch", "荷兰语"),
        new("pl", "Polish", "波兰语"),
        new("sv", "Swedish", "瑞典语"),
        new("da", "Danish", "丹麦语"),
        new("fi", "Finnish", "芬兰语"),
        new("no", "Norwegian", "挪威语"),
        new("cs", "Czech", "捷克语"),
        new("el", "Greek", "希腊语"),
        new("hu", "Hungarian", "匈牙利语"),
        new("ro", "Romanian", "罗马尼亚语"),
        new("he", "Hebrew", "希伯来语"),
        new("fa", "Persian", "波斯语"),
        new("fil", "Filipino", "菲律宾语"),
    ];

    /// <summary>Target languages for LLM translation.</summary>
    public static readonly IReadOnlyList<LanguageOption> Target =
    [
        new("zh", "Simplified Chinese", "简体中文"),
        new("zh-Hant", "Traditional Chinese", "繁体中文"),
        new("en", "English", "英语"),
        new("ja", "Japanese", "日语"),
        new("ko", "Korean", "韩语"),
        new("de", "German", "德语"),
        new("fr", "French", "法语"),
        new("es", "Spanish", "西班牙语"),
        new("pt", "Portuguese", "葡萄牙语"),
        new("it", "Italian", "意大利语"),
        new("ru", "Russian", "俄语"),
        new("uk", "Ukrainian", "乌克兰语"),
        new("ar", "Arabic", "阿拉伯语"),
        new("hi", "Hindi", "印地语"),
        new("th", "Thai", "泰语"),
        new("vi", "Vietnamese", "越南语"),
        new("tr", "Turkish", "土耳其语"),
        new("id", "Indonesian", "印尼语"),
        new("nl", "Dutch", "荷兰语"),
        new("pl", "Polish", "波兰语"),
        new("sv", "Swedish", "瑞典语"),
        new("cs", "Czech", "捷克语"),
        new("el", "Greek", "希腊语"),
        new("he", "Hebrew", "希伯来语"),
    ];

    public static LanguageOption FindSource(string code)
        => Source.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)) ?? Source[0];

    public static LanguageOption FindTarget(string code)
        => Target.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)) ?? Target[0];

    /// <summary>True if the text already appears to be written in the target language (skip the LLM round-trip).</summary>
    public static bool LooksLike(string text, string targetCode)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;

        int letters = 0, cjk = 0, hiraganaKatakana = 0, hangul = 0, cyrillic = 0, arabic = 0;
        foreach (var ch in text)
        {
            if (!char.IsLetter(ch)) continue;
            letters++;
            if (ch >= 0x4E00 && ch <= 0x9FFF) cjk++;
            else if ((ch >= 0x3040 && ch <= 0x30FF)) hiraganaKatakana++;
            else if (ch >= 0xAC00 && ch <= 0xD7AF) hangul++;
            else if (ch >= 0x0400 && ch <= 0x04FF) cyrillic++;
            else if (ch >= 0x0600 && ch <= 0x06FF) arabic++;
        }

        if (letters == 0) return true;
        var ratio = (double)letters;

        return targetCode switch
        {
            "zh" or "zh-Hant" => cjk / ratio > 0.5 && hiraganaKatakana == 0,
            "ja" => (cjk + hiraganaKatakana) / ratio > 0.4,
            "ko" => hangul / ratio > 0.4,
            "ru" or "uk" => cyrillic / ratio > 0.5,
            "ar" or "fa" => arabic / ratio > 0.5,
            _ => false,
        };
    }
}
