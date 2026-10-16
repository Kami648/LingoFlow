using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using LingoFlow.Services;

namespace LingoFlow;

public partial class MainWindow
{
    public ObservableCollection<Subtitle> RefineSubtitles { get; } = [];
    private IReadOnlyList<Subtitle> refineInput = [];
    private readonly List<Subtitle> refined = [];
    private CancellationTokenSource? refineCancellation;
    private Task? refineFlow;
    private bool refining, refineFinished;
    private string refineName = "字幕", refineSource = "auto", refineTarget = "zh";
    private void InitializeRefinePage()
    {
        RefineSourceBox.Items.Add(new ComboBoxItem { Content = "自动识别", Tag = "auto" });
        foreach (var language in Languages.All)
        {
            RefineSourceBox.Items.Add(new ComboBoxItem { Content = language.Name, Tag = language.Code });
            RefineTargetBox.Items.Add(new ComboBoxItem { Content = language.Name, Tag = language.Code });
        }
        Select(RefineRouteBox, settings.Route); Select(RefineSourceBox, settings.SourceLanguage); Select(RefineTargetBox, settings.TargetLanguage);
        TranslationPromptBox.Text = TranslationPrompts.Render(settings.TranslationPrompt, SelectedTag(RefineSourceBox), SelectedTag(RefineTargetBox));
        RefineContextBox.Text = settings.RefineContextTokens.ToString(); RefineChunkBox.Text = settings.RefineChunkTokens.ToString(); RefineOutputBox.Text = settings.RefineOutputTokens.ToString();
        UpdateRefineBudget();
    }
    private void RefineNav_Click(object sender, RoutedEventArgs e) => SwitchPage("refine");
    private void RefinePage_SizeChanged(object sender, SizeChangedEventArgs e)
    { if (RefineSettingsCard != null && e.NewSize.Height > 0) RefineSettingsCard.MaxHeight = Math.Max(280, e.NewSize.Height - 140); }
    private void SetRefineInput(IReadOnlyList<Subtitle> rows, string name, string source, string target)
    {
        refineInput = rows.Where(s => !s.IsPending).ToArray(); refineName = name;
        refined.Clear(); RefineSubtitles.Clear(); refineFinished = false; RefineProgressBar.Value = 0;
        RefineStatusText.Text = "原文已载入，选择翻译方式后开始二次翻译。";
        RefineSourceText.Text = $"{name} · 已载入 {refineInput.Count} 条原文；识别失败的位置会保留标记。";
        Select(RefineSourceBox, source); Select(RefineTargetBox, target); SetControls();
    }
    private void LoadRefineSrt_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择原文轨 SRT（不是译文或双语轨）", Filter = "原文字幕 SRT|*.srt", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (Path.GetFileName(dialog.FileName).Contains("-双语") || Path.GetFileName(dialog.FileName).Contains("-译文-")) throw new IOException("请选择导出时的“原文”SRT，避免把旧译文再次作为原文发送。");
            SetRefineInput(SubtitleImport.Read(dialog.FileName), Path.GetFileNameWithoutExtension(dialog.FileName), SelectedTag(RefineSourceBox), SelectedTag(RefineTargetBox));
        }
        catch (Exception ex) { RefineStatusText.Text = ex.Message; }
    }
    private void RefineLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!loaded) return;
        TranslationPromptBox.Text = TranslationPrompts.Render(TranslationPromptBox.Text, SelectedTag(RefineSourceBox), SelectedTag(RefineTargetBox));
    }
    private void RefineBudget_Changed(object sender, TextChangedEventArgs e) { if (loaded) UpdateRefineBudget(); }
    private async void RefineBudget_Expanded(object sender, RoutedEventArgs e)
    {
        if (!loaded || e.OriginalSource != RefineBudgetControls) return;
        // Expansion changes the scroll extent. Wait for layout before revealing
        // the complete input row, rather than leaving its labels at the bottom.
        await Dispatcher.InvokeAsync(() =>
        {
            if (!RefineBudgetControls.IsExpanded || !RefinePage.IsVisible) return;
            RefineControlsScroll.UpdateLayout();
            RefineBudgetEditor.BringIntoView();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private void SaveRefineBudget_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = settings.Copy(); ReadRefineBudget(s);
            SettingsStore.Save(s); settings = s;
            RefineStatusText.Text = "云端切分设置已保存，下次精修使用新数值。";
        }
        catch (Exception ex) { RefineStatusText.Text = ex.Message; }
    }
    private void ResetRefineBudget_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();
        RefineContextBox.Text = defaults.RefineContextTokens.ToString(); RefineChunkBox.Text = defaults.RefineChunkTokens.ToString(); RefineOutputBox.Text = defaults.RefineOutputTokens.ToString();
        RefineStatusText.Text = "已填入默认值；点击“保存切分设置”保存。";
        RefineBudgetEditor.BringIntoView();
    }
    private void UpdateRefineBudget()
    {
        try
        {
            var s = settings.Copy(); ReadRefineBudget(s); var limits = CloudRefinement.Limits(s);
            RefineBudgetHint.Text = $"自动参考上下文 1,019,715 tokens（5 家代表模型平均值）。当前有效上下文 {limits.Context:N0}、输出上限 {limits.Output:N0}；批量会同时预留校对原文、译文与安全余量。0 表示按预算尽量整段发送，过长才切分。";
        }
        catch (Exception ex) { RefineBudgetHint.Text = ex.Message; }
    }
    private void ReadRefineBudget(AppSettings s)
    {
        static int Read(TextBox box, int min, int max, string label)
        { if (!int.TryParse(box.Text.Trim(), out int n) || n < min || n > max) throw new InvalidOperationException($"{label}请输入 {min:N0} 至 {max:N0} 的整数。"); return n; }
        s.RefineContextTokens = Read(RefineContextBox, 4096, 4_000_000, "上下文预算");
        s.RefineChunkTokens = Read(RefineChunkBox, 0, 4_000_000, "每批输入预算");
        s.RefineOutputTokens = Read(RefineOutputBox, 1024, 400_000, "单次输出上限");
    }
    private void UseFileRefine_Click(object sender, RoutedEventArgs e) => SetRefineInput(fileArchive.Snapshot(), importedName, fileArchive.Source, fileArchive.Target);
    private void SaveTranslationPrompt_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ReadRefineBudget(settings.Copy());
            TranslationPromptBox.Text = TranslationPrompts.Render(TranslationPromptBox.Text, SelectedTag(RefineSourceBox), SelectedTag(RefineTargetBox));
            SaveSettings(); RefineStatusText.Text = "提示词与切分设置已保存；首行随当前任务语言更新，正文用于实时、文件翻译和字幕精修。";
        }
        catch (Exception ex) { RefineStatusText.Text = ex.Message; }
    }
    private void ResetTranslationPrompt_Click(object sender, RoutedEventArgs e)
    { TranslationPromptBox.Text = TranslationPrompts.Render(AppSettings.DefaultTranslationPrompt, SelectedTag(RefineSourceBox), SelectedTag(RefineTargetBox)); SaveTranslationPrompt_Click(sender, e); }
    private async void StartRefine_Click(object sender, RoutedEventArgs e)
    {
        if (refining || sessionActive || importing || preparing || testingApi || refineInput.Count == 0) return;
        AppSettings s;
        try { s = ReadSettings(); ReadRefineBudget(s); s.Route = SelectedTag(RefineRouteBox); s.SourceLanguage = SelectedTag(RefineSourceBox); s.TargetLanguage = SelectedTag(RefineTargetBox); TranslationPipeline.Validate(s); SaveSettings(); }
        catch (Exception ex) { RefineStatusText.Text = ex.Message; return; }
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择精修字幕保存目录", InitialDirectory = SubtitleFilesExport.DirectoryFor(settings) };
        if (dialog.ShowDialog(this) != true) return;
        refining = true; refineFinished = false; refineCancellation = new(); refined.Clear(); RefineSubtitles.Clear();
        refineSource = s.SourceLanguage; refineTarget = s.TargetLanguage;
        RefineProgressBar.Value = 0; StateBadge.Text = "● 字幕精修中"; SetControls();
        refineFlow = RunRefinementAsync(s, dialog.FolderName, refineCancellation.Token); await refineFlow;
    }
    private async Task RunRefinementAsync(AppSettings s, string output, CancellationToken token)
    {
        try
        {
            RefineStatusText.Text = s.LocalTranslation ? "正在准备文字翻译模型…" : "正在估算整段字幕的输入与输出预算…";
            await SubtitleRefinement.RunAsync(refineInput, s, row => Dispatcher.BeginInvoke(() =>
            {
                refined.Add(row); RefineSubtitles.Add(row); while (RefineSubtitles.Count > 500) RefineSubtitles.RemoveAt(0);
                RefineHistory.ScrollIntoView(row);
            }), new Progress<string>(text => RefineStatusText.Text = text),
                (done, total) => Dispatcher.BeginInvoke(() => { RefineProgressBar.Value = 100d * done / total; RefineStatusText.Text = $"正在精修 {done} / {total} · 失败 {refined.Count(s => s.HasError)}"; }), token);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            refineFinished = true; WriteRefined(output);
        }
        catch (OperationCanceledException) { RefineStatusText.Text = "精修已取消；已处理部分保留，可单独导出。原文件保持不变。"; }
        catch (Exception ex) { RefineStatusText.Text = "未完成：" + ex.Message + " 已处理部分仍可导出。"; }
        finally
        {
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            refining = false; refineCancellation?.Dispose(); refineCancellation = null;
            StateBadge.Text = "● 就绪"; SetControls();
        }
    }
    private void WriteRefined(string output)
    {
        if (refined.Count == 0) return;
        SubtitleFilesExport.Write(output, refined, refineSource, refineTarget,
            refineName + "-AI精修" + (refineFinished ? "" : "-部分") + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        RefineStatusText.Text = $"已另存三份{(refineFinished ? "精修" : "部分精修")} SRT · {refined.Count} 段 · 失败 {refined.Count(s => s.HasError)} · 待核对 {refined.Count(s => s.SourceUncertain)}\n原文有修改时会附校对记录。{output}";
    }
    private void CancelRefine_Click(object sender, RoutedEventArgs e) { refineCancellation?.Cancel(); RefineCancelButton.IsEnabled = false; }
    private void ExportRefine_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "保存已处理的精修字幕" };
        if (dialog.ShowDialog(this) == true) { try { WriteRefined(dialog.FolderName); } catch (Exception ex) { RefineStatusText.Text = ex.Message; } }
    }
}
