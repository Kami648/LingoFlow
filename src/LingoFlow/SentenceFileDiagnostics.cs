using System.Collections.Concurrent;
using System.Text.Json;
using LingoFlow.Services;

namespace LingoFlow;

internal static class SentenceFileDiagnostics
{
    internal static async Task<IReadOnlyList<string>> CheckDecoderAsync(AppSettings settings, string input)
    {
        var results = new List<string>();
        double? duration = await MediaDecoder.ProbeAsync(input, settings, CancellationToken.None);
        long samples = 0;
        double decoded = await MediaDecoder.DecodeAsync(input, settings, (part, _) => { samples += part.Length; return Task.CompletedTask; }, CancellationToken.None);
        if (samples == 0 || Math.Abs(samples / 16000d - decoded) > .0001 || duration is > 0 && Math.Abs(decoded - duration.Value) > .25) throw new Exception("Decoded audio timeline mismatch");
        results.Add("Media with spaces and Chinese in its path decodes to a complete 16 kHz mono timeline");
        using (var cancellation = new CancellationTokenSource())
        {
            bool cancelled = false;
            try
            {
                await MediaDecoder.DecodeAsync(input, settings, (_, _) => { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); return Task.CompletedTask; }, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) { cancelled = true; }
            if (!cancelled) throw new Exception("Decoder cancellation did not interrupt processing");
            results.Add("Cancellation interrupts decoding and closes its child process and pipe");
        }
        string noAudio = Path.Combine(AppPaths.Root, "no-audio.srt"); File.WriteAllText(noAudio, "1\n00:00:00,000 --> 00:00:01,000\ntext\n");
        bool rejected = false;
        try { await MediaDecoder.ProbeAsync(noAudio, settings, CancellationToken.None); }
        catch (IOException ex) { rejected = ex.Message.Contains("没有可翻译的音轨"); }
        if (!rejected) throw new Exception("File without an audio stream was not rejected");
        results.Add("Files without an audio stream return a clear error before model inference");
        string corrupt = Path.Combine(AppPaths.Root, "corrupt-media.bin"); File.WriteAllBytes(corrupt, [0, 1, 2, 3]); rejected = false;
        try { await MediaDecoder.ProbeAsync(corrupt, settings, CancellationToken.None); } catch (IOException) { rejected = true; }
        if (!rejected) throw new Exception("Corrupt media was not rejected");
        results.Add("Corrupt media returns an actionable error");
        string silent = Path.Combine(AppPaths.Root, "silent.wav"); File.WriteAllBytes(silent, Wave.Encode(new short[16000]));
        int silentSamples = 0;
        await MediaDecoder.DecodeAsync(silent, settings, (part, _) => { if (part.Any(n => n != 0)) throw new Exception("Silence changed during decode"); silentSamples += part.Length; return Task.CompletedTask; }, CancellationToken.None);
        if (silentSamples != 16000) throw new Exception("Silence timeline was not preserved");
        results.Add("A silent one-second file stays silent and preserves every sample");
        return results;
    }
    public static async Task CheckAsync(Action<bool, string> check)
    {
        static AudioChunk Clip(double start, bool end = false) => new(new short[64000], start, start + 4) { IsUtteranceEnd = end };
        var recognized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var translations = new ConcurrentQueue<string>(); var completed = new ConcurrentQueue<Subtitle>();
        await using (var queue = new SpeechWorkQueue((chunk, _) => Task.FromResult(new SpeechResult(chunk.Start == 0 ? "If I had known" : "you were coming, I would have waited.", "en")),
            (speech, _, _) => { translations.Enqueue(speech.Text); return Task.FromResult("如果我早知道你会来，我就会等你。"); }, accurate: true, joinUtterances: true))
        {
            queue.Recognized += _ => recognized.TrySetResult(); queue.Completed += completed.Enqueue;
            queue.Enqueue(Clip(0)); await recognized.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check(translations.IsEmpty && queue.Backlog.Seconds == 4, "Unfinished conditional stays in recognition buffer; source preview appears without premature translation");
            queue.Enqueue(Clip(4, true)); await queue.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            check(translations.Count == 1 && translations.Single() == "If I had known you were coming, I would have waited.", "Clauses spanning forced slices reach translator in one request");
            check(completed.Count == 1 && completed.Single().Start == 0 && completed.Single().End == 8 && queue.Backlog.Seconds == 0, "Merged utterance preserves timeline and releases every retained slice");
        }
        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); translations.Clear();
        await using (var queue = new SpeechWorkQueue((_, _) => Task.FromResult(new SpeechResult("まだ話が", "ja")),
            (s, _, _) => { translations.Enqueue(s.Text); return Task.FromResult("话还没说完"); }, joinUtterances: true))
        {
            queue.Recognized += _ => heard.TrySetResult(); queue.Enqueue(Clip(0)); await heard.Task.WaitAsync(TimeSpan.FromSeconds(2));
            queue.Enqueue(new([], 4, 4)); await queue.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            check(translations.Count == 1, "EOF or stop marker drains an unfinished final sentence without losing its text");
        }
        translations.Clear();
        await using (var queue = new SpeechWorkQueue((chunk, _) => Task.FromResult(new SpeechResult(chunk.Start == 0 ? "前半句" : "", "zh")),
            (s, _, _) => { translations.Enqueue(s.Text); return Task.FromResult("first half"); }, joinUtterances: true))
        {
            queue.Enqueue(Clip(0)); queue.Enqueue(Clip(4)); await queue.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            check(translations.Count == 1 && translations.Single() == "前半句", "Rejected non-speech closes held speech without adding a music or silence caption");
        }
        var slices = new List<AudioChunk>(); var segmenter = new AudioSegmenter(true, .008); segmenter.Ready += slices.Add;
        segmenter.Push(Enumerable.Repeat((short)5000, 16000).ToArray()); segmenter.Push(new short[4800]);
        check(slices.Count == 0, "A 300 ms mid-sentence pause does not finalize an accurate utterance");
        segmenter.Push(Enumerable.Repeat((short)5000, 16000).ToArray()); segmenter.Push(new short[16000]);
        check(slices.Count == 1 && slices[0].IsUtteranceEnd && slices[0].End > 3, "A real end pause closes both sides of a short hesitation together");
        var asset = MediaDecoder.Asset(new() { ModelDirectory = Path.Combine(AppPaths.Root, "models") });
        check(asset.Path.StartsWith(Path.Combine(AppPaths.Root, "models")) && asset.Sha256.Length == 64 && asset.Size == 109728040, "File decoder uses selected model storage and pinned official package hash");
    }
}

