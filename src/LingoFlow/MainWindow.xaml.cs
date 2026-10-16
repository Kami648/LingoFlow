using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using LingoFlow.Services;

namespace LingoFlow;

public partial class MainWindow : Window
{
    public ObservableCollection<Subtitle> Subtitles { get; } = [];
    private readonly SessionArchive archive = new();
    private CancellationTokenSource? finishCancellation;
    private bool stopping;
    private AppSettings settings;
    private DeviceReport? device;
    private OverlayWindow? overlay;
    private System.Windows.Forms.NotifyIcon? tray;
    private TranslationPipeline? pipeline;
    private CancellationTokenSource? sessionCancellation, installCancellation;
    private Task? startFlow;
    private Task? stopFlow;
    private bool loaded, sessionActive, quitting, preparing, testingApi;
    private long lastMeter;
    private HwndSource? source;
    private GlobalHotkey? hotkey;

    public MainWindow(AppSettings settings)
    {
        this.settings = settings;
        Theme.Normalize(settings.Colors); Theme.Apply(settings.Colors);
        InitializeComponent(); DataContext = this;
        foreach (var model in LocalTranslationModels.All) TranslationModelBox.Items.Add(new ComboBoxItem { Content = model.Name, Tag = model.Id });
        SourceLanguageBox.Items.Add(new ComboBoxItem { Content = "自动识别", Tag = "auto" });
        foreach (var language in Languages.All)
        {
            SourceLanguageBox.Items.Add(new ComboBoxItem { Content = language.Name, Tag = language.Code });
            TargetLanguageBox.Items.Add(new ComboBoxItem { Content = language.Name, Tag = language.Code });
        }
        foreach (uint key in Enumerable.Range(0x41, 26).Concat(Enumerable.Range(0x30, 10)).Concat(Enumerable.Range(0x70, 24)))
            HotkeyKeyBox.Items.Add(new ComboBoxItem { Content = HotkeyBinding.KeyName(key), Tag = key.ToString() });
        FillSettings(); InitializeFilePage(); InitializeRefinePage(); RefreshSources(); UpdateColorButtons();
        loaded = true; UpdateRoute(); UpdateDownloadPlan(); ApplyAppearance(); SetControls();
        Loaded += (_, _) => { DetectDevice(); SetupTray(); if (!settings.FirstRunComplete) SwitchPage("models"); };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) Hide(); };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            ApplyTitleBar();
            source = HwndSource.FromHwnd(handle); source.AddHook(Hook);
            hotkey = new GlobalHotkey(handle);
            hotkey.TryApply(settings.HotkeyEnabled ? new HotkeyBinding(settings.HotkeyModifiers, settings.HotkeyKey) : null, out string problem);
            UpdateHotkeyUi(problem);
        };
        Closing += (_, e) =>
        {
            if (quitting) return;
            e.Cancel = true; Hide();
            tray?.ShowBalloonTip(2200, "译流仍在运行", "从托盘恢复窗口或退出；悬浮字幕可以继续显示。", System.Windows.Forms.ToolTipIcon.Info);
        };
    }
    private void FillSettings()
    {
        Select(RouteBox, settings.Route); Select(SourceLanguageBox, settings.SourceLanguage); Select(TargetLanguageBox, settings.TargetLanguage);
        Select(WhisperModelBox, settings.WhisperModel); Select(TranslationModelBox, settings.TranslationModel); Select(DownloadHostBox, settings.DownloadHost);
        Select(AccelerationBox, settings.Acceleration);
        FastRadio.IsChecked = !settings.Accurate; AccurateRadio.IsChecked = settings.Accurate; MixedBox.IsChecked = settings.MixedLanguage;
        TranslationUrlBox.Text = settings.Translation.BaseUrl; TranslationApiModelBox.Text = settings.Translation.Model; TranslationKeyBox.Password = settings.Translation.Key;
        FontSlider.Value = settings.SubtitleSize; OpacitySlider.Value = settings.SubtitleOpacity;
        OriginalBox.IsChecked = settings.ShowOriginal; TypewriterBox.IsChecked = settings.Typewriter; StreamTranslationBox.IsChecked = settings.StreamTranslation; ThresholdSlider.Value = settings.VoiceThreshold;
        Select(OverlayLinesBox, settings.OverlayLines.ToString());
        ModelDirectoryBox.Text = AppPaths.ModelsFor(settings);
        ExportOnStopBox.IsChecked = settings.ExportOnStop; ExportDirectoryBox.Text = SubtitleFilesExport.DirectoryFor(settings);
        HotkeyEnabledBox.IsChecked = settings.HotkeyEnabled;
        Select(HotkeyModifiersBox, settings.HotkeyModifiers.ToString()); Select(HotkeyKeyBox, settings.HotkeyKey.ToString());
    }
    private static void Select(ComboBox box, string value)
    { foreach (ComboBoxItem item in box.Items) if ((string)item.Tag == value) { box.SelectedItem = item; return; } box.SelectedIndex = 0; }
    private static string SelectedTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
    private AppSettings ReadSettings()
    {
        var s = settings.Copy();
        s.Route = SelectedTag(RouteBox); s.SourceLanguage = SelectedTag(SourceLanguageBox); s.TargetLanguage = SelectedTag(TargetLanguageBox);
        s.Accurate = AccurateRadio.IsChecked == true; s.MixedLanguage = MixedBox.IsChecked == true;
        s.WhisperModel = SelectedTag(WhisperModelBox); s.TranslationModel = SelectedTag(TranslationModelBox); s.DownloadHost = SelectedTag(DownloadHostBox);
        s.Acceleration = SelectedTag(AccelerationBox);
        s.Translation.BaseUrl = TranslationUrlBox.Text.Trim(); s.Translation.Model = TranslationApiModelBox.Text.Trim(); s.Translation.Key = TranslationKeyBox.Password.Trim();
        s.SubtitleSize = FontSlider.Value; s.SubtitleOpacity = OpacitySlider.Value;
        s.ShowOriginal = OriginalBox.IsChecked == true; s.Typewriter = TypewriterBox.IsChecked == true; s.VoiceThreshold = ThresholdSlider.Value;
        s.StreamTranslation = StreamTranslationBox.IsChecked == true;
        s.OverlayLines = int.TryParse(SelectedTag(OverlayLinesBox), out int lines) ? Math.Clamp(lines, 1, 6) : 3;
        s.ExportOnStop = ExportOnStopBox.IsChecked == true;
        s.TranslationPrompt = TranslationPrompts.Body(TranslationPromptBox.Text);
        // Invalid, unfinished numeric edits must not interrupt live translation.
        if (int.TryParse(RefineContextBox.Text, out int context) && context is >= 4096 and <= 4_000_000) s.RefineContextTokens = context;
        if (int.TryParse(RefineChunkBox.Text, out int chunk) && chunk is >= 0 and <= 4_000_000) s.RefineChunkTokens = chunk;
        if (int.TryParse(RefineOutputBox.Text, out int output) && output is >= 1024 and <= 400_000) s.RefineOutputTokens = output;
        overlay?.CapturePosition(s); return s;
    }
    private void SaveSettings() { settings = ReadSettings(); SettingsStore.Save(settings); }
    private void SwitchPage(string page)
    {
        LivePage.Visibility = page == "live" ? Visibility.Visible : Visibility.Collapsed;
        ModelsPage.Visibility = page == "models" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        ImportPage.Visibility = page == "import" ? Visibility.Visible : Visibility.Collapsed;
        RefinePage.Visibility = page == "refine" ? Visibility.Visible : Visibility.Collapsed;
        RefineNav.Background = NavBrush(page == "refine");
        LiveActionButtons.Visibility = page is "import" or "refine" ? Visibility.Collapsed : Visibility.Visible;
        PageTitle.Text = page switch { "refine" => "字幕精修", "import" => "文件翻译", "models" => "设备与模型", "settings" => "接口与外观", _ => "实时翻译" };
        PageCaption.Text = page switch { "import" => "让视频和音频，拥有自己的双语字幕。", "models" => "找到适合这台设备的方案，让翻译运行得更从容。", "settings" => "连接你的 AI，调成你喜欢的样子。", _ => "捕获应用声音，让每一句话自然流动。" };
        ImportNav.Background = NavBrush(page == "import");
        if (page == "refine") PageCaption.Text = "让上下文连起来，让术语保持一致。";
        LiveNav.Background = NavBrush(page == "live"); ModelsNav.Background = NavBrush(page == "models"); SettingsNav.Background = NavBrush(page == "settings");
    }
    private static Brush NavBrush(bool active) => active ? (Brush)Application.Current.Resources["Selected"] : Brushes.Transparent;
    private void LiveNav_Click(object sender, RoutedEventArgs e) => SwitchPage("live");
    private void ModelsNav_Click(object sender, RoutedEventArgs e) => SwitchPage("models");
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => SwitchPage("settings");
    private void RefreshSources()
    {
        int? selected = (SourceBox.SelectedItem as AudioSource)?.Pid;
        var items = DeviceProbe.Sources(); SourceBox.ItemsSource = items;
        SourceBox.SelectedItem = items.FirstOrDefault(x => x.Pid == selected);
        if (SourceBox.SelectedItem == null) SourceBox.SelectedItem = items.FirstOrDefault(x => x.Name.Equals("msedge", StringComparison.OrdinalIgnoreCase)) ?? items.FirstOrDefault(x => x.Pid != 0);
        if (!DeviceProbe.SupportsProcessCapture) CaptureHint.Text = DeviceProbe.CanAttemptProcessCapture ? "此 Windows 10 版本的进程捕获需在启动时验证；若不支持，可手动选择系统全部声音。" : "此 Windows 版本不支持指定进程捕获。可手动选择“系统全部声音”，该选项会包含其他应用声音。";
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshSources();
    private void DetectDevice()
    {
        try
        {
            device = DeviceProbe.Detect(AppPaths.ModelsFor(ReadSettings()));
            DeviceText.Text = $"{device.System}\n{device.Cpu} · {device.Threads} 个逻辑处理器\n内存 {device.RamGb:F1} GB · 可用 {device.AvailableRamGb:F1} GB · 模型磁盘剩余 {device.DiskGb:F1} GB\n显卡：{device.Gpu}\n指定进程音频：{(device.ProcessCapture ? "系统支持，将在启动捕获时验证" : DeviceProbe.CanAttemptProcessCapture ? "此 Windows 10 版本需启动验证，不支持时可选择系统全部声音" : "系统不支持，可明确选择系统全部声音")}";
            DeviceText.Text += "\n" + GpuAcceleration.Describe(ReadSettings());
            RecommendationText.Text = "推荐：" + RouteName(device.Recommendation); RecommendationReason.Text = device.Reason;
            if (!settings.FirstRunComplete) StatusText.Text = "已检测设备。可应用推荐方案，再准备模型或配置云端接口。";
        }
        catch (Exception ex) { DeviceText.Text = "设备检测未能完成：" + ex.Message; }
    }
    private void Detect_Click(object sender, RoutedEventArgs e) { GpuAcceleration.Detect(true); DetectDevice(); UpdateDownloadPlan(); }
    private static string RouteName(string route) => route == "local" ? "本地识别＋本地翻译" : "本地识别＋云端文字翻译";
    private void ApplyRecommendation_Click(object sender, RoutedEventArgs e)
    {
        if (sessionActive || preparing || device == null) return;
        Select(RouteBox, device.Recommendation);
        Select(WhisperModelBox, device.AvailableRamGb >= 3 ? "small" : device.AvailableRamGb >= 2 ? "base" : "tiny");
        Select(TranslationModelBox, LocalTranslationModels.Default);
        StatusText.Text = "已应用推荐方案；你可以调整模型档位，再下载或开始。";
    }
    private void Route_Changed(object sender, SelectionChangedEventArgs e) { if (loaded) { UpdateRoute(); UpdateDownloadPlan(); } }
    private void Model_Changed(object sender, SelectionChangedEventArgs e) { if (loaded) UpdateDownloadPlan(); }
    private void UpdateRoute()
    {
        PrivacyText.Text = SelectedTag(RouteBox) switch
        {
            "local" => "离线模式：识别和翻译均在本机运行，不向云端发送音频或文字。",
            _ => "混合模式：音频在本地识别，仅将识别后的文字发送到翻译服务。"
        };
    }
    private void UpdateDownloadPlan()
    {
        var plan = ModelInstaller.Plan(ReadSettings());
        DownloadPlanText.Text = $"当前方案：{RouteName(SelectedTag(RouteBox))} · 完整下载约 {plan.Sum(x => x.Size) / 1e6:F0} MB\n" + string.Join(" / ", plan.Select(x => x.Name)) + $"\n保存位置：{AppPaths.ModelsFor(ReadSettings())}";
        AccelerationHint.Text = GpuAcceleration.Describe(ReadSettings()) + "。实际使用情况会在翻译页面显示。";
    }
    private void ChooseModelDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (sessionActive || preparing || testingApi) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择模型保存文件夹", Multiselect = false };
        string current = AppPaths.ModelsFor(ReadSettings());
        if (Directory.Exists(current)) dialog.InitialDirectory = current;
        if (dialog.ShowDialog(this) == true) ApplyModelDirectory(dialog.FolderName);
    }
    private void ResetModelDirectory_Click(object sender, RoutedEventArgs e) => ApplyModelDirectory("");
    private void ApplyModelDirectory(string directory)
    {
        if (sessionActive || preparing || testingApi) return;
        try
        {
            var candidate = ReadSettings(); candidate.ModelDirectory = directory;
            AppPaths.ValidateModelLocation(candidate);
            SettingsStore.Save(candidate); settings = candidate;
            ModelDirectoryBox.Text = AppPaths.ModelsFor(candidate);
            UpdateDownloadPlan(); DetectDevice();
            DownloadProgress.Value = 0;
            DownloadStatus.Text = "目录已保存，下载、校验和本地推理将使用此位置。";
            StatusText.Text = "模型目录已更改并保存。新目录中已有的模型会校验后复用。";
        }
        catch (Exception ex) { Report(ex); }
    }
    private void OpenModelDirectory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = ReadSettings(); AppPaths.ValidateModelLocation(s);
            Process.Start(new ProcessStartInfo(AppPaths.ModelsFor(s)) { UseShellExecute = true });
        }
        catch (Exception ex) { Report(ex); }
    }
    private async Task PrepareAsync(AppSettings s, CancellationToken token)
    {
        preparing = true; SetControls(); CancelPrepareButton.IsEnabled = true;
        DownloadStatus.Text = "正在检查已有文件并校验…";
        try
        {
            await new ModelInstaller().PrepareAsync(s, new Progress<InstallProgress>(p => { DownloadStatus.Text = p.Message; DownloadProgress.Value = p.Fraction; StatusText.Text = p.Message; }), token);
            settings.FirstRunComplete = true;
        }
        finally { preparing = false; CancelPrepareButton.IsEnabled = false; SetControls(); }
    }
    private async void Prepare_Click(object sender, RoutedEventArgs e)
    {
        if (preparing || sessionActive) return;
        installCancellation = new();
        try { await PrepareAsync(ReadSettings(), installCancellation.Token); SaveSettings(); }
        catch (OperationCanceledException) { DownloadStatus.Text = "下载已取消，保留未完成文件，下次可续传。"; StatusText.Text = DownloadStatus.Text; }
        catch (Exception ex) { Report(ex); DownloadStatus.Text = "准备失败，可重试：" + ex.Message; }
        finally { installCancellation.Dispose(); installCancellation = null; }
    }
    private void CancelPrepare_Click(object sender, RoutedEventArgs e) { installCancellation?.Cancel(); sessionCancellation?.Cancel(); }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (sessionActive || preparing || importing || refining) return;
        var s = ReadSettings();
        if (SourceBox.SelectedItem is not AudioSource audio) { StatusText.Text = "请先选择需要捕获的应用，或明确选择系统全部声音。"; SwitchPage("live"); return; }
        try { TranslationPipeline.Validate(s); SaveSettings(); }
        catch (Exception ex) { Report(ex); SwitchPage("settings"); return; }
        if (audio.Pid != 0 && !DeviceProbe.CanAttemptProcessCapture) { StatusText.Text = "当前系统不能捕获指定进程。请手动选择系统全部声音，或升级 Windows。"; return; }
        Subtitles.Clear(); EmptyState.Visibility = Visibility.Visible;
        overlay?.ClearPendingDisplay(true);
        archive.Reset(s.SourceLanguage, s.TargetLanguage);
        sessionActive = true; sessionCancellation = new(); SetControls(); SwitchPage("live");
        startFlow = StartSessionAsync(s, audio, sessionCancellation.Token); await startFlow;
    }
    private async Task StartSessionAsync(AppSettings s, AudioSource audio, CancellationToken token)
    {
        try
        {
            await PrepareAsync(s, token); token.ThrowIfCancellationRequested();
            var session = new TranslationPipeline(s); pipeline = session;
            session.Status += message => UiFor(session, () => StatusText.Text = message);
            session.Level += level =>
            {
                long now = Environment.TickCount64;
                if (now - Interlocked.Read(ref lastMeter) < 80) return;
                Interlocked.Exchange(ref lastMeter, now); UiFor(session, () => AudioMeter.Value = level);
            };
            session.Recognized += subtitle => UiFor(session, () => UpdateSubtitle(subtitle, s.StreamTranslation));
            session.TranslationUpdated += subtitle => UiFor(session, () => UpdateSubtitle(subtitle, true));
            session.Completed += subtitle => UiFor(session, () => UpdateSubtitle(subtitle, !subtitle.HasError));
            session.BacklogChanged += _ => UiFor(session, () =>
            {
                var backlog = session.Backlog;
                BacklogText.Text = $"待识别 {backlog.Recognition} · 待翻译 {backlog.Translation} · 失败 {backlog.Failed}\n待处理音频约 {backlog.Seconds:F1} 秒";
                RetryFailedButton.IsEnabled = backlog.Failed > 0;
            });
            session.CapturePausedChanged += value => UiFor(session, () =>
            {
                StateBadge.Text = stopping ? "●  完成字幕中" : value ? "●  收音暂停" : "●  聆听中";
                PauseButton.Content = value ? "继续收音" : "暂停收音";
                SetControls();
            });
            session.Fatal += ex => Dispatcher.BeginInvoke(new Action(async () => { if (!ReferenceEquals(pipeline, session)) return; finishCancellation?.Cancel(); await StopSessionAsync(false); Report(ex); }));
            await session.StartAsync(audio.Pid, token);
            EngineStatusText.Text = session.RuntimeDescription;
            settings.FirstRunComplete = true; SaveSettings();
            EnsureOverlay(); overlay!.Show(); StateBadge.Text = "●  聆听中"; SetControls();
        }
        catch (OperationCanceledException) { await DisposePipelineAsync(); sessionActive = false; SetControls(); StatusText.Text = "已取消启动。"; }
        catch (Exception ex) { await DisposePipelineAsync(); sessionActive = false; SetControls(); Report(ex); }
    }
    private void UpdateSubtitle(Subtitle subtitle, bool showOverlay)
    {
        var adjusted = subtitle;
        archive.Upsert(adjusted);
        int index = -1;
        for (int i = 0; i < Subtitles.Count; i++) if (Subtitles[i].Sequence == subtitle.Sequence) { index = i; break; }
        if (index >= 0) Subtitles[index] = adjusted;
        else
        {
            int insert = 0; while (insert < Subtitles.Count && Subtitles[insert].Sequence < subtitle.Sequence) insert++;
            Subtitles.Insert(insert, adjusted);
        }
        // Keep unfinished rows available for retry even during a long session.
        while (Subtitles.Count > 500) Subtitles.RemoveAt(0);
        EmptyState.Visibility = Visibility.Collapsed; History.ScrollIntoView(adjusted);
        HistoryFootnote.Text = $"界面滚动保留 500 条 · 本次完整记录 {archive.Count} 条 · 导出包含全部记录";
        if (showOverlay)
        {
            LatencyText.Text = subtitle.IsPending ? "译文输出中…" : $"处理 {subtitle.ProcessingSeconds:F1}s";
            EnsureOverlay(); overlay!.Present(adjusted, ReadSettings());
        }
    }
    private void UiFor(TranslationPipeline session, Action action) => Dispatcher.BeginInvoke(new Action(() => { if (ReferenceEquals(pipeline, session) && !sessionCancellation!.IsCancellationRequested) action(); }));
    private async Task DisposePipelineAsync()
    { var p = pipeline; pipeline = null; if (p != null) await p.DisposeAsync(); }
    private Task StopSessionAsync(bool drain = true)
    {
        if (stopFlow is { IsCompleted: false }) { if (!drain) finishCancellation?.Cancel(); return stopFlow; }
        return stopFlow = StopCoreAsync(drain);
    }
    private async Task StopCoreAsync(bool drain)
    {
        bool hadSession = sessionActive, interrupted = !drain;
        stopping = hadSession; finishCancellation = new(); SetControls();
        try
        {
            if (startFlow is { IsCompleted: false }) { sessionCancellation?.Cancel(); await startFlow; }
            var current = pipeline;
            if (current != null)
            {
                if (drain)
                {
                    try { await current.FinishAsync(finishCancellation.Token); }
                    catch (OperationCanceledException) { interrupted = true; }
                }
                if (interrupted)
                {
                    var unfinished = await current.AbortAsync();
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                    foreach (var row in unfinished) UpdateSubtitle(row, false);
                }
                else await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            }
            sessionCancellation?.Cancel(); await DisposePipelineAsync();
            sessionCancellation?.Dispose(); sessionCancellation = null;
            sessionActive = false;
            overlay?.ClearPendingDisplay(); RetryFailedButton.IsEnabled = false; PauseButton.Content = "暂停收音";
            BacklogText.Text = "没有待处理片段"; AudioMeter.Value = 0;
            EngineStatusText.Text = "推理设备：启动后显示实际使用情况";
            StateBadge.Text = "●  就绪";
            int failed = archive.Snapshot().Count(s => s.HasError);
            StatusText.Text = interrupted ? "已立即结束，未完成片段已标记，完整会话记录仍可导出。" : failed > 0 ? $"已停止，成功片段已完成；{failed} 个失败位置已标记，可导出原文后在字幕精修中重译。" : "已停止，全部已接收片段已处理完成。";
            if (hadSession && settings.ExportOnStop && archive.Count > 0) ExportArchive(SubtitleFilesExport.DirectoryFor(settings));
        }
        catch (Exception ex) { Report(ex); }
        finally { stopping = false; finishCancellation.Dispose(); finishCancellation = null; SetControls(); }
    }
    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        var current = pipeline; if (current == null) return;
        PauseButton.IsEnabled = false;
        try { if (current.IsCapturePaused) await current.ResumeCaptureAsync(); else await current.PauseCaptureAsync(); }
        catch (Exception ex) { Report(ex); }
        finally { SetControls(); }
    }
    private void RetryFailed_Click(object sender, RoutedEventArgs e) { RetryFailedButton.IsEnabled = false; pipeline?.RetryFailed(); }
    private async void Stop_Click(object sender, RoutedEventArgs e)
    { if (stopping) { finishCancellation?.Cancel(); return; } await StopSessionAsync(); }
    private void SetControls()
    {
        bool enabled = !sessionActive && !preparing && !testingApi && !importing && !refining;
        RefineInputControls.IsEnabled = RefineOptionsControls.IsEnabled = TranslationPromptControls.IsEnabled = enabled;
        RefineBudgetControls.IsEnabled = enabled;
        RefineStartButton.IsEnabled = enabled && refineInput.Count > 0;
        RefineCancelButton.IsEnabled = refining; RefineExportButton.IsEnabled = !refining && refined.Count > 0;
        FileInputControls.IsEnabled = FileOptionsControls.IsEnabled = FileOutputControls.IsEnabled = enabled;
        FileStartButton.IsEnabled = enabled; FileCancelButton.IsEnabled = importing;
        FileExportButton.IsEnabled = !importing && CompletedFileRows().Length > 0;
        SessionControls.IsEnabled = enabled; SourceBox.IsEnabled = enabled; ModelChoices.IsEnabled = enabled; AccelerationBox.IsEnabled = enabled;
        ApiControls.IsEnabled = enabled; DownloadHostBox.IsEnabled = enabled;
        ModelLocationControls.IsEnabled = enabled;
        PrepareButton.IsEnabled = enabled; BenchmarkButton.IsEnabled = enabled;
        StartButton.IsEnabled = enabled; StartButton.Content = "▶  开始翻译";
        PauseButton.IsEnabled = sessionActive && pipeline != null && !stopping; StopButton.IsEnabled = sessionActive;
        StopButton.Content = stopping ? "立即结束" : "停止并完成";
        overlay?.SetTransportState(sessionActive, pipeline?.IsCapturePaused == true, preparing || importing || refining || testingApi || sessionActive && pipeline == null, stopping);
    }
    private void EnsureOverlay()
    {
        if (overlay == null)
        {
            overlay = new OverlayWindow(ReadSettings());
            overlay.StartRequested += () =>
            {
                if (pipeline?.IsCapturePaused == true) Pause_Click(this, new RoutedEventArgs());
                else { if (SourceBox.SelectedItem is not AudioSource) { Restore(); SwitchPage("live"); } Start_Click(this, new RoutedEventArgs()); }
            };
            overlay.PauseRequested += () => Pause_Click(this, new RoutedEventArgs());
            overlay.StopRequested += () => Stop_Click(this, new RoutedEventArgs());
            overlay.BoundsChanged += () => { try { SaveSettings(); } catch (Exception ex) { Report(ex); } };
        }
        overlay.UpdateHotkeyHint(hotkey?.Active?.ToString());
        overlay.SetTransportState(sessionActive, pipeline?.IsCapturePaused == true, preparing || importing || refining || testingApi || sessionActive && pipeline == null, stopping);
    }
    private void CaptionLines_Changed(object sender, SelectionChangedEventArgs e) { if (loaded) ApplyAppearance(); }
    private void UpdateHotkeyUi(string? problem = null)
    {
        string? active = hotkey?.Active?.ToString();
        HotkeySidebar.Text = active != null ? active + "\n切换字幕鼠标穿透" : settings.HotkeyEnabled ? "快捷键未生效\n右键托盘可解锁" : "快捷键已关闭\n右键托盘可解锁";
        HotkeyStatusText.Text = !string.IsNullOrEmpty(problem)
            ? problem + (active != null ? $"\n当前仍可使用 {active}，也可从托盘解锁。" : "\n请更换组合，或使用立即解锁按钮、托盘菜单。")
            : active != null ? $"当前生效：{active}。" : "全局快捷键已关闭，仍可使用立即解锁按钮或托盘菜单。";
        HotkeyStatusText.Foreground = string.IsNullOrEmpty(problem) ? (Brush)Application.Current.Resources["Accent"] : new SolidColorBrush(Color.FromRgb(220, 140, 60));
        overlay?.UpdateHotkeyHint(active);
        if (active == null) overlay?.SetLocked(false);
    }
    private void ApplyHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (hotkey == null) return;
        var candidate = ReadSettings();
        candidate.HotkeyEnabled = HotkeyEnabledBox.IsChecked == true;
        if (!uint.TryParse(SelectedTag(HotkeyModifiersBox), out uint modifiers) || !uint.TryParse(SelectedTag(HotkeyKeyBox), out uint key))
        { HotkeyStatusText.Text = "请先选择有效的快捷键组合。"; return; }
        candidate.HotkeyModifiers = modifiers; candidate.HotkeyKey = key;
        var before = hotkey.Active;
        if (!hotkey.TryApply(candidate.HotkeyEnabled ? new HotkeyBinding(modifiers, key) : null, out string problem))
        { UpdateHotkeyUi(problem); return; }
        try
        {
            SettingsStore.Save(candidate); settings = candidate;
            UpdateHotkeyUi(); StatusText.Text = "字幕快捷键设置已保存并立即生效。";
        }
        catch (Exception ex)
        {
            hotkey.TryApply(before, out string rollbackProblem);
            UpdateHotkeyUi("快捷键设置保存失败：" + ex.Message + (string.IsNullOrEmpty(rollbackProblem) ? "" : "\n" + rollbackProblem));
        }
    }
    private void UnlockOverlay_Click(object sender, RoutedEventArgs e)
    { EnsureOverlay(); overlay!.Show(); overlay.SetLocked(false); StatusText.Text = "字幕已解锁，可以直接拖动或操作。"; }
    private void Overlay_Click(object sender, RoutedEventArgs e) { EnsureOverlay(); if (overlay!.IsVisible) overlay.Hide(); else overlay.Show(); }
    private void PreviewOverlay_Click(object sender, RoutedEventArgs e)
    {
        EnsureOverlay(); overlay!.Present(new(DateTimeOffset.Now, 0, 3, "A new way to understand the world.", "换一种语言，听见更大的世界。", "en", 0), ReadSettings(), true); overlay.Show();
    }
    private void Appearance_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (loaded) ApplyAppearance(); }
    private void Appearance_Click(object sender, RoutedEventArgs e) { if (loaded) ApplyAppearance(); }
    private void ApplyAppearance()
    {
        var s = ReadSettings(); AppearancePreview.FontSize = FontSlider.Value;
        AppearanceSurface.Background = Theme.Brush(s.Colors.SubtitleBackground);
        AppearanceOriginal.Foreground = Theme.Brush(s.Colors.Original); AppearancePreview.Foreground = Theme.Brush(s.Colors.Translation);
        overlay?.Apply(s);
    }
    private void UpdateColorButtons()
    {
        foreach (var button in new[] { ColorBackgroundButton, ColorPanelButton, ColorTextButton, ColorAccentButton, ColorSubtitleBackgroundButton, ColorOriginalButton, ColorTranslationButton })
        {
            string value = (string)typeof(ColorSettings).GetProperty((string)button.Tag)!.GetValue(settings.Colors)!;
            var color = Theme.Color(value); button.Background = new SolidColorBrush(color);
            button.Foreground = color.R * .299 + color.G * .587 + color.B * .114 > 160 ? Brushes.Black : Brushes.White;
            button.ToolTip = value;
        }
    }
    private void ChooseColor_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender; var property = typeof(ColorSettings).GetProperty((string)button.Tag)!;
        var current = Theme.Color((string)property.GetValue(settings.Colors)!);
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B) };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var colors = settings.Colors.Copy(); property.SetValue(colors, $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}"); CommitColors(colors);
    }
    private void ColorPreset_Click(object sender, RoutedEventArgs e) => CommitColors((string)((Button)sender).Tag == "light" ? ColorSettings.Light() : new());
    private void CommitColors(ColorSettings colors)
    {
        try
        {
            var candidate = ReadSettings(); candidate.Colors = colors; SettingsStore.Save(candidate); settings = candidate;
            Theme.Apply(colors); UpdateColorButtons(); ApplyAppearance(); ApplyTitleBar();
            SwitchPage("settings"); StatusText.Text = "颜色已应用并保存。";
        }
        catch (Exception ex) { Report(ex); }
    }
    private void ApplyTitleBar()
    {
        var handle = new WindowInteropHelper(this).Handle; if (handle == IntPtr.Zero) return;
        var bg = Theme.Color(settings.Colors.Background); int dark = bg.R * .299 + bg.G * .587 + bg.B * .114 < 128 ? 1 : 0;
        if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0) DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
    }
    private void ExportSettings_Click(object sender, RoutedEventArgs e) { try { SaveSettings(); } catch (Exception ex) { Report(ex); } }
    private void ChooseExportDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择字幕自动导出文件夹" };
        if (dialog.ShowDialog(this) != true) return;
        try { var candidate = ReadSettings(); candidate.ExportDirectory = dialog.FolderName; SettingsStore.Save(candidate); settings = candidate; ExportDirectoryBox.Text = dialog.FolderName; }
        catch (Exception ex) { Report(ex); }
    }
    private void OpenExportDirectory_Click(object sender, RoutedEventArgs e)
    {
        try { string path = SubtitleFilesExport.DirectoryFor(ReadSettings()); Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Report(ex); }
    }
    private void ExportArchive(string directory)
    {
        var files = SubtitleFilesExport.Write(directory, archive.Snapshot(), archive.Source, archive.Target);
        ExportStatusText.Text = "已导出：\n" + files.Bilingual + "\n" + files.Original + "\n" + files.Translation;
        if (files.Failures != null) ExportStatusText.Text += "\n失败清单：" + files.Failures;
        StatusText.Text = $"已导出{(sessionActive ? "当前字幕快照" : "完整会话记录")} {archive.Count} 条，双语、原文、译文三份字幕：{directory}";
        if (files.Failures != null) StatusText.Text += "；含失败标记及清单，可在字幕精修中重译。";
    }
    private void Save_Click(object sender, RoutedEventArgs e) { try { SaveSettings(); StatusText.Text = "设置已保存。运行方案的更改将在下一次开始时生效。"; } catch (Exception ex) { Report(ex); } }
    private async void TestTranslation_Click(object sender, RoutedEventArgs e) => await TestApi();
    private async Task TestApi()
    {
        testingApi = true; SetControls(); ApiTestResult.Text = "正在发送测试请求…";
        try
        {
            var s = ReadSettings(); using var client = new InferenceClient(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await client.TestAsync(s.Translation, timeout.Token);
            ApiTestResult.Text = "文字翻译接口测试成功。"; SaveSettings();
        }
        catch (Exception ex) { ApiTestResult.Text = "测试失败：" + ex.Message; }
        finally { testingApi = false; SetControls(); }
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (archive.Count == 0) { StatusText.Text = "当前没有可导出的字幕。"; return; }
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择保存三份字幕的文件夹" };
        if (dialog.ShowDialog(this) != true) return;
        try { ExportArchive(dialog.FolderName); }
        catch (Exception ex) { Report(ex); }
    }
    private async void Benchmark_Click(object sender, RoutedEventArgs e)
    {
        if (sessionActive || preparing) return;
        var s = ReadSettings(); s.SourceLanguage = "en"; s.TargetLanguage = "zh"; s.MixedLanguage = false;
        installCancellation = new(); var token = installCancellation.Token;
        try
        {
            await PrepareAsync(s, token); preparing = true; SetControls(); CancelPrepareButton.IsEnabled = true;
            BenchmarkResult.Text = "正在加载模型并实测，首次加载可能稍慢…";
            await using var servers = new LocalServers(); using var inference = new InferenceClient();
            await servers.StartAsync(s, new Progress<string>(message => BenchmarkResult.Text = message), token);
            var samples = ReadBenchmarkWave(); var chunk = new AudioChunk(samples, 0, samples.Length / 16000d);
            var watch = Stopwatch.StartNew(); var recognized = await inference.RecognizeAsync(chunk, s, servers, token);
            double recognition = watch.Elapsed.TotalSeconds, ratio = recognition / chunk.Duration;
            string result = $"{chunk.Duration:F1} 秒音频 → 识别 {recognition:F1} 秒，实时率 {ratio:F2}（越低越快）。\n";
            if (s.LocalTranslation)
            {
                watch.Restart(); var translated = await inference.TranslateAsync(recognized.Text, recognized.Language, s, servers, [], token);
                result += $"该句离线翻译 {watch.Elapsed.TotalSeconds:F1} 秒。\n";
                result += ratio > .8 || watch.Elapsed.TotalSeconds > 5 ? "建议使用更小识别模型或云端文字翻译，减少字幕积压。" : "此样本速度可以尝试本地方案，仍需用实际播放内容验证。";
                result += "\n识别：" + recognized.Text + "\n译文：" + translated;
            }
            else result += ratio > .8 ? "本地识别可能跟不上播放速度，建议更小识别模型或更快的本机引擎。" : "本地识别速度可尝试云端文字翻译；翻译延迟仍取决于服务与网络。";
            BenchmarkResult.Text = result; StatusText.Text = "本地速度实测完成。";
        }
        catch (OperationCanceledException) { BenchmarkResult.Text = "实测已取消。"; }
        catch (Exception ex) { BenchmarkResult.Text = "实测未完成：" + ex.Message; }
        finally { preparing = false; CancelPrepareButton.IsEnabled = false; SetControls(); installCancellation.Dispose(); installCancellation = null; }
    }
    public static short[] ReadBenchmarkWave() => Wave.ReadPcm16(Path.Combine(AppContext.BaseDirectory, "Assets", "benchmark.wav"));
    private void SetupTray()
    {
        tray = new System.Windows.Forms.NotifyIcon { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application, Text = "译流 LingoFlow", Visible = true };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开译流", null, (_, _) => Dispatcher.Invoke(Restore));
        menu.Items.Add("显示 / 隐藏字幕", null, (_, _) => Dispatcher.Invoke(() => Overlay_Click(this, new RoutedEventArgs())));
        menu.Items.Add("锁定 / 解锁字幕穿透", null, (_, _) => Dispatcher.Invoke(() => { EnsureOverlay(); overlay!.Show(); overlay.ToggleLock(); }));
        menu.Items.Add("立即解锁字幕", null, (_, _) => Dispatcher.Invoke(() => UnlockOverlay_Click(this, new RoutedEventArgs())));
        menu.Items.Add("停止翻译", null, (_, _) => Dispatcher.Invoke(async () => await StopSessionAsync()));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出译流", null, (_, _) => Dispatcher.Invoke(async () => await QuitAsync()));
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(Restore);
    }
    private void Restore() { Show(); WindowState = WindowState.Normal; Activate(); }
    private async Task QuitAsync()
    {
        if (quitting) return; quitting = true; installCancellation?.Cancel(); fileCancellation?.Cancel();
        if (fileFlow != null) await fileFlow;
        refineCancellation?.Cancel(); if (refineFlow != null) await refineFlow;
        await StopSessionAsync(false);
        try { SaveSettings(); } catch { }
        hotkey?.Dispose();
        tray?.Dispose(); overlay?.Close(); Close(); Application.Current.Shutdown();
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        uint value = unchecked((uint)lParam.ToInt64());
        if (message == 0x0312 && hotkey?.Matches(wParam.ToInt32(), value & 0xFFFF, value >> 16) == true)
        { EnsureOverlay(); overlay!.Show(); overlay.ToggleLock(); handled = true; }
        return IntPtr.Zero;
    }
    private void Report(Exception ex) { StatusText.Text = "未能完成：" + ex.Message; StateBadge.Text = "●  需要处理"; }
    public void ShowPreviewPage(string page)
    {
        SwitchPage(page is "colors" or "export" ? "settings" : page == "models-detail" ? "models" : page);
        if (page is "colors" or "export")
        {
            UpdateLayout();
            var card = page == "colors" ? ColorSettingsCard : ExportSettingsCard;
            SettingsPage.ScrollToVerticalOffset(card.TranslatePoint(new Point(0, 0), (UIElement)SettingsPage.Content).Y);
            SettingsPage.UpdateLayout();
        }
        if (page == "models-detail")
        {
            UpdateLayout();
            ModelsPage.ScrollToVerticalOffset(ModelChoices.TranslatePoint(new Point(0, 0), (UIElement)ModelsPage.Content).Y - 65);
            ModelsPage.UpdateLayout();
        }
        if (page is "settings" or "colors") PreviewOverlay_Click(this, new RoutedEventArgs());
    }
    public void CaptureOverlayPreview(string path)
    {
        EnsureOverlay(); overlay!.CompletePreviewAnimation(); overlay.UpdateLayout();
        var image = new System.Windows.Media.Imaging.RenderTargetBitmap((int)overlay.ActualWidth, (int)overlay.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(overlay); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var output = File.Create(path); encoder.Save(output);
    }
    protected override void OnClosed(EventArgs e) { hotkey?.Dispose(); base.OnClosed(e); }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

