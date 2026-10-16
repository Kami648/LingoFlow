namespace LingoFlow.Services;

public static class TranslationPrompts
{
    // Only this reserved first line is managed by the language selectors.
    public static string Body(string? prompt)
    {
        string text = prompt ?? "";
        if (!text.StartsWith("【翻译方向】", StringComparison.Ordinal)) return text;
        int newline = text.IndexOf('\n');
        return newline < 0 ? "" : text[(newline + 1)..];
    }
    private static string Name(string code) => code switch
    { "zh" => "简体中文", "en" => "英语", "ja" => "日语", "ko" => "韩语", _ => "原语言" };
    public static string Render(string? prompt, string source, string target) =>
        "【翻译方向】" + (source is "auto" or "" ? "自动识别原语言，翻译为" : "将" + Name(source) + "翻译为") + Name(target) + "。\n" + Body(prompt);
}
