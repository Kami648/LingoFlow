using System.Runtime.InteropServices;

namespace LingoFlow.Services;

public sealed record VoiceEvidence(bool Confirmed, double FreshSeconds, double Peak, int FirstSample, int LastSample);

public sealed class LocalVoiceDetector : IDisposable
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IntPtr> Modules = new(StringComparer.OrdinalIgnoreCase);
    [StructLayout(LayoutKind.Sequential)] private struct Parameters { public int Threads; public byte Gpu; public int Device; }
    [StructLayout(LayoutKind.Sequential)] private struct Loader { public IntPtr Context, Read, Eof, Close; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nuint ReadCallback(IntPtr context, IntPtr output, nuint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool EofCallback(IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CloseCallback(IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Init(ref Loader loader, Parameters parameters);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool Detect(IntPtr context, float[] samples, int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Count(IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Probabilities(IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Free(IntPtr context);
    private readonly IntPtr library;
    private IntPtr context;
    private readonly Detect detect;
    private readonly Count count;
    private readonly Probabilities probabilities;
    private readonly Free free;
    private readonly object gate = new();

    public LocalVoiceDetector(AppSettings settings)
    {
        string dll = ModelInstaller.FindExecutable(ModelInstaller.WhisperDirectory(settings), "whisper.dll");
        // ggml keeps backend worker pools and registered function pointers.
        // Keep its module loaded for the process lifetime; release model contexts.
        library = Modules.GetOrAdd(dll, path => LoadLibraryEx(path, IntPtr.Zero, 0x00000100 | 0x00001000));
        if (library == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "本地人声检测引擎未能加载。");
        T Function<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        try
        {
            detect = Function<Detect>("whisper_vad_detect_speech"); count = Function<Count>("whisper_vad_n_probs");
            probabilities = Function<Probabilities>("whisper_vad_probs"); free = Function<Free>("whisper_vad_free");
            using var file = File.OpenRead(ModelInstaller.VadPath(settings)); Exception? readError = null;
            ReadCallback read = (_, output, requested) =>
            {
                try
                {
                    byte[] bytes = new byte[checked((int)requested)]; int used = 0;
                    while (used < bytes.Length) { int n = file.Read(bytes, used, bytes.Length - used); if (n == 0) break; used += n; }
                    Marshal.Copy(bytes, 0, output, used); return (nuint)used;
                }
                catch (Exception ex) { readError = ex; return 0; }
            };
            EofCallback eof = _ => file.Position >= file.Length; CloseCallback close = _ => { };
            var loader = new Loader { Read = Marshal.GetFunctionPointerForDelegate(read), Eof = Marshal.GetFunctionPointerForDelegate(eof), Close = Marshal.GetFunctionPointerForDelegate(close) };
            context = Function<Init>("whisper_vad_init_with_params")(ref loader, new() { Threads = 2 });
            GC.KeepAlive(read); GC.KeepAlive(eof); GC.KeepAlive(close);
            if (context == IntPtr.Zero) throw new IOException("人声检测模型未能加载，请重新校验组件。", readError);
        }
        catch { throw; }
    }

    public VoiceEvidence Analyze(AudioChunk chunk)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(context == IntPtr.Zero, this);
            var samples = Array.ConvertAll(chunk.Samples, sample => sample / 32768f);
            if (!detect(context, samples, samples.Length)) throw new IOException("本地人声检测未完成，已阻止不可靠识别结果。");
            int frames = count(context);
            if (frames != (samples.Length + 511) / 512) throw new IOException("人声检测返回了不匹配的音频帧，已停止此片段处理。");
            var values = new float[frames]; Marshal.Copy(probabilities(context), values, 0, frames);
            return Evaluate(values, samples.Length, (int)Math.Round(chunk.OverlapSeconds * 16000));
        }
    }
    internal static VoiceEvidence Evaluate(float[] probabilities, int samples, int overlap)
    {
        int fresh = 0, first = samples, last = 0, startRun = -1, endRun = 0, highSamples = 0, gap = 0;
        double peak = 0, runPeak = 0;
        var regions = new List<(int First, int Last)>();
        void FinishRun()
        {
            if (startRun >= 0 && highSamples >= 2560 && runPeak >= .8) regions.Add((startRun, endRun));
            startRun = -1; highSamples = 0; runPeak = 0; gap = 0;
        }
        for (int i = 0; i < probabilities.Length; i++)
        {
            int start = i * 512, end = Math.Min(samples, start + 512);
            if (probabilities[i] >= .65)
            {
                if (startRun < 0) startRun = start;
                endRun = end; highSamples += end - start; gap = 0; runPeak = Math.Max(runPeak, probabilities[i]);
            }
            else if (++gap >= 3) FinishRun();
        }
        FinishRun();
        foreach (var region in regions)
        {
            first = Math.Min(first, region.First); last = Math.Max(last, region.Last);
            for (int i = region.First / 512; i < probabilities.Length && i * 512 < region.Last; i++)
            {
                if (probabilities[i] < .65) continue;
                int added = Math.Max(0, Math.Min(samples, (i + 1) * 512) - Math.Max(i * 512, overlap));
                fresh += added; if (added > 0) peak = Math.Max(peak, probabilities[i]);
            }
        }
        bool confirmed = fresh >= 3200 && peak >= .8;
        return new(confirmed, fresh / 16000d, peak, last > 0 ? Math.Max(0, first - 1600) : 0, last > 0 ? Math.Min(samples, last + 1600) : 0);
    }
    public void Dispose()
    {
        lock (gate) { if (context == IntPtr.Zero) return; free(context); context = IntPtr.Zero; }
    }
    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
}
