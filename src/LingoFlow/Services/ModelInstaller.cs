using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace LingoFlow.Services;

public record DownloadAsset(string Name, string Url, string Path, long Size, string Sha256);
public record InstallProgress(string Message, double Fraction);

public sealed partial class ModelInstaller
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None }) { Timeout = Timeout.InfiniteTimeSpan };
    public static string WhisperDirectory(AppSettings s) => Path.Combine(AppPaths.EnginesFor(s), "whisper-1.7.6");
    public static string WhisperGpuDirectory(AppSettings s) => Path.Combine(AppPaths.EnginesFor(s), "whisper-1.7.6-cuda");
    public const string LlamaVersion = "b11643";
    public static string LlamaDirectory(AppSettings s) => Path.Combine(AppPaths.EnginesFor(s), "llama-" + LlamaVersion);
    public static string LlamaGpuDirectory(AppSettings s) => Path.Combine(AppPaths.EnginesFor(s), "llama-" + LlamaVersion + "-vulkan");
    public static string WhisperPath(AppSettings s) => Path.Combine(AppPaths.ModelsFor(s), $"ggml-{s.WhisperModel}.bin");
    public static string VerificationWhisperPath(AppSettings s) => Path.Combine(AppPaths.ModelsFor(s), "ggml-tiny.bin");
    public static string VadPath(AppSettings s) => Path.Combine(AppPaths.ModelsFor(s), "ggml-silero-v5.1.2.bin");
    public static string TranslationPath(AppSettings s) => Path.Combine(AppPaths.ModelsFor(s), LocalTranslationModels.Get(s.TranslationModel).FileName);
    public static string FindExecutable(string directory, string name) => Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory, name, SearchOption.AllDirectories).FirstOrDefault() ?? "" : "";
    public static List<DownloadAsset> Plan(AppSettings s, bool? useGpu = null, bool translationOnly = false)
    {
        if (!new[] { "tiny", "base", "small" }.Contains(s.WhisperModel)) throw new InvalidOperationException("不支持的识别模型。");
        var translation = LocalTranslationModels.Get(s.TranslationModel);
        var list = new List<DownloadAsset>();
        string host = s.DownloadHost == "https://huggingface.co" ? s.DownloadHost : "https://hf-mirror.com";
        if (s.LocalSpeech && !translationOnly)
        {
            list.Add(new("Whisper 本地识别引擎", "https://github.com/ggml-org/whisper.cpp/releases/download/v1.7.6/whisper-bin-x64.zip", Path.Combine(AppPaths.EnginesFor(s), "whisper-1.7.6.zip"), 3675974, "0d2eca299c248f965bd0341bcb219db4b433c7f0c0ce2200d4df85765e8156a9"));
            var (size, sha) = s.WhisperModel switch
            {
                "tiny" => (77691713L, "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21"),
                "small" => (487601967L, "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b"),
                _ => (147951465L, "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe")
            };
            list.Add(new($"Whisper {s.WhisperModel} 多语言模型", $"{host}/ggerganov/whisper.cpp/resolve/main/ggml-{s.WhisperModel}.bin", WhisperPath(s), size, sha));
            list.Add(new("Silero VAD 本地人声检测", $"{host}/ggml-org/whisper-vad/resolve/9ffd54a1e1ee413ddf265af9913beaf518d1639b/ggml-silero-v5.1.2.bin", VadPath(s), 885098, "29940d98d42b91fbd05ce489f3ecf7c72f0a42f027e4875919a28fb4c04ea2cf"));
            if (s.WhisperModel != "tiny") list.Add(new("Whisper Tiny 独立识别复核", $"{host}/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin", VerificationWhisperPath(s), 77691713, "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21"));
        }
        if (s.LocalTranslation)
        {
            list.Add(new("llama.cpp 本地翻译引擎", $"https://github.com/ggml-org/llama.cpp/releases/download/{LlamaVersion}/llama-{LlamaVersion}-bin-win-cpu-x64.zip", Path.Combine(AppPaths.EnginesFor(s), "llama-" + LlamaVersion + ".zip"), 19523823, "7bf670617ed02a54de52fae4637d16cb633b8a2dfeb851469630e827cf9854d5"));
            list.Add(new(translation.Name, $"{host}/{translation.Repository}/resolve/{translation.Revision}/{translation.FileName}", TranslationPath(s), translation.Size, translation.Sha256));
        }
        if (useGpu ?? GpuAcceleration.ShouldUse(s))
        {
            if (!translationOnly) list.Add(new("Whisper NVIDIA 显卡加速组件", "https://github.com/ggml-org/whisper.cpp/releases/download/v1.7.6/whisper-cublas-12.4.0-bin-x64.zip", Path.Combine(AppPaths.EnginesFor(s), "whisper-1.7.6-cuda.zip"), 443253884, "3fc4d3ebd9a678313de50c04d9e59c43117ae190f0cb7bff602d4aeefc4efe3d"));
            if (s.LocalTranslation) list.Add(new("llama.cpp 显卡翻译组件", $"https://github.com/ggml-org/llama.cpp/releases/download/{LlamaVersion}/llama-{LlamaVersion}-bin-win-vulkan-x64.zip", Path.Combine(AppPaths.EnginesFor(s), "llama-" + LlamaVersion + "-vulkan.zip"), 32164763, "cd89a6e50f28e2805f1219010c2ec357c0c570d5ed8e91b27b6fe636109d528a"));
        }
        return list;
    }
    private static bool IsGpu(DownloadAsset asset) => asset.Path.EndsWith("-cuda.zip", StringComparison.OrdinalIgnoreCase) || asset.Path.EndsWith("-vulkan.zip", StringComparison.OrdinalIgnoreCase);
    public async Task PrepareAsync(AppSettings settings, IProgress<InstallProgress>? progress, CancellationToken token, bool translationOnly = false)
    {
        if (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
            throw new NotSupportedException("此安装包仅支持 Windows x64，请在 64 位 Windows 上运行。");
        settings.GpuPrepared = false; settings.GpuPreparationIssue = "";
        var assets = Plan(settings, translationOnly: translationOnly); bool gpuFailed = false;
        if (assets.Count > 0)
        {
            if (!System.Runtime.Intrinsics.X86.Avx2.IsSupported) throw new NotSupportedException("本地语音识别引擎需要支持 AVX2 的 CPU。请使用兼容的本机引擎或设备。");
            AppPaths.ValidateModelLocation(settings);
            await WindowsRuntime.EnsureAsync(AppPaths.EnginesFor(settings), progress, token);
        }
        Directory.CreateDirectory(AppPaths.Root);
        var ready = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets.Where(a => a.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
        {
            progress?.Report(new("正在校验已解压的 " + asset.Name, 0));
            if (await InstalledArchiveValidAsync(asset, ArchiveDirectory(asset), ArchiveExecutables(asset), token)) ready.Add(asset.Path);
        }
        long needed = assets.Where(x => !IsGpu(x) && !ready.Contains(x.Path) && !File.Exists(x.Path)).Sum(x => x.Size) +
            (assets.Any(x => !IsGpu(x) && x.Path.EndsWith(".zip") && !ready.Contains(x.Path)) ? 300_000_000 : 1_000_000);
        long available = AppPaths.FreeBytes(AppPaths.ModelsFor(settings));
        if (assets.Count > 0 && available < needed)
            throw new IOException($"模型所在磁盘空间不足，至少需要 {needed / 1e9:F1} GB 空间。");
        bool needsExtraction = assets.Any(x => IsGpu(x) && !ready.Contains(x.Path));
        long gpuNeeded = assets.Where(x => IsGpu(x) && !ready.Contains(x.Path) && !File.Exists(x.Path)).Sum(x => x.Size) + (needsExtraction ? 1_500_000_000 : 0);
        if (assets.Any(IsGpu) && available < needed + gpuNeeded)
        { gpuFailed = true; settings.GpuPreparationIssue = "显卡组件解压空间不足"; progress?.Report(new(settings.GpuPreparationIssue + "，本次使用 CPU。", 0)); }
        long total = assets.Sum(x => x.Size), completed = 0;
        foreach (var asset in assets)
        {
            if (IsGpu(asset) && gpuFailed) continue;
            long before = completed;
            progress?.Report(new($"正在准备 {asset.Name} · 来源 {new Uri(asset.Url).Host}（连接超时将自动重试）", before / (double)total));
            try
            {
                var download = new Progress<long>(bytes => progress?.Report(new($"{asset.Name}  ·  {bytes / 1e6:F1} / {asset.Size / 1e6:F1} MB", (before + bytes) / (double)total)));
                if (asset.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    await PrepareArchiveAsync(asset, ArchiveDirectory(asset), ArchiveExecutables(asset), download, token, ready.Contains(asset.Path));
                else await DownloadAsync(asset, download, token);
            }
            catch (Exception ex) when (IsGpu(asset) && ex is IOException or HttpRequestException or UnauthorizedAccessException)
            { gpuFailed = true; settings.GpuPreparationIssue = asset.Name + "准备失败：" + DescribeFailure(ex); WriteGpuFailure(settings, "下载/解压/校验", asset.Name, ex); progress?.Report(new(settings.GpuPreparationIssue, (before + asset.Size) / (double)total)); }
            completed += asset.Size;
        }
        settings.GpuPrepared = !gpuFailed && assets.Any(IsGpu);
        progress?.Report(new(gpuFailed ? "基础组件已就绪；" + settings.GpuPreparationIssue + "。本次使用 CPU。可停止翻译后重试。" : settings.GpuPrepared ? "组件校验完成，已准备显卡加速；启动时验证可用性。" : "组件校验完成，CPU 推理已就绪。", 1));
    }
    public static Task DownloadAsync(DownloadAsset asset, IProgress<long>? progress, CancellationToken token) => DownloadUsingAsync(asset, progress, token, Http);
    internal static async Task DownloadUsingAsync(DownloadAsset asset, IProgress<long>? progress, CancellationToken token, HttpClient client)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { await DownloadCoreAsync(asset, progress, token, client); return; }
            catch (Exception ex) when (!token.IsCancellationRequested && attempt < 2 && Retryable(ex))
            { await Task.Delay(TimeSpan.FromMilliseconds(400 * (attempt + 1)), token); }
            catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
            { throw new IOException("下载连接超时（90 秒无进展），已自动重试，可稍后继续续传。", ex); }
        }
    }
    private static bool Retryable(Exception ex) => ex is OperationCanceledException || ex is HttpRequestException h &&
        (h.StatusCode == null || h.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)h.StatusCode >= 500) ||
        ex is HttpIOException || ex is IOException && ex.InnerException is System.Net.Sockets.SocketException;
    public static string DescribeFailure(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } code } => $"下载服务器返回 HTTP {(int)code}；请确认对应官方源可以访问",
        HttpRequestException => "无法连接下载源，或安全连接失败；请检查网络后重试",
        OperationCanceledException => "下载超时；请稍后重试，已有进度会保留",
        UnauthorizedAccessException => "目录没有写入权限或文件被安全软件阻止；请选择可写目录",
        IOException when ex.InnerException is OperationCanceledException => "下载连接超时，已自动重试；已有进度可继续续传",
        IOException when (ex.HResult & 0xffff) is 32 or 33 => "组件文件正在使用；请先停止翻译再重试",
        _ => System.Text.RegularExpressions.Regex.Replace(ex.Message, @"https?://\S+", "[下载地址]")
    };
    private static void WriteGpuFailure(AppSettings settings, string stage, string component, Exception ex)
    {
        try { File.WriteAllText(Path.Combine(AppPaths.EnginesFor(settings), "gpu-prepare-error.json"), System.Text.Json.JsonSerializer.Serialize(new
            { time = DateTimeOffset.Now, stage, component, source = "GitHub 官方发布源", error = DescribeFailure(ex), exceptionType = ex.GetType().Name, ex.HResult }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException) { }
    }
    private static async Task DownloadCoreAsync(DownloadAsset asset, IProgress<long>? progress, CancellationToken token, HttpClient client)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(asset.Path)!);
        if (File.Exists(asset.Path))
        {
            if (await ValidAsync(asset.Path, asset, token)) { progress?.Report(asset.Size); return; }
            File.Move(asset.Path, asset.Path + ".invalid", true);
        }
        string part = asset.Path + ".part";
        long offset = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (offset > asset.Size) { File.Delete(part); offset = 0; }
        if (offset < asset.Size)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
            request.Headers.UserAgent.ParseAdd("LingoFlow/0.2.4");
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
            stall.CancelAfter(TimeSpan.FromSeconds(90));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
            response.EnsureSuccessStatusCode();
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                if (response.Content.Headers.ContentRange?.From != offset) throw new IOException("下载服务器返回了错误的续传位置，请重试。");
            }
            else offset = 0;
            using var output = new FileStream(part, offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None, 131072, true);
            using var input = await response.Content.ReadAsStreamAsync(stall.Token);
            byte[] buffer = new byte[131072];
            long count = offset;
            while (true)
            {
                stall.CancelAfter(TimeSpan.FromSeconds(90));
                int read = await input.ReadAsync(buffer, stall.Token);
                if (read == 0) break;
                count += read;
                if (count > asset.Size) throw new IOException("下载大小与清单不符，已停止下载。");
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                progress?.Report(count);
            }
        }
        if (!await ValidAsync(part, asset, token)) { File.Delete(part); throw new IOException($"{asset.Name} 文件校验失败，请重试或切换下载源。"); }
        File.Move(part, asset.Path, true); progress?.Report(asset.Size);
    }
    private static async Task<bool> ValidAsync(string path, DownloadAsset asset, CancellationToken token)
    {
        if (new FileInfo(path).Length != asset.Size) return false;
        using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase);
    }
    internal static Task ExtractAsync(string archive, string destination, string executable, CancellationToken token) => Task.Run(() =>
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("引擎压缩包包含不安全的路径。");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target) && new FileInfo(target).Length == entry.Length)
            {
                using var current = File.OpenRead(target); using var source = entry.Open();
                if (SHA256.HashData(current).SequenceEqual(SHA256.HashData(source))) continue;
            }
            entry.ExtractToFile(target, true);
        }
        if (string.IsNullOrEmpty(FindExecutable(root, executable))) throw new IOException($"下载的引擎中缺少 {executable}。");
    }, token);
}

