using System.Text.RegularExpressions;

namespace LingoFlow.Services;

public sealed class TranslationLanguageException(string message) : InvalidOperationException(message);
public sealed class IncompleteTranslationException(string message) : InvalidOperationException(message);

public static class TranslationLanguage
{
    private static readonly Regex Kana = new(@"[\u3040-\u30ff\uff66-\uff9f]");
    private static readonly Regex Hangul = new(@"[\uac00-\ud7af\u1100-\u11ff]");
    private static readonly Regex Han = new(@"[\u3400-\u9fff]");
    private static readonly Regex Latin = new(@"[A-Za-z]+(?:['’-][A-Za-z]+)*");
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    { "API", "AI", "CPU", "GPU", "RAM", "USB", "URL", "HTTP", "HTTPS", "HTML", "JSON", "SRT", "OpenAI", "DeepSeek", "Qwen", "GPT", "ChatGPT", "Whisper", "Windows", "Microsoft", "Edge", "Chrome", "Google", "YouTube", "bilibili", "LingoFlow", "ffmpeg", "llama", "cpp", "SDK", "WiFi", "iPhone", "iPad", "macOS", "Android", "Linux", "GitHub", "Index", "Translate" };
    // Latin spelling is available to the model; no term list forces a word's fate.
    public static string ChineseGrammarFor(string source) => "root ::= item+\nitem ::= " + @"[A-Za-z\u3400-\u9fff\u3000-\u303f\u2000-\u206f\u0009-\u000d\u0020-\u0040\u005b-\u0060\u007b-\u007e\uff00-\uff20\uff3b-\uff40\uff5b-\uff65]" + "\n";
    private static string WithoutProtected(string text) => Regex.Replace(text, @"https?://[^\s<>]+|\b[A-Za-z][A-Za-z0-9_-]*[0-9][A-Za-z0-9_.-]*\b", "");
    public static bool IsNeutral(string text) => Regex.IsMatch(text.Trim(), @"^(?:[B-HJ-Z]|[0-9][0-9\s.,:%+\-]*|[\p{P}\p{S}\s]+)$");
    public static bool IsValid(string text, string target, string sourceText = "", string source = "auto", bool allowModelTerms = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string value = WithoutProtected(text.Normalize(System.Text.NormalizationForm.FormKC));
        bool singleSourceWord = allowModelTerms && Regex.IsMatch(sourceText.Trim(), @"^[A-Za-z]+(?:['’-][A-Za-z]+)*[.!?。！？]?$" , RegexOptions.CultureInvariant);
        bool kana = Kana.IsMatch(value), hangul = Hangul.IsMatch(value), han = Han.IsMatch(value);
        if (target == "zh" && (kana || hangul)) return false;
        if (target == "en" && (kana || hangul || han)) return false;
        if (target == "ja" && hangul || target == "ko" && kana) return false;
        if (target == "ko" && han && !hangul) return false;
        if (target != "en")
        {
            int modelTerms = 0;
            foreach (Match word in Latin.Matches(value))
            {
                string token = word.Value;
                // Names and acronyms can remain in subtitles. Ordinary lower-case
                // vocabulary such as "everyone is" must be translated.
                if (Names.Contains(token) || token.Length <= 5 && token.All(char.IsUpper)) continue;
                if (token.Length > 1 && char.IsUpper(token[0]) && token.Skip(1).All(char.IsLower) && sourceText.Contains(token, StringComparison.Ordinal) && (han || kana || hangul)) continue;
                if (allowModelTerms && (han || kana || hangul || singleSourceWord) &&
                    Regex.IsMatch(sourceText, @"(?<![A-Za-z])" + Regex.Escape(token) + @"(?![A-Za-z])", RegexOptions.IgnoreCase))
                { modelTerms++; continue; }
                return false;
            }
            // Permit model-chosen source terms without admitting a copied English
            // paragraph with just a token amount of target-language text appended.
            if (modelTerms > Math.Max(2, value.Count(SubtitleText.IsCjk))) return false;
        }
        if (source != "auto" && source != target && sourceText.Length > 6)
        {
            static string Compact(string s) => Regex.Replace(s, @"[\s\p{P}\p{S}]", "");
            if (Compact(text).Equals(Compact(sourceText), StringComparison.OrdinalIgnoreCase) && !singleSourceWord) return false;
        }
        return true;
    }
    public static string TargetName(string target) => target switch { "en" => "英语", "ja" => "日语", "ko" => "韩语", _ => "简体中文" };
    public static string Rule(string target) => target switch
    {
        "zh" => "所有普通词句必须翻译成简体中文，禁止夹杂未翻译的日文、韩文或英文。人名、品牌名、网址和必要缩写可以保留。",
        "en" => "Translate all ordinary speech into English. Do not copy Chinese, Japanese or Korean sentences into the result.",
        "ja" => "日常の文はすべて自然な日本語に訳してください。未翻訳の中国語、韓国語、英語の文を混ぜないでください。",
        _ => "일반 문장은 모두 자연스러운 한국어로 번역하세요. 번역하지 않은 일본어, 중국어, 영어 문장을 섞지 마세요."
    };
}
