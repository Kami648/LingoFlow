using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LingoFlow.Services;

namespace LingoFlow;

public static class Diagnostics
{
    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request);
    }
    public static async Task SelfTestAsync(string path)
    {
        var tests = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception("FAILED: " + name); tests.Add(name); }
        Check(InferenceClient.Endpoint("https://example.com/v1/", "chat/completions").AbsoluteUri == "https://example.com/v1/chat/completions", "API base path");
        Check(InferenceClient.Endpoint("https://example.com/v1/chat/completions", "chat/completions").AbsoluteUri == "https://example.com/v1/chat/completions", "Full endpoint without duplication");
        bool rejected = false; try { InferenceClient.Endpoint("http://example.com/v1", "chat/completions"); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Reject unencrypted remote API");
        Check(InferenceClient.Endpoint("http://127.0.0.1:9988/v1", "chat/completions").IsLoopback, "Allow local compatible server");
        Check(DeviceProbe.Recommend(4, 1, 2, 30).Route == "hybrid", "Low-resource recommendation keeps recognition local");
        Check(DeviceProbe.Recommend(16, 8, 8, 30).Route == "local", "Local candidate recommendation");
        Check(DeviceProbe.Recommend(8, 4, 8, 30).Route == "hybrid", "Hybrid recommendation");
        Check(ModelInstaller.Plan(new() { Route = "cloud" }, false).Count == 4, "Legacy cloud route migrates to local speech and cloud text");
        Check(ModelInstaller.Plan(new() { Route = "hybrid" }, false).Count == 4, "Hybrid installs ASR with voice detection and verification");
        Check(ModelInstaller.Plan(new() { Route = "local" }, false).Count == 6, "Offline mode installs ASR voice verification and translation");
        Check(ModelInstaller.Plan(new() { Route = "local" }).All(x => x.Sha256.Length == 64 && new Uri(x.Url).Scheme == "https"), "Pinned download hashes and HTTPS");
        await HyMtDiagnostics.CheckAsync(Check);
        Check(HotkeyBinding.Default == new HotkeyBinding(7, 0x4C) && HotkeyBinding.Default.IsValid, "New default adds Shift and avoids legacy shortcut");
        Check(!new HotkeyBinding(4, 0x4C).IsValid && !new HotkeyBinding(8, 0x4C).IsValid && new HotkeyBinding(6, 0x87).IsValid, "Shortcut validates modifiers and function keys");
        Check(new HotkeyBinding(6, 0x78).ToString() == "Ctrl + Shift + F9", "Custom shortcut has readable dynamic label");
        var bindings = new Dictionary<int, HotkeyBinding>(); var occupied = new HashSet<HotkeyBinding>(); var calls = new List<string>();
        using (var manager = new GlobalHotkey(new IntPtr(1), (_, id, modifiers, key) =>
        {
            calls.Add("register"); var value = new HotkeyBinding(modifiers & 7, key);
            if (occupied.Contains(value) || bindings.ContainsValue(value)) return false;
            bindings[id] = value; return true;
        }, (_, id) => { calls.Add("unregister"); return bindings.Remove(id); }))
        {
            Check(manager.TryApply(HotkeyBinding.Default, out _), "Initial shortcut registration");
            var blockedShortcut = new HotkeyBinding(6, 0x78); occupied.Add(blockedShortcut);
            Check(!manager.TryApply(blockedShortcut, out _) && manager.Active == HotkeyBinding.Default && bindings.ContainsValue(HotkeyBinding.Default), "Conflict preserves the previous working shortcut");
            calls.Clear(); var replacement = new HotkeyBinding(6, 0x79);
            Check(manager.TryApply(replacement, out _) && calls.SequenceEqual(new[] { "register", "unregister" }) && bindings.Count == 1, "Replacement is registered before old shortcut is released");
            int activeId = bindings.Single().Key;
            Check(manager.Matches(activeId, replacement.Modifiers, replacement.Key) && !manager.Matches(activeId, 7, 0x4C), "Hotkey messages match the active combination and reject stale messages");
            calls.Clear(); manager.TryApply(replacement, out _);
            Check(calls.Count == 0, "Applying the same shortcut does not disrupt registration");
            Check(manager.TryApply(null, out _) && manager.Active == null && bindings.Count == 0, "Disabling global shortcut releases registration");
        }
        CheckNativeHotkey(Check);
        var subtitleWindow = new OverlayWindow(new());
        subtitleWindow.SetLocked(true); subtitleWindow.UpdateHotkeyHint("Ctrl + Shift + F9");
        Check(subtitleWindow.LockHint.Text.Contains("Ctrl + Shift + F9"), "Locked subtitle shows actual custom unlock shortcut");
        subtitleWindow.UpdateHotkeyHint(null);
        Check(subtitleWindow.LockHint.Text.Contains("托盘"), "Unavailable shortcut directs locked subtitle to tray unlock");
        subtitleWindow.Close();
        string dataDrive = Path.GetPathRoot(Path.GetFullPath(AppPaths.Root))!;
        string systemDrive = Path.GetPathRoot(Environment.SystemDirectory)!;
        if (!AppPaths.IsSystemDrive(dataDrive))
        {
            string program = Path.Combine(dataDrive, "Apps", "LingoFlow");
            Check(AppPaths.ChooseDefaultModelsDirectory(program, [systemDrive, dataDrive]) == Path.Combine(program, "models"), "Non-system installation stores models beside executable");
            Check(!AppPaths.IsSystemDrive(AppPaths.ChooseDefaultModelsDirectory(Path.Combine(systemDrive, "Apps", "LingoFlow"), [systemDrive, dataDrive])), "System-disk installation defaults to another disk");
        }
        var custom = new AppSettings { Route = "local", ModelDirectory = Path.Combine(AppPaths.Root, "手动选择的 models") };
        AppPaths.ValidateModelLocation(custom);
        Check(ModelInstaller.Plan(custom).All(a => Path.GetFullPath(a.Path).StartsWith(Path.GetFullPath(custom.ModelDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)), "Every model and engine download uses selected directory");
        Check(ModelInstaller.WhisperDirectory(custom).StartsWith(custom.ModelDirectory) && ModelInstaller.LlamaDirectory(custom).StartsWith(custom.ModelDirectory), "Both inference servers resolve selected storage");
        Check(AppPaths.FreeBytes(custom.ModelDirectory) > 0, "Disk capacity is read at selected model directory");
        string preserved = Path.Combine(custom.ModelDirectory, "existing-model-marker.txt"); File.WriteAllText(preserved, "keep");
        AppPaths.ValidateModelLocation(custom);
        Check(File.ReadAllText(preserved) == "keep" && !Directory.EnumerateFiles(custom.ModelDirectory, ".lingoflow-write-*").Any(), "Writable-directory check preserves existing data and cleans its probe");
        string blocked = Path.Combine(custom.ModelDirectory, "not-a-folder"); File.WriteAllText(blocked, "keep");
        bool denied = false;
        try { AppPaths.ValidateModelLocation(new() { ModelDirectory = blocked }); } catch (IOException) { denied = true; }
        Check(denied && File.ReadAllText(blocked) == "keep", "Invalid model destination is rejected without overwriting a file");
        var other = custom.Copy(); other.ModelDirectory = Path.Combine(AppPaths.Root, "another-location");
        Check(ModelInstaller.WhisperPath(custom) != ModelInstaller.WhisperPath(other), "Storage configuration is isolated per session snapshot");
        Check(new AppSettings { Route = "cloud" }.LocalSpeech && new AppSettings { Route = "local" }.LocalSpeech, "Every translation route requires local recognition");
        var segmenter = new AudioSegmenter(false, .008); var chunks = new List<AudioChunk>(); segmenter.Ready += chunks.Add;
        segmenter.Push(new short[16000]); Check(chunks.Count == 0, "Silence never queues recognition");
        var voice = Enumerable.Range(0, 16000).Select(i => (short)(5000 * Math.Sin(i * .09))).ToArray();
        segmenter.Push(voice); segmenter.Push(new short[16000]);
        Check(chunks.Count == 1 && chunks[0].Duration is > 1 and < 2, "Speech end produces bounded utterance");
        Check(chunks[0].Start > .7 && chunks[0].End > chunks[0].Start, "Subtitle timing includes preroll");
        var continuous = new AudioSegmenter(false, .008); var longChunks = new List<AudioChunk>(); continuous.Ready += longChunks.Add;
        for (int i = 0; i < 5; i++) continuous.Push(voice);
        Check(longChunks.Count == 2 && longChunks.All(x => x.Duration <= 2.41), "Continuous speech has maximum duration");
        var precise = new AudioSegmenter(true, .008); int accurateChunks = 0; precise.Ready += _ => accurateChunks++;
        for (int i = 0; i < 5; i++) precise.Push(voice);
        Check(accurateChunks == 1, "Accurate mode preserves longer context with a four-second upper bound");
        var boundary = new AudioSegmenter(false, .008); var boundaryChunks = new List<AudioChunk>(); boundary.Ready += boundaryChunks.Add;
        boundary.Push(Enumerable.Repeat((short)4000, 39217).ToArray()); boundary.Flush();
        Check(boundaryChunks.Count == 2 && boundaryChunks[1].Start < boundaryChunks[0].End && boundaryChunks[1].End >= 39217 / 16000d, "Fast boundary retains acoustic overlap and short final tail including partial frame");
        Check(boundaryChunks[1].Samples.Length > 3200, "Continuation includes fresh audio after overlap");
        var exact = new AudioSegmenter(false, .008); int exactCount = 0; exact.Ready += _ => exactCount++;
        exact.Push(Enumerable.Repeat((short)4000, 38400).ToArray()); exact.Flush();
        Check(exactCount == 1, "Overlap without fresh voice is not emitted as a duplicate");
        await CheckSpeechQueueAsync(Check);
        await CheckSubtitleDisplayAsync(Check);
        await SpeedDiagnostics.CheckAsync(Check);
        await UpdateDiagnostics.CheckAsync(Check);
        await RepetitionDiagnostics.CheckAsync(Check);
        await QualityDiagnostics.CheckAsync(Check);
        await GpuDiagnostics.CheckAsync(Check);
        await DisplayDownloadDiagnostics.CheckAsync(Check);
        await SentenceFileDiagnostics.CheckAsync(Check);
        await Update021Diagnostics.CheckAsync(Check);
        await Update022Diagnostics.CheckAsync(Check);
        await Update023Diagnostics.CheckAsync(Check);
        var wav = Wave.Encode(voice); Check(wav.Length == 32044 && Encoding.ASCII.GetString(wav, 0, 4) == "RIFF" && BitConverter.ToInt32(wav, 24) == 16000, "Valid mono 16k PCM WAV");
        var srt = SubtitleExport.Srt([new(DateTimeOffset.Now, 1.23, 4.56, "Hello", "你好", "en", .8)]);
        Check(srt.Contains("00:00:01,230 --> 00:00:04,560") && srt.Contains("Hello\n你好"), "Bilingual SRT timestamps and content");
        var settings = new AppSettings { HotkeyEnabled = false, HotkeyModifiers = 6, HotkeyKey = 0x78, ModelDirectory = custom.ModelDirectory, Translation = new() { Key = "test-secret-never-plain" } };
        SettingsStore.Save(settings); var roundtrip = SettingsStore.Load();
        Check(roundtrip.Translation.Key == settings.Translation.Key, "DPAPI current-user key roundtrip");
        Check(roundtrip.ModelDirectory == custom.ModelDirectory, "Selected model folder survives a settings reload");
        Check(!roundtrip.HotkeyEnabled && roundtrip.HotkeyModifiers == 6 && roundtrip.HotkeyKey == 0x78, "Custom shortcut and disabled state survive restart");
        Check(!File.ReadAllText(AppPaths.Settings).Contains("test-secret-never-plain"), "Keys never saved in plaintext");
        var copy = settings.Copy(); copy.Translation.Model = "different"; Check(settings.Translation.Model != copy.Translation.Model, "Session settings are independent snapshots");
        string? requestBody = null; string? auth = null; string? requestPath = null;
        using (var client = new InferenceClient(new FakeHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(); auth = request.Headers.Authorization?.Parameter; requestPath = request.RequestUri!.AbsolutePath;
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"<think>hidden</think>你好。\"}}]}") };
        })))
        {
            var s = new AppSettings { Route = "cloud", Accurate = true, Translation = new() { BaseUrl = "https://example.com/v1", Model = "mock-model", Key = "mock-key" } };
            string translation = await client.TranslateAsync("Hello.", "en", s, new(), [new(DateTimeOffset.Now, 0, 1, "Earlier", "之前", "en", 1)], CancellationToken.None);
            Check(translation == "你好。", "Compatible translation parses content and strips reasoning");
            Check(auth == "mock-key" && requestPath == "/v1/chat/completions", "Translation authentication and endpoint");
            using var json = JsonDocument.Parse(requestBody!);
            var input = JsonDocument.Parse(json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()![(json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!.IndexOf('{'))..]);
            Check(input.RootElement.GetProperty("context").GetArrayLength() == 1, "Accurate translation includes source context without old assistant output");
        }
        using (var client = new InferenceClient(new FakeHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(); auth = request.Headers.Authorization?.Parameter; requestPath = request.RequestUri!.AbsolutePath;
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\"Hello 世界\"}") };
        })))
        {
            var s = new AppSettings { Route = "hybrid", SourceLanguage = "auto" };
            var benchmark = MainWindow.ReadBenchmarkWave();
            var recognition = await client.RecognizeAsync(new(benchmark, 0, benchmark.Length / 16000d), s, new() { SpeechUrl = "http://127.0.0.1:9988", AnalyzeVoice = chunk => new(true, 1, .99, 0, chunk.Samples.Length) }, CancellationToken.None);
            Check(recognition.Text == "Hello 世界" && recognition.Language == "auto", "Local ASR accepts transcript JSON");
            Check(auth == null && requestPath == "/inference", "ASR uses only the local inference endpoint without cloud credentials");
            Check(requestBody!.Contains("verbose_json") && requestBody.Contains("auto"), "Local automatic language and response format are explicit");
        }
        using (var client = new InferenceClient(new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("secret response") }))))
        {
            bool protectedError = false;
            try { await client.TranslateAsync("Hello", "en", new() { Route = "cloud" }, new(), [], CancellationToken.None); }
            catch (HttpRequestException ex) { protectedError = ex.StatusCode == HttpStatusCode.Unauthorized && !ex.Message.Contains("secret response"); }
            Check(protectedError, "Errors preserve HTTP status without exposing provider body");
        }
        Check(MainWindow.ReadBenchmarkWave().Length > 160000, "Bundled benchmark is available");
        File.WriteAllText(AppPaths.Settings, "{\"FirstRunComplete\":true,\"Route\":\"local\"}");
        var legacy = SettingsStore.Load();
        Check(legacy.HotkeyEnabled && new HotkeyBinding(legacy.HotkeyModifiers, legacy.HotkeyKey) == HotkeyBinding.Default, "Legacy settings migrate to non-conflicting new default");
        File.WriteAllText(path, JsonSerializer.Serialize(new { passed = tests.Count, tests, device = DeviceProbe.Detect() }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!condition()) await Task.Delay(15, timeout.Token);
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task CheckSpeechQueueAsync(Action<bool, string> check)
    {
        static AudioChunk Clip(int i, int samples = 3200) => new(new short[samples], i, i + samples / 16000d);
        var releaseRecognition = Signal(); var releaseTranslation = Signal();
        var originals = new ConcurrentQueue<Subtitle>(); var completed = new ConcurrentQueue<Subtitle>();
        await using (var queue = new SpeechWorkQueue(async (chunk, token) =>
        {
            await releaseRecognition.Task.WaitAsync(token); return new SpeechResult(chunk.Start.ToString(), "en");
        }, async (speech, _, token) => { await releaseTranslation.Task.WaitAsync(token); return "译" + speech.Text; }))
        {
            queue.Recognized += originals.Enqueue; queue.Completed += completed.Enqueue;
            bool accepted = true; for (int i = 0; i < 32; i++) accepted &= queue.Enqueue(Clip(i));
            check(accepted, "All 32 burst clips are accepted");
            check(queue.Backlog.Recognition == 32, "Backlog accepts more than three clips without loss");
            releaseRecognition.TrySetResult(); await WaitUntilAsync(() => originals.Count == 32);
            check(completed.IsEmpty && queue.Backlog.Translation == 32, "Recognition advances independently while translation is blocked");
            check(originals.All(s => s.IsPending), "Original text is emitted before translation finishes");
            releaseTranslation.TrySetResult(); await WaitUntilAsync(() => queue.Backlog.Seconds == 0);
            check(completed.Select(s => s.Original).SequenceEqual(Enumerable.Range(0, 32).Select(i => i.ToString())), "Every burst clip is translated exactly once in FIFO order");
            check(completed.Select(s => s.Sequence).Distinct().Count() == 32 && completed.All(s => !s.HasError && !s.IsPending), "Completed rows retain stable unique IDs");
        }
        int attempts = 0;
        await using (var queue = new SpeechWorkQueue((_, _) => Task.FromResult(new SpeechResult("hello", "en")), (_, _, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new HttpRequestException("temporary", null, HttpStatusCode.ServiceUnavailable);
            return Task.FromResult("你好");
        }))
        {
            completed.Clear(); queue.Completed += completed.Enqueue; queue.Enqueue(Clip(0));
            await WaitUntilAsync(() => completed.Count == 1);
            check(attempts == 2 && completed.Single().Translation == "你好", "Transient failure retries the same clip instead of advancing");
        }
        bool fail = true; var paused = Signal(); completed.Clear();
        await using (var queue = new SpeechWorkQueue((chunk, _) => Task.FromResult(new SpeechResult(chunk.Start.ToString(), "en")), (speech, _, _) =>
        {
            if (fail) throw new HttpRequestException("authorization", null, HttpStatusCode.Unauthorized);
            return Task.FromResult("译" + speech.Text);
        }))
        {
            queue.Completed += completed.Enqueue; queue.PauseRequested += _ => paused.TrySetResult();
            for (int i = 0; i < 3; i++) queue.Enqueue(Clip(i));
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(6));
            check(queue.Backlog.Failed == 1 && queue.Backlog.Seconds >= .59 && completed.Single().HasError, "Terminal failure requests capture pause and keeps all pending audio");
            check(completed.Single().Original == "0", "Translation failure preserves the recognized original");
            fail = false; check(queue.RetryFailed() == 1, "Manual retry targets retained failed work");
            await WaitUntilAsync(() => queue.Backlog.Seconds == 0);
            check(completed.Where(s => !s.HasError).Select(s => s.Original).SequenceEqual(new[] { "0", "1", "2" }), "Retry completes failed original before later translations");
            check(completed.First().Sequence == completed.First(s => !s.HasError).Sequence, "Failed row is replaced using the same sequence ID");
        }
        int recognitionAttempts = 0; paused = Signal(); completed.Clear();
        await using (var queue = new SpeechWorkQueue((_, _) =>
        {
            if (Interlocked.Increment(ref recognitionAttempts) == 1) throw new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden);
            return Task.FromResult(new SpeechResult("recovered", "en"));
        }, (speech, _, _) => Task.FromResult(speech.Text)))
        {
            queue.Completed += completed.Enqueue; queue.PauseRequested += _ => paused.TrySetResult(); queue.Enqueue(Clip(0));
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(6)); queue.RetryFailed();
            await WaitUntilAsync(() => queue.Backlog.Seconds == 0);
            check(recognitionAttempts == 2 && completed.Last().Original == "recovered", "Failed ASR retains audio for manual recognition retry");
        }
        int pauses = 0, callbacks = 0;
        int silentAttempts = 0; paused = Signal(); completed.Clear();
        await using (var queue = new SpeechWorkQueue((_, _) =>
        {
            if (Interlocked.Increment(ref silentAttempts) == 1) throw new HttpRequestException("unavailable", null, HttpStatusCode.Forbidden);
            return Task.FromResult(new SpeechResult("", "en"));
        }, (_, _, _) => throw new Exception("Non-speech must not be translated")))
        {
            queue.Completed += completed.Enqueue; queue.PauseRequested += _ => paused.TrySetResult(); queue.Enqueue(Clip(0));
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(6)); queue.RetryFailed();
            await WaitUntilAsync(() => queue.Backlog.Seconds == 0);
            check(completed.Count == 2 && !completed.Last().IsPending && !completed.Last().HasError && completed.First().Sequence == completed.Last().Sequence,
                "ASR retry returning non-speech closes its existing pending row");
        }
        var blocked = new SpeechWorkQueue(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new SpeechResult("", "en"); }, (_, _, _) => Task.FromResult(""), 2);
        blocked.PauseRequested += _ => pauses++; blocked.Completed += _ => callbacks++;
        for (int i = 0; i < 3; i++) blocked.Enqueue(Clip(i, 16000));
        check(pauses == 1 && !blocked.CanCapture && blocked.Backlog.Recognition == 3, "Memory threshold requests a single pause without discarding accepted clips");
        await blocked.DisposeAsync();
        check(!blocked.Enqueue(Clip(4)) && callbacks == 0 && blocked.Backlog.Seconds == 0, "Stop cancels blocked work and releases all retained audio");
    }
    private static async Task CheckSubtitleDisplayAsync(Action<bool, string> check)
    {
        var mode = System.Windows.Application.Current.ShutdownMode;
        System.Windows.Application.Current.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        var settings = new AppSettings { Typewriter = true, OverlayLines = 1, StreamTranslation = false };
        var overlay = new OverlayWindow(settings) { Opacity = 0, ShowActivated = false };
        try
        {
            overlay.Show(); overlay.SetLocked(true);
            var order = new List<long>();
            overlay.PresentationStarted += id => { order.Add(id); };
            for (int i = 1; i <= 3; i++) overlay.Present(new(DateTimeOffset.Now, i, i + 1, "original" + i, "字幕" + i, "en", 0) { Sequence = i }, settings);
            await Task.Delay(800);
            check(overlay.TranslatedText.Text == "字幕1", "Burst subtitles allow the first typewriter animation to finish");
            await WaitUntilAsync(() => order.Count == 3);
            check(order.SequenceEqual(new long[] { 1, 2, 3 }), "Visible overlay presents burst translations in FIFO order");
            overlay.Hide();
            overlay.Present(new(DateTimeOffset.Now, 4, 5, "hidden", "隐藏时最新字幕", "en", 0), settings);
            check(overlay.TranslatedText.Text == "隐藏时最新字幕", "Hidden overlay keeps latest text without invisible playback backlog");
            overlay.Show();
            overlay.Present(new(DateTimeOffset.Now, 6, 7, "old", "旧队列", "en", 0) { Sequence = 6 }, settings);
            overlay.Present(new(DateTimeOffset.Now, 7, 8, "old", "应当取消", "en", 0) { Sequence = 7 }, settings);
            overlay.ClearPendingDisplay();
            overlay.Present(new(DateTimeOffset.Now, 8, 9, "new", "新会话", "en", 0) { Sequence = 8 }, settings);
            check(order.Last() == 8 && !order.Contains(7), "Stopping overlay display clears queued subtitles before a new session");
        }
        finally { overlay.Close(); System.Windows.Application.Current.ShutdownMode = mode; }
    }
    private static void CheckNativeHotkey(Action<bool, string> check)
    {
        using var firstWindow = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("LingoFlow hotkey test A") { Width = 0, Height = 0, WindowStyle = 0 });
        using var secondWindow = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("LingoFlow hotkey test B") { Width = 0, Height = 0, WindowStyle = 0 });
        using var first = new GlobalHotkey(firstWindow.Handle); using var second = new GlobalHotkey(secondWindow.Handle);
        foreach (uint key in Enumerable.Range(0x7C, 12).Reverse()) if (first.TryApply(new(7, key), out _)) break;
        foreach (uint key in Enumerable.Range(0x7C, 12)) if (second.TryApply(new(7, key), out _)) break;
        check(first.Active != null && second.Active != null, "Windows registers two available test shortcuts");
        var held = first.Active; var working = second.Active;
        check(!second.TryApply(held, out _) && second.Active == working, "Windows collision leaves previous shortcut registered");
        check(first.TryApply(null, out _) && second.TryApply(held, out _) && first.TryApply(working, out _), "Windows replacement succeeds after conflict clears and releases old combination");
    }
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern bool PlaySound(string? sound, IntPtr module, uint flags);
    public static async Task CaptureCheckAsync(string path)
    {
        var results = new List<object>();
        foreach (int pid in new[] { 0, Environment.ProcessId })
        {
            if (pid != 0 && !DeviceProbe.SupportsProcessCapture) continue;
            long samples = 0; int peak = 0;
            await using var capture = new AudioCapture();
            Exception? failure = null;
            capture.Samples += data => { Interlocked.Add(ref samples, data.Length); foreach (short s in data) peak = Math.Max(peak, Math.Abs((int)s)); };
            capture.Failed += ex => failure = ex;
            await capture.StartAsync(pid, CancellationToken.None);
            if (!PlaySound(Path.Combine(AppContext.BaseDirectory, "Assets", "benchmark.wav"), IntPtr.Zero, 0x00020001)) throw new Exception("测试音频播放失败。");
            await Task.Delay(2200); PlaySound(null, IntPtr.Zero, 0); await capture.DisposeAsync();
            if (failure != null) throw failure;
            results.Add(new { pid, samples, peak });
            if (samples < 16000 || peak < 100) throw new Exception($"音频捕获未获得有效信号：PID={pid}, samples={samples}, peak={peak}");
        }
        if (DeviceProbe.SupportsProcessCapture)
        {
            using var foreign = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--diagnostic-idle") { UseShellExecute = false, CreateNoWindow = true })!;
            try
            {
                await using var isolated = new AudioCapture(); int unrelatedPeak = 0;
                isolated.Samples += data => { foreach (short sample in data) unrelatedPeak = Math.Max(unrelatedPeak, Math.Abs((int)sample)); };
                await isolated.StartAsync(foreign.Id, CancellationToken.None);
                PlaySound(Path.Combine(AppContext.BaseDirectory, "Assets", "benchmark.wav"), IntPtr.Zero, 0x00020001);
                await Task.Delay(1700); PlaySound(null, IntPtr.Zero, 0); await isolated.DisposeAsync();
                if (unrelatedPeak > 10) throw new Exception("指定进程捕获意外包含其他应用的音频。");
                results.Add(new { test = "unrelated-process-exclusion", unrelatedPeak });
            }
            finally { PlaySound(null, IntPtr.Zero, 0); if (!foreign.HasExited) foreign.Kill(); }
        }
        File.WriteAllText(path, JsonSerializer.Serialize(new { passed = true, captures = results }, new JsonSerializerOptions { WriteIndented = true }));
    }
    public static async Task LocalCheckAsync(string path, bool translation)
    {
        var settings = new AppSettings { Route = translation ? "local" : "hybrid", ModelDirectory = Path.Combine(AppPaths.Root, "models"), WhisperModel = "tiny", TranslationModel = LocalTranslationModels.Default, Accurate = false };
        await new ModelInstaller().PrepareAsync(settings, null, CancellationToken.None);
        await using var servers = new LocalServers(); await servers.StartAsync(settings, null, CancellationToken.None);
        using var inference = new InferenceClient(); var samples = MainWindow.ReadBenchmarkWave(); var watch = Stopwatch.StartNew();
        var result = await inference.RecognizeAsync(new(samples, 0, samples.Length / 16000d), settings, servers, CancellationToken.None);
        double seconds = watch.Elapsed.TotalSeconds;
        if (!result.Text.Contains("country", StringComparison.OrdinalIgnoreCase)) throw new Exception("本地识别未产生预期语句：" + result.Text);
        string? translated = null;
        double? firstTranslationSeconds = null; int updates = 0;
        var translationWatch = Stopwatch.StartNew();
        if (translation) translated = await inference.TranslateAsync(result.Text, result.Language, settings, servers, [], CancellationToken.None, _ => { firstTranslationSeconds ??= translationWatch.Elapsed.TotalSeconds; updates++; });
        File.WriteAllText(path, JsonSerializer.Serialize(new { passed = true, result, speechSeconds = seconds, audioSeconds = samples.Length / 16000d, translated, translationSeconds = translationWatch.Elapsed.TotalSeconds, firstTranslationSeconds, updates }, new JsonSerializerOptions { WriteIndented = true }));
    }
    public static async Task PipelineCheckAsync(string path)
    {
        var settings = new AppSettings { Route = "local", ModelDirectory = Path.Combine(AppPaths.Root, "models"), WhisperModel = "tiny", TranslationModel = LocalTranslationModels.Default, Accurate = false };
        await new ModelInstaller().PrepareAsync(settings, null, CancellationToken.None);
        var results = new List<Subtitle>(); var gate = new object();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumedCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int resumeTarget = int.MaxValue;
        int afterStop = 0; bool stopped = false;
        var captureClock = new Stopwatch(); long firstOriginalMs = -1, firstTranslationMs = -1, firstCompleteMs = -1; int streamUpdates = 0;
        await using var pipeline = new TranslationPipeline(settings);
        pipeline.Recognized += _ => Interlocked.CompareExchange(ref firstOriginalMs, captureClock.ElapsedMilliseconds, -1);
        pipeline.TranslationUpdated += _ => { Interlocked.Increment(ref streamUpdates); Interlocked.CompareExchange(ref firstTranslationMs, captureClock.ElapsedMilliseconds, -1); };
        pipeline.Completed += subtitle =>
        {
            lock (gate)
            {
                if (stopped) afterStop++;
                if (!subtitle.HasError) Interlocked.CompareExchange(ref firstCompleteMs, captureClock.ElapsedMilliseconds, -1);
                results.Add(subtitle);
                if (subtitle.HasError) { completion.TrySetException(new Exception(subtitle.Translation)); resumedCompletion.TrySetException(new Exception(subtitle.Translation)); }
                else { if (results.Count >= 2) completion.TrySetResult(); if (results.Count >= resumeTarget) resumedCompletion.TrySetResult(); }
            }
        };
        pipeline.Fatal += exception => completion.TrySetException(exception);
        await pipeline.StartAsync(Environment.ProcessId, CancellationToken.None);
        captureClock.Start();
        PlaySound(Path.Combine(AppContext.BaseDirectory, "Assets", "benchmark.wav"), IntPtr.Zero, 0x00020001);
        bool pauseDrained = false, resumed = false;
        try
        {
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
            await pipeline.PauseCaptureAsync(); PlaySound(null, IntPtr.Zero, 0);
            await WaitUntilAsync(() => pipeline.Backlog.Seconds == 0);
            pauseDrained = pipeline.IsCapturePaused;
            lock (gate) resumeTarget = results.Count + 2;
            resumed = await pipeline.ResumeCaptureAsync();
            PlaySound(Path.Combine(AppContext.BaseDirectory, "Assets", "benchmark.wav"), IntPtr.Zero, 0x00020001);
            await resumedCompletion.Task.WaitAsync(TimeSpan.FromSeconds(45));
        }
        finally { PlaySound(null, IntPtr.Zero, 0); await pipeline.DisposeAsync(); }
        lock (gate) stopped = true;
        await Task.Delay(350);
        if (afterStop != 0 || results.Count < 4 || !pauseDrained || !resumed || results.Any(s => s.HasError)) throw new Exception("完整链路、暂停排空、继续收音或停止隔离检查失败。");
        File.WriteAllText(path, JsonSerializer.Serialize(new { passed = true, pauseDrained, resumed, afterStop, firstOriginalMs, firstTranslationMs, firstCompleteMs, streamUpdates, subtitles = results }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
