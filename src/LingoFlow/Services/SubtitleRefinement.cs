using System.Globalization;
using System.Text.RegularExpressions;

namespace LingoFlow.Services;

public static class SubtitleImport
{
    public static IReadOnlyList<Subtitle> Read(string path)
    {
        if (new FileInfo(path).Length > 20_000_000) throw new IOException("字幕文件超过 20 MB，请先分段处理。");
        try
        {
            using var reader = new StreamReader(path, new System.Text.UTF8Encoding(false, true), true);
            return Parse(reader.ReadToEnd());
        }
        catch (System.Text.DecoderFallbackException ex) { throw new IOException("字幕编码无法读取，请先另存为 UTF-8 或带 BOM 的 UTF-16 SRT，避免乱码翻译。", ex); }
    }
    public static IReadOnlyList<Subtitle> Parse(string content)
    {
        var rows = new List<Subtitle>();
        foreach (string block in Regex.Split(content.Trim('\uFEFF', ' ', '\r', '\n').Replace("\r\n", "\n"), @"\n(?:[ \t]*\n)+"))
        {
            string[] lines = block.Split('\n'); int timeIndex = lines.Length > 0 && lines[0].Contains("-->") ? 0 : 1;
            if (timeIndex >= lines.Length) throw new IOException($"第 {rows.Count + 1} 段字幕缺少时间轴，请选择原文 SRT。");
            var match = Regex.Match(lines[timeIndex], @"^\s*(\d{2,}:[0-5]\d:[0-5]\d[,.]\d{3})\s*-->\s*(\d{2,}:[0-5]\d:[0-5]\d[,.]\d{3})(?:\s.*)?$");
            static double Seconds(string t) { string[] p = t.Replace(',', '.').Split(':'); return double.Parse(p[0], CultureInfo.InvariantCulture) * 3600 + int.Parse(p[1]) * 60 + double.Parse(p[2], CultureInfo.InvariantCulture); }
            if (!match.Success) throw new IOException($"第 {rows.Count + 1} 段字幕时间轴无效。");
            double start = Seconds(match.Groups[1].Value), end = Seconds(match.Groups[2].Value);
            string text = string.Join("\n", lines.Skip(timeIndex + 1)).Trim();
            if (end <= start || !double.IsFinite(end) || text.Length == 0) throw new IOException($"第 {rows.Count + 1} 段字幕缺少原文或时间范围无效。");
            bool missing = text.StartsWith("（此位置原文缺失）") || text.StartsWith("（此位置语音识别失败）") || text.StartsWith("（此片段尚未完成识别）") || text.StartsWith("（此片段未识别出文字）") || text.StartsWith("（此片段识别失败");
            rows.Add(new(DateTimeOffset.Now, start, end, text, "", "auto", 0) { Sequence = rows.Count + 1, SourceMissing = missing, HasError = missing });
        }
        if (rows.Count == 0) throw new IOException("字幕没有可翻译的原文。");
        return rows.OrderBy(s => s.Start).ToArray();
    }
}

public static class SubtitleRefinement
{
    public static IReadOnlyList<Subtitle> Group(IEnumerable<Subtitle> rows)
    {
        var result = new List<Subtitle>(); var held = new List<Subtitle>();
        void Flush()
        {
            if (held.Count == 0) return;
            result.Add(held[0] with { Original = SubtitleText.Join(held.Select(s => s.Original)), Translation = "", End = held.Max(s => s.End),
                Sequence = result.Count + 1, IsPending = false, HasError = false, FailureReason = "" });
            held.Clear();
        }
        foreach (var row in rows.OrderBy(s => s.Start))
        {
            if (row.SourceMissing || string.IsNullOrWhiteSpace(row.Original))
            {
                Flush(); result.Add(row with { Sequence = result.Count + 1, HasError = true, SourceMissing = true, IsPending = false,
                    Original = string.IsNullOrWhiteSpace(row.Original) ? "（此位置原文缺失）" : row.Original,
                    Translation = string.IsNullOrWhiteSpace(row.Translation) ? "（原文缺失，请核对音轨）" : row.Translation }); continue;
            }
            if (held.Count > 0 && (row.Start - held[^1].End > 1.5 || row.End - held[0].Start > 24 || held.Sum(s => s.Original.Length) + row.Original.Length > 700)) Flush();
            held.Add(row);
            // Two adjacent source cues give a cut-off clause some forward context.
            if (held.Count >= 2 && Regex.IsMatch(row.Original.TrimEnd(), "[。！？.!?][\"'”’）)]*$")) Flush();
        }
        Flush(); return result;
    }
    internal static async Task<IReadOnlyList<Subtitle>> RunAsync(IReadOnlyList<Subtitle> original, AppSettings settings,
        Func<string, IReadOnlyList<Subtitle>, CancellationToken, Task<string>> translate, Action<Subtitle> update,
        Action<int, int>? progress, CancellationToken token)
    {
        var groups = Group(original); var result = new List<Subtitle>();
        for (int i = 0; i < groups.Count; i++)
        {
            token.ThrowIfCancellationRequested(); var row = groups[i];
            if (row.SourceMissing) { result.Add(row); update(row); progress?.Invoke(i + 1, groups.Count); continue; }
            var context = groups.Skip(Math.Max(0, i - 2)).Take(Math.Min(groups.Count, i + 3) - Math.Max(0, i - 2)).Where(s => s.Sequence != row.Sequence && !s.SourceMissing).ToArray();
            string? translation = null; Exception? failure = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try { translation = await translate(row.Original, context, token); break; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { failure = ex; if (attempt == 0) await Task.Delay(250, token); }
            }
            row = row with { Translation = translation ?? "（二次翻译失败，原文已保留）", HasError = translation == null, FailureReason = translation == null ? failure?.Message ?? "未知错误" : "", IsPending = false };
            result.Add(row); update(row); progress?.Invoke(i + 1, groups.Count);
        }
        return result;
    }
    public static async Task RunAsync(IReadOnlyList<Subtitle> original, AppSettings settings, Action<Subtitle> update,
        IProgress<string>? status, Action<int, int>? progress, CancellationToken token)
    {
        var s = settings.Copy(); s.Accurate = true; s.StreamTranslation = false; s.SubtitleRefinement = true;
        TranslationPipeline.Validate(s);
        if (!s.LocalTranslation)
        {
            using var cloud = new InferenceClient();
            await Task.Run(() => CloudRefinement.RunAsync(original, s, (batch, ct) => cloud.RefineBatchAsync(batch, s, ct), update, status, progress, token), token);
            return;
        }
        if (s.LocalTranslation) await new ModelInstaller().PrepareAsync(s, new Progress<InstallProgress>(p => status?.Report(p.Message)), token, translationOnly: true);
        await using var servers = new LocalServers(); using var client = new InferenceClient();
        await servers.StartAsync(s, status, token, translationOnly: true);
        client.Notice += message => status?.Report(message);
        await RunAsync(original, s, (text, context, ct) => client.TranslateAsync(text, s.SourceLanguage, s, servers, context, ct), update, progress, token);
    }
}
