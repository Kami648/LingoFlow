using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LingoFlow.Services;

public sealed class LocalServers : IAsyncDisposable
{
    private readonly List<Process> children = [];
    private ChildProcessJob? job;
    private readonly string sessionKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
    private readonly Queue<string> errors = new();
    public string SpeechUrl { get; internal set; } = "";
    public string VerificationSpeechUrl { get; internal set; } = "";
    public string TranslationUrl { get; internal set; } = "";
    public string TranslationKey => sessionKey;
    public string SpeechBackend { get; private set; } = "CPU";
    public string VerificationBackend { get; private set; } = "CPU";
    public string TranslationBackend { get; private set; } = "云端";
    private string fallbackNote = "";
    public string RuntimeDescription => $"语音：{SpeechBackend} · 复核：{VerificationBackend} · 翻译：{TranslationBackend}" +
        (AccelerationFailures.Count > 0 ? "\n显卡启动异常，已回退 CPU；可重新准备组件或检查驱动。" : fallbackNote);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> gpuConfirmed = new();
    internal List<string> BackendEvidence { get; } = [];
    internal List<string> AccelerationFailures { get; } = [];
    private LocalVoiceDetector? voiceDetector;
    internal Func<AudioChunk, VoiceEvidence>? AnalyzeVoice { get; set; }
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    public async Task StartAsync(AppSettings settings, IProgress<string>? progress, CancellationToken token, bool translationOnly = false)
    {
        int threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        SpeechBackend = translationOnly ? "未启用" : "CPU"; VerificationBackend = SpeechBackend; TranslationBackend = settings.LocalTranslation ? "CPU" : "云端";
        fallbackNote = "";
        var device = settings.GpuPrepared && GpuAcceleration.ShouldUse(settings) ? GpuAcceleration.Detect() : null;
        if ((settings.LocalTranslation || !translationOnly) && !settings.GpuPrepared && GpuAcceleration.ShouldUse(settings)) fallbackNote = "\n" + (settings.GpuPreparationIssue.Length > 0 ? settings.GpuPreparationIssue : "显卡组件未就绪，本次使用 CPU。");
        try
        {
            if (settings.LocalSpeech && !translationOnly)
            {
                voiceDetector = new LocalVoiceDetector(settings); AnalyzeVoice = voiceDetector.Analyze;
                progress?.Report("正在加载语音识别模型…");
                async Task<(string Url, bool Gpu)> StartSpeech(string model, bool useGpu, bool verifier)
                {
                    Process? process = null;
                    try
                    {
                        int port = FreePort(); string url = $"http://127.0.0.1:{port}";
                        string executable = ModelInstaller.FindExecutable(useGpu ? ModelInstaller.WhisperGpuDirectory(settings) : ModelInstaller.WhisperDirectory(settings), "whisper-server.exe");
                        var args = new List<string> { "-m", RelativeModel(executable, model), "--host", "127.0.0.1", "--port", port.ToString(), "-t", (verifier ? 4 : threads).ToString(), "-l", "auto", "-bs", verifier ? "8" : settings.Accurate ? "5" : "1", "-bo", "1", "-sns", "-nc", "-nth", "1.0" };
                        args.Add(useGpu ? "-fa" : "-ng");
                        process = Launch(executable, args.ToArray(), useGpu ? device?.Index : null);
                        await WaitReady(process, url + "/", false, token);
                        if (useGpu && !gpuConfirmed.ContainsKey(process.Id)) throw new IOException("显卡识别没有确认加载到显存。");
                        return (url, useGpu);
                    }
                    catch (Exception ex) when (useGpu && !token.IsCancellationRequested && ex is not OperationCanceledException)
                    {
                        AccelerationFailures.Add(ex.Message);
                        if (process != null) StopChild(process);
                        progress?.Report(verifier ? "显卡复核未启动，复核改用 CPU。" : "显卡识别未能启动，已改用 CPU。可在设备页检查显卡和驱动。");
                        return await StartSpeech(model, false, verifier);
                    }
                }
                var main = await StartSpeech(ModelInstaller.WhisperPath(settings), device != null, false);
                SpeechUrl = main.Url; SpeechBackend = main.Gpu ? "GPU" : "CPU";
                VerificationSpeechUrl = SpeechUrl;
                VerificationBackend = SpeechBackend;
                if (settings.WhisperModel != "tiny")
                {
                    var verifier = await StartSpeech(ModelInstaller.VerificationWhisperPath(settings), main.Gpu, true);
                    VerificationSpeechUrl = verifier.Url; VerificationBackend = verifier.Gpu ? "GPU" : "CPU";
                }
            }
            if (settings.LocalTranslation)
            {
                progress?.Report("正在加载离线翻译模型…");
                async Task StartTranslation(bool useGpu)
                {
                    Process? process = null;
                    try
                    {
                        int port = FreePort(); TranslationUrl = $"http://127.0.0.1:{port}/v1";
                        string executable = ModelInstaller.FindExecutable(useGpu ? ModelInstaller.LlamaGpuDirectory(settings) : ModelInstaller.LlamaDirectory(settings), "llama-server.exe");
                        string? gpuDevice = useGpu ? await GpuAcceleration.VulkanDeviceAsync(executable, device!.Name, token) : null;
                        if (useGpu && gpuDevice == null) throw new IOException("翻译引擎未找到匹配的显卡。");
                        var args = new List<string> { "-m", RelativeModel(executable, ModelInstaller.TranslationPath(settings)), "--host", "127.0.0.1", "--port", port.ToString(), "-t", threads.ToString(), "-c", "4096", "-np", "1", "-lv", "4", "-ngl", useGpu ? "99" : "0", "--jinja", "--reasoning-budget", "0", "--api-key", sessionKey };
                        if (gpuDevice != null) { args.Add("--device"); args.Add(gpuDevice); }
                        process = Launch(executable, args.ToArray());
                        await WaitReady(process, $"http://127.0.0.1:{port}/health", true, token);
                        if (useGpu && !gpuConfirmed.ContainsKey(process.Id)) throw new IOException("翻译模型没有确认加载到显存。");
                        TranslationBackend = useGpu ? "GPU" : "CPU";
                    }
                    catch (Exception ex) when (useGpu && !token.IsCancellationRequested && ex is not OperationCanceledException)
                    {
                        AccelerationFailures.Add(ex.Message);
                        if (process != null) StopChild(process);
                        progress?.Report("显卡翻译未能启动，已改用 CPU；也可选择云端文字翻译。");
                        await StartTranslation(false);
                    }
                }
                await StartTranslation(device != null);
            }
            if (SpeechBackend == "GPU" || TranslationBackend == "GPU")
            {
                progress?.Report("正在预热显卡模型，减少第一句字幕的等待…");
                using var warmup = new InferenceClient();
                var warmSettings = settings.Copy(); warmSettings.SourceLanguage = "en"; warmSettings.TargetLanguage = "zh"; warmSettings.StreamTranslation = false;
                var sample = Wave.ReadPcm16(Path.Combine(AppContext.BaseDirectory, "Assets", "benchmark.wav"));
                // Local public sample only. Never call a cloud translation API,
                // publish these results, or seed later recognition text context.
                if (SpeechBackend == "GPU") await warmup.RecognizeAsync(new(sample, 0, sample.Length / 16000d), warmSettings, this, token);
                if (TranslationBackend == "GPU") await warmup.TranslateAsync("Hello.", "en", warmSettings, this, [], token);
            }
            progress?.Report(RuntimeDescription);
        }
        catch (Exception ex) when (device != null && !token.IsCancellationRequested && ex is not OperationCanceledException && (SpeechBackend == "GPU" || TranslationBackend == "GPU"))
        {
            AccelerationFailures.Add("显卡预热未完成，已改用 CPU：" + ex.Message);
            await DisposeAsync();
            var cpuSettings = settings.Copy(); cpuSettings.Acceleration = "cpu"; cpuSettings.GpuPrepared = false;
            progress?.Report("显卡预热未能完成，正在改用 CPU…");
            await StartAsync(cpuSettings, progress, token, translationOnly);
        }
        catch { await DisposeAsync(); throw; }
    }
    private static string RelativeModel(string executable, string model) => string.IsNullOrEmpty(executable) ? model : Path.GetRelativePath(Path.GetDirectoryName(executable)!, model);
    private Process Launch(string file, string[] args, int? cudaDevice = null)
    {
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) throw new FileNotFoundException("缺少本地引擎，请先准备模型与组件。");
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = Path.GetDirectoryName(file)! };
        if (cudaDevice.HasValue) info.Environment["CUDA_VISIBLE_DEVICES"] = cudaDevice.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (string arg in args) info.ArgumentList.Add(arg);
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        int processId = 0;
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (errors) { errors.Enqueue(e.Data); while (errors.Count > 8) errors.Dequeue(); }
            if (e.Data.Contains("using CUDA0 backend") || Regex.IsMatch(e.Data, @"offloaded [1-9]\d*/\d+ layers to GPU"))
            { gpuConfirmed[processId] = true; lock (BackendEvidence) BackendEvidence.Add(e.Data); }
        };
        process.OutputDataReceived += (_, _) => { };
        process.Start(); processId = process.Id; children.Add(process);
        job ??= new ChildProcessJob(); job.Add(process);
        process.BeginErrorReadLine(); process.BeginOutputReadLine(); return process;
    }
    private void StopChild(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (InvalidOperationException) { }
        finally { children.Remove(process); process.Dispose(); }
    }
    private async Task WaitReady(Process process, string url, bool requireSuccess, CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var start = Stopwatch.StartNew();
        while (start.Elapsed < TimeSpan.FromMinutes(3))
        {
            token.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                string detail; lock (errors) detail = string.Join("\n", errors);
                throw new InvalidOperationException($"本地引擎启动失败（{process.ExitCode}）。请检查微软 VC++ 运行库、磁盘与可用内存。\n{detail}");
            }
            try { using var response = await http.GetAsync(url, token); if (requireSuccess ? response.IsSuccessStatusCode : response.StatusCode != HttpStatusCode.ServiceUnavailable) return; }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!token.IsCancellationRequested) { }
            await Task.Delay(250, token);
        }
        throw new TimeoutException("本地模型在 3 分钟内未能加载，请尝试更小的本地模型；离线翻译过慢可改用云端文字翻译。");
    }
    public ValueTask DisposeAsync()
    {
        AnalyzeVoice = null; voiceDetector?.Dispose(); voiceDetector = null;
        foreach (var p in children)
        {
            try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { }
            finally { p.Dispose(); }
        }
        children.Clear(); job?.Dispose(); job = null; return ValueTask.CompletedTask;
    }
}

