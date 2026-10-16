using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LingoFlow.Services;

namespace LingoFlow;

public partial class App : Application
{
    private Mutex? instance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? Value(string option) { int index = Array.IndexOf(e.Args, option); return index >= 0 && index + 1 < e.Args.Length ? e.Args[index + 1] : null; }
        if (e.Args.Contains("--diagnostic-idle")) { await Task.Delay(20000); Shutdown(0); return; }
        if (e.Args.Contains("--refine-budget-ui-check"))
        {
            string report = Value("--out") ?? "refine-budget-ui-check.json";
            try { await LingoFlow.MainWindow.CheckBudget024UiAsync(report); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(Path.ChangeExtension(report, ".error.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--cloud-refine-check"))
        {
            string report = Value("--out") ?? "cloud-refine-check.json";
            try { await LingoFlow.MainWindow.CheckCloudRefine023Async(report); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(Path.ChangeExtension(report, ".error.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--refine-ui-check"))
        {
            string report = Value("--out") ?? "refine-ui-check.json";
            try
            {
                var checks = new List<string>();
                LingoFlow.MainWindow.CheckRefine023Ui((ok, name) => { if (!ok) throw new Exception(name); checks.Add(name); }, Path.ChangeExtension(report, ".png"));
                File.WriteAllText(report, System.Text.Json.JsonSerializer.Serialize(new { passed = true, checks })); Shutdown(0);
            }
            catch (Exception ex) { File.WriteAllText(Path.ChangeExtension(report, ".error.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--subtitle-refine-check"))
        {
            string report = Value("--out") ?? "subtitle-refine-check.json";
            try { await LingoFlow.MainWindow.CheckRefinementAsync(Value("--models-dir") ?? Path.Combine(AppPaths.Root, "models"), report, e.Args.Contains("--cpu")); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(Path.ChangeExtension(report, ".error.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (Value("--media-file") is { } mediaFile)
        {
            string report = Value("--out") ?? "media-file-check.json";
            try { await LingoFlow.MainWindow.CheckFileImportAsync(Value("--models-dir") ?? Path.Combine(AppPaths.Root, "models"), mediaFile, report, e.Args.Contains("--cpu")); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(Path.ChangeExtension(report, ".error.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--acceleration-check"))
        {
            string report = Value("--out") ?? "acceleration-check.json";
            try { await GpuDiagnostics.SmokeAsync(Value("--models-dir") ?? Path.Combine(AppPaths.Root, "models"), report, e.Args.Contains("--cpu"), e.Args.Contains("--missing-gpu")); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(Path.ChangeExtension(report, ".error.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--stop-music-check"))
        {
            string report = Value("--out") ?? "stop-music-check.json";
            try { await QualityDiagnostics.StopMusicAsync(Value("--models-dir") ?? Path.Combine(AppPaths.Root, "models"), Value("--fixtures") ?? ".", report); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(Path.ChangeExtension(report, ".error.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--recognition-repeat-check"))
        {
            string report = Value("--out") ?? "recognition-repeat-check.json";
            try { await RepetitionDiagnostics.NativeAsync(Value("--models-dir") ?? Path.Combine(AppPaths.Root, "models"), Value("--input-wave"), report); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(Path.ChangeExtension(report, ".error.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--translation-model-check"))
        {
            string report = Value("--out") ?? "translation-model-check.json";
            try { await HyMtDiagnostics.SmokeAsync(Value("--models-dir") ?? Path.Combine(AppPaths.Root, "models"), report); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(report, ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--ui-stop-check"))
        {
            string report = Value("--out") ?? "ui-stop-check.json";
            try { await LingoFlow.MainWindow.CheckStopExportAsync(Value("--models-dir") ?? Path.Combine(AppPaths.Root, "models"), report); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(report, ex.ToString()); Shutdown(1); }
            return;
        }
        if (Value("--retranslate-file") is { } translationFile)
        {
            string report = Value("--out") ?? "translation-regression.json";
            try { await UpdateDiagnostics.RetranslateAsync(translationFile, Value("--models-dir") ?? Path.Combine(AppPaths.Root, "models"), report); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(report, ex.ToString()); Shutdown(1); }
            return;
        }
        if (Value("--audio-file") is { } audioFile)
        {
            string report = Value("--out") ?? "audio-file-check.json";
            try { await AudioFileDiagnostics.RunAsync(audioFile, report, Value("--models-dir") ?? Path.Combine(AppPaths.Root, "models"), e.Args.Contains("--accurate"), e.Args.Contains("--realtime"), e.Args.Contains("--cpu")); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(report, ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--self-test"))
        {
            try { await Diagnostics.SelfTestAsync(Value("--out") ?? "self-test.json"); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(Value("--out") ?? "self-test.json", ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--capture-check"))
        {
            try { await Diagnostics.CaptureCheckAsync(Value("--out") ?? "capture-check.json"); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(Value("--out") ?? "capture-check.json", ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--local-check"))
        {
            try { await Diagnostics.LocalCheckAsync(Value("--out") ?? "local-check.json", e.Args.Contains("--translation")); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(Value("--out") ?? "local-check.json", ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--pipeline-check"))
        {
            try { await Diagnostics.PipelineCheckAsync(Value("--out") ?? "pipeline-check.json"); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(Value("--out") ?? "pipeline-check.json", ex.ToString()); Shutdown(1); }
            return;
        }
        if (!e.Args.Contains("--screenshot"))
        {
            instance = new Mutex(true, "Local\\LingoFlow.Desktop", out bool firstInstance);
            if (!firstInstance) { MessageBox.Show("译流已经在运行，请从系统托盘打开窗口。", "译流"); Shutdown(); return; }
        }
        AppSettings settings;
        try { settings = SettingsStore.Load(); }
        catch (Exception ex) { MessageBox.Show("设置未能读取，将使用默认设置。原文件保留。\n" + ex.Message, "译流", MessageBoxButton.OK, MessageBoxImage.Information); settings = new(); }
        if (e.Args.Contains("--screenshot") && e.Args.Contains("--theme-light")) settings.Colors = ColorSettings.Light();
        var window = new MainWindow(settings); MainWindow = window; window.Show();
        string? screenshot = Value("--screenshot");
        if (screenshot != null)
        {
            await Dispatcher.InvokeAsync(() => window.ShowPreviewPage(Value("--page") ?? "live"), DispatcherPriority.ApplicationIdle);
            await Task.Delay(600);
            await Dispatcher.InvokeAsync(() =>
            {
                window.UpdateLayout();
                var content = (FrameworkElement)window.Content;
                var image = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var output = File.Create(screenshot); encoder.Save(output);
            }, DispatcherPriority.ApplicationIdle);
            string? overlayShot = Value("--overlay-screenshot");
            if (overlayShot != null) window.CaptureOverlayPreview(overlayShot);
            Shutdown(0);
        }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
