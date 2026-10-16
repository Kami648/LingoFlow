using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace LingoFlow.Services;

public static class WindowsRuntime
{
    public static bool Available
    {
        get
        {
            foreach (string library in new[] { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll" })
            {
                if (!NativeLibrary.TryLoad(library, out var handle)) return false;
                NativeLibrary.Free(handle);
            }
            return true;
        }
    }
    public static async Task EnsureAsync(string enginesDirectory, IProgress<InstallProgress>? progress, CancellationToken token)
    {
        if (Available) return;
        progress?.Report(new("需要微软 VC++ 运行库，正在准备官方安装程序…", 0));
        string bundled = Path.Combine(AppContext.BaseDirectory, "Dependencies", "vc_redist.x64.exe");
        string file = bundled;
        if (!File.Exists(file))
        {
            file = Path.Combine(enginesDirectory, "vc_redist.x64.exe");
            Directory.CreateDirectory(enginesDirectory);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var input = await http.GetStreamAsync("https://aka.ms/vs/17/release/vc_redist.x64.exe", token);
            using (var output = File.Create(file + ".part")) await input.CopyToAsync(output, token);
            File.Move(file + ".part", file, true);
        }
        VerifyMicrosoftSignature(file);
        token.ThrowIfCancellationRequested();
        progress?.Report(new("正在安装本地识别所需的微软运行库，Windows 可能请求管理员权限。请完成安装后继续。", 0));
        using var process = Process.Start(new ProcessStartInfo(file, "/install /passive /norestart") { UseShellExecute = true, Verb = "runas" }) ?? throw new IOException("无法启动微软运行库安装程序。");
        await process.WaitForExitAsync(token);
        if (process.ExitCode is not (0 or 3010 or 1638) || !Available) throw new IOException("微软运行库尚未就绪。请完成运行库安装后重试，本地语音识别需要此组件。");
    }
    [StructLayout(LayoutKind.Sequential)] private struct TrustFile { public uint Size; public IntPtr Path, File, Subject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
    {
        public uint Size; public IntPtr Policy, Sip; public uint Ui, Revocation, Choice; public IntPtr File; public uint State;
        public IntPtr StateData, Url; public uint Flags, Context; public IntPtr Signature;
    }
    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    public static void VerifyMicrosoftSignature(string path)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = Marshal.StringToCoTaskMemUni(path) };
        IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        try
        {
            Marshal.StructureToPtr(file, pointer, false);
            var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), Ui = 2, Choice = 1, File = pointer, Flags = 0x1000 };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0) throw new IOException("运行库安装包的 Windows 数字签名校验未通过，已拒绝执行。");
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            if (!certificate.GetNameInfo(X509NameType.SimpleName, false).Equals("Microsoft Corporation", StringComparison.OrdinalIgnoreCase))
                throw new IOException("运行库安装程序不是由 Microsoft Corporation 签名，已拒绝执行。");
        }
        finally { Marshal.FreeCoTaskMem(file.Path); Marshal.FreeHGlobal(pointer); }
    }
}

// A Job Object prevents inference servers being left running if the desktop app crashes.
internal sealed class ChildProcessJob : IDisposable
{
    private sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public JobHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Limits
    {
        public long ProcessTime, JobTime; public uint Flags; public UIntPtr Minimum, Maximum;
        public uint Processes; public UIntPtr Affinity; public uint Priority, Scheduling;
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    private readonly JobHandle handle;
    public ChildProcessJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var limits = new Limits { Flags = 0x2000 };
        if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<Limits>()))
        { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new System.ComponentModel.Win32Exception(error); }
    }
    public void Add(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法管理本地引擎的生命周期。");
    }
    public void Dispose() => handle.Dispose();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern JobHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(JobHandle handle, int type, ref Limits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(JobHandle job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
