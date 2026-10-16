using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using LingoFlow.Services;

namespace LingoFlow;

public partial class MainWindow
{
    public ObservableCollection<Subtitle> FileSubtitles { get; } = [];
    private readonly SessionArchive fileArchive = new();
    private CancellationTokenSource? fileCancellation;
    private Task? fileFlow;
    private bool importing;
    private bool fileFinished;
    private string importedName = "文件字幕";
    private void InitializeFilePage()
    {
        FileSourceBox.Items.Add(new ComboBoxItem { Content = "自动识别 / 多语", Tag = "auto" });
        foreach (var language in Languages.All)
        {
            FileSourceBox.Items.Add(new ComboBoxItem { Content = language.Name, Tag = language.Code });
            FileTargetBox.Items.Add(new ComboBoxItem { Content = language.Name, Tag = language.Code });
        }
        Select(FileRouteBox, settings.Route); Select(FileSourceBox, settings.SourceLanguage); Select(FileTargetBox, settings.TargetLanguage);
        Select(FileModeBox, "accurate"); FileOutputBox.Text = SubtitleFilesExport.DirectoryFor(settings);
    }
    private void ImportNav_Click(object sender, RoutedEventArgs e) => SwitchPage("import");
    private void ChooseMedia_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择需要翻译的音频或视频", CheckFileExists = true,
            Filter = "音频和视频|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.m4v;*.mp3;*.m4a;*.aac;*.wav;*.flac;*.ogg;*.opus;*.wma;*.wmv;*.ts|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true) FileInputBox.Text = dialog.FileName;
    }
    private void ChooseFileOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择字幕保存目录", InitialDirectory = FileOutputBox.Text };
        if (dialog.ShowDialog(this) == true) FileOutputBox.Text = dialog.FolderName;
    }
    private async void TranslateFile_Click(object sender, RoutedEventArgs e)
    {
        if (importing || sessionActive || preparing || testingApi || refining) return;
        if (!File.Exists(FileInputBox.Text)) { FileStatusText.Text = "请先选择一个存在的音频或视频文件。"; return; }
        var s = ReadSettings(); s.Route = SelectedTag(FileRouteBox); s.SourceLanguage = SelectedTag(FileSourceBox); s.TargetLanguage = SelectedTag(FileTargetBox);
        s.Accurate = SelectedTag(FileModeBox) == "accurate"; s.MixedLanguage = s.SourceLanguage == "auto" || s.MixedLanguage;
        try
        {
            TranslationPipeline.Validate(s);
            Directory.CreateDirectory(FileOutputBox.Text);
            // Confirm destination is writable before a potentially lengthy translation.
            string probe = Path.Combine(FileOutputBox.Text, ".lingoflow-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (File.Create(probe)) { } File.Delete(probe);
        }
        catch (Exception ex) { FileStatusText.Text = ex.Message; return; }
        importing = true; fileFinished = false; fileCancellation = new(); FileSubtitles.Clear();
        fileArchive.Reset(s.SourceLanguage, s.TargetLanguage); importedName = Path.GetFileNameWithoutExtension(FileInputBox.Text);
        FileProgressBar.Value = 0; FileStatusText.ToolTip = null; StateBadge.Text = "● 文件翻译中"; SetControls();
        fileFlow = RunFileAsync(FileInputBox.Text, FileOutputBox.Text, s, fileCancellation.Token);
        await fileFlow;
    }
    private async Task RunFileAsync(string input, string output, AppSettings s, CancellationToken token)
    {
        FileTranslation? session = null;
        try
        {
            FileStatusText.Text = "正在准备模型与音视频组件…";
            var progress = new Progress<InstallProgress>(p => { FileStatusText.Text = p.Message; FileProgressBar.IsIndeterminate = false; FileProgressBar.Value = Math.Clamp(p.Fraction * 100, 0, 100); });
            await new ModelInstaller().PrepareAsync(s, progress, token);
            await MediaDecoder.PrepareAsync(s, progress, token);
            token.ThrowIfCancellationRequested();
            settings.FirstRunComplete = true;
            FileProgressBar.IsIndeterminate = true;
            session = new(s);
            session.Updated += row => Dispatcher.BeginInvoke(() =>
            {
                // Keep exports complete while bounding the visible list.
                fileArchive.Upsert(row);
                int index = -1; for (int i = 0; i < FileSubtitles.Count; i++) if (FileSubtitles[i].Sequence == row.Sequence) { index = i; break; }
                if (index >= 0) FileSubtitles[index] = row;
                else { FileSubtitles.Add(row); while (FileSubtitles.Count > 500) FileSubtitles.RemoveAt(0); }
                FileHistory.ScrollIntoView(row);
            });
            session.Status += text => Dispatcher.BeginInvoke(() => { FileStatusText.Text = text; });
            session.Progress += p => Dispatcher.BeginInvoke(() =>
            {
                FileProgressBar.IsIndeterminate = p.Duration == null;
                FileProgressBar.Value = p.Duration is > 0 ? Math.Min(98, 98 * p.Position / p.Duration.Value) : 0;
                FileStatusText.Text = $"{p.Message} 已读取 {TimeSpan.FromSeconds(p.Position):hh\\:mm\\:ss}" +
                    (p.Duration is > 0 ? $" / {TimeSpan.FromSeconds(p.Duration.Value):hh\\:mm\\:ss}" : "") +
                    $" · 待识别 {p.Backlog.Recognition} · 待翻译 {p.Backlog.Translation} · 失败 {p.Backlog.Failed}";
            });
            await session.RunAsync(input, token);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            fileFinished = true; FileProgressBar.IsIndeterminate = false; FileProgressBar.Value = 100;
            if (CompletedFileRows().Length == 0) FileStatusText.Text = "处理完成，未识别到可生成字幕的人声。未创建空字幕文件。";
            else ExportFileRows(output);
        }
        catch (OperationCanceledException) { FileStatusText.Text = "已取消；已完成的字幕已保留，可点击“导出已完成字幕”。未完成内容不会写入正式字幕。"; }
        catch (Exception ex) { FileStatusText.Text = "未完成：" + ex.Message + " 已完成部分仍可手动导出。"; }
        finally
        {
            if (session != null) await session.DisposeAsync();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            FileProgressBar.IsIndeterminate = false; importing = false; fileCancellation?.Dispose(); fileCancellation = null;
            StateBadge.Text = fileFinished ? "● 就绪" : "● 文件已停止"; SetControls();
        }
    }
    private Subtitle[] CompletedFileRows() => fileArchive.Snapshot().Where(s => !s.IsPending).ToArray();
    private void ExportFileRows(string directory)
    {
        var rows = CompletedFileRows(); if (rows.Length == 0) { FileStatusText.Text = "还没有完成翻译的字幕。"; return; }
        var files = SubtitleFilesExport.Write(directory, rows, fileArchive.Source, fileArchive.Target,
            importedName + (fileFinished ? "" : "-部分") + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        FileStatusText.Text = $"{(fileFinished ? "处理结束" : "已导出部分字幕")} · {rows.Length} 条 · 失败 {rows.Count(s => s.HasError)} · 已保存三份 SRT" +
            (files.Failures != null ? " 和失败清单；可在字幕精修中重译。" : "") + "\n" + directory;
        FileStatusText.ToolTip = files.Bilingual + "\n" + files.Original + "\n" + files.Translation;
    }
    private void CancelFile_Click(object sender, RoutedEventArgs e) { fileCancellation?.Cancel(); FileCancelButton.IsEnabled = false; FileStatusText.Text = "正在取消并释放本地模型…"; }
    private void ExportFile_Click(object sender, RoutedEventArgs e) { try { ExportFileRows(FileOutputBox.Text); } catch (Exception ex) { FileStatusText.Text = ex.Message; } }
    private void OpenFileOutput_Click(object sender, RoutedEventArgs e)
    { try { Directory.CreateDirectory(FileOutputBox.Text); Process.Start(new ProcessStartInfo(FileOutputBox.Text) { UseShellExecute = true }); } catch (Exception ex) { FileStatusText.Text = ex.Message; } }
}
