using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LingoFlow.Services;

namespace LingoFlow;

internal static class AudioFileDiagnostics
{
    internal static short[] ReadWave(string path) => Wave.ReadPcm16(path);
    public static async Task RunAsync(string input, string report, string models, bool accurate, bool realtime = false, bool cpu = false)
    {
        var samples = ReadWave(input); double duration = samples.Length / 16000d;
        var settings = new AppSettings { Route = "local", ModelDirectory = Path.GetFullPath(models), WhisperModel = "small", TranslationModel = LocalTranslationModels.Default, Accurate = accurate, SourceLanguage = "ja", MixedLanguage = false };
        settings.Acceleration = cpu ? "cpu" : "auto";
        string stem = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, Path.GetFileNameWithoutExtension(report));
        object progressGate = new();
        void Save(object value) { lock (progressGate) File.WriteAllText(stem + ".progress.json", JsonSerializer.Serialize(value)); }
        Save(new { stage = "loading", duration, accurate });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        await new ModelInstaller().PrepareAsync(settings, null, cancel.Token);
        await using var servers = new LocalServers(); using var client = new InferenceClient();
        await servers.StartAsync(settings, null, cancel.Token);
        var chunks = new List<AudioChunk>(); var segmenter = new AudioSegmenter(accurate, settings.VoiceThreshold); segmenter.Ready += chunks.Add;
        segmenter.UtteranceEnded += end => chunks.Add(new([], end, end));
        for (int at = 0; at < samples.Length; at += 640) segmenter.Push(samples.AsSpan(at, Math.Min(640, samples.Length - at)).ToArray());
        segmenter.Flush();
        var results = new ConcurrentQueue<Subtitle>(); var originals = new ConcurrentQueue<string>(); var timings = new ConcurrentQueue<object>();
        var delivery = new ConcurrentQueue<double>(); var recognitionTimes = new ConcurrentQueue<double>();
        int failed = 0, updates = 0;
        var failedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = Stopwatch.StartNew();
        Save(new { stage = "processing", duration, chunks = chunks.Count, completed = 0 });
        await using var queue = new SpeechWorkQueue(async (chunk, token) =>
        {
            var timer = Stopwatch.StartNew();
            var result = await client.RecognizeAsync(chunk, settings, servers, token);
            recognitionTimes.Enqueue(timer.Elapsed.TotalSeconds); return result;
        }, (speech, context, token) => client.TranslateAsync(speech.Text, speech.Language, settings, servers, context, token),
            translateStreaming: async (speech, context, update, token) =>
            {
                var timer = Stopwatch.StartNew(); double? first = null;
                string result = await client.TranslateAsync(speech.Text, speech.Language, settings, servers, context, token, partial => { first ??= timer.Elapsed.TotalSeconds; update(partial); });
                timings.Enqueue(new { text = speech.Text, firstTokenSeconds = first, fullSeconds = timer.Elapsed.TotalSeconds }); return result;
            }, accurate: accurate, joinUtterances: true);
        queue.Recognized += row => originals.Enqueue(row.Original);
        queue.TranslationUpdated += _ => Interlocked.Increment(ref updates);
        queue.PauseRequested += message => { Interlocked.Increment(ref failed); failedSignal.TrySetException(new Exception(message)); };
        queue.Completed += row =>
        {
            results.Enqueue(row); if (row.HasError) failedSignal.TrySetException(new Exception(row.Translation));
            if (realtime) delivery.Enqueue(watch.Elapsed.TotalSeconds - row.End);
            Save(new { stage = "processing", duration, chunks = chunks.Count, completed = results.Count, audioThroughSeconds = row.End, elapsedSeconds = watch.Elapsed.TotalSeconds });
            File.WriteAllText(stem + ".srt", SubtitleExport.Srt(results), new UTF8Encoding(true));
        };
        foreach (var chunk in chunks)
        {
            if (realtime && chunk.End > watch.Elapsed.TotalSeconds)
                await Task.Delay(TimeSpan.FromSeconds(chunk.End - watch.Elapsed.TotalSeconds), cancel.Token);
            while (queue.Backlog.Seconds > 150) { cancel.Token.ThrowIfCancellationRequested(); await Task.WhenAny(Task.Delay(50, cancel.Token), failedSignal.Task); if (failedSignal.Task.IsCompleted) await failedSignal.Task; }
            queue.Enqueue(chunk);
        }
        while (queue.Backlog.Seconds > 0)
        { await Task.WhenAny(Task.Delay(100, cancel.Token), failedSignal.Task); cancel.Token.ThrowIfCancellationRequested(); if (failedSignal.Task.IsCompleted) await failedSignal.Task; }
        double processingSeconds = watch.Elapsed.TotalSeconds;
        string textPath = stem + ".txt";
        File.WriteAllText(textPath, string.Join("\n\n", results.Select(s => $"[{s.Start:F2}–{s.End:F2}]\n{s.Original}\n{s.Translation}")), new UTF8Encoding(true));
        File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            passed = failed == 0 && results.Count > 0, input = Path.GetFileName(input), audioSeconds = duration, accurate, realtime, settings.Acceleration, servers.RuntimeDescription, servers.BackendEvidence, settings.WhisperModel, settings.TranslationModel,
            processingSeconds, processingToAudioRatio = processingSeconds / duration, chunks = chunks.Count, subtitles = results.Count, failed, updates,
            maxSliceSeconds = chunks.Max(c => c.Duration), meanSliceSeconds = chunks.Average(c => c.Duration), recognitionTimes, deliveryLagFromSliceEndSeconds = delivery, timings, results
        }, new JsonSerializerOptions { WriteIndented = true }));
        Save(new { stage = "complete", duration, completed = results.Count, elapsedSeconds = processingSeconds });
    }
}
