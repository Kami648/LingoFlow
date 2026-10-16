using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using LingoFlow.Services;

namespace LingoFlow;

internal static class Update022Diagnostics
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request); }
    private static HttpResponseMessage Reply(string value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = value }, finish_reason = "stop" } } })) };
    public static async Task CheckAsync(Action<bool, string> check)
    {
        const string prompt = "根据上下文判断 token 的译法，技术语境可保留 token，普通词义正常翻译。CUSTOM_PROMPT_022";
        foreach (string mode in new[] { "generic", "hy-cloud", "local", "index" })
        {
            int calls = 0;
            using var client = new InferenceClient(new Handler(async request =>
            {
                string bodyText = await request.Content!.ReadAsStringAsync(); using var doc = JsonDocument.Parse(bodyText);
                string message = doc.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
                check(message.Contains(prompt) && !message.Contains("LFTERM"), mode + ": custom instructions are sent literally without protected-term placeholders");
                if (mode == "local") check(doc.RootElement.GetProperty("grammar").GetString()!.Contains("A-Za-z"), "Local output grammar allows the model to spell an English term without a word whitelist");
                calls++;
                return Reply(calls == 1 ? "本次预算为100个 token。" : "这是一份表达谢意的象征。");
            }));
            var settings = new AppSettings { Route = mode == "local" ? "local" : "hybrid", SourceLanguage = "en", TargetLanguage = "zh", StreamTranslation = false, TranslationPrompt = prompt,
                Translation = new() { BaseUrl = "https://example.com/v1", Model = mode == "hy-cloud" ? "Hy-MT2" : mode == "index" ? "Index-Translate" : "test" } };
            var servers = new LocalServers { TranslationUrl = "http://127.0.0.1:9989/v1" };
            string technical = await client.TranslateAsync("The budget is 100 token.", "en", settings, servers, [], CancellationToken.None);
            string ordinary = await client.TranslateAsync("This is a token of appreciation.", "en", settings, servers, [], CancellationToken.None);
            check(calls == 2 && technical.Contains("token") && ordinary.Contains("象征"), mode + ": both AI-chosen retention and translation are accepted without a forced Token rule");
        }
        using (var client = new InferenceClient(new Handler(async request =>
        {
            string body = await request.Content!.ReadAsStringAsync();
            check(body.Contains("Token") && !body.Contains("LFTERM"), "A single Token word reaches AI instead of bypassing inference through the old term list");
            return Reply("令牌");
        })))
            check(await client.TranslateAsync("Token", "en", new() { SourceLanguage = "en", TranslationPrompt = "按语境翻译。", Translation = new() { BaseUrl = "https://example.com/v1", Model = "test" } }, new(), [], CancellationToken.None) == "令牌", "AI may translate a standalone former protected term");
        check(TranslationLanguage.IsValid("token", "zh", "token", "en", allowModelTerms: true) && TranslationLanguage.IsValid("使用 embedding 计算相似度。", "zh", "Use embedding to calculate similarity.", "en", allowModelTerms: true),
            "Model-selected source terminology is not restricted to the old fixed list or capitalization");
        check(!TranslationLanguage.IsValid("everyone is happy and everything is good 没错", "zh", "everyone is happy and everything is good", "en", allowModelTerms: true) &&
            !TranslationLanguage.IsValid("啊，everyone is...それはそう！", "zh", "あ、それはそう！", "ja", allowModelTerms: true), "Copied English paragraphs and Japanese mixed-language regression remain rejected");
        File.WriteAllText(AppPaths.Settings, "{\"PreservedTerms\":\"OLD_FORCED_LIST\"}");
        check(SettingsStore.Load().TranslationPrompt == AppSettings.DefaultTranslationPrompt, "Old term-list settings upgrade to default translation instructions rather than enforcing the old list");
        var saved = new AppSettings { TranslationPrompt = "第一行 {上下文}\n第二行：由 AI 判断。" }; SettingsStore.Save(saved);
        check(SettingsStore.Load().TranslationPrompt == saved.TranslationPrompt && !File.ReadAllText(AppPaths.Settings).Contains("PreservedTerms"), "Multiline translation instructions persist verbatim and obsolete term settings are no longer saved");
        saved.TranslationPrompt = ""; SettingsStore.Save(saved);
        check(SettingsStore.Load().TranslationPrompt == "", "An explicitly empty prompt survives restart");
        MainWindow.CheckTranslationPromptUi(check);
    }
}

public partial class MainWindow
{
    internal static void CheckTranslationPromptUi(Action<bool, string> check)
    {
        var window = new MainWindow(new() { FirstRunComplete = true, TranslationPrompt = "自定义提示词" });
        try
        {
            check(TranslationPrompts.Body(window.TranslationPromptBox.Text) == "自定义提示词", "Refinement page displays the saved translation prompt with a managed direction line");
            window.TranslationPromptBox.Text = "请根据金融语境判断普通词和术语。";
            window.SaveTranslationPrompt_Click(window, new RoutedEventArgs());
            check(SettingsStore.Load().TranslationPrompt == TranslationPrompts.Body(window.TranslationPromptBox.Text), "Save prompt action persists the UI instructions independently of the managed direction line");
            window.ResetTranslationPrompt_Click(window, new RoutedEventArgs());
            check(SettingsStore.Load().TranslationPrompt == AppSettings.DefaultTranslationPrompt, "Restore default prompt action restores and saves the contextual instructions");
        }
        finally { window.quitting = true; window.Close(); }
    }
}
