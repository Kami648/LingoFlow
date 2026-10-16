namespace LingoFlow.Services;

public record FileProgress(string Message, double Position, double? Duration, WorkBacklog Backlog);

public sealed class FileTranslation : IAsyncDisposable
{
    private readonly AppSettings settings;
    private readonly LocalServers servers = new();
    private readonly InferenceClient inference = new();
    private readonly SpeechWorkQueue queue;
    private readonly CancellationTokenSource lifetime = new();
    private string detected = "auto";
    private string? failure;
    public string RuntimeDescription => servers.RuntimeDescription;
    public event Action<Subtitle>? Updated;
    public event Action<FileProgress>? Progress;
    public event Action<string>? Status;
    public FileTranslation(AppSettings settings)
    {
        this.settings = settings.Copy();
        queue = new(Recognize, (speech, context, token) => inference.TranslateAsync(speech.Text, speech.Language, this.settings, servers, context, token),
            translateStreaming: (speech, context, update, token) => inference.TranslateAsync(speech.Text, speech.Language, this.settings, servers, context, token, update),
            accurate: this.settings.Accurate, joinUtterances: true, continueOnFailure: true);
        queue.Notice += message => Status?.Invoke(message);
        queue.Recognized += s => Updated?.Invoke(s); queue.Completed += s => Updated?.Invoke(s);
        queue.TranslationUpdated += s => { if (this.settings.StreamTranslation) Updated?.Invoke(s); };
        // Batch work must not sit indefinitely waiting for an invisible retry button.
        queue.PauseRequested += message => { failure = message; lifetime.Cancel(); };
        inference.Notice += message => Status?.Invoke(message);
    }
    private async Task<SpeechResult> Recognize(AudioChunk chunk, CancellationToken token)
    {
        var s = settings;
        if (!s.MixedLanguage && s.SourceLanguage == "auto" && detected != "auto") { s = settings.Copy(); s.SourceLanguage = detected; }
        var result = await inference.RecognizeAsync(chunk, s, servers, token);
        if (Languages.All.Any(l => l.Code == result.Language)) detected = result.Language;
        return result;
    }
    public async Task RunAsync(string file, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        var ct = linked.Token;
        TranslationPipeline.Validate(settings);
        double position = 0; double? duration = await MediaDecoder.ProbeAsync(file, settings, ct);
        queue.BacklogChanged += backlog => Progress?.Invoke(new("正在识别和翻译…", position, duration, backlog));
        try
        {
            await servers.StartAsync(settings, new Progress<string>(s => Status?.Invoke(s)), ct);
            Status?.Invoke(RuntimeDescription);
            // Files need no live preview deadline: give ASR substantially more
            // acoustic context instead of recognizing every four-second fragment.
            var segmenter = new AudioSegmenter(settings.Accurate, settings.VoiceThreshold, settings.Accurate ? 16 : 8);
            segmenter.Ready += chunk => queue.Enqueue(chunk);
            segmenter.UtteranceEnded += end => queue.Enqueue(new([], end, end));
            await MediaDecoder.DecodeAsync(file, settings, async (samples, seconds) =>
            {
                // Bound decoded audio in memory even for multi-hour media and CPU inference.
                while (queue.Backlog.Seconds > 36) await Task.Delay(80, ct);
                ct.ThrowIfCancellationRequested(); segmenter.Push(samples); position = seconds;
                Progress?.Invoke(new("正在读取音轨、识别和翻译…", position, duration, queue.Backlog));
            }, ct);
            segmenter.Flush();
            Progress?.Invoke(new("音轨读取完成，正在完成剩余字幕…", position, duration, queue.Backlog));
            await queue.WaitForIdleAsync(ct);
        }
        catch (OperationCanceledException) when (failure != null) { throw new IOException("文件翻译未完成：" + failure); }
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); await queue.DisposeAsync(); await servers.DisposeAsync(); inference.Dispose(); lifetime.Dispose();
    }
}
