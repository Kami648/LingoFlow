namespace LingoFlow;

public sealed class EndpointSettings
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "whisper-1";
    public string ProtectedKey { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string Key { get; set; } = "";
    public EndpointSettings Copy() => (EndpointSettings)MemberwiseClone();
}

public sealed class AppSettings
{
    public bool FirstRunComplete { get; set; }
    private string route = "hybrid";
    public string Route { get => route; set => route = value == "local" ? "local" : "hybrid"; }
    public string SourceLanguage { get; set; } = "auto";
    public string TargetLanguage { get; set; } = "zh";
    public bool Accurate { get; set; } = true;
    public int RecognitionQualityVersion { get; set; }
    public bool MixedLanguage { get; set; } = true;
    public double VoiceThreshold { get; set; } = .008;
    public string WhisperModel { get; set; } = "small";
    public string Acceleration { get; set; } = "auto";
    [System.Text.Json.Serialization.JsonIgnore] public bool GpuPrepared { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string GpuPreparationIssue { get; set; } = "";
    public string TranslationModel { get; set; } = Services.LocalTranslationModels.Default;
    public int LocalModelDefaultsVersion { get; set; }
    public string DownloadHost { get; set; } = "https://hf-mirror.com";
    public int DownloadSourceVersion { get; set; }
    public string ModelDirectory { get; set; } = "";
    public EndpointSettings Translation { get; set; } = new() { BaseUrl = "https://api.deepseek.com/v1", Model = "deepseek-flash" };
    public double SubtitleSize { get; set; } = 30;
    public double SubtitleOpacity { get; set; } = .87;
    public bool ShowOriginal { get; set; } = true;
    public bool Typewriter { get; set; } = true;
    public int OverlayLines { get; set; } = 3;
    public int OverlayLayoutVersion { get; set; }
    public bool StreamTranslation { get; set; } = true;
    public int RealtimeCaptionsVersion { get; set; }
    public bool ExportOnStop { get; set; } = true;
    public string ExportDirectory { get; set; } = "";
    public const string DefaultTranslationPrompt = "请结合上下文，准确、自然地翻译为所选目标语言。普通词汇应翻译；专业术语、人名、品牌名、产品名和缩写由你根据语境判断是否保留原文，并保持前后一致。例如 Token 表示大模型技术概念时可保留 Token，表示普通词义时应按语境翻译。不要机械地保留所有英文，也不要照抄整句原文。忠实保留信息，不编造原文没有的内容，只输出译文，不添加解释、标签或思考过程。";
    public string TranslationPrompt { get; set; } = DefaultTranslationPrompt;
    public int RefineContextTokens { get; set; } = 1_019_715;
    public int RefineChunkTokens { get; set; } // 0: automatic, including response budget
    public int RefineOutputTokens { get; set; } = 65_536;
    [System.Text.Json.Serialization.JsonIgnore] public bool SubtitleRefinement { get; set; }
    public ColorSettings Colors { get; set; } = new();
    public bool HotkeyEnabled { get; set; } = true;
    public uint HotkeyModifiers { get; set; } = 7;
    public uint HotkeyKey { get; set; } = 0x4C;
    public double OverlayLeft { get; set; } = double.NaN;
    public double OverlayTop { get; set; } = double.NaN;
    public double OverlayWidth { get; set; } = 900;
    public double OverlayHeight { get; set; } = 340;
    public bool LocalSpeech => true;
    public bool LocalTranslation => Route == "local";
    public AppSettings Copy()
    {
        var result = (AppSettings)MemberwiseClone();
        result.Translation = Translation.Copy(); result.Colors = Colors.Copy();
        return result;
    }
}

public record AudioSource(int Pid, string Name, string Title)
{
    public override string ToString() => Pid == 0 ? "系统全部声音（包含其他应用）" : $"{Name}  ·  {Title}  [{Pid}]";
}
public record SpeechResult(string Text, string Language)
{
    public bool NeedsVerification { get; init; }
    public bool SpeechEnded { get; init; }
}
public record Subtitle(DateTimeOffset Time, double Start, double End, string Original, string Translation, string Language, double ProcessingSeconds)
{
    public long Sequence { get; init; }
    public bool IsPending { get; init; }
    public bool HasError { get; init; }
    public bool SourceMissing { get; init; }
    public string FailureReason { get; init; } = "";
    public string BeforeRefinement { get; init; } = "";
    public bool SourceUncertain { get; init; }
}

public static class Languages
{
    public static readonly (string Code, string Name)[] All = [("zh", "中文"), ("en", "English"), ("ja", "日本語"), ("ko", "한국어")];
    public static string Name(string code) => All.FirstOrDefault(x => x.Code == code).Name ?? code;
}
