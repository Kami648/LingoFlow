using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using LingoFlow.Services;

namespace LingoFlow;

internal static class Update021Diagnostics
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request); }
    private static HttpResponseMessage Reply(string content) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content }, finish_reason = "stop" } } })) };
    private static AudioChunk Clip(int index) => new(new short[3200], index, index + .2);
    public static async Task CheckAsync(Action<bool, string> check)
    {
        var rows = new ConcurrentQueue<Subtitle>(); int pauses = 0; bool fail = true;
        await using (var queue = new SpeechWorkQueue((chunk, _) => Task.FromResult(new SpeechResult(chunk.Start.ToString(), "en")),
            (s, _, _) => s.Text == "0" && fail ? throw new TranslationLanguageException("invalid language") : Task.FromResult("译" + s.Text), continueOnFailure: true))
        {
            queue.Completed += rows.Enqueue; queue.PauseRequested += _ => pauses++;
            for (int i = 0; i < 3; i++) queue.Enqueue(Clip(i));
            await queue.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            check(pauses == 0 && rows.Count == 3 && rows.First().HasError && rows.Last().Original == "2" && queue.Backlog.Failed == 1 && queue.Backlog.Seconds == 0,
                "A failed translation is parked with its original while later captions finish without stopping capture or batch drain");
            fail = false; check(queue.RetryFailed() == 1, "Parked translation can be retried without replaying successful later rows");
            await queue.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            check(rows.Last().Sequence == 1 && !rows.Last().HasError && queue.Backlog.Failed == 0, "Late retry repairs the original row with its stable ID");
        }
        rows.Clear(); fail = true;
        await using (var queue = new SpeechWorkQueue((c, _) => Task.FromResult(new SpeechResult(c.Start == 0 ? "first half" : "second half", "en")),
            (s, _, _) => fail ? throw new TranslationLanguageException("fixture") : Task.FromResult("完整译文"), joinUtterances: true, continueOnFailure: true))
        {
            queue.Completed += rows.Enqueue;
            queue.Enqueue(new(new short[3200], 0, .2) { IsUtteranceEnd = false }); queue.Enqueue(new(new short[3200], .2, .4));
            await queue.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            fail = false; int retries = queue.RetryFailed(); await queue.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            check(retries == 1 && rows.Count == 2 && rows.Last().Original == "first half second half" && rows.Last().Translation == "完整译文", "Merged failed utterances retry once as a parent without separately translating their audio member slices");
        }
        rows.Clear(); bool failRecognition = true;
        await using (var queue = new SpeechWorkQueue((chunk, _) => chunk.Start == 0 && failRecognition ? throw new HttpRequestException("asr failure", null, HttpStatusCode.Forbidden) : Task.FromResult(new SpeechResult("source" + chunk.Start, "en")),
            (s, _, _) => Task.FromResult("译" + s.Text), joinUtterances: true, continueOnFailure: true))
        {
            queue.Completed += rows.Enqueue; queue.Enqueue(Clip(0)); queue.Enqueue(Clip(1));
            await queue.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            check(rows.First().SourceMissing && !rows.Last().HasError && queue.RetainedFailedAudioSeconds > 0, "ASR failure is visible, keeps retryable audio and does not prevent later speech translation");
            failRecognition = false; queue.RetryFailed(); await queue.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            check(rows.Last().Sequence == 1 && rows.Last().Original == "source0" && !rows.Last().HasError, "Old ASR retry is not merged into a newer utterance or overlap-deduplicated against future speech");
        }
        await using (var queue = new SpeechWorkQueue((_, _) => throw new HttpRequestException("unavailable", null, HttpStatusCode.Forbidden), (_, _, _) => throw new Exception("unreachable"), continueOnFailure: true))
        {
            for (int i = 0; i < 40; i++)
            {
                queue.Enqueue(new(new short[32000], i * 2, i * 2 + 2));
                await queue.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            }
            check(queue.RetainedFailedAudioSeconds <= 60 && queue.Backlog.Failed == 40 && queue.Backlog.Seconds == 0,
                "Repeated ASR failures bound retained audio to one minute while preserving all failure positions");
        }
        var imported = SubtitleImport.Parse("1\n25:00:00,100 --> 25:00:02,200\nIf I had known\n\n2\n25:00:02,200 --> 25:00:04,900\nyou were coming, I would have waited.\n");
        var groups = SubtitleRefinement.Group(imported);
        check(groups.Count == 1 && groups[0].Original == "If I had known you were coming, I would have waited." && groups[0].Start == 90000.1 && groups[0].End == 90004.9,
            "Imported adjacent SRT fragments are retranslated as one complete conditional while preserving the source time range");
        check(SubtitleExport.Srt(imported, SubtitleTrack.Original).Contains("25:00:00,100"), "SRT times over 24 hours do not wrap to the next day");
        bool badSrt = false; try { SubtitleImport.Parse("1\n00:00:01,000 --> 00:00:00,000\nbad"); } catch (IOException) { badSrt = true; }
        check(badSrt, "Invalid SRT ranges fail clearly instead of dropping subtitle cues");
        var source = imported.Concat(new[] { new Subtitle(DateTimeOffset.Now, 90040, 90043, "next sentence", "", "en", 0) { Sequence = 3 } }).ToArray();
        var results = await SubtitleRefinement.RunAsync(source, new(), (text, _, _) => text.Contains("If I") ? throw new TranslationLanguageException("fixture") : Task.FromResult("下一句"), _ => { }, null, CancellationToken.None);
        check(results.Count == 2 && results[0].HasError && results[1].Translation == "下一句", "AI refinement records a failed group and continues through the rest of the imported subtitles");
        using (var cancel = new CancellationTokenSource())
        {
            int processed = 0; bool cancelled = false;
            try { await SubtitleRefinement.RunAsync(source, new(), (_, _, _) => Task.FromResult("译文"), _ => { processed++; cancel.Cancel(); }, null, cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && processed == 1 && source[0].Original == "If I had known", "Cancelling AI refinement retains processed groups without modifying its original subtitle input");
        }
        string root = Path.Combine(AppPaths.Root, "failure-export"); var files = SubtitleFilesExport.Write(root, results, "en", "zh", "failure-fixture");
        check(files.Failures != null && File.Exists(files.Failures) && File.ReadAllText(files.Original).Contains("If I had known"), "Exports retain failed originals in SRT and write a separate timestamped failure report");
        check(ModelInstaller.Plan(new() { Route = "local" }, true, translationOnly: true).All(a => !a.Name.Contains("Whisper") && !a.Name.Contains("VAD")) && ModelInstaller.Plan(new() { Route = "hybrid" }, false, translationOnly: true).Count == 0,
            "Subtitle refinement only prepares translation assets and needs no speech model for cloud text translation");
        var overlay = new OverlayWindow(new()); int start = 0, pause = 0, stop = 0;
        overlay.StartRequested += () => start++; overlay.PauseRequested += () => pause++; overlay.StopRequested += () => stop++;
        overlay.SetTransportState(false, false, false, false); overlay.OverlayStartButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        overlay.SetTransportState(true, false, false, false); overlay.OverlayPauseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); overlay.OverlayStopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(start == 1 && pause == 1 && stop == 1 && !overlay.OverlayStartButton.IsEnabled, "Floating subtitle buttons dispatch start pause and stop with correct active-state controls");
        overlay.SetTransportState(true, true, false, false);
        check(overlay.OverlayStartButton.IsEnabled && overlay.OverlayStartButton.Content.ToString()!.Contains("继续") && !overlay.OverlayPauseButton.IsEnabled, "Paused overlay enables resume and prevents duplicate pause requests");
        overlay.Close();
    }
}

