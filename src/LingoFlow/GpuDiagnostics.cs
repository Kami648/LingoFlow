using System.Diagnostics;
using System.Text.Json;
using LingoFlow.Services;

namespace LingoFlow;

internal static class GpuDiagnostics
{
    public static async Task SmokeAsync(string models, string report, bool cpu, bool missingGpu)
    {
        var settings = new AppSettings { Route = "local", ModelDirectory = Path.GetFullPath(models), SourceLanguage = "en", TargetLanguage = "zh", Acceleration = cpu ? "cpu" : "auto" };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        if (!missingGpu) await new ModelInstaller().PrepareAsync(settings, null, cancel.Token);
        else settings.GpuPrepared = true;
        await using var servers = new LocalServers(); using var client = new InferenceClient();
        var watch = Stopwatch.StartNew(); await servers.StartAsync(settings, null, cancel.Token);
        double load = watch.Elapsed.TotalSeconds; watch.Restart();
        var samples = MainWindow.ReadBenchmarkWave();
        var speech = await client.RecognizeAsync(new(samples, 0, samples.Length / 16000d), settings, servers, cancel.Token);
        double recognition = watch.Elapsed.TotalSeconds; watch.Restart();
        string translated = await client.TranslateAsync(speech.Text, speech.Language, settings, servers, [], cancel.Token);
        double translation = watch.Elapsed.TotalSeconds;
        bool expectedGpu = !cpu && !missingGpu && GpuAcceleration.ShouldUse(settings);
        bool passed = speech.Text.Contains("country", StringComparison.OrdinalIgnoreCase) && TranslationLanguage.IsValid(translated, "zh") &&
            (expectedGpu ? servers.SpeechBackend == "GPU" && servers.TranslationBackend == "GPU" : servers.SpeechBackend == "CPU" && servers.TranslationBackend == "CPU");
        File.WriteAllText(report, JsonSerializer.Serialize(new { passed, expectedGpu, missingGpu, device = GpuAcceleration.Detect(), servers.RuntimeDescription,
            servers.BackendEvidence, servers.AccelerationFailures, settings.GpuPrepared, loadingSeconds = load, recognitionSeconds = recognition, translationSeconds = translation, speech.Text, translation = translated }, new JsonSerializerOptions { WriteIndented = true }));
        if (!passed) throw new Exception("Acceleration check failed; inspect report.");
    }
    public static async Task CheckAsync(Action<bool, string> check)
    {
        var device = new NvidiaDevice(0, "NVIDIA GeForce RTX 4070 SUPER", 596.49, 12282, 11000);
        check(GpuAcceleration.CanUse(new() { Route = "local" }, device), "A compatible NVIDIA device with enough free VRAM enables GPU inference");
        check(!GpuAcceleration.CanUse(new() { Acceleration = "cpu" }, device) && !GpuAcceleration.CanUse(new(), device with { FreeMb = 1000 }) && !GpuAcceleration.CanUse(new(), device with { Driver = 530 }), "CPU selection, low free VRAM and older drivers keep a CPU path");
        check(GpuAcceleration.Parse("0, NVIDIA RTX 4070, 596.49, 12282, 11000\nnot a device\n1, NVIDIA Other, bad, N/A, N/A").Count == 1, "GPU detection handles malformed and unavailable driver measurements");
        var settings = new AppSettings { Route = "local" };
        check(ModelInstaller.Plan(settings, true).Count == ModelInstaller.Plan(settings, false).Count + 2 && ModelInstaller.Plan(settings, true).All(a => a.Sha256.Length == 64), "GPU adds pinned official acceleration packages while retaining CPU fallback components");
        check(ModelInstaller.Plan(new() { Route = "hybrid" }, true).Count == ModelInstaller.Plan(new() { Route = "hybrid" }, false).Count + 1, "Cloud text translation does not download a local GPU translation engine");
        int requests = 0;
        using var client = new InferenceClient(new Handler(_ => { requests++; return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"text\":\"会议将在明天下午开始\",\"language\":\"zh\"}") }; }));
        var samples = MainWindow.ReadBenchmarkWave();
        var servers = new LocalServers { SpeechUrl = "http://127.0.0.1:9988", AnalyzeVoice = _ => new(true, 1.5, .999, 0, samples.Length) };
        check((await client.RecognizeAsync(new(samples, 0, samples.Length / 16000d), new() { Accurate = true }, servers, CancellationToken.None)).Text.Contains("会议") && requests == 1, "Clear ordinary speech is recognized once in accurate mode without redundant high-beam decoding");
        servers.AnalyzeVoice = _ => new(true, 1.5, .9, 0, samples.Length);
        await client.RecognizeAsync(new(samples, 0, samples.Length / 16000d), new() { Accurate = true }, servers, CancellationToken.None);
        check(requests == 3, "Weaker human-voice evidence still requires a second recognition");
        var chunks = new List<AudioChunk>(); var segmenter = new AudioSegmenter(true, .008); segmenter.Ready += chunks.Add;
        segmenter.Push(Enumerable.Repeat((short)4000, 30000).ToArray()); segmenter.Push(new short[4000]);
        check(chunks.Count == 0, "Accurate mode waits through a 250 ms hesitation");
        segmenter.Push(new short[11200]);
        check(chunks.Count == 1 && chunks[0].End < 3, "Accurate short utterances still finish on an end pause without waiting ten seconds");
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(action(request)); }
}
