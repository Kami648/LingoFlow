using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using LingoFlow.Services;

namespace LingoFlow;

internal static class RepetitionDiagnostics
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request); }

    public static async Task CheckAsync(Action<bool, string> check)
    {
        var settings = new AppSettings { SourceLanguage = "ja", Accurate = false };
        var vad = ModelInstaller.Plan(settings).Single(a => a.Path == ModelInstaller.VadPath(settings));
        check(vad.Size == 885098 && vad.Sha256 == "29940d98d42b91fbd05ce489f3ecf7c72f0a42f027e4875919a28fb4c04ea2cf" && vad.Url.Contains("/resolve/9ffd54a1e1ee413ddf265af9913beaf518d1639b/"), "Both translation routes install the pinned local voice detector");
        int requests = 0;
        using (var client = new InferenceClient(new Handler(async request =>
        {
            requests++;
            var form = (MultipartFormDataContent)request.Content!;
            var fields = new Dictionary<string, string>();
            foreach (var part in form) if (part.Headers.ContentDisposition?.Name?.Trim('"') is { } name && name != "file") fields[name] = await part.ReadAsStringAsync();
            check(fields["prompt"] == "" && fields["no_context"] == "true", "Every ASR request explicitly clears text prompts and previous decoder context");
            check(fields["vad"] == "false" && fields["temperature_inc"] == "0.0", "Independent voice evidence precedes a deterministic decoder request");
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\"同じ文章です\",\"language\":\"japanese\"}") };
        })))
        {
            var servers = new LocalServers { SpeechUrl = "http://127.0.0.1:9988", AnalyzeVoice = chunk => new(true, 1, .99, 0, chunk.Samples.Length) };
            var samples = MainWindow.ReadBenchmarkWave().Take(16000).ToArray();
            var first = await client.RecognizeAsync(new(samples, 0, 1), settings, servers, CancellationToken.None);
            var second = await client.RecognizeAsync(new(samples, .64, 1.64) { OverlapSeconds = .36 }, settings, servers, CancellationToken.None);
            check(first.Text == second.Text && requests == 2, "Real repeated phrases are allowed on consecutive and overlapping slices");
            var quiet = await client.RecognizeAsync(new(new short[16000], 2, 3), settings, servers, CancellationToken.None);
            check(quiet.Text == "" && requests == 2, "Digital silence does not invoke recognition after a repeated phrase");
        }
        short[] tones = Enumerable.Range(0, 38400).Select(i => (short)(2200 * (Math.Sin(2 * Math.PI * 220 * i / 16000) + .7 * Math.Sin(2 * Math.PI * 330 * i / 16000) + .4 * Math.Sin(2 * Math.PI * 440 * i / 16000)))).ToArray();
        check(AudioSignal.IsStationaryTone(tones), "A sustained instrumental chord is not treated as fresh speech");
        check(!AudioSignal.IsStationaryTone(MainWindow.ReadBenchmarkWave()), "Normal speech with vocal pitch and amplitude changes is retained");
        check(!AudioSignal.IsStationaryTone(tones.Take(6400).ToArray()), "Short fragments are not rejected by the sustained-tone detector");
        SpeechResult Parse(object response) => SpeechOutput.Parse(JsonSerializer.SerializeToElement(response), "ja");
        var bad = new { text = "ご視聴ありがとうございました", no_speech_prob = .95, avg_logprob = -1.8 };
        check(Parse(new { text = bad.text, segments = new[] { bad } }).NeedsVerification, "Weak decoder metadata requests independent verification instead of deciding whether voice exists");
        var good = new { text = "ご視聴ありがとうございました", no_speech_prob = .01, avg_logprob = -.1 };
        check(Parse(new { text = good.text, segments = new[] { good, good } }).Text == good.text, "Confidence filtering does not blacklist the screenshot phrase or collapse genuine repetition");
        check(Parse(new { text = "uncertain speech", segments = new[] { new { text = "uncertain speech", no_speech_prob = .1, avg_logprob = -1.4 } } }).Text == "uncertain speech", "Low decoding confidence alone does not discard quiet or uncertain speech");
        check(Parse(new { text = "valid speech", segments = new[] { new { text = "valid speech", no_speech_prob = (double?)null, avg_logprob = (double?)null } } }).Text == "valid speech", "Unavailable confidence values are not mistaken for silence or treated as request errors");
        check(Parse(new { text = "real text then hallucination", segments = new[] { new { text = "real text", no_speech_prob = .02, avg_logprob = -.2 }, new { text = " hallucination", no_speech_prob = .9, avg_logprob = -2.0 } } }).NeedsVerification, "An uncertain segment flags the whole utterance for verification before publication");
        var rows = new ConcurrentQueue<Subtitle>(); int index = 0;
        await using var queue = new SpeechWorkQueue((_, _) => Task.FromResult(new SpeechResult(++index <= 2 ? good.text : index <= 12 ? "" : "新しい話です", "ja")), (speech, _, _) => Task.FromResult(speech.Text));
        queue.Completed += rows.Enqueue;
        for (int i = 0; i < 13; i++) queue.Enqueue(new(new short[16000], i, i + 1));
        await queue.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        check(rows.Count == 3 && rows.Take(2).All(r => r.Original == good.text) && rows.Last().Original == "新しい話です", "Two genuine repetitions survive, non-speech creates no subtitles, and new speech resumes");
    }

    public static async Task NativeAsync(string models, string? input, string report)
    {
        var settings = new AppSettings { Route = "hybrid", ModelDirectory = Path.GetFullPath(models), WhisperModel = "small", Accurate = true, SourceLanguage = input == null ? "en" : "ja", MixedLanguage = false };
        await new ModelInstaller().PrepareAsync(settings, null, CancellationToken.None);
        await using var servers = new LocalServers();
        await servers.StartAsync(settings, null, CancellationToken.None);
        short[] audio = input == null ? MainWindow.ReadBenchmarkWave() : AudioFileDiagnostics.ReadWave(input);
        short[] first = input == null ? audio.Take(5 * 16000).ToArray() : audio.Skip(7 * 16000).Take(5 * 16000).ToArray();
        short[] last = input == null ? audio.Skip(5 * 16000).ToArray() : audio.Skip(38 * 16000).Take(5 * 16000).ToArray();
        var random = new Random(816); int length = 38400;
        short[] noise = Enumerable.Range(0, length).Select(_ => (short)random.Next(-2600, 2601)).ToArray();
        short[] music = Enumerable.Range(0, length).Select(i => (short)(2200 * (Math.Sin(2 * Math.PI * 220 * i / 16000) + .7 * Math.Sin(2 * Math.PI * 330 * i / 16000) + .4 * Math.Sin(2 * Math.PI * 440 * i / 16000)))).ToArray();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var raw = new List<string>();
        using var transport = new HttpMessageInvoker(new HttpClientHandler());
        using var client = new InferenceClient(new Handler(async request => { var response = await transport.SendAsync(request, CancellationToken.None); raw.Add(await response.Content.ReadAsStringAsync()); return response; }));
        var baseline = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            using var form = new MultipartFormDataContent(); form.Add(new ByteArrayContent(Wave.Encode(i == 2 ? noise : music)), "file", "noise.wav");
            foreach (var pair in new Dictionary<string, string> { ["response_format"] = "verbose_json", ["language"] = settings.SourceLanguage, ["prompt"] = "ご視聴ありがとうございました", ["no_context"] = "false", ["vad"] = "false", ["temperature"] = "0.0" }) form.Add(new StringContent(pair.Value), pair.Key);
            using var response = await http.PostAsync(servers.SpeechUrl + "/inference", form); response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); baseline.Add(json.RootElement.GetProperty("text").GetString() ?? "");
        }
        var results = new List<object>(); var words = new List<string>(); int falseSubtitles = 0;
        for (int i = 0; i < 15; i++)
        {
            short[] data = i < 2 ? first : i == 14 ? last : i % 3 == 0 ? new short[length] : i % 3 == 1 ? music : noise;
            string kind = i < 2 ? "repeated-speech" : i == 14 ? "new-speech" : i % 3 == 0 ? "silence" : i % 3 == 1 ? "instrumental-tones" : "noise";
            var speech = await client.RecognizeAsync(new(data, i * 6, i * 6 + data.Length / 16000d) { OverlapSeconds = i == 1 ? .36 : 0 }, settings, servers, CancellationToken.None);
            words.Add(speech.Text); if (i is >= 2 and < 14 && speech.Text.Length > 0) falseSubtitles++;
            var evidence = servers.AnalyzeVoice!(new(data, 0, data.Length / 16000d) { OverlapSeconds = i == 1 ? .36 : 0 });
            results.Add(new { index = i, kind, speech.Text, speech.Language, evidence });
        }
        bool passed = words[0].Length > 0 && words[0] == words[1] && falseSubtitles == 0 && words[^1].Length > 0 && words[^1] != words[0];
        File.WriteAllText(report, JsonSerializer.Serialize(new { passed, baseline, falseSubtitles, results, raw }, new JsonSerializerOptions { WriteIndented = true }));
        if (!passed) throw new InvalidOperationException("Native repetition/noise regression failed; see " + report);
    }
}
