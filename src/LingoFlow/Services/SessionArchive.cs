namespace LingoFlow.Services;

public sealed class SessionArchive
{
    private readonly SortedDictionary<long, Subtitle> rows = [];
    private readonly object gate = new();
    public string Source { get; private set; } = "auto";
    public string Target { get; private set; } = "zh";
    public int Count { get { lock (gate) return rows.Count; } }
    public void Reset(string source, string target) { lock (gate) { rows.Clear(); Source = source; Target = target; } }
    public void Upsert(Subtitle subtitle) { lock (gate) rows[subtitle.Sequence] = subtitle; }
    public IReadOnlyList<Subtitle> Snapshot() { lock (gate) return rows.Values.ToArray(); }
}

public record SubtitleFiles(string Bilingual, string Original, string Translation, string? Failures = null, string? Corrections = null);

public static class SubtitleFilesExport
{
    public static string DirectoryFor(AppSettings settings) => string.IsNullOrWhiteSpace(settings.ExportDirectory) ? Path.Combine(AppContext.BaseDirectory, "subtitles") : Path.GetFullPath(settings.ExportDirectory);
    public static SubtitleFiles Write(string directory, IEnumerable<Subtitle> rows, string source, string target, string? prefix = null)
    {
        var subtitles = rows.OrderBy(s => s.Sequence).ToArray();
        prefix ??= "译流-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        if (prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new IOException("导出名称包含无效字符。");
        Directory.CreateDirectory(directory);
        string sourceName = source == "auto" ? "自动识别" : TranslationLanguage.TargetName(source).Replace("简体", "");
        string targetName = TranslationLanguage.TargetName(target).Replace("简体", "");
        var failures = subtitles.Where(s => s.HasError).ToArray();
        var corrections = subtitles.Where(s => s.BeforeRefinement.Length > 0 && (s.BeforeRefinement != s.Original || s.SourceUncertain))
            .Select(s => new { s.Sequence, s.Start, s.End, Original = s.BeforeRefinement, Corrected = s.Original, s.SourceUncertain }).ToArray();
        string[] Paths(string name) => new[] { Path.Combine(directory, name + "-双语.srt"), Path.Combine(directory, name + "-原文-" + sourceName + ".srt"), Path.Combine(directory, name + "-译文-" + targetName + ".srt") }
            .Concat(failures.Length > 0 ? [Path.Combine(directory, name + "-失败片段.json")] : Array.Empty<string>())
            .Concat(corrections.Length > 0 ? [Path.Combine(directory, name + "-原文校对记录.json")] : Array.Empty<string>()).ToArray();
        string[] paths = Paths(prefix); int suffix = 2;
        while (paths.Any(File.Exists)) paths = Paths(prefix + "-" + suffix++);
        string[] contents = new[] { SubtitleExport.Srt(subtitles), SubtitleExport.Srt(subtitles, SubtitleTrack.Original), SubtitleExport.Srt(subtitles, SubtitleTrack.Translation) }
            .Concat(failures.Length > 0 ? [System.Text.Json.JsonSerializer.Serialize(failures, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })] : Array.Empty<string>())
            .Concat(corrections.Length > 0 ? [System.Text.Json.JsonSerializer.Serialize(corrections, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })] : Array.Empty<string>()).ToArray();
        var temporary = new List<string>(); var created = new List<string>();
        try
        {
            for (int i = 0; i < paths.Length; i++)
            {
                string temp = Path.Combine(directory, ".lingoflow-" + Guid.NewGuid().ToString("N") + ".tmp"); temporary.Add(temp);
                using var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                byte[] bom = new System.Text.UTF8Encoding(true).GetPreamble(), bytes = System.Text.Encoding.UTF8.GetBytes(contents[i]);
                file.Write(bom); file.Write(bytes); file.Flush(true);
            }
            for (int i = 0; i < paths.Length; i++) { File.Move(temporary[i], paths[i]); created.Add(paths[i]); }
        }
        catch { foreach (string path in created) File.Delete(path); throw; }
        finally { foreach (string path in temporary) if (File.Exists(path)) File.Delete(path); }
        return new(paths[0], paths[1], paths[2], failures.Length > 0 ? paths[3] : null, corrections.Length > 0 ? paths[^1] : null);
    }
}
