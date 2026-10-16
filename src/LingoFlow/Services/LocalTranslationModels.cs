namespace LingoFlow.Services;

public sealed record LocalTranslationModel(string Id, string Name, string FileName, string Repository, string Revision, long Size, string Sha256, bool IsHyMt2 = false);

public static class LocalTranslationModels
{
    public const string Default = "hy-mt2-q4_k_m";
    private const string HyRevision = "a0c709d9fac510f2c807aa3af52872340dc37a4a";
    public static IReadOnlyList<LocalTranslationModel> All { get; } = new LocalTranslationModel[]
    {
        new(Default, "Hy-MT2 1.8B · Q4_K_M · 1.13 GB · 默认", "Hy-MT2-1.8B-Q4_K_M.gguf", "tencent/Hy-MT2-1.8B-GGUF", HyRevision, 1133080448, "dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699", true),
        new("hy-mt2-q6_k", "Hy-MT2 1.8B · Q6_K · 1.47 GB", "Hy-MT2-1.8B-Q6_K.gguf", "tencent/Hy-MT2-1.8B-GGUF", HyRevision, 1474785120, "d98fe604dec1f28f58f80d7d560f7177e584d3b8e5835862687660e5ff97cb40", true),
        new("hy-mt2-q8_0", "Hy-MT2 1.8B · Q8_0 · 1.91 GB", "Hy-MT2-1.8B-Q8_0.gguf", "tencent/Hy-MT2-1.8B-GGUF", HyRevision, 1908528192, "5c3fe0b1408a5ceb0143184ef247b11b579c525f4b02b060e6c851bb76fef1a4", true),
        new("0.6b", "Qwen3 0.6B · Q8 · 640 MB · 旧模型", "Qwen3-0.6B-Q8_0.gguf", "Qwen/Qwen3-0.6B-GGUF", "main", 639446688, "9465e63a22add5354d9bb4b99e90117043c7124007664907259bd16d043bb031"),
        new("1.7b", "Qwen3 1.7B · Q8 · 1.83 GB · 旧模型", "Qwen3-1.7B-Q8_0.gguf", "Qwen/Qwen3-1.7B-GGUF", "main", 1834426016, "061b54daade076b5d3362dac252678d17da8c68f07560be70818cace6590cb1a")
    };
    public static LocalTranslationModel Get(string id) => All.FirstOrDefault(m => m.Id == id) ?? throw new InvalidOperationException("不支持的翻译模型。");
    public static void Migrate(AppSettings settings)
    {
        // Earlier releases used 1.7B as their default. Keep explicitly selected
        // lightweight models, and run this migration only once.
        if (settings.LocalModelDefaultsVersion < 1 && settings.TranslationModel == "1.7b") settings.TranslationModel = Default;
        if (!All.Any(m => m.Id == settings.TranslationModel)) settings.TranslationModel = Default;
        settings.LocalModelDefaultsVersion = 1;
    }
}
