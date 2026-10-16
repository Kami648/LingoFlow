using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using LingoFlow.Services;

namespace LingoFlow;

public partial class MainWindow
{
    internal static async Task CheckStopExportAsync(string models, string report)
    {
        var settings = new AppSettings { FirstRunComplete = true, Route = "local", WhisperModel = "small", TranslationModel = LocalTranslationModels.Default, ModelDirectory = Path.GetFullPath(models), SourceLanguage = "en", TargetLanguage = "zh", MixedLanguage = false,
            ExportOnStop = true, ExportDirectory = Path.Combine(AppPaths.Root, "stop-export-" + Guid.NewGuid().ToString("N")) };
        var window = new MainWindow(settings);
        window.EnsureOverlay(); window.overlay!.Opacity = 0; window.overlay.ShowActivated = false;
        window.SourceBox.ItemsSource = new[] { new AudioSource(Environment.ProcessId, "diagnostic", "test audio") }; window.SourceBox.SelectedIndex = 0;
        try
        {
            window.overlay.OverlayStartButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (window.startFlow != null) await window.startFlow.WaitAsync(TimeSpan.FromSeconds(45));
            if (window.pipeline == null) throw new Exception("UI session did not start: " + window.StatusText.Text);
            var sound = Path.Combine(AppContext.BaseDirectory, "Assets", "benchmark.wav");
            PlayDiagnosticSound(sound, IntPtr.Zero, 0x00020001);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            while (window.archive.Count == 0) await Task.Delay(20, timeout.Token);
            int before = window.archive.Count;
            window.overlay.OverlayPauseButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            while (!window.pipeline.IsCapturePaused) await Task.Delay(20, timeout.Token);
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (!window.overlay.OverlayStartButton.IsEnabled) throw new Exception("Overlay did not enable resume after pause");
            window.overlay.OverlayStartButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            while (window.pipeline.IsCapturePaused) await Task.Delay(20, timeout.Token);
            window.overlay.OverlayStopButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await window.stopFlow!.WaitAsync(TimeSpan.FromSeconds(40));
            var rows = window.archive.Snapshot(); var files = Directory.GetFiles(settings.ExportDirectory, "*.srt");
            if (files.Length != 3 || window.pipeline != null || window.sessionActive || rows.Count < before || rows.Any(r => r.IsPending || r.HasError)) throw new Exception("Stop/export UI workflow did not preserve all completed subtitles: " + window.StatusText.Text);
            if (rows.Any(r => !TranslationLanguage.IsValid(r.Translation, "zh"))) throw new Exception("Wrong-language subtitle escaped UI workflow");
            File.WriteAllText(report, JsonSerializer.Serialize(new { passed = true, beforeStop = before, afterStop = rows.Count, files, window.StatusText.Text, rows }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            PlayDiagnosticSound(null, IntPtr.Zero, 0);
            window.quitting = true; await window.StopSessionAsync(false); window.overlay?.Close(); window.tray?.Dispose(); window.Close();
        }
    }
    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)] private static extern bool PlayDiagnosticSound(string? sound, IntPtr module, uint flags);
}
