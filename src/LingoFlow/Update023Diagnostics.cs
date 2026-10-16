using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LingoFlow.Services;

namespace LingoFlow;

internal static class Update023Diagnostics
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request); }
    private sealed class Immediate<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    private static HttpResponseMessage Reply(string content, string finish = "stop") => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content }, finish_reason = finish } } })) };
    private static Subtitle Row(int n, string text) => new(DateTimeOffset.Now, n * 2, n * 2 + 1.9, text, "", "en", 0) { Sequence = n };
    private static string Answer(RefinementBatch batch) => JsonSerializer.Serialize(new { cues = batch.Rows.Where(r => !r.SourceMissing).Select(r =>
        new { id = r.Sequence, source = r.Sequence == 1 ? "The knight arrived." : r.Original, translation = "骑士已经到达。", uncertain = r.Sequence == 3 }) });
    public static async Task CheckAsync(Action<bool, string> check)
    {
        var s = new AppSettings { SourceLanguage = "en", TargetLanguage = "zh", Translation = new() { BaseUrl = "https://example.com/v1", Model = "deepseek-flash" } };
        check(s.RefineContextTokens == (1_000_000 + 1_000_000 + 1_050_000 + 1_048_576 + 1_000_000) / 5 && CloudRefinement.Limits(s).Context == 1_000_000,
            "Representative official context average is the default and a known model's lower limit still applies");
        var original = new[] { Row(1, "The night arrived."), Row(2, "The knight carried a sword."), Row(3, "I heard a name.") };
        var output = new List<Subtitle>(); int calls = 0;
        using (var client = new InferenceClient(new Handler(async request =>
        {
            calls++; using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            string message = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            check(message.Contains("The night arrived.") && message.Contains("The knight carried a sword.") && message.Contains("将英语翻译为简体中文"),
                "A single cloud request includes the entire subtitle document and concrete selected language direction");
            check(body.RootElement.GetProperty("max_tokens").GetInt32() > 0 && !body.RootElement.GetProperty("stream").GetBoolean(), "Document requests reserve structured response space independently of realtime streaming");
            return Reply(Answer(CloudRefinement.Batch(original, 0, original.Length, [])));
        })))
            await CloudRefinement.RunAsync(original, s, (batch, ct) => client.RefineBatchAsync(batch, s, ct), output.Add, null, null, CancellationToken.None);
        check(calls == 1 && output.Count == 3 && output[0].Original == "The knight arrived." && output[0].BeforeRefinement == "The night arrived." && output[2].SourceUncertain,
            "Cloud refinement accepts contextual ASR correction and explicitly marks an uncertain cue");
        check(original[0].Original == "The night arrived." && output.Select(r => (r.Start, r.End)).SequenceEqual(original.Select(r => (r.Start, r.End))),
            "Cloud corrections never mutate the input or change individual subtitle timestamps");
        int compatibilityCalls = 0;
        var official = s.Copy(); official.Translation.BaseUrl = "https://api.deepseek.com/v1";
        using (var client = new InferenceClient(new Handler(async request =>
        {
            compatibilityCalls++; using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            check(body.RootElement.GetProperty("thinking").GetProperty("type").GetString() == "disabled", "Official DeepSeek document requests reserve output for subtitle content instead of default hidden reasoning");
            if (compatibilityCalls == 1) return new(HttpStatusCode.BadRequest) { Content = new StringContent("max_tokens unsupported, use max_completion_tokens") };
            if (compatibilityCalls == 2) return new(HttpStatusCode.BadRequest) { Content = new StringContent("temperature not supported; only default value is supported") };
            check(body.RootElement.TryGetProperty("max_completion_tokens", out _) && !body.RootElement.TryGetProperty("temperature", out _), "Compatible cloud services can request modern output-token parameters and default sampling without reducing context");
            return Reply(Answer(CloudRefinement.Batch(original, 0, original.Length, [])));
        }))) await client.RefineBatchAsync(CloudRefinement.Batch(original, 0, original.Length, []), official, CancellationToken.None);
        check(compatibilityCalls == 3, "Parameter compatibility retries are bounded and retain the entire document");
        var files = SubtitleFilesExport.Write(Path.Combine(AppPaths.Root, "cloud-refinement-export"), output, "en", "zh", "whole-document");
        check(files.Corrections != null && File.Exists(files.Corrections) && files.Failures == null && File.ReadAllText(files.Corrections).Contains("The night arrived.") && SubtitleImport.Read(files.Original).Count == 3,
            "Three subtitle tracks are accompanied by a separate original-versus-corrected audit without a false failure-file reference");
        foreach (string failure in new[] { "truncated", "duplicate", "omitted", "mixed-language", "context" })
        {
            output.Clear(); int attempted = 0;
            using var client = new InferenceClient(new Handler(async request =>
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                string user = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
                using var data = JsonDocument.Parse(user[(user.IndexOf("以下为字幕数据：\n", StringComparison.Ordinal) + "以下为字幕数据：\n".Length)..]);
                var ids = data.RootElement.GetProperty("cues").EnumerateArray().Select(c => c.GetProperty("id").GetInt64()).ToArray();
                var batch = CloudRefinement.Batch(original.Where(r => ids.Contains(r.Sequence)).ToArray(), 0, ids.Length, []); attempted++;
                if (attempted == 1) return failure switch
                {
                    "truncated" => Reply("{\"cues\":[", "length"),
                    "context" => new(HttpStatusCode.BadRequest) { Content = new StringContent("maximum context length exceeded PRIVATE_PROVIDER_TEXT") },
                    "duplicate" => Reply("{\"cues\":[{\"id\":1,\"source\":\"Hello\",\"translation\":\"你好\"},{\"id\":1,\"source\":\"Hello\",\"translation\":\"你好\"}]}"),
                    "mixed-language" => Reply("{\"cues\":[{\"id\":1,\"source\":\"Hello\",\"translation\":\"啊，everyone is...それはそう！\"}]}"),
                    _ => Reply("{\"cues\":[]}")
                };
                return Reply(Answer(batch));
            }));
            await CloudRefinement.RunAsync(original, s, (batch, ct) => client.RefineBatchAsync(batch, s, ct), output.Add, null, null, CancellationToken.None);
            check(attempted == 3 && output.Count == 3 && output.All(r => !r.HasError) && output.Select(r => r.Sequence).SequenceEqual(new long[] { 1, 2, 3 }),
                failure + ": invalid complete-document results shrink into valid batches with no missing or duplicated exports");
        }
        var longRows = Enumerable.Range(1, 30).Select(n => Row(n, "The technical term Token is used in this sentence. " + new string('x', 160))).ToArray();
        var small = s.Copy(); small.RefineContextTokens = 8192; small.RefineChunkTokens = 2200; small.RefineOutputTokens = 2048;
        int batches = 0; output.Clear(); bool overlap = false;
        await CloudRefinement.RunAsync(longRows, small, (batch, _) =>
        {
            check(CloudRefinement.Fits(batch, small), "Every dispatched batch fits the configured input, response and combined context budgets");
            using var data = JsonDocument.Parse(batch.Data); overlap |= data.RootElement.GetProperty("context").GetArrayLength() > 0;
            batches++; return Task.FromResult(Answer(batch));
        }, output.Add, null, null, CancellationToken.None);
        check(batches > 1 && overlap && output.Count == 30 && output.All(r => !r.HasError) && output.Select(r => r.Sequence).Distinct().Count() == 30,
            "User-defined small budgets split long subtitles with neighboring context while emitting each cue once");
        using (var cancel = new CancellationTokenSource())
        {
            output.Clear(); bool cancelled = false;
            try { await CloudRefinement.RunAsync(longRows, small, (batch, _) => Task.FromResult(Answer(batch)), row => { output.Add(row); cancel.Cancel(); }, null, null, cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && output.Count > 0 && output.Count < 30, "Cancelling document refinement preserves completed output for partial export");
        }
        output.Clear(); batches = 0;
        await CloudRefinement.RunAsync(original, s, (batch, _) =>
        {
            batches++; if (batch.Rows[0].Sequence == 1) throw new IOException("temporary provider failure"); return Task.FromResult(Answer(batch));
        }, output.Add, null, null, CancellationToken.None);
        check(batches == 2 && output.Count == 3 && output.All(r => r.HasError), "A failed cloud batch keeps every original and failure position after retry");
        output.Clear();
        await CloudRefinement.RunAsync(longRows.Take(9).ToArray(), small, (batch, _) =>
        { if (batch.Rows[0].Sequence == 1) throw new IOException("temporary batch failure"); return Task.FromResult(Answer(batch)); }, output.Add, null, null, CancellationToken.None);
        check(output.Count == 9 && output.Any(r => r.HasError) && !output[^1].HasError, "A failed early cloud batch does not stop later subtitle batches");
        var tooLong = s.Copy(); tooLong.TranslationPrompt = new string('字', 10000); tooLong.RefineContextTokens = 4096; output.Clear(); int forbiddenCalls = 0;
        await CloudRefinement.RunAsync(original, tooLong, (_, _) => { forbiddenCalls++; return Task.FromResult("{}"); }, output.Add, null, null, CancellationToken.None);
        check(forbiddenCalls == 0 && output.Count == 3 && output.All(r => r.HasError), "An oversized custom prompt is reported without dispatching over-budget cloud requests or dropping originals");
        var missing = new[] { Row(1, "（此位置原文缺失）") with { SourceMissing = true }, Row(2, "Valid speech.") }; output.Clear();
        await CloudRefinement.RunAsync(missing, s, (batch, _) =>
        { using var data = JsonDocument.Parse(batch.Data); check(data.RootElement.GetProperty("cues").GetArrayLength() == 1, "Missing speech markers are never sent for AI reconstruction"); return Task.FromResult(Answer(batch)); }, output.Add, null, null, CancellationToken.None);
        check(output[0].HasError && !output[1].HasError, "Missing originals remain visible while real neighboring speech is refined");
        await CheckArchivesAsync(check);
        MainWindow.CheckRefine023Ui(check);
    }
    private static async Task CheckArchivesAsync(Action<bool, string> check)
    {
        string root = Path.Combine(AppPaths.Root, "archive-cleanup"); Directory.CreateDirectory(root);
        DownloadAsset Fixture(string name, params (string Path, string Content)[] contents)
        {
            string path = Path.Combine(root, name + ".zip");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) foreach (var entry in contents)
                using (var writer = new StreamWriter(zip.CreateEntry(entry.Path).Open())) writer.Write(entry.Content);
            return new(name, "https://127.0.0.1:1/never-download", path, new FileInfo(path).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }
        var asset = Fixture("engine", ("bin/engine.exe", "binary fixture"), ("bin/dependency.dll", "dll fixture"), ("LICENSE", "license fixture"));
        byte[] package = File.ReadAllBytes(asset.Path); string destination = Path.ChangeExtension(asset.Path, null);
        await ModelInstaller.PrepareArchiveAsync(asset, destination, ["engine.exe"], null, CancellationToken.None);
        check(!File.Exists(asset.Path) && await ModelInstaller.InstalledArchiveValidAsync(asset, destination, ["engine.exe"], CancellationToken.None),
            "Verified extraction records all-file hashes and deletes only its successful ZIP");
        await ModelInstaller.PrepareArchiveAsync(asset, destination, ["engine.exe"], null, CancellationToken.None);
        check(!File.Exists(asset.Path) && File.ReadAllText(Path.Combine(destination, "LICENSE")) == "license fixture", "Next preparation reuses installed files without downloading a deleted archive and retains licenses");
        File.WriteAllText(Path.Combine(destination, "bin/dependency.dll"), "corrupt");
        check(!await ModelInstaller.InstalledArchiveValidAsync(asset, destination, ["engine.exe"], CancellationToken.None), "A damaged extracted DLL invalidates the install record even when its executable exists");
        File.WriteAllBytes(asset.Path, package); await ModelInstaller.PrepareArchiveAsync(asset, destination, ["engine.exe"], null, CancellationToken.None);
        check(!File.Exists(asset.Path) && File.ReadAllText(Path.Combine(destination, "bin/dependency.dll")) == "dll fixture", "Repair restores a damaged dependency and cleans its verified package again");
        var broken = Fixture("incomplete", ("engine.exe", "binary")); bool failed = false;
        try { await ModelInstaller.PrepareArchiveAsync(broken, Path.ChangeExtension(broken.Path, null), ["engine.exe", "dependency.dll"], null, CancellationToken.None); } catch (IOException) { failed = true; }
        check(failed && File.Exists(broken.Path) && !File.Exists(Path.Combine(Path.ChangeExtension(broken.Path, null), ".lingoflow-installed.json")), "Incomplete extraction retains the archive and never records a successful installation");
        var stopped = Fixture("cancelled", ("engine.exe", "binary"));
        using (var cancel = new CancellationTokenSource())
        {
            bool cancelled = false;
            try { await ModelInstaller.PrepareArchiveAsync(stopped, Path.ChangeExtension(stopped.Path, null), ["engine.exe"], new Immediate<long>(_ => cancel.Cancel()), cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && File.Exists(stopped.Path), "Cancellation before extraction retains the completed download for the next attempt");
        }
        var unsafeZip = Fixture("unsafe", ("../outside.txt", "must not write"), ("engine.exe", "binary")); failed = false;
        try { await ModelInstaller.PrepareArchiveAsync(unsafeZip, Path.ChangeExtension(unsafeZip.Path, null), ["engine.exe"], null, CancellationToken.None); } catch (IOException) { failed = true; }
        check(failed && File.Exists(unsafeZip.Path) && !File.Exists(Path.Combine(root, "outside.txt")), "Unsafe ZIP paths are rejected and never trigger archive deletion or outside writes");
    }
}

