using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LingoFlow.Services;

namespace LingoFlow;

internal static class DisplayDownloadDiagnostics
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> action) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(action(request)); }
    private sealed class InterruptedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (Position >= 512) throw new IOException("Interrupted fixture", new System.Net.Sockets.SocketException(10054));
            return base.ReadAsync(buffer[..Math.Min(128, buffer.Length)], token);
        }
    }
    public static async Task CheckAsync(Action<bool, string> check)
    {
        var manifest = ModelInstaller.Plan(new() { Route = "local" }, true);
        check(new AppSettings().DownloadHost == "https://hf-mirror.com" && manifest.Where(a => a.Path.EndsWith(".bin") || a.Path.EndsWith(".gguf")).All(a => new Uri(a.Url).Host == "hf-mirror.com"), "HF Mirror is the default for every recognition, VAD and translation model");
        check(manifest.Where(a => a.Path.EndsWith(".zip")).All(a => new Uri(a.Url).Host == "github.com"), "HF Mirror never receives a fabricated GitHub engine path");
        byte[] data = Enumerable.Range(0, 2048).Select(i => (byte)(i % 251)).ToArray();
        string root = Path.Combine(AppPaths.Root, "download-fixtures-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        DownloadAsset Asset(string name) => new(name, "https://example.com/model.bin", Path.Combine(root, name), data.Length, Convert.ToHexString(SHA256.HashData(data)));
        int calls = 0;
        using (var client = new HttpClient(new Handler(_ => ++calls == 1 ? new(HttpStatusCode.ServiceUnavailable) : new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) })))
        {
            var asset = Asset("retry.bin"); await ModelInstaller.DownloadUsingAsync(asset, null, CancellationToken.None, client);
            check(calls == 2 && File.ReadAllBytes(asset.Path).SequenceEqual(data), "A transient HTTP failure retries and verifies the downloaded bytes");
        }
        calls = 0; long? resumeAt = null;
        using (var client = new HttpClient(new Handler(request =>
        {
            if (++calls == 1) return new(HttpStatusCode.OK) { Content = new StreamContent(new InterruptedStream(data)) };
            resumeAt = request.Headers.Range?.Ranges.Single().From;
            var content = new ByteArrayContent(data[512..]); content.Headers.ContentRange = new ContentRangeHeaderValue(512, data.Length - 1, data.Length);
            return new(HttpStatusCode.PartialContent) { Content = content };
        })))
        {
            var asset = Asset("resume.bin"); await ModelInstaller.DownloadUsingAsync(asset, null, CancellationToken.None, client);
            check(calls == 2 && resumeAt == 512 && File.ReadAllBytes(asset.Path).SequenceEqual(data), "Interrupted transfers resume from retained bytes and still require the full official checksum");
        }
        calls = 0; string error = "";
        using (var client = new HttpClient(new Handler(_ => { calls++; return new(HttpStatusCode.Forbidden); })))
        {
            try { await ModelInstaller.DownloadUsingAsync(Asset("denied.bin"), null, CancellationToken.None, client); }
            catch (HttpRequestException ex) { error = ModelInstaller.DescribeFailure(ex); }
            check(calls == 1 && error.Contains("403"), "Permanent HTTP rejection reports its status without pointless repeated requests");
        }
        using (var cancel = new CancellationTokenSource())
        using (var client = new HttpClient(new Handler(_ => { cancel.Cancel(); cancel.Token.ThrowIfCancellationRequested(); return new(HttpStatusCode.OK); })))
        {
            bool cancelled = false;
            try { await ModelInstaller.DownloadUsingAsync(Asset("cancel.bin"), null, cancel.Token, client); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "User cancellation interrupts download without automatic retry");
        }
        SettingsStore.Save(new() { DownloadHost = "https://huggingface.co", StreamTranslation = false, OverlayLines = 4 });
        var persisted = SettingsStore.Load();
        check(persisted.DownloadHost == "https://huggingface.co" && !persisted.StreamTranslation && persisted.OverlayLines == 4, "Explicit later source, streaming and row-count choices survive restart");
        var mode = Application.Current.ShutdownMode; Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var settings = new AppSettings { OverlayWidth = 640, OverlayHeight = 340, OverlayLines = 3, StreamTranslation = true };
        var overlay = new OverlayWindow(settings) { Opacity = 0, ShowActivated = false };
        try
        {
            overlay.Show();
            Subtitle Row(long id, string text, bool pending = false) => new(DateTimeOffset.Now, id, id + 1, "We can keep several sentences visible.", text, "en", 0) { Sequence = id, IsPending = pending };
            overlay.Present(Row(1, "正在翻译…", true), settings); overlay.Present(Row(2, "正在翻译…", true), settings);
            overlay.Present(Row(1, "上一句会保留在上方，新句不必等待动画结束。"), settings);
            check(overlay.RecentRows.Single().Translation.StartsWith("上一句") && overlay.OriginalText.Text.StartsWith("We can"), "A delayed translation updates its own earlier row while the latest source stays visible");
            overlay.Present(Row(2, "翻译结果随模型返回逐步补齐。"), settings);
            overlay.Present(Row(3, "拖动边框或右下角，就能调整字幕窗口的大小。"), settings);
            check(overlay.RecentRows.Select(r => r.Sequence).SequenceEqual(new long[] { 1, 2 }) && overlay.TranslatedText.Text.StartsWith("拖动"), "Three recent captions remain visible and a new sentence displays without an artificial presentation queue");
            int changed = 0; overlay.BoundsChanged += () => changed++;
            var handle = overlay.ResizeHandles.Children.OfType<Thumb>().Single(t => (string)t.Tag == "SE");
            handle.RaiseEvent(new DragDeltaEventArgs(120, 100) { RoutedEvent = Thumb.DragDeltaEvent });
            handle.RaiseEvent(new DragCompletedEventArgs(120, 100, false) { RoutedEvent = Thumb.DragCompletedEvent });
            check(overlay.Width == 760 && overlay.Height == 440 && changed == 1, "The visible corner handle resizes both dimensions and notifies persistence");
            overlay.SetLocked(true); overlay.ResizeCaption("SE", 100, 100);
            check(overlay.Width == 760 && overlay.ResizeHandles.Visibility == Visibility.Collapsed, "Locked click-through captions cannot be accidentally resized");
            overlay.SetLocked(false); overlay.CapturePosition(settings); SettingsStore.Save(settings);
            var restored = SettingsStore.Load();
            check(restored.OverlayWidth == 760 && restored.OverlayHeight == 440, "User-adjusted overlay bounds survive configuration reload");
            overlay.UpdateLayout(); await Task.Delay(250);
            var content = (FrameworkElement)overlay.Content;
            var bitmap = new RenderTargetBitmap((int)overlay.Width, (int)overlay.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using (var output = File.Create(Path.Combine(AppPaths.Root, "multiline-overlay.png"))) png.Save(output);
            overlay.Present(Row(4, "更早的字幕仍保存在主窗口和完整导出文件中。"), settings);
            check(overlay.RecentRows.Select(r => r.Sequence).SequenceEqual(new long[] { 2, 3 }), "The floating row limit never requires deleting the full main-window transcript");
        }
        finally { overlay.Close(); Application.Current.ShutdownMode = mode; }
    }
}
