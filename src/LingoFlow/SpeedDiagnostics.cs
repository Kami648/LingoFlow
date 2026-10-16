using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Windows;
using LingoFlow.Services;

namespace LingoFlow;

internal static class SpeedDiagnostics
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request);
    }
    private sealed class GatedStream(string first, string last, Task release) : Stream
    {
        private readonly byte[] prefix = Encoding.UTF8.GetBytes(first), suffix = Encoding.UTF8.GetBytes(last);
        private int offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => offset; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (offset >= prefix.Length) await release.WaitAsync(token);
            byte[] data = offset < prefix.Length ? prefix : suffix;
            int start = offset < prefix.Length ? offset : offset - prefix.Length;
            int count = Math.Min(7, Math.Min(buffer.Length, data.Length - start));
            if (count > 0) { data.AsMemory(start, count).CopyTo(buffer); offset += count; }
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int start, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(start, count), token).AsTask();
        public override int Read(byte[] buffer, int start, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string Delta(string text) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text } } } }) + "\n\n";
    private const string Stop = "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
    private static HttpResponseMessage JsonReply() => new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"你好\"},\"finish_reason\":\"stop\"}]}", Encoding.UTF8, "application/json") };
    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    public static async Task CheckAsync(Action<bool, string> check)
    {
        var speech = new AudioSegmenter(false, .008); var chunks = new List<AudioChunk>(); speech.Ready += chunks.Add;
        speech.Push(Enumerable.Repeat((short)4000, 19200).ToArray()); speech.Push(new short[2240]);
        check(chunks.Count == 0, "A 140 ms hesitation no longer ends a fast-mode sentence");
        speech.Push(new short[8320]);
        check(chunks.Count == 1 && chunks[0].End < 2, "Fast mode still releases a short utterance after a 650 ms end pause");
        var trough = new AudioSegmenter(false, .008); chunks.Clear(); trough.Ready += chunks.Add;
        var input = Enumerable.Repeat((short)4000, 28800).Concat(new short[960]).Concat(Enumerable.Repeat((short)4000, 10240)).ToArray();
        trough.Push(input); trough.Flush();
        check(chunks.Count == 2 && chunks[0].End < 2 && chunks[1].OverlapSeconds > .3, "Forced slice chooses a recent energy trough and records overlap");
        bool covered = chunks[0].Start == 0 && chunks[^1].End >= input.Length / 16000d;
        for (int i = 1; i < chunks.Count; i++) covered &= chunks[i].Start <= chunks[i - 1].End;
        check(covered, "Adaptive cuts retain the entire audio timeline including the suffix");
        check(SubtitleText.RemoveOverlap("Welcome to the world", "the world is changing", .36) == "is changing", "Confirmed English overlap is removed");
        check(SubtitleText.RemoveOverlap("这是一个测试", "一个测试现在开始", .36) == "现在开始", "Confirmed Chinese overlap is removed");
        check(SubtitleText.RemoveOverlap("very good", "very good", .36) == "very good" && SubtitleText.RemoveOverlap("yes yes", "yes yes again", 0) == "yes yes again", "Whole repeated phrases and non-overlapping repetitions remain intact");
        foreach (string text in new[] { string.Concat(Enumerable.Repeat("This is a long sentence, with meaningful clauses. ", 12)), string.Concat(Enumerable.Repeat("今天我们讨论一个很长的话题，接下来继续解释。", 12)), string.Concat(Enumerable.Repeat("👩🏽‍💻これはテストです。", 15)), new string('x', 600) })
        {
            var parts = SubtitleText.Split(text, false);
            check(parts.Count > 1 && string.Concat(parts) == text && parts.All(p => p.Length > 0 && !char.IsLowSurrogate(p[0]) && !char.IsHighSurrogate(p[^1])), "Long transcript splitting preserves every character and Unicode boundary");
        }
        check(SubtitleText.Join(["今天", "继续测试。", "测试结束"]) == "今天继续测试。测试结束", "Chinese phrase translations join without artificial spaces");
        check(ChatOutput.VisibleText("<thi") == "" && ChatOutput.VisibleText("<think>secret</thi") == "" && ChatOutput.VisibleText("<think>secret</think>你好") == "你好", "Split thinking tags never appear in visible streamed text");
        var release = Signal(); var firstVisible = Signal(); var updates = new List<string>(); bool requestedStream = false;
        using (var client = new InferenceClient(new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); requestedStream = json.RootElement.GetProperty("stream").GetBoolean();
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new GatedStream(Delta("你"), Delta("好") + Stop, release.Task)) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream"); return response;
        })))
        {
            var task = client.TranslateAsync("hello", "en", new() { Route = "cloud", StreamTranslation = true }, new(), [], CancellationToken.None, text => { updates.Add(text); firstVisible.TrySetResult(); });
            await firstVisible.Task.WaitAsync(TimeSpan.FromSeconds(3));
            check(requestedStream && !task.IsCompleted && updates[0] == "你", "First translation token is delivered while the response body is still blocked");
            release.TrySetResult(); string result = await task.WaitAsync(TimeSpan.FromSeconds(3));
            check(result == "你好" && updates[^1] == result, "SSE decoding handles fragmented UTF-8 and emits complete final text");
        }
        int requests = 0; var flags = new List<bool>(); bool fallbackNotice = false;
        using (var client = new InferenceClient(new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); flags.Add(json.RootElement.GetProperty("stream").GetBoolean());
            if (++requests == 1) return new(HttpStatusCode.BadRequest) { Content = new StringContent("unsupported stream") };
            return JsonReply();
        })))
        {
            client.Notice += _ => fallbackNotice = true;
            for (int i = 0; i < 2; i++) await client.TranslateAsync("hello", "en", new() { Route = "cloud", StreamTranslation = true }, new(), [], CancellationToken.None);
            check(fallbackNotice && flags.SequenceEqual(new[] { true, false, false }), "Unsupported streaming falls back once and is remembered for the current endpoint/model");
        }
        using (var client = new InferenceClient(new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); var body = json.RootElement;
            check(!body.GetProperty("stream").GetBoolean(), "Streaming can be disabled explicitly"); return JsonReply();
        }))) await client.TranslateAsync("hello", "en", new() { Route = "cloud", StreamTranslation = false }, new(), [], CancellationToken.None);
        bool fakePartial = false;
        using (var client = new InferenceClient(new Handler(_ => Task.FromResult(JsonReply()))))
            await client.TranslateAsync("hello", "en", new() { Route = "cloud", StreamTranslation = false }, new(), [], CancellationToken.None, _ => fakePartial = true);
        check(!fakePartial, "A complete JSON response is not mislabelled as a streamed draft and retains the typewriter path");
        using (var client = new InferenceClient(new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); var body = json.RootElement;
            check(body.GetProperty("messages").GetArrayLength() == 1 && body.GetProperty("messages")[0].GetProperty("content").GetString()!.StartsWith("请将以下文本翻译为") && !body.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean() && body.GetProperty("temperature").GetDouble() == 0, "Index-Translate uses its documented prompt and greedy non-thinking settings");
            return JsonReply();
        }))) await client.TranslateAsync("hello", "en", new() { Route = "cloud", Translation = new() { Model = "Index-Translate-2B" } }, new(), [], CancellationToken.None);
        bool truncated = false;
        try { using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Delta("未完成"))); await ChatOutput.ReadStreamAsync(stream, null, CancellationToken.None); }
        catch (IOException) { truncated = true; }
        check(truncated, "Premature SSE EOF is a retriable failure instead of a successful truncated subtitle");
        bool capped = false;
        try { using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Delta("未完成") + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\n")); await ChatOutput.ReadStreamAsync(stream, null, CancellationToken.None); }
        catch (InvalidOperationException) { capped = true; }
        check(capped, "Token-limit truncation is detected instead of silently committing partial text");
        using (var cancel = new CancellationTokenSource())
        {
            using var stream = new GatedStream(": heartbeat\n\n", Stop, Signal().Task);
            var task = ChatOutput.ReadStreamAsync(stream, null, cancel.Token); cancel.Cancel(); bool canceled = false;
            try { await task; } catch (OperationCanceledException) { canceled = true; }
            check(canceled, "Stop cancels a blocked streaming read");
        }
        await CheckQueue(check);
        await CheckOverlay(check);
    }
    private static async Task CheckQueue(Action<bool, string> check)
    {
        var release = Signal(); var visible = Signal(); var final = new ConcurrentQueue<Subtitle>(); var updates = new ConcurrentQueue<Subtitle>();
        await using (var queue = new SpeechWorkQueue((_, _) => Task.FromResult(new SpeechResult("hello", "en")), (_, _, _) => Task.FromResult("unused"),
            translateStreaming: async (_, _, update, token) => { update("你"); visible.TrySetResult(); await release.Task.WaitAsync(token); return "你好"; }))
        {
            queue.Completed += final.Enqueue; queue.TranslationUpdated += updates.Enqueue; queue.Enqueue(new(new short[16000], 0, 1));
            await visible.Task.WaitAsync(TimeSpan.FromSeconds(3));
            check(final.IsEmpty && updates.First().IsPending, "Pipeline publishes in-progress translation before committing the subtitle");
            release.TrySetResult(); await WaitUntil(() => queue.Backlog.Seconds == 0);
            check(final.Single().Sequence == updates.First().Sequence && final.Single().Translation == "你好", "Stream updates and final subtitle share one stable row ID");
        }
        string longText = string.Concat(Enumerable.Repeat("这是需要分段翻译的长句，", 16));
        var parts = SubtitleText.Split(longText, false); var calls = new List<string>(); var pause = Signal(); bool fail = true; final.Clear();
        await using (var queue = new SpeechWorkQueue((_, _) => Task.FromResult(new SpeechResult(longText, "zh")), (speech, _, _) =>
        {
            calls.Add(speech.Text);
            if (calls.Count >= 2 && fail) throw new HttpRequestException("mock forbidden", null, HttpStatusCode.Forbidden);
            return Task.FromResult(speech.Text);
        }))
        {
            queue.Completed += final.Enqueue; queue.PauseRequested += _ => pause.TrySetResult(); queue.Enqueue(new(new short[16000], 0, 1));
            await pause.Task.WaitAsync(TimeSpan.FromSeconds(3)); fail = false; queue.RetryFailed(); await WaitUntil(() => queue.Backlog.Seconds == 0);
            check(calls.Count == parts.Count + 1 && calls[0] == parts[0].Trim() && calls[1] == calls[2], "Retry of a long sentence resumes the failed phrase without retranslating committed phrases");
            check(final.Last().Original == longText && final.Last().Translation == longText && !final.Last().HasError, "All phrases of a long transcript remain in one complete history row");
        }
        int recognized = 0; final.Clear();
        await using (var queue = new SpeechWorkQueue((_, _) => Task.FromResult(new SpeechResult(++recognized == 1 ? "Welcome to the world" : "the world is changing", "en")), (speech, _, _) => Task.FromResult(speech.Text)))
        {
            queue.Completed += final.Enqueue; queue.Enqueue(new(new short[32000], 0, 2)); queue.Enqueue(new(new short[32000], 1.64, 3.64) { OverlapSeconds = .36 });
            await WaitUntil(() => queue.Backlog.Seconds == 0);
            check(final.Last().Original == "is changing", "Audio-overlap metadata drives transcript deduplication before translation");
        }
        var recognitionChunks = new ConcurrentQueue<AudioChunk>(); var firstStarted = Signal(); release = Signal(); final.Clear();
        await using (var queue = new SpeechWorkQueue(async (chunk, token) =>
        {
            recognitionChunks.Enqueue(chunk);
            if (chunk.Start == 0) { firstStarted.TrySetResult(); await release.Task.WaitAsync(token); }
            return new SpeechResult(chunk.Start.ToString(), "en");
        }, (speech, _, _) => Task.FromResult(speech.Text), coalesceAudio: true))
        {
            queue.Completed += final.Enqueue; queue.Enqueue(new(Enumerable.Repeat((short)1, 32000).ToArray(), 0, 2));
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (int i = 1; i < 6; i++) queue.Enqueue(new(Enumerable.Repeat((short)(i + 1), 32000).ToArray(), i * 2, i * 2 + 2));
            release.TrySetResult(); await WaitUntil(() => queue.Backlog.Seconds == 0);
            var received = recognitionChunks.ToArray();
            check(received.Length == 3 && received[1].Start == 2 && received[1].End == 10 && received[1].Samples.Where((_, i) => i % 32000 == 0).SequenceEqual(new short[] { 2, 3, 4, 5 }), "ASR backlog merges adjacent audio without losing samples or changing order");
            check(final.Count == 3 && final.Last().End == 12 && queue.Backlog.Recognition == 0 && queue.Backlog.Translation == 0, "Merged audio releases every member and commits complete timeline ranges");
        }
        bool batchFail = true; pause = Signal(); final.Clear();
        await using (var queue = new SpeechWorkQueue((chunk, _) => Task.FromResult(new SpeechResult(chunk.Start.ToString(), "en")), (_, _, _) =>
        {
            if (batchFail) throw new HttpRequestException("mock denied", null, HttpStatusCode.Forbidden);
            return Task.FromResult("recovered");
        }, coalesceAudio: true))
        {
            queue.PauseRequested += _ => pause.TrySetResult(); queue.Completed += final.Enqueue;
            for (int i = 0; i < 4; i++) queue.Enqueue(new(new short[32000], i * 2, i * 2 + 2));
            await pause.Task.WaitAsync(TimeSpan.FromSeconds(3)); batchFail = false; queue.RetryFailed();
            await WaitUntil(() => queue.Backlog.Seconds == 0);
            check(final.Last().HasError == false && final.Last().End == 8, "Merged audio is retained through translation failure and fully released after retry");
        }
    }
    private static async Task CheckOverlay(Action<bool, string> check)
    {
        var mode = Application.Current.ShutdownMode; Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var settings = new AppSettings { OverlayLines = 1, StreamTranslation = true }; var overlay = new OverlayWindow(settings) { Opacity = 0, ShowActivated = false };
        try
        {
            overlay.Show(); overlay.SetLocked(true); var starts = new List<long>(); overlay.PresentationStarted += starts.Add;
            Subtitle Row(long id, string text, bool pending = false) => new(DateTimeOffset.Now, id, id + 1, "original", text, "en", 0) { Sequence = id, IsPending = pending };
            overlay.Present(Row(1, "你", true), settings);
            overlay.Present(Row(1, "你好", true), settings);
            check(overlay.TranslatedText.Text == "你好" && starts.SequenceEqual(new long[] { 1 }), "Active streaming row updates immediately without restarting or queuing its animation");
            overlay.Present(Row(2, "旧草稿", true), settings); overlay.Present(Row(2, "最新完整内容"), settings);
            overlay.Present(Row(1, "你好"), settings);
            await WaitUntil(() => starts.Count == 2); await Task.Delay(750);
            check(starts.SequenceEqual(new long[] { 1, 2 }) && overlay.TranslatedText.Text == "最新完整内容", "Queued stream revisions collapse into one latest subtitle without duplicate playback");
        }
        finally { overlay.Close(); Application.Current.ShutdownMode = mode; }
    }
}
