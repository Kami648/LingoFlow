using System.Diagnostics;

namespace LingoFlow.Services;

public sealed class TranslationPipeline : IAsyncDisposable
{
    private readonly AppSettings settings;
    private readonly InferenceClient inference = new();
    private readonly LocalServers servers = new();
    private readonly AudioCapture capture = new();
    private readonly SpeechWorkQueue queue;
    private readonly SemaphoreSlim captureGate = new(1, 1);
    private readonly object segmentGate = new();
    private AudioSegmenter? segmenter;
    private readonly CancellationTokenSource cancel = new();
    private readonly Stopwatch clock = new();
    private int disposed;
    private string detectedLanguage = "auto";
    private int processId;
    public bool IsCapturePaused { get; private set; } = true;
    public WorkBacklog Backlog => queue.Backlog;
    public string RuntimeDescription => servers.RuntimeDescription;
    public event Action<Subtitle>? Recognized;
    public event Action<Subtitle>? Completed;
    public event Action<Subtitle>? TranslationUpdated;
    public event Action<WorkBacklog>? BacklogChanged;
    public event Action<bool>? CapturePausedChanged;
    public event Action<string>? Status;
    public event Action<double>? Level;
    public event Action<Exception>? Fatal;
    public TranslationPipeline(AppSettings settings)
    {
        this.settings = settings.Copy();
        queue = new(RecognizeAsync, (speech, context, token) => inference.TranslateAsync(speech.Text, speech.Language, this.settings, servers, context, token),
            translateStreaming: (speech, context, update, token) => inference.TranslateAsync(speech.Text, speech.Language, this.settings, servers, context, token, update), accurate: this.settings.Accurate, joinUtterances: true, continueOnFailure: true);
        queue.Notice += message => Status?.Invoke(message);
        queue.Recognized += subtitle => Recognized?.Invoke(subtitle);
        queue.Completed += subtitle => Completed?.Invoke(subtitle);
        queue.TranslationUpdated += subtitle => { if (this.settings.StreamTranslation) TranslationUpdated?.Invoke(subtitle); };
        inference.Notice += message => Status?.Invoke(message);
        queue.BacklogChanged += backlog => BacklogChanged?.Invoke(backlog);
        queue.PauseRequested += RequestPause;
        capture.Samples += samples => { lock (segmentGate) segmenter?.Push(samples); };
        capture.Failed += ex => RequestPause("收音中断：" + ex.Message + " 已接收的片段会继续处理。");
    }
    private async Task<SpeechResult> RecognizeAsync(AudioChunk chunk, CancellationToken token)
    {
        var recognitionSettings = settings;
        if (!settings.MixedLanguage && settings.SourceLanguage == "auto" && detectedLanguage != "auto")
        { recognitionSettings = settings.Copy(); recognitionSettings.SourceLanguage = detectedLanguage; }
        var result = await inference.RecognizeAsync(chunk, recognitionSettings, servers, token);
        if (Languages.All.Any(x => x.Code == result.Language)) detectedLanguage = result.Language;
        return result;
    }
    public async Task StartAsync(int pid, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancel.Token);
        Validate(settings);
        await servers.StartAsync(settings, new Progress<string>(s => Status?.Invoke(s)), linked.Token);
        processId = pid; clock.Start();
        await ResumeCaptureAsync(linked.Token);
    }
    public static void Validate(AppSettings s)
    {
        if (!s.LocalTranslation) ValidateEndpoint(s.Translation, "chat/completions", "文字翻译");
        if (s.SourceLanguage != "auto" && s.SourceLanguage == s.TargetLanguage) throw new InvalidOperationException("原语言和目标语言相同，请调整语言方向，或选择自动识别。");
    }
    private static void ValidateEndpoint(EndpointSettings endpoint, string path, string name)
    {
        var uri = InferenceClient.Endpoint(endpoint.BaseUrl, path);
        if (string.IsNullOrWhiteSpace(endpoint.Model)) throw new InvalidOperationException($"请填写{name}模型名称。");
        if (uri.Host is "api.openai.com" or "api.deepseek.com" && string.IsNullOrWhiteSpace(endpoint.Key))
            throw new InvalidOperationException($"请先在“接口与外观”填写{name}服务的 API Key，或选择完全本地模式。");
    }
    private void RequestPause(string reason)
    {
        // Never wait for the audio worker from its own Samples callback.
        _ = Task.Run(async () =>
        {
            try { await PauseCaptureAsync(reason); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (disposed == 0) Fatal?.Invoke(ex); }
        });
    }
    public async Task PauseCaptureAsync(string reason = "收音已暂停，已接收的片段继续识别和翻译。可先暂停视频，等待积压处理完成。")
    {
        await captureGate.WaitAsync();
        try
        {
            if (disposed != 0) return;
            if (!IsCapturePaused)
            {
                await capture.DisposeAsync();
                lock (segmentGate) { segmenter?.Flush(); segmenter = null; }
                IsCapturePaused = true; Level?.Invoke(0); CapturePausedChanged?.Invoke(true);
            }
            Status?.Invoke(reason);
        }
        finally { captureGate.Release(); }
    }
    public async Task<bool> ResumeCaptureAsync(CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancel.Token);
        await captureGate.WaitAsync(linked.Token);
        try
        {
            if (!IsCapturePaused) return true;
            if (!queue.CanCapture) { Status?.Invoke("待处理片段较多，请等待积压减少后继续收音。"); return false; }
            double offset = clock.Elapsed.TotalSeconds;
            var next = new AudioSegmenter(settings.Accurate, settings.VoiceThreshold);
            next.Level += level => Level?.Invoke(level);
            next.Ready += chunk => queue.Enqueue(chunk with { Start = chunk.Start + offset, End = chunk.End + offset });
            next.UtteranceEnded += end => queue.Enqueue(new([], end + offset, end + offset));
            lock (segmentGate) segmenter = next;
            await capture.StartAsync(processId, linked.Token);
            IsCapturePaused = false; CapturePausedChanged?.Invoke(false);
            Status?.Invoke("正在聆听 · 已接收片段按顺序处理"); return true;
        }
        finally { captureGate.Release(); }
    }
    public void RetryFailed()
    {
        int count = queue.RetryFailed();
        Status?.Invoke(count > 0 ? $"正在单独重试 {count} 个失败片段；其他内容继续处理。" : queue.Backlog.Failed > 0 ? "剩余识别失败的音频已超出内存保留上限，请导出失败位置后重新核对原音轨。" : "当前没有等待重试的片段。");
    }
    public async Task FinishAsync(CancellationToken token)
    {
        await PauseCaptureAsync("已停止收音，正在处理全部剩余字幕；失败片段可重试，也可选择立即结束。");
        await queue.WaitForIdleAsync(token);
    }
    public async Task<IReadOnlyList<Subtitle>> AbortAsync()
    {
        await PauseCaptureAsync(); return await queue.StopAndSnapshotAsync();
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        cancel.Cancel(); await captureGate.WaitAsync();
        try { await capture.DisposeAsync(); lock (segmentGate) segmenter = null; }
        finally { captureGate.Release(); }
        await queue.DisposeAsync();
        await servers.DisposeAsync(); inference.Dispose(); cancel.Dispose();
    }
}

public enum SubtitleTrack { Bilingual, Original, Translation }
public static class SubtitleExport
{
    public static string Srt(IEnumerable<Subtitle> subtitles, SubtitleTrack track = SubtitleTrack.Bilingual)
    {
        var text = new System.Text.StringBuilder(); int n = 1;
        static string Time(double seconds)
        {
            long ms = (long)Math.Round(Math.Max(0, seconds) * 1000);
            return $"{ms / 3600000:D2}:{ms / 60000 % 60:D2}:{ms / 1000 % 60:D2},{ms % 1000:D3}";
        }
        foreach (var s in subtitles)
        {
            text.Append(n++).Append('\n').Append(Time(s.Start)).Append(" --> ").Append(Time(s.End)).Append('\n');
            if (track != SubtitleTrack.Translation) text.Append(s.Original.Replace("\r\n", "\n").Replace("\n\n", "\n")).Append('\n');
            if (track != SubtitleTrack.Original) text.Append(s.Translation.Replace("\r\n", "\n").Replace("\n\n", "\n")).Append('\n');
            text.Append('\n');
        }
        return text.ToString();
    }
}