public sealed partial class InferenceClient : IDisposable
{
    private readonly HttpClient http;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> nonStreaming = new();
    public event Action<string>? Notice;
    internal event Action<string>? RejectedOutput;
    public InferenceClient(HttpMessageHandler? handler = null) { http = handler == null ? new() : new(handler); http.Timeout = Timeout.InfiniteTimeSpan; }
    public static Uri Endpoint(string baseUrl, string path)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("接口地址必须是有效的 HTTP(S) 基础地址，且不能包含账号、查询参数或片段。");
        if (uri.Scheme == "http" && !uri.IsLoopback) throw new InvalidOperationException("远程云端接口请使用 HTTPS。本机服务可以使用 HTTP。");
        var raw = uri.AbsoluteUri.TrimEnd('/');
        return new(raw.EndsWith("/" + path, StringComparison.OrdinalIgnoreCase) ? raw : raw + "/" + path);
    }
    private static void Authenticate(HttpRequestMessage request, string key)
    {
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
    }
    public async Task<SpeechResult> RecognizeAsync(AudioChunk chunk, AppSettings settings, LocalServers servers, CancellationToken token)
    {
        if (!Uri.TryCreate(servers.SpeechUrl + "/inference", UriKind.Absolute, out var uri) || !uri.IsLoopback)
            throw new InvalidOperationException("本地语音识别服务未启动，音频只能交给本机识别引擎。");
        token.ThrowIfCancellationRequested();
        if (chunk.Samples.All(sample => sample is >= -2 and <= 2)) return new("", settings.SourceLanguage);
        if (AudioSignal.IsStationaryTone(chunk.Samples)) return new("", settings.SourceLanguage);
        var evidence = servers.AnalyzeVoice?.Invoke(chunk) ?? throw new InvalidOperationException("独立人声校验尚未启动，已阻止未经确认的识别。");
        if (!evidence.Confirmed) return new("", settings.SourceLanguage);
        var trimmed = chunk with { Samples = chunk.Samples[evidence.FirstSample..evidence.LastSample] };
        var result = await TranscribeAsync(trimmed, settings, uri, settings.Accurate ? 5 : 1, token);
        if (string.IsNullOrWhiteSpace(result.Text)) return result;
        bool risky = SpeechVerification.IsRisky(result.Text);
        // A second high-beam decode of every clear utterance doubled latency
        // without proving correctness. Keep verification for suspicious output.
        if (risky || result.NeedsVerification || evidence.Peak < .98)
        {
            int freshStart = Math.Max(evidence.FirstSample, (int)Math.Round(chunk.OverlapSeconds * 16000));
            if (risky && (evidence.FreshSeconds < .64 || evidence.Peak < .98 || evidence.LastSample - freshStart < 6400))
            { Notice?.Invoke("当前片段的人声依据不足，可疑识别未生成字幕。"); return new("", settings.SourceLanguage); }
            var verificationUri = uri;
            if ((risky || evidence.Peak < .98) && servers.VerificationSpeechUrl.Length > 0)
            {
                verificationUri = new Uri(servers.VerificationSpeechUrl + "/inference");
                if (!verificationUri.IsLoopback) throw new InvalidOperationException("识别复核服务必须在本机运行。");
            }
            var verification = await TranscribeAsync(risky ? chunk with { Samples = chunk.Samples[freshStart..evidence.LastSample] } : trimmed, settings, verificationUri, 8, token);
            if (!SpeechVerification.Agrees(result.Text, verification.Text))
            { Notice?.Invoke("两次语音识别不一致，当前片段暂不生成译文。"); return new("", settings.SourceLanguage); }
        }
        return result with { SpeechEnded = chunk.Duration - evidence.LastSample / 16000d >= (settings.Accurate ? .8 : .55) };
    }
    private async Task<SpeechResult> TranscribeAsync(AudioChunk chunk, AppSettings settings, Uri uri, int beam, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(90)); token = timeout.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        using var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(chunk.ToWave()); audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "speech.wav");
        form.Add(new StringContent("verbose_json"), "response_format");
        form.Add(new StringContent(settings.SourceLanguage), "language");
        form.Add(new StringContent(beam.ToString()), "beam_size"); form.Add(new StringContent("0.0"), "temperature");
        form.Add(new StringContent("0.0"), "temperature_inc");
        // Audio overlap already carries the acoustic context. Reusing generated
        // text lets a hallucination seed every later slice, especially in music.
        form.Add(new StringContent(""), "prompt");
        form.Add(new StringContent("true"), "no_context");
        // Voice evidence is checked independently before HTTP. Do not delegate
        // the decision to a decoder that can still return text for background.
        form.Add(new StringContent("false"), "vad");
        form.Add(new StringContent("0.5"), "vad_threshold");
        form.Add(new StringContent("100"), "vad_min_speech_duration_ms");
        form.Add(new StringContent("100"), "vad_min_silence_duration_ms");
        form.Add(new StringContent("80"), "vad_speech_pad_ms");
        request.Content = form;
        using var response = await http.SendAsync(request, token);
        string json = await ReadResponse(response, token);
        using var doc = JsonDocument.Parse(json);
        return SpeechOutput.Parse(doc.RootElement, settings.SourceLanguage);
    }
    public async Task<string> TranslateAsync(string text, string source, AppSettings settings, LocalServers servers, IReadOnlyList<Subtitle> context, CancellationToken token, Action<string>? progress = null)
    {
        if (TranslationLanguage.IsNeutral(text)) return text.Trim();
        string customPrompt = TranslationPrompts.Body(settings.TranslationPrompt).Trim();
        var endpoint = settings.LocalTranslation
            ? new EndpointSettings { BaseUrl = servers.TranslationUrl, Model = "local", Key = servers.TranslationKey }
            : settings.Translation;
        bool indexModel = endpoint.Model.Contains("Index-Translate", StringComparison.OrdinalIgnoreCase);
        bool hyModel = settings.LocalTranslation ? LocalTranslationModels.Get(settings.TranslationModel).IsHyMt2 : endpoint.Model.Contains("Hy-MT2", StringComparison.OrdinalIgnoreCase);
        string sourceCode = settings.SourceLanguage != "auto" ? settings.SourceLanguage : source;
        if (customPrompt.Length > 0) customPrompt = TranslationPrompts.Render(customPrompt, sourceCode, settings.TargetLanguage);
        string targetName = TranslationLanguage.TargetName(settings.TargetLanguage);
        string rule = TranslationLanguage.Rule(settings.TargetLanguage);
        string data = JsonSerializer.Serialize(new { source_language = sourceCode, target_language = settings.TargetLanguage,
            context = context.TakeLast(settings.SubtitleRefinement ? 6 : settings.Accurate ? 2 : 1).Select(s => s.Original), text });
        void SafeProgress(string partial)
        {
            if (TranslationLanguage.IsValid(partial, settings.TargetLanguage, text, sourceCode, allowModelTerms: true)) progress?.Invoke(partial);
        }
        for (int repair = 0; repair < 3; repair++)
        {
            var messages = new List<object>();
            string task = $"请将以下文本翻译为{targetName}，直接输出翻译结果，不要进行任何解释。{rule}" + (customPrompt.Length > 0 ? "\n\n翻译提示词：\n" + customPrompt : "");
            string correction = repair == 0 ? "" : $"上一轮未满足目标语言要求，请根据原文重新翻译为{targetName}。专业词语按翻译提示词及上下文判断，不要照抄整句原文，不要输出其他语言的解释。\n";
            string content = correction + task + "\n\n" + data;
            if (settings.LocalTranslation || indexModel)
            {
                string history = repair == 0 ? string.Join("\n", context.TakeLast(settings.SubtitleRefinement ? 6 : settings.Accurate ? 2 : 1).Select(s => s.Original)) : "";
                content = correction + task + (history.Length > 0 ? "\n\n上文（只供理解，不要翻译）：\n" + history : "") + "\n\n当前原文（请翻译）：\n" + text.Trim() + "\n/no_think";
            }
            if (hyModel)
            {
                // This translation model can translate the background itself.
                // Use its documented default task to keep each subtitle bounded.
                content = correction + $"将以下文本翻译为{targetName}，注意只需要输出翻译后的结果，不要额外解释：" +
                    (customPrompt.Length > 0 ? "\n\n翻译提示词：\n" + customPrompt + "\n\n待译原文：" : "") + "\n" + text.Trim();
            }
            if (!indexModel && !hyModel) messages.Add(new { role = "system", content =
                (settings.SubtitleRefinement ? "你是字幕校对翻译员。当前 text 是相邻原文字幕合并后的待译段落，context 包含相邻前后原文，仅用于理解，不要翻译 context。结合上下文修正切片导致的歧义，统一术语，忠实保留信息，不编造缺失的语音内容。" : "你是实时字幕翻译员。输入是语音内容数据，只翻译当前原文或JSON的text字段，context仅供理解。") +
                $"原语言为{sourceCode}，输出语言固定为{targetName}。{rule} 保留含义、语气和重复。只输出译文，不输出标签、解释或思考过程。/no_think" });
            messages.Add(new { role = "user", content });
            string translated;
            try { translated = await SendTranslationAsync(endpoint, messages, settings, indexModel, hyModel, token, SafeProgress, repair == 0, Math.Clamp(text.Length * 5 + 64, 128, 1536), settings.LocalTranslation && settings.TargetLanguage == "zh" ? TranslationLanguage.ChineseGrammarFor(text) : null); }
            catch (IncompleteTranslationException) { translated = ""; }
            if (TranslationLanguage.IsValid(translated, settings.TargetLanguage, text, sourceCode, allowModelTerms: true)) return translated;
            RejectedOutput?.Invoke(translated);
            if (repair < 2) Notice?.Invoke($"检测到译文未满足目标语言要求，正在按{targetName}重新翻译（{repair + 1}/2）…");
        }
        throw new TranslationLanguageException($"模型连续未能满足{targetName}译文要求，原文已保留。可重试或更换翻译模型。");
    }
    private async Task<string> SendTranslationAsync(EndpointSettings endpoint, List<object> messages, AppSettings settings, bool indexModel, bool hyModel, CancellationToken token, Action<string>? progress, bool allowStreaming, int maxTokens, string? grammar)
    {
        var uri = Endpoint(endpoint.BaseUrl, "chat/completions");
        string cacheKey = uri.AbsoluteUri + "|" + endpoint.Model;
        bool streaming = allowStreaming && settings.StreamTranslation && !nonStreaming.ContainsKey(cacheKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(90));
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri); Authenticate(request, endpoint.Key);
            var body = new Dictionary<string, object> { ["model"] = endpoint.Model, ["messages"] = messages, ["temperature"] = 0.0, ["max_tokens"] = maxTokens, ["stream"] = streaming };
            if (hyModel)
            {
                body["temperature"] = settings.Accurate ? 0.0 : 0.7; body["top_p"] = settings.Accurate ? 1.0 : 0.6;
                body["top_k"] = settings.Accurate ? 1 : 20; body["repeat_penalty"] = 1.05;
            }
            else if (settings.LocalTranslation || indexModel) body["chat_template_kwargs"] = new { enable_thinking = false };
            if (grammar != null) body["grammar"] = grammar;
            request.Content = JsonContent.Create(body);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (streaming && attempt == 0 && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotAcceptable or HttpStatusCode.UnsupportedMediaType or HttpStatusCode.UnprocessableEntity or HttpStatusCode.NotImplemented)
            {
                nonStreaming[cacheKey] = 0; streaming = false;
                Notice?.Invoke("此翻译服务不接受流式请求，当前会话已自动使用完整响应。"); continue;
            }
            if (!response.IsSuccessStatusCode) await ReadResponse(response, timeout.Token);
            if (response.Content.Headers.ContentType?.MediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
            {
                using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                return await ChatOutput.ReadStreamAsync(stream, progress, timeout.Token);
            }
            string translated = ChatOutput.ParseJson(await response.Content.ReadAsStringAsync(timeout.Token));
            return translated;
        }
    }
    public async Task TestAsync(EndpointSettings endpoint, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Model)) throw new InvalidOperationException("请填写模型名称。");
        var settings = new AppSettings { Route = "hybrid", Translation = endpoint.Copy(), TargetLanguage = "zh" };
        await TranslateAsync("Hello.", "en", settings, new LocalServers(), [], token);
    }
    private static async Task<string> ReadResponse(HttpResponseMessage response, CancellationToken token)
    {
        string body = await response.Content.ReadAsStringAsync(token);
        if (response.IsSuccessStatusCode) return body;
        // Provider error bodies may echo credentials or utterances, so keep them out of logs.
        throw new HttpRequestException((int)response.StatusCode switch
        {
            401 or 403 => "API 身份验证失败，请检查密钥与模型权限。",
            404 => "API 路径或模型不存在，请检查基础地址、模型名及接口能力。",
            429 => "API 请求超限或余额不足，请检查额度，或切换服务商。",
            _ => $"模型服务返回 HTTP {(int)response.StatusCode}，请检查服务状态与接口兼容性。"
        }, null, response.StatusCode);
    }
    public void Dispose() => http.Dispose();
}
