using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using LingoFlow.Services;

namespace LingoFlow;

internal static class UpdateDiagnostics
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request); }
    private static HttpResponseMessage Reply(string text) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text }, finish_reason = "stop" } } }), Encoding.UTF8, "application/json") };
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static async Task CheckAsync(Action<bool, string> check)
    {
        check(new AppSettings { Route = "cloud" }.Route == "hybrid" && typeof(AppSettings).GetProperty("Speech") == null, "Legacy cloud settings migrate and no cloud speech endpoint is configurable");
        int speechRequests = 0;
        using (var client = new InferenceClient(new Handler(_ => { speechRequests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); })))
        {
            bool rejected = false;
            try { await client.RecognizeAsync(new(new short[1600], 0, .1), new(), new() { SpeechUrl = "https://example.com" }, CancellationToken.None); }
            catch (InvalidOperationException) { rejected = true; }
            check(rejected && speechRequests == 0, "Recognition rejects a non-loopback server before transmitting audio");
        }
        check(!TranslationLanguage.IsValid("啊，everyone is...それはそう！それはそう！", "zh"), "Screenshot mixed Chinese English Japanese output fails target validation");
        check(!TranslationLanguage.IsValid("啊，ｅｖｅｒｙｏｎｅ ｉｓ...", "zh") && !TranslationLanguage.IsValid("啊，`それはそう`", "zh"), "Full-width Latin and quoted foreign speech cannot bypass target validation");
        check(TranslationLanguage.IsValid("啊，大家都赚得不少……确实如此！确实如此！", "zh"), "Chinese translation with repeated emphasis is accepted");
        check(TranslationLanguage.IsValid("请在 Microsoft Edge 使用 DeepSeek API。", "zh"), "Necessary product names and acronyms remain valid in Chinese");
        check(!TranslationLanguage.IsValid("everyone is happy", "zh") && !TranslationLanguage.IsValid("你好，这是测试", "en"), "Wrong ordinary-language text is rejected for Chinese and English targets");
        check(TranslationLanguage.IsValid("こんにちは、世界。", "ja") && TranslationLanguage.IsValid("안녕하세요.", "ko") && !TranslationLanguage.IsValid("こんにちは", "ko"), "Japanese and Korean target checks distinguish their scripts");
        int neutralRequests = 0;
        using (var client = new InferenceClient(new Handler(_ => { neutralRequests++; return Task.FromResult(Reply("中文")); })))
        {
            check(await client.TranslateAsync("M", "ja", new(), new(), [], CancellationToken.None) == "M" && neutralRequests == 0, "Language-neutral fragments do not trigger repeated model requests");
        }
        bool localGrammar = false, cloudGrammar = true, otherGrammar = true;
        using (var client = new InferenceClient(new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            bool hasGrammar = body.RootElement.TryGetProperty("grammar", out var grammar);
            if (request.RequestUri!.IsLoopback && hasGrammar) localGrammar = grammar.GetString()!.Contains("root ::= item+");
            else if (request.RequestUri.IsLoopback) otherGrammar = hasGrammar;
            else cloudGrammar = hasGrammar;
            return Reply(hasGrammar || !request.RequestUri.IsLoopback ? "你好。" : "Hello.");
        })))
        {
            var servers = new LocalServers { TranslationUrl = "http://127.0.0.1:9989" };
            await client.TranslateAsync("こんにちは。", "ja", new() { Route = "local", TargetLanguage = "zh" }, servers, [], CancellationToken.None);
            await client.TranslateAsync("こんにちは。", "ja", new() { Route = "hybrid", TargetLanguage = "zh", Translation = new() { BaseUrl = "https://example.com/v1", Model = "test" } }, servers, [], CancellationToken.None);
            await client.TranslateAsync("こんにちは。", "ja", new() { Route = "local", TargetLanguage = "en" }, servers, [], CancellationToken.None);
            check(localGrammar && !cloudGrammar && !otherGrammar, "Chinese output grammar applies only to the bundled local Chinese translator");
        }
        const string original = "あ、全員もがっぽり…それはそう！それはそう！";
        var settings = new AppSettings { Route = "hybrid", SourceLanguage = "ja", TargetLanguage = "zh", MixedLanguage = false, StreamTranslation = true };
        var visible = new List<string>(); int attempts = 0; bool repairIsNonStreaming = false, boundLanguages = false;
        using (var client = new InferenceClient(new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            string content = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            using var data = JsonDocument.Parse(content[content.IndexOf('{')..]);
            boundLanguages = data.RootElement.GetProperty("source_language").GetString() == "ja" && data.RootElement.GetProperty("target_language").GetString() == "zh";
            if (++attempts == 1)
            {
                string sse = "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = "啊，" } } } }) + "\n\n" +
                    "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = "everyone is...それはそう！" } } } }) + "\n\ndata: [DONE]\n\n";
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8) };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream"); return response;
            }
            repairIsNonStreaming = !body.RootElement.GetProperty("stream").GetBoolean();
            return Reply("啊，大家都赚得不少……确实如此！确实如此！");
        })))
        {
            string result = await client.TranslateAsync(original, "en", settings, new(), [], CancellationToken.None, visible.Add);
            check(attempts == 2 && repairIsNonStreaming && TranslationLanguage.IsValid(result, "zh"), "Mixed output is repaired before committing a Chinese subtitle");
            check(boundLanguages, "Explicit Japanese source and Chinese target stay bound even if detected language differs");
            check(visible.All(text => TranslationLanguage.IsValid(text, "zh")), "Mixed-language streaming drafts never reach the display callback");
        }
        attempts = 0; bool recovered = false; var failed = Signal(); var rows = new ConcurrentQueue<Subtitle>();
        using (var client = new InferenceClient(new Handler(_ => { Interlocked.Increment(ref attempts); return Task.FromResult(Reply(recovered ? "确实如此！" : "everyone is...それはそう！")); })))
        await using (var queue = new SpeechWorkQueue((_, _) => Task.FromResult(new SpeechResult(original, "ja")), (speech, context, token) => client.TranslateAsync(speech.Text, speech.Language, settings, new(), context, token)))
        {
            queue.Completed += row => { rows.Enqueue(row); if (row.HasError) failed.TrySetResult(); };
            queue.Enqueue(new(new short[16000], 0, 1)); await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            check(attempts == 3 && queue.Backlog.Failed == 1 && rows.Single().Original == original, "Exhausted language repairs preserve the original without multiplying automatic retries");
            var drain = queue.WaitForIdleAsync(CancellationToken.None);
            check(!drain.IsCompleted, "Graceful stop continues waiting for failed work instead of discarding it");
            recovered = true; queue.RetryFailed(); await drain.WaitAsync(TimeSpan.FromSeconds(4));
            check(rows.Last().Translation == "确实如此！" && !rows.Last().HasError, "Manual retry recovers the retained sentence and completes graceful drain");
        }
        var release = Signal(); var started = Signal(); rows.Clear();
        await using (var queue = new SpeechWorkQueue(async (_, token) => { started.TrySetResult(); await release.Task.WaitAsync(token); return new SpeechResult("last original", "en"); }, (_, _, _) => Task.FromResult("最后一句")))
        {
            queue.Completed += rows.Enqueue; queue.Enqueue(new(new short[16000], 0, 1)); await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var drain = queue.WaitForIdleAsync(CancellationToken.None); check(!drain.IsCompleted, "Stop waits for the last captured utterance");
            release.TrySetResult(); await drain.WaitAsync(TimeSpan.FromSeconds(3));
            check(rows.Single().Translation == "最后一句" && queue.Backlog.Seconds == 0, "Drain completes only after final subtitle callback and audio release");
        }
        await using (var queue = new SpeechWorkQueue(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new SpeechResult("", "auto"); }, (_, _, _) => Task.FromResult("")))
        {
            queue.Enqueue(new(new short[16000], 2, 3)); var unfinished = await queue.StopAndSnapshotAsync();
            check(unfinished.Count == 1 && unfinished[0].Start == 2 && unfinished[0].HasError && !queue.Enqueue(new(new short[1600], 3, 3.1)), "Immediate finish records unfinished audio explicitly and closes input");
        }
        var archive = new SessionArchive(); archive.Reset("ja", "zh");
        for (int i = 1; i <= 1205; i++) archive.Upsert(new(DateTimeOffset.Now, i, i + .8, "日本語" + i, "中文" + i, "ja", 0) { Sequence = i });
        archive.Upsert(archive.Snapshot()[0] with { Translation = "中文修正1" });
        check(archive.Count == 1205 && archive.Snapshot()[0].Translation == "中文修正1", "Full-session archive keeps over 500 rows and replaces drafts without duplicates");
        string directory = Path.Combine(AppPaths.Root, "three-subtitle-export");
        var files = SubtitleFilesExport.Write(directory, archive.Snapshot(), "ja", "zh", "完整会话");
        string both = File.ReadAllText(files.Bilingual), source = File.ReadAllText(files.Original), target = File.ReadAllText(files.Translation);
        check(both.Contains("日本語1205") && both.Contains("中文1205") && source.Contains("日本語1205") && !source.Contains("中文") && target.Contains("中文1205") && !target.Contains("日本語"), "Three exported tracks contain every row and their intended language content");
        string[] Times(string text) => Regex.Matches(text, @"\d\d:\d\d:\d\d,\d{3} --> [^\n]+").Select(m => m.Value).ToArray();
        check(Times(both).Length == 1205 && Times(both).SequenceEqual(Times(source)) && Times(source).SequenceEqual(Times(target)), "Bilingual source and translation tracks have identical complete cue timing");
        var again = SubtitleFilesExport.Write(directory, archive.Snapshot(), "ja", "zh", "完整会话");
        check(again.Bilingual != files.Bilingual && File.ReadAllText(files.Bilingual) == both && !Directory.EnumerateFiles(directory, "*.tmp").Any(), "Repeated exports preserve earlier files and leave no temporary artifacts");
        check(File.ReadAllBytes(files.Bilingual).Take(3).SequenceEqual(new byte[] { 239, 187, 191 }), "Exported SRT uses UTF-8 BOM for common Windows players");
        var palette = ColorSettings.Light(); palette.Original = "#123456"; palette.Translation = "#654321";
        settings.Colors = palette; settings.ExportOnStop = false; settings.ExportDirectory = directory; SettingsStore.Save(settings);
        var loaded = SettingsStore.Load();
        check(loaded.Colors.Original == "#123456" && loaded.Colors.Translation == "#654321" && !loaded.ExportOnStop && loaded.ExportDirectory == directory, "Colors and export preferences survive restart");
        var independent = settings.Copy(); independent.Colors.Accent = "#000000";
        check(settings.Colors.Accent != independent.Colors.Accent, "Palette session snapshots are independent");
        loaded.Colors.Panel = "invalid"; Theme.Normalize(loaded.Colors);
        check(loaded.Colors.Panel == new ColorSettings().Panel, "Invalid saved colors fall back safely");
        Theme.Apply(palette);
        check(((SolidColorBrush)Application.Current.Resources["Bg"]).Color == Theme.Color(palette.Background), "Theme applies custom window background resources");
        var overlay = new OverlayWindow(settings);
        check(((SolidColorBrush)overlay.OriginalText.Foreground).Color == Theme.Color("#123456") && ((SolidColorBrush)overlay.TranslatedText.Foreground).Color == Theme.Color("#654321"), "Overlay original and translation colors use independent preferences");
        overlay.Close(); Theme.Apply(new());
    }

    public static async Task RetranslateAsync(string input, string models, string report)
    {
        var settings = new AppSettings { Route = "local", ModelDirectory = Path.GetFullPath(models), WhisperModel = "tiny", TranslationModel = LocalTranslationModels.Default, SourceLanguage = "ja", TargetLanguage = "zh", MixedLanguage = false };
        await new ModelInstaller().PrepareAsync(settings, null, CancellationToken.None);
        await using var servers = new LocalServers(); using var client = new InferenceClient(); await servers.StartAsync(settings, null, CancellationToken.None);
        var sourceRows = new List<Subtitle> { new(DateTimeOffset.Now, 0, 3, "あ、全員もがっぽり…それはそう！それはそう！", "", "ja", 0) { Sequence = 1 } };
        using var doc = JsonDocument.Parse(File.ReadAllText(input));
        sourceRows.AddRange(doc.RootElement.GetProperty("results").Deserialize<Subtitle[]>()!);
        var results = new List<Subtitle>(); int repairs = 0, drafts = 0, failed = 0;
        var rejected = new List<string>(); client.RejectedOutput += rejected.Add;
        client.Notice += message => { if (message.Contains("重新翻译")) repairs++; };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var source in sourceRows)
        {
            var parts = new List<string>(); var context = results.Where(s => !s.HasError).TakeLast(2).ToList();
            try
            {
                foreach (string phrase in SubtitleText.Split(source.Original, false))
                {
                    var text = await client.TranslateAsync(phrase, "ja", settings, servers, context, CancellationToken.None, partial => { if (!TranslationLanguage.IsValid(partial, "zh")) throw new Exception("Mixed draft escaped validation"); drafts++; });
                    parts.Add(text); context.Add(source with { Original = phrase, Translation = text });
                }
                results.Add(source with { Sequence = results.Count + 1, Translation = SubtitleText.Join(parts), HasError = false, IsPending = false });
            }
            catch (TranslationLanguageException)
            { failed++; results.Add(source with { Sequence = results.Count + 1, Translation = "（未能得到合格中文译文，原文保留，可重试）", HasError = true, IsPending = false }); }
            File.WriteAllText(Path.ChangeExtension(report, ".progress.json"), JsonSerializer.Serialize(new { completed = results.Count, total = sourceRows.Count, failed, repairs, seconds = clock.Elapsed.TotalSeconds }));
            File.WriteAllText(Path.ChangeExtension(report, ".partial.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }
        string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "Hy-MT2-日译中回归");
        var files = SubtitleFilesExport.Write(directory, results.Skip(1), "ja", "zh", "日译中-本地校验");
        File.WriteAllText(report, JsonSerializer.Serialize(new { total = results.Count, failed, repairs, drafts, rejected, seconds = clock.Elapsed.TotalSeconds, valid = results.All(s => s.HasError || TranslationLanguage.IsValid(s.Translation, "zh")), files, results }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
