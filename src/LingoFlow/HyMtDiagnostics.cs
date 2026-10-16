using System.Net;
using System.Text;
using System.Text.Json;
using LingoFlow.Services;

namespace LingoFlow;

internal static class HyMtDiagnostics
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => action(request); }

    public static async Task CheckAsync(Action<bool, string> check)
    {
        check(new AppSettings().TranslationModel == LocalTranslationModels.Default, "New settings default to Hy-MT2 Q4_K_M");
        var settings = new AppSettings { Route = "local", Accurate = false, TranslationPrompt = "" };
        var plan = ModelInstaller.Plan(settings, false); var model = plan.Last();
        check(model.Path.EndsWith("Hy-MT2-1.8B-Q4_K_M.gguf") && model.Size == 1133080448 && model.Sha256 == "dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699" && model.Url.Contains("/tencent/Hy-MT2-1.8B-GGUF/resolve/a0c709d9fac510f2c807aa3af52872340dc37a4a/"), "Default model pins Tencent repository revision size and SHA-256");
        check(plan.Any(asset => asset.Url.Contains("/b11643/")) && ModelInstaller.LlamaDirectory(settings).EndsWith("llama-b11643"), "Compatible local translation engine is isolated from the older version");
        check(LocalTranslationModels.All.Where(m => m.IsHyMt2).All(m => ModelInstaller.Plan(new() { Route = "local", TranslationModel = m.Id }, false).Last().Size == m.Size), "All three official Hy-MT2 quantization choices have complete download plans");
        check(ModelInstaller.Plan(new() { Route = "hybrid" }).All(asset => !asset.Path.EndsWith(".gguf")), "Cloud text translation does not download an unused local translation model");
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(AppPaths.Settings, JsonSerializer.Serialize(new { TranslationModel = "1.7b", LocalModelDefaultsVersion = 0 }));
        check(SettingsStore.Load().TranslationModel == LocalTranslationModels.Default, "An older default Qwen selection migrates to Hy-MT2 once");
        SettingsStore.Save(new() { TranslationModel = "1.7b" });
        check(SettingsStore.Load().TranslationModel == "1.7b", "An explicit legacy selection in the new version survives restart");
        var lightweight = new AppSettings { TranslationModel = "0.6b" }; LocalTranslationModels.Migrate(lightweight);
        check(lightweight.TranslationModel == "0.6b", "An earlier explicitly lightweight model remains available");
        int calls = 0;
        using var client = new InferenceClient(new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); var root = body.RootElement;
            var messages = root.GetProperty("messages"); string prompt = messages[0].GetProperty("content").GetString()!;
            check(messages.GetArrayLength() == 1 && messages[0].GetProperty("role").GetString() == "user" && prompt.EndsWith("こんにちは。") && !prompt.Contains("/no_think"), "Hy-MT2 uses a single user translation task without Qwen-specific prompt tokens");
            check(root.GetProperty("temperature").GetDouble() == .7 && root.GetProperty("top_p").GetDouble() == .6 && root.GetProperty("top_k").GetInt32() == 20 && root.GetProperty("repeat_penalty").GetDouble() == 1.05 && !root.TryGetProperty("chat_template_kwargs", out _), "Hy-MT2 uses Tencent decoding parameters without Qwen template fields");
            if (++calls == 1) check(prompt.StartsWith("将以下文本翻译为简体中文"), "Hy-MT2 target uses a full language name in its documented task");
            else check(!prompt.Contains("昨日の会議") && !prompt.Contains("old incorrect translation") && prompt == "将以下文本翻译为简体中文，注意只需要输出翻译后的结果，不要额外解释：\nこんにちは。", "Hy-MT2 excludes earlier subtitles so background cannot be retranslated into the current subtitle");
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"你好。\"},\"finish_reason\":\"stop\"}]}", Encoding.UTF8, "application/json") };
        }));
        var servers = new LocalServers { TranslationUrl = "http://127.0.0.1:9989/v1" };
        await client.TranslateAsync("こんにちは。", "ja", settings, servers, [], CancellationToken.None);
        await client.TranslateAsync("こんにちは。", "ja", settings, servers, [new(DateTimeOffset.Now, 0, 1, "昨日の会議", "old incorrect translation", "ja", 0)], CancellationToken.None);
    }

    public static async Task SmokeAsync(string directory, string report)
    {
        var settings = new AppSettings { Route = "local", ModelDirectory = Path.GetFullPath(directory), WhisperModel = "tiny", MixedLanguage = false };
        await new ModelInstaller().PrepareAsync(settings, null, CancellationToken.None);
        await using var servers = new LocalServers(); using var client = new InferenceClient();
        await servers.StartAsync(settings, null, CancellationToken.None);
        (string Source, string Target, string Text)[] cases =
        [
            ("zh", "en", "会议将在明天下午三点开始，请提前十分钟到场。"),
            ("en", "zh", "The meeting starts at three tomorrow afternoon. Please arrive ten minutes early."),
            ("zh", "ja", "会议将在明天下午三点开始，请提前十分钟到场。"),
            ("ja", "zh", "明日の午後三時に会議が始まります。十分前に到着してください。"),
            ("zh", "ko", "会议将在明天下午三点开始，请提前十分钟到场。"),
            ("ko", "zh", "회의는 내일 오후 세 시에 시작합니다. 십 분 일찍 도착해 주세요.")
        ];
        var rows = new List<object>();
        foreach (var item in cases)
        {
            settings.SourceLanguage = item.Source; settings.TargetLanguage = item.Target;
            var clock = System.Diagnostics.Stopwatch.StartNew(); double? first = null; int drafts = 0;
            string translated = await client.TranslateAsync(item.Text, item.Source, settings, servers, [], CancellationToken.None, _ => { first ??= clock.Elapsed.TotalSeconds; drafts++; });
            if (!TranslationLanguage.IsValid(translated, item.Target, item.Text, item.Source)) throw new Exception("Wrong output language: " + item.Target);
            rows.Add(new { item.Source, item.Target, Original = item.Text, Translation = translated, seconds = clock.Elapsed.TotalSeconds, firstSeconds = first, drafts });
        }
        File.WriteAllText(report, JsonSerializer.Serialize(new { passed = true, model = LocalTranslationModels.Get(settings.TranslationModel).FileName, engine = ModelInstaller.LlamaVersion, rows }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
