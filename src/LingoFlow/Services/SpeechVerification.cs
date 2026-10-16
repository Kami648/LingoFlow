using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace LingoFlow.Services;

public static class SpeechVerification
{
    private static readonly string[] RiskPatterns = new[] { "ご視聴", "ご清聴", "チャンネル登録", "字幕由", "字幕by", "字幕提供", "字幕制作", "感谢观看", "感谢您的观看", "谢谢观看", "thankyouforwatching", "thanksforwatching", "시청해", "시청해주셔서", "구독" }.Select(Normalize).ToArray();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int LCMapStringEx(string locale, uint flags, string source, int sourceLength,
        StringBuilder destination, int destinationLength, IntPtr version, IntPtr reserved, IntPtr sortHandle);

    public static string Normalize(string text)
    {
        text = text.Normalize(System.Text.NormalizationForm.FormKC);
        if (text.Length == 0) return "";
        // Call Windows NLS directly: StrConv depends on optional .NET locale support.
        int size = LCMapStringEx("zh-CN", 0x02000000, text, -1, null!, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (size > 0)
        {
            var simplified = new StringBuilder(size);
            if (LCMapStringEx("zh-CN", 0x02000000, text, -1, simplified, size, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) > 0)
                text = simplified.ToString();
        }
        return Regex.Replace(text.ToLowerInvariant(), @"[\s\p{P}\p{S}]", "");
    }
    public static bool IsRisky(string text)
    {
        string value = Normalize(text);
        return RiskPatterns.Any(value.Contains);
    }
    public static bool Agrees(string first, string second)
    {
        first = Normalize(first); second = Normalize(second);
        if (first.Length == 0 || second.Length == 0) return false;
        var row = Enumerable.Range(0, second.Length + 1).ToArray();
        for (int i = 1; i <= first.Length; i++)
        {
            int diagonal = row[0]; row[0] = i;
            for (int j = 1; j <= second.Length; j++)
            {
                int old = row[j]; row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (first[i - 1] == second[j - 1] ? 0 : 1)); diagonal = old;
            }
        }
        return row[^1] <= Math.Max(first.Length, second.Length) * .1;
    }
}
