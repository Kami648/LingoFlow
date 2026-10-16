using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LingoFlow.Services;

namespace LingoFlow;

public partial class MainWindow
{
    internal static async Task CheckBudget024UiAsync(string report)
    {
        Directory.CreateDirectory(AppPaths.Root); var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); }
        foreach (var mode in new[] { "default-light", "minimum-light", "default-dark" })
        {
            var s = new AppSettings { FirstRunComplete = true, SourceLanguage = "ja", TargetLanguage = "zh", Colors = mode.EndsWith("light") ? ColorSettings.Light() : new() };
            var window = new MainWindow(s) { ShowActivated = false, Opacity = 0, Width = mode.StartsWith("minimum") ? 1050 : 1210, Height = mode.StartsWith("minimum") ? 720 : 850 };
            try
            {
                window.SetRefineInput(Enumerable.Range(1, 12).Select(n => new Subtitle(DateTimeOffset.Now, n, n + .9, "これは字幕レイアウトの確認用テキストです。", "", "ja", 0) { Sequence = n }).ToArray(), "layout-source-ja", "ja", "zh");
                window.SwitchPage("refine"); window.Show();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window.RefineControlsScroll.ScrollToTop(); window.RefineBudgetControls.IsExpanded = true;
                // No manual scroll after expansion: exercise the actual user's path.
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
                bool InViewport(FrameworkElement element)
                {
                    var bounds = element.TransformToAncestor(window.RefineControlsScroll).TransformBounds(new Rect(element.RenderSize));
                    return bounds.Top >= -1 && bounds.Bottom <= window.RefineControlsScroll.ViewportHeight + 1;
                }
                var boxes = new[] { window.RefineContextBox, window.RefineChunkBox, window.RefineOutputBox };
                Check(boxes.All(b => InViewport(b) && b.ActualHeight >= 36 && b.IsEnabled && !b.IsReadOnly), mode + ": expanding reveals all three complete, editable numeric fields without manual scrolling");
                Check(InViewport(window.SaveRefineBudgetButton) && InViewport(window.ResetRefineBudgetButton), mode + ": save and restore buttons are visible with the fields");
                Check(boxes.Select(b => b.Text).SequenceEqual(new[] { "1019715", "0", "65536" }), mode + ": default numeric values are loaded and visible");
                string[] edits = ["250000", "12000", "16000"];
                for (int n = 0; n < boxes.Length; n++) { boxes[n].SelectAll(); boxes[n].SelectedText = edits[n]; }
                window.TranslationPromptBox.Text = "unsaved prompt edit";
                window.SaveRefineBudgetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); var saved = SettingsStore.Load();
                Check(saved.RefineContextTokens == 250000 && saved.RefineChunkTokens == 12000 && saved.RefineOutputTokens == 16000 && saved.TranslationPrompt == s.TranslationPrompt,
                    mode + ": edited numeric text saves independently without saving an unfinished prompt edit");
                window.RefineChunkBox.Text = "not a number"; window.SaveRefineBudgetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(SettingsStore.Load().RefineChunkTokens == 12000 && window.RefineStatusText.Text.Contains("请输入"), mode + ": invalid input reports an error without overwriting saved values");
                window.ResetRefineBudgetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.SaveRefineBudgetButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                saved = SettingsStore.Load();
                Check(saved.RefineContextTokens == 1019715 && saved.RefineChunkTokens == 0 && saved.RefineOutputTokens == 65536, mode + ": restore default values and save survive settings reload");
                window.TranslationPromptBox.Text = TranslationPrompts.Render(s.TranslationPrompt, "ja", "zh"); window.UpdateLayout();
                var content = (FrameworkElement)window.Content;
                var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, Path.GetFileNameWithoutExtension(report) + "-" + mode + ".png")); png.Save(output);
            }
            finally { window.quitting = true; window.tray?.Dispose(); window.Close(); }
        }
        File.WriteAllText(report, JsonSerializer.Serialize(new { passed = true, checks }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
