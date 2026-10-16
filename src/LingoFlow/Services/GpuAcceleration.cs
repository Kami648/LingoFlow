using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LingoFlow.Services;

public sealed record NvidiaDevice(int Index, string Name, double Driver, long TotalMb, long FreeMb);

public static class GpuAcceleration
{
    private static readonly object Gate = new();
    private static DateTime checkedAt;
    private static NvidiaDevice? cached;
    public static NvidiaDevice? Detect(bool refresh = false)
    {
        lock (Gate)
        {
            if (!refresh && DateTime.UtcNow - checkedAt < TimeSpan.FromSeconds(30)) return cached;
            checkedAt = DateTime.UtcNow; cached = null;
            try
            {
                string file = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
                if (!File.Exists(file)) file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
                if (!File.Exists(file)) return null;
                using var process = Process.Start(new ProcessStartInfo(file)
                {
                    Arguments = "--query-gpu=index,name,driver_version,memory.total,memory.free --format=csv,noheader,nounits",
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                });
                if (process == null) return null;
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(2500)) { process.Kill(true); return null; }
                if (process.ExitCode != 0) return null;
                var devices = Parse(output.GetAwaiter().GetResult());
                cached = devices.Where(d => d.Driver >= 551.61).OrderByDescending(d => d.FreeMb).FirstOrDefault();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return cached;
        }
    }
    internal static List<NvidiaDevice> Parse(string csv)
    {
        var devices = new List<NvidiaDevice>();
        foreach (string line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(',').Select(p => p.Trim()).ToArray();
            if (parts.Length == 5 && int.TryParse(parts[0], out int index) && index >= 0 &&
                double.TryParse(parts[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double driver) &&
                long.TryParse(parts[3], out long total) && long.TryParse(parts[4], out long free) && total > 0 && free >= 0)
                devices.Add(new(index, parts[1], driver, total, free));
        }
        return devices;
    }
    public static bool CanUse(AppSettings settings, NvidiaDevice? device)
    {
        if (settings.Acceleration == "cpu" || device == null || device.Driver < 551.61 || device.TotalMb < 4096) return false;
        long speech = settings.WhisperModel == "small" ? 1600 : 900;
        long translation = settings.LocalTranslation ? LocalTranslationModels.Get(settings.TranslationModel).Size / 1_000_000 + 1200 : 0;
        return device.FreeMb >= Math.Max(3000, speech + translation + 600);
    }
    public static bool ShouldUse(AppSettings settings) => settings.Acceleration != "cpu" && CanUse(settings, Detect());
    public static string Describe(AppSettings settings)
    {
        if (settings.Acceleration == "cpu") return "已选择 CPU 推理";
        var device = Detect();
        return CanUse(settings, device)
            ? $"自动使用显卡：{device!.Name} · 可用显存 {device.FreeMb / 1024d:F1} GB"
            : "当前使用 CPU：未检测到满足驱动及可用显存条件的 NVIDIA 显卡";
    }
    internal static async Task<string?> VulkanDeviceAsync(string executable, string name, CancellationToken token)
    {
        using var process = Process.Start(new ProcessStartInfo(executable)
        { Arguments = "--list-devices", UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = Path.GetDirectoryName(executable)! });
        if (process == null) return null;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(token); var error = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(15), token);
            string text = await output + "\n" + await error;
            if (process.ExitCode != 0) return null;
            foreach (Match match in Regex.Matches(text, @"(Vulkan\d+):\s*([^\r\n]+)"))
                if (match.Groups[2].Value.Contains(name, StringComparison.OrdinalIgnoreCase)) return match.Groups[1].Value;
            return null;
        }
        finally { if (!process.HasExited) process.Kill(true); }
    }
}
