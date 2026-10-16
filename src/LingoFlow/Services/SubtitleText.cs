using System.Globalization;
using System.Text.RegularExpressions;

namespace LingoFlow.Services;

public static class SubtitleText
{
    private static readonly Regex Units = new(@"[\u3400-\u9fff\u3040-\u30ff\uac00-\ud7af]|[\p{L}\p{N}]+", RegexOptions.Compiled);
    public static bool IsCjk(char ch) => ch is >= '\u3400' and <= '\u9fff' or >= '\u3040' and <= '\u30ff' or >= '\uac00' and <= '\ud7af';
    public static string RemoveOverlap(string previous, string current, double overlap)
    {
        if (overlap <= 0 || string.IsNullOrEmpty(previous)) return current;
        var before = Units.Matches(previous); var after = Units.Matches(current);
        int limit = Math.Min(Math.Min(before.Count, after.Count - 1), Math.Clamp((int)Math.Ceiling(overlap * 16), 2, 8));
        for (int length = limit; length >= 2; length--)
        {
            bool cjk = after.Take(length).Any(m => IsCjk(m.Value[0]));
            if (cjk && length < 3) continue;
            bool equal = true;
            for (int i = 0; i < length; i++)
                if (!before[before.Count - length + i].Value.Equals(after[i].Value, StringComparison.OrdinalIgnoreCase)) { equal = false; break; }
            if (equal) return current[after[length].Index..].TrimStart();
        }
        return current;
    }
    public static IReadOnlyList<string> Split(string text, bool accurate)
    {
        // Keep every character and prefer punctuation/word boundaries. No tail is
        // truncated; even a single very long token is split on grapheme boundaries.
        int max = text.Any(IsCjk) ? (accurate ? 72 : 48) : (accurate ? 220 : 160);
        var result = new List<string>();
        while (text.Length > max)
        {
            int cut = -1;
            var boundaries = StringInfo.ParseCombiningCharacters(text);
            foreach (int at in boundaries)
            {
                if (at == 0 || at > max) continue;
                char last = text[at - 1];
                if (at >= max / 3 && ("。！？!?；;，,、\n".Contains(last) || last == '.' && (at == text.Length || char.IsWhiteSpace(text[at])))) cut = at;
            }
            if (cut < 0) cut = text.LastIndexOf(' ', max - 1, max);
            if (cut < max / 3) cut = boundaries.Where(i => i > 0 && i <= max).LastOrDefault();
            if (cut == 0) cut = boundaries.FirstOrDefault(i => i > max, text.Length);
            // Include whitespace at a word boundary so Join reproduces the input.
            if (cut < text.Length && text[cut] == ' ') cut++;
            result.Add(text[..cut]); text = text[cut..];
        }
        if (text.Length > 0) result.Add(text);
        return result;
    }
    public static string Join(IEnumerable<string> parts)
    {
        var result = new System.Text.StringBuilder();
        foreach (string part in parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()))
        {
            if (result.Length > 0 && !IsCjk(result[^1]) && !IsCjk(part[0]) && !"。！？；，、".Contains(result[^1])) result.Append(' ');
            result.Append(part);
        }
        return result.ToString();
    }
}
