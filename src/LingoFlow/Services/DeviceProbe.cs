using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LingoFlow.Services;

public record DeviceReport(string System, string Cpu, int Threads, double RamGb, double AvailableRamGb, double DiskGb, string Gpu, bool ProcessCapture, string Recommendation, string Reason);

public static class DeviceProbe
{
    public static bool SupportsProcessCapture => Environment.OSVersion.Version.Build >= 20348;
    public static bool CanAttemptProcessCapture => Environment.OSVersion.Version.Build >= 19041;
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, Extended;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice display, uint flags);
    public static DeviceReport Detect(string? modelsDirectory = null)
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        double ram = memory.TotalPhysical / 1073741824d, available = memory.AvailablePhysical / 1073741824d;
        string cpu = "未知 CPU";
        using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
            cpu = key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? cpu;
        var gpus = new List<string>();
        for (uint i = 0; i < 12; i++)
        {
            var gpu = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>(), Name = "", Description = "", Id = "", Key = "" };
            if (!EnumDisplayDevices(null, i, ref gpu, 0)) break;
            if (!gpus.Contains(gpu.Description)) gpus.Add(gpu.Description);
        }
        double disk = AppPaths.FreeBytes(modelsDirectory ?? AppPaths.DefaultModelsDirectory) / 1073741824d;
        var (route, reason) = Recommend(ram, available, Environment.ProcessorCount, disk);
        if (!System.Runtime.Intrinsics.X86.Avx2.IsSupported) { route = "hybrid"; reason = "本地语音识别引擎需要 AVX2，此 CPU 不支持，需要更换兼容引擎或设备。云端文字翻译不能替代本地识别。"; }
        int build = Environment.OSVersion.Version.Build;
        return new($"Windows {(build >= 22000 ? "11" : "10")} · Build {build} · {RuntimeInformation.OSArchitecture}", cpu, Environment.ProcessorCount,
            ram, available, disk, string.Join(" / ", gpus), SupportsProcessCapture, route, reason);
    }
    public static (string Route, string Reason) Recommend(double ram, double available, int threads, double disk)
    {
        if (disk < 3 || ram < 6 || available < 2 || threads < 4)
            return ("hybrid", "可用资源较少，建议 Tiny 本地识别＋云端文字翻译；语音始终在本机处理。模型磁盘至少需要约 0.4 GB，仍需实测识别速度。");
        if (ram >= 16 && available >= 7 && threads >= 8 && disk >= 6)
            return ("local", "可以尝试完全本地模式。支持的 NVIDIA 显卡将自动加速；启动失败会回退 CPU。建议准备组件后实测，仍跟不上时可改为云端文字翻译。");
        return ("hybrid", "推荐本地识别＋云端翻译，减少本地翻译模型的内存占用。云端仅接收识别出的文字。是否流畅仍需安装后实测。");
    }
    public static List<AudioSource> Sources()
    {
        var sources = new List<AudioSource> { new(0, "系统全部声音", "") };
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == Environment.ProcessId || p.HasExited || string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                    sources.Add(new(p.Id, p.ProcessName, p.MainWindowTitle));
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        }
        return sources.OrderBy(x => x.Pid != 0).ThenBy(x => x.Name).ToList();
    }
}