public partial class MainWindow
{
    internal static async Task CheckCloudRefine023Async(string report)
    {
        Directory.CreateDirectory(AppPaths.Root);
        string input = "1\n00:00:01,100 --> 00:00:03,200\nThe night arrived.\n\n2\n00:00:03,200 --> 00:00:06,900\nThe knight carried a sword.\n\n3\n00:00:08,000 --> 00:00:10,000\nThe name was unclear.\n";
        string inputFile = Path.Combine(AppPaths.Root, "source.srt"); File.WriteAllText(inputFile, input);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port, requests = 0;
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(stop.Token); using var stream = connection.GetStream();
            var header = new List<byte>(); byte[] one = new byte[1];
            while (true)
            {
                await stream.ReadExactlyAsync(one, stop.Token); header.Add(one[0]);
                if (header.Count > 65536) throw new IOException("Fixture HTTP header too large");
                if (header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
            }
            string headers = System.Text.Encoding.ASCII.GetString(header.ToArray());
            string? lengthHeader = headers.Split("\r\n").FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            async Task<string> Line()
            {
                var line = new List<byte>();
                while (true) { await stream.ReadExactlyAsync(one, stop.Token); line.Add(one[0]); if (line.Count > 8192) throw new IOException("Fixture HTTP line too large"); if (line.Count >= 2 && line[^2] == 13 && line[^1] == 10) return System.Text.Encoding.ASCII.GetString(line.ToArray()[..^2]); }
            }
            using var collected = new MemoryStream();
            if (lengthHeader != null)
            {
                int length = int.Parse(lengthHeader.Split(':')[1]); if (length > 2_000_000) throw new IOException("Fixture HTTP body too large");
                byte[] data = new byte[length]; await stream.ReadExactlyAsync(data, stop.Token); collected.Write(data);
            }
            else
            {
                if (!headers.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase)) throw new IOException("Missing fixture request body framing");
                while (true)
                {
                    int length = Convert.ToInt32((await Line()).Split(';')[0], 16);
                    if (length == 0) { await Line(); break; }
                    if (length < 0 || collected.Length + length > 2_000_000) throw new IOException("Fixture chunked body too large");
                    byte[] data = new byte[length]; await stream.ReadExactlyAsync(data, stop.Token); collected.Write(data);
                    if (await Line() != "") throw new IOException("Invalid fixture chunk terminator");
                }
            }
            byte[] payload = collected.ToArray();
            if (!headers.StartsWith("POST /v1/chat/completions ") || headers.Contains("Authorization:")) throw new Exception("Native fixture endpoint or credentials were incorrect");
            using var request = JsonDocument.Parse(payload); string user = request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            if (!user.Contains("The night arrived.") || !user.Contains("The knight carried a sword.") || !user.Contains("The name was unclear.")) throw new Exception("Native request did not carry the entire subtitle context");
            requests++;
            var result = new { cues = new[] {
                new { id = 1, source = "The knight arrived.", translation = "骑士已经到达。", uncertain = false },
                new { id = 2, source = "The knight carried a sword.", translation = "骑士佩着一把剑。", uncertain = false },
                new { id = 3, source = "The name was unclear.", translation = "名字听不清。", uncertain = true } } };
            byte[] response = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = JsonSerializer.Serialize(result) }, finish_reason = "stop" } } }));
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
            await stream.WriteAsync(response, stop.Token);
        }, stop.Token);
        var s = new AppSettings { FirstRunComplete = true, Route = "hybrid", SourceLanguage = "en", TargetLanguage = "zh", TranslationPrompt = "结合全文校对同音词。",
            Translation = new() { BaseUrl = $"http://127.0.0.1:{port}/v1", Model = "fixture" } };
        var window = new MainWindow(s); var before = System.Diagnostics.Process.GetProcessesByName("whisper-server").Select(p => p.Id).ToHashSet();
        try
        {
            window.SetRefineInput(SubtitleImport.Read(inputFile), "cloud-document", "en", "zh"); window.refineSource = "en"; window.refineTarget = "zh";
            window.refining = true; window.refineCancellation = new(); window.SetControls(); string output = Path.Combine(AppPaths.Root, "export");
            await window.RunRefinementAsync(s, output, stop.Token); await server;
            var rows = window.refined.ToArray(); var files = Directory.Exists(output) ? Directory.GetFiles(output) : [];
            if (!window.refineFinished || requests != 1 || rows.Length != 3 || rows.Any(r => r.HasError) || rows[0].Original != "The knight arrived." || !rows[2].SourceUncertain ||
                files.Count(p => p.EndsWith(".srt")) != 3 || files.Count(p => p.EndsWith("-原文校对记录.json")) != 1 || File.ReadAllText(inputFile) != input || !window.StartButton.IsEnabled)
                throw new Exception("Native cloud refinement/export contract failed: " + window.RefineStatusText.Text);
            if (System.Diagnostics.Process.GetProcessesByName("whisper-server").Any(p => !before.Contains(p.Id))) throw new Exception("Cloud refinement unexpectedly started an ASR process");
            File.WriteAllText(report, JsonSerializer.Serialize(new { passed = true, simulatedCloud = true, requests, inputUnchanged = true, timestampsPreserved = rows.Select(r => (r.Start, r.End)).SequenceEqual(SubtitleImport.Read(inputFile).Select(r => (r.Start, r.End))),
                noCloudCredentialsUsed = true, files, rows }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { stop.Cancel(); listener.Stop(); window.quitting = true; window.Close(); }
    }
    internal static void CheckRefine023Ui(Action<bool, string> check, string? screenshot = null)
    {
        var window = new MainWindow(new() { FirstRunComplete = true, SourceLanguage = "ja", TargetLanguage = "zh", Colors = ColorSettings.Light(), TranslationPrompt = "保留我编辑的规则。\n根据技术语境判断 Token。" });
        try
        {
            check(window.TranslationPromptBox.Text.StartsWith("【翻译方向】将日语翻译为简体中文。"), "Refinement editor renders the selected source and target language into its prompt");
            Select(window.RefineSourceBox, "en"); Select(window.RefineTargetBox, "ko");
            check(window.TranslationPromptBox.Text.StartsWith("【翻译方向】将英语翻译为韩语。") && TranslationPrompts.Body(window.TranslationPromptBox.Text) == "保留我编辑的规则。\n根据技术语境判断 Token。",
                "Switching both languages updates only the reserved direction line and preserves multiline user instructions");
            window.SaveTranslationPrompt_Click(window, new RoutedEventArgs());
            check(!SettingsStore.Load().TranslationPrompt.Contains("【翻译方向】") && TranslationPrompts.Render(SettingsStore.Load().TranslationPrompt, "ja", "zh").StartsWith("【翻译方向】将日语翻译为简体中文。"),
                "Saved instructions do not leak the refinement language choice into later realtime or file jobs");
            Select(window.RefineSourceBox, "auto"); check(window.TranslationPromptBox.Text.Contains("自动识别原语言"), "Automatic source selection is reflected in the prompt");
            window.RefineChunkBox.Text = "12000"; window.SaveTranslationPrompt_Click(window, new RoutedEventArgs());
            check(SettingsStore.Load().RefineChunkTokens == 12000, "The user-selected cloud chunk size survives settings reload");
            foreach (var colors in new[] { ColorSettings.Light(), new ColorSettings() })
            {
                Theme.Apply(colors); window.TranslationPromptBox.ApplyTemplate();
                check(((SolidColorBrush)window.TranslationPromptBox.CaretBrush).Color == Theme.Color(colors.Text), "Text caret follows " + (colors.Panel == "#FFFFFF" ? "light" : "dark") + " theme text color");
            }
            check(window.RefineInputControls.Children.OfType<Button>().Count() == 2, "Refinement source buttons keep SRT and file sources after removing the live-session shortcut");
            if (screenshot != null)
            {
                Theme.Apply(ColorSettings.Light()); Select(window.RefineSourceBox, "ja"); Select(window.RefineTargetBox, "zh");
                window.Width = 1210; window.Height = 850; window.SwitchPage("refine"); window.RefineBudgetControls.IsExpanded = true;
                window.Show(); window.TranslationPromptBox.Focus(); window.TranslationPromptBox.CaretIndex = window.TranslationPromptBox.Text.Length; window.UpdateLayout();
                check(window.RefineControlsScroll.ScrollableHeight > 0 && window.RefineControlsScroll.ViewportHeight > 0 &&
                    window.RefineStartButton.TransformToAncestor(window).Transform(new Point()).Y + window.RefineStartButton.ActualHeight < window.ActualHeight - 50,
                    "Expanded cloud settings scroll within the default window while export actions remain visible");
                window.RefineControlsScroll.ScrollToEnd(); window.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(screenshot); png.Save(file);
            }
        }
        finally { window.quitting = true; window.Close(); }
    }
}