public partial class MainWindow
{
    internal static async Task CheckFileImportAsync(string models, string input, string report, bool cpu)
    {
        var settings = new AppSettings { FirstRunComplete = true, Route = "local", SourceLanguage = "auto", TargetLanguage = "zh", Accurate = true,
            ModelDirectory = Path.GetFullPath(models), Acceleration = cpu ? "cpu" : "auto", ExportDirectory = Path.Combine(AppPaths.Root, "file-export-" + Guid.NewGuid().ToString("N")) };
        var window = new MainWindow(settings);
        try
        {
            window.FileInputBox.Text = Path.GetFullPath(input);
            window.TranslateFile_Click(window.FileStartButton, new System.Windows.RoutedEventArgs());
            if (window.fileFlow == null) throw new Exception("Import was not started: " + window.FileStatusText.Text);
            await window.fileFlow.WaitAsync(TimeSpan.FromMinutes(10));
            var rows = window.fileArchive.Snapshot(); var files = Directory.GetFiles(settings.ExportDirectory, "*.srt");
            if (!window.fileFinished || window.importing || files.Length != 3 || rows.Count == 0 || rows.Any(s => s.IsPending || s.HasError)) throw new Exception("Import/export workflow failed: " + window.FileStatusText.Text);
            if (rows.Any(s => !TranslationLanguage.IsValid(s.Translation, "zh"))) throw new Exception("Invalid target language");
            var expected = new[] { SubtitleExport.Srt(rows), SubtitleExport.Srt(rows, SubtitleTrack.Original), SubtitleExport.Srt(rows, SubtitleTrack.Translation) };
            if (files.Any(file => !expected.Contains(File.ReadAllText(file)))) throw new Exception("Exported tracks differ from full archive");
            var decoderChecks = await SentenceFileDiagnostics.CheckDecoderAsync(settings, input);
            string completedStatus = window.FileStatusText.Text;
            // Exercise the same buttons as a user: interrupt a second import once
            // its first translation is complete, then export only finished rows.
            window.TranslateFile_Click(window.FileStartButton, new System.Windows.RoutedEventArgs());
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            while (window.CompletedFileRows().Length == 0 && window.importing) await Task.Delay(10, deadline.Token);
            if (!window.importing || window.CompletedFileRows().Length == 0) throw new Exception("Cancellation fixture finished before it could be interrupted");
            window.CancelFile_Click(window.FileCancelButton, new System.Windows.RoutedEventArgs());
            await window.fileFlow!.WaitAsync(TimeSpan.FromSeconds(20));
            if (window.fileFinished || window.importing || !window.FileExportButton.IsEnabled) throw new Exception("Cancelled import did not retain exportable completed subtitles");
            window.ExportFile_Click(window.FileExportButton, new System.Windows.RoutedEventArgs());
            var partialFiles = Directory.GetFiles(settings.ExportDirectory, "*部分*.srt");
            var partial = window.CompletedFileRows();
            if (partialFiles.Length != 3 || partial.Length == 0 || partialFiles.Any(f => File.ReadAllText(f).Contains("正在听完整句") || File.ReadAllText(f).Contains("正在翻译"))) throw new Exception("Partial export contains pending text or missing tracks");
            File.WriteAllText(report, JsonSerializer.Serialize(new { passed = true, input, count = rows.Count, decoderChecks, files, status = completedStatus,
                cancellation = new { passed = true, completedRows = partial.Length, partialFiles }, rows }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { window.fileCancellation?.Cancel(); window.quitting = true; window.tray?.Dispose(); window.Close(); }
    }
}
