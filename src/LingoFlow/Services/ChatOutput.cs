using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LingoFlow.Services;

public static class ChatOutput
{
    public static string VisibleText(string raw)
    {
        string visible = Regex.Replace(raw, @"<think>[\s\S]*?(?:</think>|$)", "", RegexOptions.IgnoreCase);
        const string tag = "<think>";
        for (int length = Math.Min(tag.Length - 1, visible.Length); length > 0; length--)
            if (tag.StartsWith(visible[^length..], StringComparison.OrdinalIgnoreCase)) { visible = visible[..^length]; break; }
        return visible.Trim();
    }
    private static void CheckFinish(JsonElement choice)
    {
        if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
        {
            if (reason.GetString() == "length") throw new IncompleteTranslationException("模型未完整输出译文，原片段已保留。请检查模型输出限制。");
            if (reason.GetString() == "content_filter") throw new InvalidOperationException("翻译服务未返回完整译文，请检查服务策略。原文已保留。");
        }
    }
    public static string ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var choice = doc.RootElement.GetProperty("choices")[0]; CheckFinish(choice);
        string text = VisibleText(choice.GetProperty("message").GetProperty("content").GetString() ?? "");
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("模型返回了空译文，请检查模型与接口能力。");
        return text;
    }
    public static async Task<string> ReadStreamAsync(Stream stream, Action<string>? progress, CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        var raw = new StringBuilder(); var eventData = new StringBuilder();
        string previous = ""; bool finished = false; var throttle = Stopwatch.StartNew();
        void Publish(bool force = false)
        {
            string text = VisibleText(raw.ToString());
            if (text != previous && (force || previous.Length == 0 || throttle.ElapsedMilliseconds >= 60))
            { previous = text; if (text.Length > 0) progress?.Invoke(text); throttle.Restart(); }
        }
        bool ConsumeEvent()
        {
            string data = eventData.ToString().Trim(); eventData.Clear();
            if (data.Length == 0) return false;
            if (data == "[DONE]") { finished = true; return true; }
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("error", out _)) throw new IOException("翻译服务在流式输出中返回错误，已保留原片段。");
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return false;
            var choice = choices[0]; CheckFinish(choice);
            if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                raw.Append(content.GetString());
            if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String) finished = true;
            Publish(); return finished;
        }
        while (await reader.ReadLineAsync(token) is { } line)
        {
            token.ThrowIfCancellationRequested();
            if (line.Length == 0) { if (ConsumeEvent()) break; }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            { if (eventData.Length > 0) eventData.Append('\n'); eventData.Append(line.AsSpan(5).TrimStart()); }
        }
        if (eventData.Length > 0) ConsumeEvent();
        if (!finished) throw new IOException("流式连接在译文完成前中断，已保留原片段并准备重试。");
        string result = VisibleText(raw.ToString());
        if (result.Length == 0) throw new InvalidOperationException("模型返回了空译文，请检查模型与接口能力。");
        Publish(true); return result;
    }
}
