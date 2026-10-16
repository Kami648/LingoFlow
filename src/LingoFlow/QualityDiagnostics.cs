using System.Net;
using System.Text.Json;
using LingoFlow.Services;

namespace LingoFlow;

internal static class QualityDiagnostics
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request); }
    private static HttpResponseMessage Reply(string text) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { text, language = "zh" })) };
    public static async Task CheckAsync(Action<bool, string> check)
    {
        var onlyOldVoice = Enumerable.Repeat(.98f, 12).Concat(Enumerable.Repeat(.01f, 80)).ToArray();
        check(!LocalVoiceDetector.Evaluate(onlyOldVoice, onlyOldVoice.Length * 512, 12 * 512).Confirmed, "Old overlapping speech followed by background cannot count as new speech");
        check(!LocalVoiceDetector.Evaluate([.98f, .98f, .98f, .98f, 0, 0, 0], 7 * 512, 0).Confirmed, "A short music probability spike is insufficient human evidence");
        var voiceThenSpike = Enumerable.Repeat(.98f, 24).Concat(Enumerable.Repeat(.01f, 25)).Concat(Enumerable.Repeat(.99f, 4)).Concat(Enumerable.Repeat(.01f, 10)).ToArray();
        var evidence = LocalVoiceDetector.Evaluate(voiceThenSpike, voiceThenSpike.Length * 512, 0);
        check(evidence.Confirmed && evidence.LastSample <= 24 * 512 + 1600, "An isolated background spike after speech does not extend the audio sent to ASR");
        check(SpeechVerification.Agrees("感谢您的观看。", "感谢您的观看！") && !SpeechVerification.Agrees("感谢您的观看", "会议将在明天下午三点开始"), "Verification tolerates punctuation but rejects different content");
        check(SpeechVerification.IsRisky("ご視聴ありがとうございました") && SpeechVerification.IsRisky("感谢您的观看"), "Known hallucination patterns require evidence rather than a blanket blacklist");
        check(SpeechVerification.Agrees("感謝您的觀看", "感谢您的观看。") && SpeechVerification.IsRisky("感謝您的觀看"), "Traditional and simplified Chinese share verification without optional locale packs");
        var samples = MainWindow.ReadBenchmarkWave();
        var settings = new AppSettings { SourceLanguage = "zh", Accurate = true };
        int calls = 0; string mode = "same";
        using var client = new InferenceClient(new Handler(_ =>
        {
            calls++;
            return Task.FromResult(Reply(mode == "different" && calls % 2 == 0 ? "明天继续开会。" : "感谢您的观看。"));
        }));
        var servers = new LocalServers { SpeechUrl = "http://127.0.0.1:9988", AnalyzeVoice = _ => new(true, 1.5, .99, 0, samples.Length) };
        var chunk = new AudioChunk(samples, 0, samples.Length / 16000d);
        check((await client.RecognizeAsync(chunk, settings, servers, CancellationToken.None)).Text.Contains("感谢") && calls == 2, "A genuinely supported closing phrase can pass both recognitions and remain visible");
        mode = "different";
        check((await client.RecognizeAsync(chunk, settings, servers, CancellationToken.None)).Text == "" && calls == 4, "Disagreeing recognition passes do not publish a guessed closing phrase");
        servers.AnalyzeVoice = _ => new(false, .128, .9, 0, 5120);
        check((await client.RecognizeAsync(chunk, settings, servers, CancellationToken.None)).Text == "" && calls == 4, "Unconfirmed background is rejected before an ASR HTTP request");
        servers.AnalyzeVoice = _ => new(true, .3, .85, 0, samples.Length); mode = "same";
        check((await client.RecognizeAsync(chunk, settings, servers, CancellationToken.None)).Text == "" && calls == 5, "A closing phrase needs stronger fresh voice evidence than an ordinary short fragment");
        var checkedHosts = new List<int>();
        using var independent = new InferenceClient(new Handler(request =>
        {
            checkedHosts.Add(request.RequestUri!.Port);
            return Task.FromResult(Reply(request.RequestUri.Port == 9989 ? "这是背景音乐。" : "感谢您的观看。"));
        }));
        servers.AnalyzeVoice = _ => new(true, 1.5, .999, 0, samples.Length);
        servers.VerificationSpeechUrl = "http://127.0.0.1:9989";
        check((await independent.RecognizeAsync(chunk, settings, servers, CancellationToken.None)).Text == "" && checkedHosts.SequenceEqual(new[] { 9988, 9989 }), "A suspicious strong-voice result must agree with the independent local model");
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(AppPaths.Settings, JsonSerializer.Serialize(new { Accurate = false, StreamTranslation = true, WhisperModel = "base", RecognitionQualityVersion = 0 }));
        var migrated = SettingsStore.Load();
        check(migrated.Accurate && migrated.StreamTranslation && migrated.WhisperModel == "small", "Existing settings migrate once to Small with verified speech and real-time translation display");
        migrated.Accurate = false; migrated.WhisperModel = "base"; SettingsStore.Save(migrated);
        check(!SettingsStore.Load().Accurate && SettingsStore.Load().WhisperModel == "base", "Explicit subsequent user model and mode choices survive restart");
    }

    public static async Task StopMusicAsync(string models, string fixtures, string report)
    {
        var settings = new AppSettings { Route = "local", ModelDirectory = Path.GetFullPath(models), SourceLanguage = "zh", TargetLanguage = "en", MixedLanguage = false };
        await new ModelInstaller().PrepareAsync(settings, null, CancellationToken.None);
        await using var servers = new LocalServers(); using var client = new InferenceClient();
        await servers.StartAsync(settings, null, CancellationToken.None);
        var closing = AudioFileDiagnostics.ReadWave(Path.Combine(fixtures, "update-017-true-closing.wav"));
        var next = AudioFileDiagnostics.ReadWave(Path.Combine(fixtures, "update-017-new-speech.wav"));
        var all = new List<short>(); var human = new List<(double Start, double End)>();
        void Voice(short[] samples) { double start = all.Count / 16000d; all.AddRange(samples); human.Add((start, all.Count / 16000d)); all.AddRange(new short[17600]); }
        Voice(closing); Voice(closing); double musicStart = all.Count / 16000d;
        var musicFiles = Directory.GetFiles(fixtures, "update-017-music-*.wav").OrderBy(x => x).ToArray();
        if (musicFiles.Length < 4) throw new Exception("Missing instrumental music fixtures");
        foreach (string file in musicFiles)
        {
            short[] music = AudioFileDiagnostics.ReadWave(file);
            for (int repeat = 0; repeat < 2; repeat++) all.AddRange(music);
        }
        double musicEnd = all.Count / 16000d; all.AddRange(new short[16000]); Voice(next); all.AddRange(new short[16000]);
        var chunks = new List<AudioChunk>(); var segmenter = new AudioSegmenter(true, settings.VoiceThreshold); segmenter.Ready += chunks.Add;
        short[] input = all.ToArray();
        for (int at = 0; at < input.Length; at += 640) segmenter.Push(input.AsSpan(at, Math.Min(640, input.Length - at)).ToArray());
        segmenter.Flush();
        var results = new List<object>(); var accepted = new List<(AudioChunk Chunk, SpeechResult Result)>(); int falseMusic = 0;
        foreach (var chunk in chunks)
        {
            var voice = servers.AnalyzeVoice!(chunk);
            var result = await client.RecognizeAsync(chunk, settings, servers, CancellationToken.None);
            bool musicOnly = !human.Any(range => chunk.Start + chunk.OverlapSeconds < range.End && chunk.End > range.Start);
            string translated = "";
            if (result.Text.Length > 0)
            {
                if (musicOnly) falseMusic++;
                accepted.Add((chunk, result)); translated = await client.TranslateAsync(result.Text, result.Language, settings, servers, [], CancellationToken.None);
            }
            results.Add(new { chunk.Start, chunk.End, chunk.OverlapSeconds, musicOnly, voice, result.Text, translation = translated });
            File.WriteAllText(Path.ChangeExtension(report, ".progress.json"), JsonSerializer.Serialize(new { completed = results.Count, total = chunks.Count, falseMusic }));
        }
        int closings = accepted.Sum(row => System.Text.RegularExpressions.Regex.Matches(SpeechVerification.Normalize(row.Result.Text), "感谢您的观看").Count);
        bool recovered = accepted.Any(row => row.Chunk.Start >= musicEnd && row.Result.Text.Contains("会议"));
        bool passed = falseMusic == 0 && closings == 2 && recovered;
        File.WriteAllText(report, JsonSerializer.Serialize(new { passed, seconds = input.Length / 16000d, musicStart, musicEnd, falseMusic, closings, recovered, chunks = chunks.Count, results }, new JsonSerializerOptions { WriteIndented = true }));
        if (!passed) throw new Exception("Stop/music regression failed; inspect report");
    }
}
