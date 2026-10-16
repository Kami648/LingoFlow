using System.Text.Json;

namespace LingoFlow.Services;

public static class SpeechOutput
{
    public static SpeechResult Parse(JsonElement response, string requestedLanguage)
    {
        string text = response.TryGetProperty("text", out var t) ? t.GetString()?.Trim() ?? "" : "";
        string language = response.TryGetProperty("language", out var l) ? l.GetString() ?? "auto" : requestedLanguage;
        language = language.ToLowerInvariant() switch { "english" => "en", "chinese" => "zh", "japanese" => "ja", "korean" => "ko", _ => language };
        bool uncertain = false;
        if (response.TryGetProperty("segments", out var segments) && segments.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in segments.EnumerateArray())
            {
                // Metadata is secondary evidence, never the voice gate itself.
                bool nonSpeech = segment.TryGetProperty("no_speech_prob", out var ns) && ns.ValueKind == JsonValueKind.Number && ns.TryGetDouble(out double probability) && probability >= .6;
                bool lowConfidence = segment.TryGetProperty("avg_logprob", out var lp) && lp.ValueKind == JsonValueKind.Number && lp.TryGetDouble(out double logProbability) && logProbability <= -1;
                if (nonSpeech && lowConfidence) uncertain = true;
            }
        }
        return new(text, language) { NeedsVerification = uncertain };
    }
}
