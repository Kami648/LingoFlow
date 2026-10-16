using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LingoFlow.Services;

// Decode locally through a private pipe. No complete decoded file or media upload.
public static class MediaDecoder
{
    public static string DirectoryFor(AppSettings s) => Path.Combine(AppPaths.EnginesFor(s), "ffmpeg-8.1.2");
    internal static DownloadAsset Asset(AppSettings s) => new("音视频解码组件 FFmpeg 8.1.2",
        "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-essentials_build.zip",
        Path.Combine(AppPaths.EnginesFor(s), "ffmpeg-8.1.2.zip"), 109728040,
        "db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec");
    public static async Task PrepareAsync(AppSettings s, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        AppPaths.ValidateModelLocation(s);
        var asset = Asset(s);
        bool ready = await ModelInstaller.InstalledArchiveValidAsync(asset, DirectoryFor(s), ["ffmpeg.exe", "ffprobe.exe"], token);
        if (!ready && AppPaths.FreeBytes(AppPaths.ModelsFor(s)) < 500_000_000) throw new IOException("模型所在磁盘需要至少 500 MB 空间来准备音视频解码组件。");
        await ModelInstaller.PrepareArchiveAsync(asset, DirectoryFor(s), ["ffmpeg.exe", "ffprobe.exe"],
            new Progress<long>(n => progress?.Report(new("准备 / 校验音视频解码组件（完成后自动清理压缩包）", n / (double)asset.Size))), token, ready);
    }
    private static Process Open(string executable, params string[] args)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException("无法启动音视频解码组件。");
    }
    private static async Task<string> ReadErrorsAsync(StreamReader reader)
    {
        var tail = new StringBuilder(); char[] buffer = new char[1024]; int n;
        while ((n = await reader.ReadAsync(buffer)) > 0) { tail.Append(buffer, 0, n); if (tail.Length > 8192) tail.Remove(0, tail.Length - 8192); }
        return tail.ToString();
    }
    internal static void Kill(Process process) { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
    public static async Task<double?> ProbeAsync(string file, AppSettings s, CancellationToken token)
    {
        if (!File.Exists(file)) throw new FileNotFoundException("导入文件不存在。", file);
        using var process = Open(ModelInstaller.FindExecutable(DirectoryFor(s), "ffprobe.exe"), "-v", "error", "-protocol_whitelist", "file,pipe", "-select_streams", "a:0",
            "-show_entries", "stream=index:format=duration", "-of", "json", "-i", Path.GetFullPath(file));
        using var job = new ChildProcessJob(); job.Add(process);
        using var stop = token.Register(() => Kill(process));
        var output = process.StandardOutput.ReadToEndAsync(token); var errors = ReadErrorsAsync(process.StandardError);
        await process.WaitForExitAsync(token); string result = await output; await errors;
        if (process.ExitCode != 0) throw new IOException("无法读取此文件，请检查文件是否完整、是否受加密保护，或换一个文件。");
        using var json = JsonDocument.Parse(result);
        if (!json.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0) throw new IOException("此文件没有可翻译的音轨。");
        if (json.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var duration) &&
            double.TryParse(duration.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && double.IsFinite(seconds) && seconds > 0) return seconds;
        return null;
    }
    public static async Task<double> DecodeAsync(string file, AppSettings s, Func<short[], double, Task> receive, CancellationToken token)
    {
        using var process = Open(ModelInstaller.FindExecutable(DirectoryFor(s), "ffmpeg.exe"), "-nostdin", "-hide_banner", "-v", "error", "-protocol_whitelist", "file,pipe",
            "-i", Path.GetFullPath(file), "-map", "0:a:0", "-vn", "-ac", "1", "-ar", "16000", "-f", "s16le", "pipe:1");
        using var job = new ChildProcessJob(); job.Add(process);
        using var stop = token.Register(() => Kill(process));
        var errors = ReadErrorsAsync(process.StandardError);
        long sampleCount = 0; byte[] buffer = new byte[32000]; int carry = 0;
        try
        {
            int n;
            while ((n = await process.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(carry), token)) > 0)
            {
                n += carry; int bytes = n & ~1;
                var samples = new short[bytes / 2]; Buffer.BlockCopy(buffer, 0, samples, 0, bytes);
                sampleCount += samples.Length;
                await receive(samples, sampleCount / 16000d);
                carry = n - bytes; if (carry > 0) buffer[0] = buffer[bytes];
            }
            await process.WaitForExitAsync(token); await errors; token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || sampleCount == 0 || carry != 0) throw new IOException("音轨解码未能完成；文件可能损坏或格式不受支持，已完成的字幕仍可导出。");
            return sampleCount / 16000d;
        }
        finally { Kill(process); await errors; }
    }
}
