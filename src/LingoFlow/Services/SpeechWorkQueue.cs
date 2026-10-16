using System.Diagnostics;
using System.Threading.Channels;

namespace LingoFlow.Services;

public record WorkBacklog(int Recognition, int Translation, int Failed, double Seconds);

// Each stage processes the first pass in order. In continue mode failed work is
// parked separately, with retryable text or bounded audio, while later work proceeds.
public sealed class SpeechWorkQueue : IAsyncDisposable
{
    private enum Stage { Recognition, Translation, FailedRecognition, FailedTranslation }
    private sealed class Work(long id, AudioChunk chunk)
    {
        public long Id = id;
        public AudioChunk Chunk = chunk;
        public readonly double ReservedSeconds = chunk.Duration;
        public readonly List<Work> Members = [];
        public SpeechResult? Speech;
        public double RecognitionSeconds;
        public Stage Stage;
        public TaskCompletionSource? Retry;
        public bool HadFailure;
        public IReadOnlyList<string> Parts = [];
        public readonly List<string> Translations = [];
        public double TranslationSeconds;
    }
    private readonly Channel<Work> audio = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<Work> text = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<long, Work> pending = [];
    private readonly object gate = new();
    private readonly Func<AudioChunk, CancellationToken, Task<SpeechResult>> recognize;
    private readonly Func<SpeechResult, IReadOnlyList<Subtitle>, CancellationToken, Task<string>> translate;
    private readonly Func<SpeechResult, IReadOnlyList<Subtitle>, Action<string>, CancellationToken, Task<string>>? translateStreaming;
    private readonly bool accurate;
    private readonly bool coalesceAudio;
    private readonly bool joinUtterances;
    private readonly bool continueOnFailure;
    private Work? sentence;
    private readonly List<Subtitle> context = [];
    private readonly CancellationTokenSource cancel = new();
    private readonly Task recognitionWorker, translationWorker;
    private readonly double bufferSeconds;
    private long sequence;
    private bool limitSignaled;
    private int disposed;
    private bool closing;
    private string previousSpeech = "";
    private double previousSpeechEnd = -1;
    private TaskCompletionSource idle = CompletedSignal();
    private static TaskCompletionSource CompletedSignal() { var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); result.SetResult(); return result; }
    public event Action<Subtitle>? Recognized;
    public event Action<Subtitle>? Completed;
    public event Action<Subtitle>? TranslationUpdated;
    public event Action<WorkBacklog>? BacklogChanged;
    public event Action<string>? PauseRequested;
    public event Action<string>? Notice;
    public SpeechWorkQueue(Func<AudioChunk, CancellationToken, Task<SpeechResult>> recognize,
        Func<SpeechResult, IReadOnlyList<Subtitle>, CancellationToken, Task<string>> translate, double bufferSeconds = 180,
        Func<SpeechResult, IReadOnlyList<Subtitle>, Action<string>, CancellationToken, Task<string>>? translateStreaming = null, bool accurate = false, bool coalesceAudio = false, bool joinUtterances = false, bool continueOnFailure = false)
    {
        this.recognize = recognize; this.translate = translate; this.bufferSeconds = bufferSeconds;
        this.translateStreaming = translateStreaming; this.accurate = accurate;
        this.coalesceAudio = coalesceAudio;
        this.joinUtterances = joinUtterances;
        this.continueOnFailure = continueOnFailure;
        recognitionWorker = Task.Run(() => RecognizeLoop(cancel.Token));
        translationWorker = Task.Run(() => TranslateLoop(cancel.Token));
    }
    public WorkBacklog Backlog
    {
        get
        {
            lock (gate)
            {
                var children = pending.Values.SelectMany(w => w.Members).Select(w => w.Id).ToHashSet();
                var roots = pending.Values.Where(w => !children.Contains(w.Id)).ToArray();
                return new(roots.Count(w => w.Stage == Stage.Recognition), roots.Count(w => w.Stage == Stage.Translation),
                    roots.Count(w => w.Stage is Stage.FailedRecognition or Stage.FailedTranslation), pending.Values.Where(w => !continueOnFailure || Active(w)).Sum(w => w.ReservedSeconds));
            }
        }
    }
    public bool CanCapture => Backlog.Seconds < bufferSeconds * .8;
    internal double RetainedFailedAudioSeconds { get { lock (gate) return pending.Values.Where(w => w.Stage == Stage.FailedRecognition).Sum(w => w.Chunk.Duration); } }
    public bool Enqueue(AudioChunk chunk)
    {
        bool pause = false;
        lock (gate)
        {
            if (disposed != 0 || closing) return false;
            if (idle.Task.IsCompleted) idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var work = new Work(++sequence, chunk); pending.Add(work.Id, work);
            if (!audio.Writer.TryWrite(work)) { pending.Remove(work.Id); return false; }
            if (!limitSignaled && pending.Values.Where(Active).Sum(w => w.ReservedSeconds) >= bufferSeconds) { pause = limitSignaled = true; }
        }
        Notify();
        if (pause) PauseRequested?.Invoke($"待处理音频已达到约 {bufferSeconds:F0} 秒，正在暂停收音；已接收片段会继续处理，请先暂停播放内容。");
        return true;
    }
    public int RetryFailed()
    {
        int count = 0;
        lock (gate)
        {
            var children = pending.Values.SelectMany(w => w.Members).Select(w => w.Id).ToHashSet();
            foreach (var work in pending.Values.Where(w => !children.Contains(w.Id)).ToArray())
            {
            if (!continueOnFailure) { if (work.Retry?.TrySetResult() == true) count++; continue; }
            if (work.Stage is not (Stage.FailedRecognition or Stage.FailedTranslation) || work.Stage == Stage.FailedRecognition && work.Chunk.Samples.Length == 0) continue;
            bool recognition = work.Stage == Stage.FailedRecognition;
            if (idle.Task.IsCompleted) idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            work.Stage = recognition ? Stage.Recognition : Stage.Translation;
            foreach (var member in work.Members) member.Stage = work.Stage;
            if ((recognition ? audio.Writer : text.Writer).TryWrite(work)) count++;
            }
        }
        Notify();
        return count;
    }
    private static bool Active(Work w) => w.Stage is Stage.Recognition or Stage.Translation;
    private void SignalIdle() { if (continueOnFailure ? !pending.Values.Any(Active) : pending.Count == 0) idle.TrySetResult(); }
    private void Notify() => BacklogChanged?.Invoke(Backlog);
    private void Finish(Work work)
    {
        lock (gate)
        {
            pending.Remove(work.Id); foreach (var member in work.Members) pending.Remove(member.Id);
            SignalIdle();
            if (pending.Values.Where(Active).Sum(w => w.ReservedSeconds) < bufferSeconds * .75) limitSignaled = false;
        }
        Notify();
    }
    public Task WaitForIdleAsync(CancellationToken token) { lock (gate) return idle.Task.WaitAsync(token); }
    public async Task<IReadOnlyList<Subtitle>> StopAndSnapshotAsync()
    {
        lock (gate) closing = true;
        cancel.Cancel(); audio.Writer.TryComplete(); text.Writer.TryComplete();
        await Task.WhenAll(recognitionWorker, translationWorker);
        return UnfinishedSubtitles();
    }
    public IReadOnlyList<Subtitle> UnfinishedSubtitles()
    {
        lock (gate)
        {
            var children = pending.Values.SelectMany(w => w.Members).Select(w => w.Id).ToHashSet();
            return pending.Values.Where(w => !children.Contains(w.Id)).OrderBy(w => w.Id).Select(w =>
                new Subtitle(DateTimeOffset.Now, w.Chunk.Start, w.Chunk.End, w.Speech?.Text ?? "（此片段尚未完成识别）",
                    SubtitleText.Join(w.Translations) + "\n（已立即结束，剩余内容未完成）", w.Speech?.Language ?? "auto", 0) { Sequence = w.Id, HasError = true, SourceMissing = w.Speech == null, FailureReason = "会话结束时尚未完成" }).ToArray();
        }
    }
    private void SetStage(Work work, Stage stage)
    {
        lock (gate) { work.Stage = stage; foreach (var member in work.Members) member.Stage = stage; }
    }
    private void Coalesce(Work work)
    {
        if (!coalesceAudio || Backlog.Recognition < 3) return;
        double maxSeconds = accurate ? 12 : 8;
        while (audio.Reader.TryPeek(out var next) && next.Chunk.Start <= work.Chunk.End + .32 && next.Chunk.Start >= work.Chunk.Start && next.Chunk.End - work.Chunk.Start <= maxSeconds)
        {
            if (!audio.Reader.TryRead(out next)) break;
            int offset = (int)Math.Round((next.Chunk.Start - work.Chunk.Start) * 16000);
            var samples = new short[Math.Max(work.Chunk.Samples.Length, offset + next.Chunk.Samples.Length)];
            work.Chunk.Samples.CopyTo(samples, 0); next.Chunk.Samples.CopyTo(samples, offset);
            work.Members.Add(next);
            work.Chunk = new(samples, work.Chunk.Start, Math.Max(work.Chunk.End, next.Chunk.End)) { OverlapSeconds = work.Chunk.OverlapSeconds, IsUtteranceEnd = next.Chunk.IsUtteranceEnd };
        }
    }
    private static async Task<T> WithRetry<T>(Func<Task<T>> action, CancellationToken token)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return await action(); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (attempt < 3 && ex is not TranslationLanguageException && ex is not HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound })
            { await Task.Delay(300 * attempt * attempt, token); }
        }
    }
    private async Task WaitForRetry(Work work, bool recognition, Exception error, CancellationToken token)
    {
        TaskCompletionSource retry = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) { work.Retry = retry; work.HadFailure = true; }
        SetStage(work, recognition ? Stage.FailedRecognition : Stage.FailedTranslation);
        var failed = new Subtitle(DateTimeOffset.Now, work.Chunk.Start, work.Chunk.End,
            work.Speech?.Text ?? "（此片段识别失败，音频已保留）", SubtitleText.Join(work.Translations) + "\n（处理失败，可点击“重试失败片段”）", work.Speech?.Language ?? "auto", 0)
            { Sequence = work.Id, HasError = true };
        Completed?.Invoke(failed); Notify();
        PauseRequested?.Invoke($"{(recognition ? "识别" : "翻译")}未能完成：{error.Message} 已保留此片段并暂停收音，请点击“重试失败片段”。");
        await retry.Task.WaitAsync(token);
        lock (gate) work.Retry = null;
        SetStage(work, recognition ? Stage.Recognition : Stage.Translation);
        Recognized?.Invoke(failed with { Translation = recognition ? "正在重新识别…" : "正在重新翻译…", HasError = false, IsPending = true });
        Notify();
    }
    private async Task<bool> HandleFailure(Work work, bool recognition, Exception error, CancellationToken token)
    {
        if (!continueOnFailure) { await WaitForRetry(work, recognition, error, token); return true; }
        if (recognition && sentence != null) await PublishSentenceAsync(token);
        work.HadFailure = true;
        bool retained = true;
        lock (gate)
        {
            // Failed translations need only text. Keep at most one minute of failed
            // recognition audio; captions/error records are retained independently.
            if (!recognition || pending.Values.Where(w => w.Stage == Stage.FailedRecognition).Sum(w => w.Chunk.Duration) + work.Chunk.Duration > 60)
            { work.Chunk = work.Chunk with { Samples = [] }; retained = !recognition; }
            foreach (var member in work.Members) member.Chunk = member.Chunk with { Samples = [] };
        }
        Completed?.Invoke(new(DateTimeOffset.Now, work.Chunk.Start, work.Chunk.End,
            work.Speech?.Text ?? "（此位置语音识别失败）", SubtitleText.Join(work.Translations) +
                (recognition ? retained ? "（识别失败，本次会话中可重试；结束后请核对音轨）" : "（识别失败，请重新导入音轨核对）" : "（翻译失败，原文已保留，可重试或字幕精修）"),
            work.Speech?.Language ?? "auto", 0) { Sequence = work.Id, HasError = true, SourceMissing = recognition, FailureReason = error.Message });
        // Publish the error row before waking Finish/Export.
        lock (gate) { SetStage(work, recognition ? Stage.FailedRecognition : Stage.FailedTranslation); SignalIdle(); }
        Notice?.Invoke($"{(recognition ? "识别" : "翻译")}片段失败：{error.Message} 已记录失败位置，继续处理后续内容。");
        Notify(); return false;
    }
    private async Task RecognizeLoop(CancellationToken token)
    {
        try
        {
            await foreach (var work in audio.Reader.ReadAllAsync(token))
            {
                if (joinUtterances && sentence != null && work.Chunk.Start > sentence.Chunk.End + .5)
                    await PublishSentenceAsync(token);
                Coalesce(work);
                while (!token.IsCancellationRequested)
                {
                    var watch = Stopwatch.StartNew();
                    try
                    {
                        var speech = work.Chunk.Samples.Length == 0 ? new SpeechResult("", "auto") : await WithRetry(() => recognize(work.Chunk, token), token);
                        if (string.IsNullOrWhiteSpace(speech.Text) || speech.Text.StartsWith("[BLANK_AUDIO]", StringComparison.OrdinalIgnoreCase))
                        {
                            // Close any existing retry placeholder; ordinary non-speech
                            // does not create a subtitle or leave a forever-pending row.
                            if (work.HadFailure)
                                Completed?.Invoke(new(DateTimeOffset.Now, work.Chunk.Start, work.Chunk.End, "（此片段未识别出文字）", "（识别服务返回空文本）", speech.Language, watch.Elapsed.TotalSeconds) { Sequence = work.Id, SourceMissing = true });
                            if (joinUtterances) await PublishSentenceAsync(token);
                            Finish(work); break;
                        }
                        token.ThrowIfCancellationRequested();
                        if (!work.HadFailure && work.Chunk.OverlapSeconds > 0 && work.Chunk.Start < previousSpeechEnd)
                            speech = speech with { Text = SubtitleText.RemoveOverlap(previousSpeech, speech.Text, work.Chunk.OverlapSeconds) };
                        if (work.Chunk.End >= previousSpeechEnd) { previousSpeech = speech.Text; previousSpeechEnd = work.Chunk.End; }
                        work.Speech = speech; work.RecognitionSeconds = watch.Elapsed.TotalSeconds;
                        if (joinUtterances)
                        {
                            if (work.HadFailure && sentence != null) await PublishSentenceAsync(token);
                            if (sentence == null) sentence = work;
                            else
                            {
                                var held = sentence;
                                lock (gate)
                                {
                                    held.Members.Add(work); held.Members.AddRange(work.Members);
                                    held.Speech = held.Speech! with { Text = SubtitleText.Join([held.Speech.Text, speech.Text]), SpeechEnded = speech.SpeechEnded };
                                    held.Chunk = held.Chunk with { End = work.Chunk.End };
                                    held.RecognitionSeconds += work.RecognitionSeconds;
                                }
                            }
                            var current = sentence;
                            Recognized?.Invoke(new(DateTimeOffset.Now, current.Chunk.Start, current.Chunk.End, current.Speech!.Text,
                                "正在听完整句…", current.Speech.Language, current.RecognitionSeconds) { Sequence = current.Id, IsPending = true });
                            // Ignore punctuation inserted by ASR at a forced cut. Wait for
                            // acoustic silence, EOF, or a bounded continuous-speech window.
                            if (work.HadFailure || work.Chunk.IsUtteranceEnd || speech.SpeechEnded || current.Chunk.End - current.Chunk.Start >= (accurate ? 20 : 12))
                                await PublishSentenceAsync(token);
                            Notify(); break;
                        }
                        work.Parts = SubtitleText.Split(speech.Text, accurate);
                        SetStage(work, Stage.Translation);
                        Recognized?.Invoke(new(DateTimeOffset.Now, work.Chunk.Start, work.Chunk.End, speech.Text, "正在翻译…", speech.Language, work.RecognitionSeconds) { Sequence = work.Id, IsPending = true });
                        await text.Writer.WriteAsync(work, token); Notify(); break;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) { if (!await HandleFailure(work, true, ex, token)) break; }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private async Task PublishSentenceAsync(CancellationToken token)
    {
        var work = sentence; if (work == null) return;
        sentence = null;
        // Translate the entire utterance together, including clauses spanning slices.
        work.Parts = [work.Speech!.Text];
        SetStage(work, Stage.Translation);
        Recognized?.Invoke(new(DateTimeOffset.Now, work.Chunk.Start, work.Chunk.End, work.Speech.Text,
            "正在翻译…", work.Speech.Language, work.RecognitionSeconds) { Sequence = work.Id, IsPending = true });
        await text.Writer.WriteAsync(work, token); Notify();
    }
    private async Task TranslateLoop(CancellationToken token)
    {
        try
        {
            await foreach (var work in text.Reader.ReadAllAsync(token))
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        while (work.Translations.Count < work.Parts.Count)
                        {
                            int index = work.Translations.Count;
                            var phrase = new SpeechResult(work.Parts[index].Trim(), work.Speech!.Language);
                            var recent = context.Where(s => s.Start <= work.Chunk.Start).ToList();
                            for (int i = 0; i < index; i++) recent.Add(new(DateTimeOffset.Now, work.Chunk.Start, work.Chunk.End, work.Parts[i].Trim(), work.Translations[i], phrase.Language, 0));
                            var phraseWatch = Stopwatch.StartNew();
                            void Update(string partial, bool pending = true)
                            {
                                token.ThrowIfCancellationRequested();
                                TranslationUpdated?.Invoke(new(DateTimeOffset.Now, work.Chunk.Start, work.Chunk.End, work.Speech.Text,
                                    SubtitleText.Join(work.Translations.Append(partial)), phrase.Language, work.RecognitionSeconds + work.TranslationSeconds + phraseWatch.Elapsed.TotalSeconds) { Sequence = work.Id, IsPending = pending });
                            }
                            string part = await WithRetry(() => translateStreaming == null ? translate(phrase, recent, token) : translateStreaming(phrase, recent, partial => Update(partial), token), token);
                            token.ThrowIfCancellationRequested(); Update(part, index < work.Parts.Count - 1);
                            work.Translations.Add(part); work.TranslationSeconds += phraseWatch.Elapsed.TotalSeconds;
                        }
                        string translated = SubtitleText.Join(work.Translations);
                        token.ThrowIfCancellationRequested();
                        var subtitle = new Subtitle(DateTimeOffset.Now, work.Chunk.Start, work.Chunk.End, work.Speech!.Text, translated, work.Speech.Language, work.RecognitionSeconds + work.TranslationSeconds) { Sequence = work.Id };
                        context.Add(subtitle); if (context.Count > 4) context.RemoveAt(0);
                        Completed?.Invoke(subtitle); Finish(work); break;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) { if (!await HandleFailure(work, false, ex, token)) break; }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (disposed != 0) return; disposed = 1; }
        cancel.Cancel(); audio.Writer.TryComplete(); text.Writer.TryComplete();
        await Task.WhenAll(recognitionWorker, translationWorker);
        lock (gate) { pending.Clear(); idle.TrySetCanceled(); } cancel.Dispose();
    }
}