public partial class MainWindow
{
    internal static async Task CheckRefinementAsync(string models, string report, bool cpu)
    {
        Directory.CreateDirectory(AppPaths.Root);
        string original = "1\n00:00:01,100 --> 00:00:03,200\nIf I had known\n\n2\n00:00:03,200 --> 00:00:05,900\nyou were coming, I would have waited.\n\n3\n00:00:30,000 --> 00:00:34,000\nThe Token budget is 100 Tokens.\n";
        string input = Path.Combine(AppPaths.Root, "原文 sample.srt"); File.WriteAllText(input, original);
        var settings = new AppSettings { FirstRunComplete = true, Route = "local", SourceLanguage = "en", TargetLanguage = "zh", Accurate = true,
            ModelDirectory = Path.GetFullPath(models), Acceleration = cpu ? "cpu" : "auto" };
        var window = new MainWindow(settings);
        var before = System.Diagnostics.Process.GetProcessesByName("whisper-server").Select(p => p.Id).ToHashSet();
        var unexpectedlyStartedSpeech = new HashSet<int>();
        try
        {
            window.SetRefineInput(SubtitleImport.Read(input), "原文 sample", "en", "zh");
            window.refineSource = "en"; window.refineTarget = "zh"; window.refining = true; window.refineCancellation = new(); window.SetControls();
            if (window.StartButton.IsEnabled || window.FileStartButton.IsEnabled) throw new Exception("Other model jobs were not disabled during refinement");
            string output = Path.Combine(AppPaths.Root, "refined-output");
            window.refineFlow = window.RunRefinementAsync(settings, output, window.refineCancellation.Token);
            while (!window.refineFlow.IsCompleted)
            {
                foreach (var process in System.Diagnostics.Process.GetProcessesByName("whisper-server"))
                { using (process) if (!before.Contains(process.Id)) unexpectedlyStartedSpeech.Add(process.Id); }
                await Task.Delay(20);
            }
            await window.refineFlow;
            var rows = window.refined.ToArray(); var files = Directory.Exists(output) ? Directory.GetFiles(output, "*.srt") : [];
            if (!window.refineFinished || rows.Length != 2 || rows.Any(s => s.HasError) || files.Length != 3) throw new Exception("Native refinement failed: " + window.RefineStatusText.Text + "\n" + JsonSerializer.Serialize(rows));
            if (rows.Any(s => s.Translation.Contains("LFTERM")) || !TranslationLanguage.IsValid(rows[1].Translation, "zh", rows[1].Original, "en", allowModelTerms: true)) throw new Exception("Prompt-based translation was invalid");
            if (File.ReadAllText(input) != original || unexpectedlyStartedSpeech.Count != 0 || !window.StartButton.IsEnabled) throw new Exception("Refinement changed its input, loaded a speech model, or failed to release UI controls");
            // Render the actual finished page without activating a visible window.
            window.SwitchPage("refine"); var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1194, 811)); content.Arrange(new Rect(0, 0, 1194, 811)); content.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1194, 811, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(content);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var outputImage = File.Create(Path.ChangeExtension(report, ".png"))) png.Save(outputImage);
            File.WriteAllText(report, JsonSerializer.Serialize(new { passed = true, inputUnchanged = true, noSpeechModelsStarted = true, files, rows }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { window.refineCancellation?.Cancel(); window.quitting = true; window.tray?.Dispose(); window.Close(); }
    }
}
