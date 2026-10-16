using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LingoFlow.Services;

// WASAPI interfaces are declared in Windows SDK audioclient.h and mmdeviceapi.h.
public static class AudioNative
{
    internal static readonly Guid AudioClientId = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    internal static readonly Guid CaptureClientId = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    internal static extern int ActivateAudioInterfaceAsync(string path, ref Guid iid, IntPtr parameters,
        IActivateCompletion completion, out IActivateOperation operation);
    [DllImport("ole32.dll")] internal static extern int CoInitializeEx(IntPtr reserved, int flags);
    [DllImport("ole32.dll")] internal static extern void CoUninitialize();
    internal static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormat
    {
        public ushort Tag, Channels;
        public uint Rate, BytesPerSecond;
        public ushort BlockAlign, Bits, Extra;
        public static WaveFormat Pcm16 => new() { Tag = 1, Channels = 1, Rate = 16000, BytesPerSecond = 32000, BlockAlign = 2, Bits = 16 };
    }
    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int mode, uint flags, long duration, long periodicity, ref WaveFormat format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int mode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long normal, out long minimum);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }
    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong position, out ulong clock);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint state, out IntPtr collection);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateOperation
    {
        [PreserveSig] int GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object client);
    }
    [ComVisible(true), Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IActivateCompletion
    {
        [PreserveSig] int ActivateCompleted(IActivateOperation operation);
    }
    [ComVisible(true), Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAgileObject { }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class Activation : IActivateCompletion, IAgileObject
    {
        private readonly TaskCompletionSource<IAudioClient> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IntPtr parameters, blob;
        private IActivateOperation? pending;
        internal IAudioClient Open(uint pid, CancellationToken token)
        {
            blob = Marshal.AllocHGlobal(12);
            Marshal.WriteInt32(blob, 0, 1); Marshal.WriteInt32(blob, 4, (int)pid); Marshal.WriteInt32(blob, 8, 0);
            parameters = Marshal.AllocHGlobal(24);
            Marshal.Copy(new byte[24], 0, parameters, 24);
            Marshal.WriteInt16(parameters, 0, 65); Marshal.WriteInt32(parameters, 8, 12); Marshal.WriteIntPtr(parameters, 16, blob);
            var iid = AudioClientId;
            int hr = ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, parameters, this, out pending);
            if (hr < 0) { Free(); Check(hr); }
            // Keep the callback and PROPVARIANT alive until Windows completes activation.
            // A timeout does not permit freeing memory which Windows might still use.
            try { return ready.Task.WaitAsync(TimeSpan.FromSeconds(12), token).GetAwaiter().GetResult(); }
            catch { _ = ready.Task.ContinueWith(t => { if (t.IsCompletedSuccessfully) Marshal.ReleaseComObject(t.Result); }, TaskScheduler.Default); throw; }
        }
        public int ActivateCompleted(IActivateOperation operation)
        {
            try { Check(operation.GetActivateResult(out var hr, out var client)); Check(hr); ready.TrySetResult((IAudioClient)client); }
            catch (Exception ex) { ready.TrySetException(ex); }
            finally { Free(); pending = null; }
            return 0;
        }
        private void Free()
        {
            var p = Interlocked.Exchange(ref parameters, IntPtr.Zero); if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            p = Interlocked.Exchange(ref blob, IntPtr.Zero); if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        }
    }
}

public sealed class AudioCapture : IAsyncDisposable
{
    private CancellationTokenSource? cancel;
    private Task? worker;
    public event Action<short[]>? Samples;
    public event Action<Exception>? Failed;

    public async Task StartAsync(int pid, CancellationToken token)
    {
        if (worker != null) throw new InvalidOperationException("音频捕获已经运行。");
        if (pid != 0 && !DeviceProbe.CanAttemptProcessCapture) throw new NotSupportedException("此 Windows 版本不支持指定进程捕获。请明确选择“系统全部声音”，或升级系统。");
        cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker = Task.Factory.StartNew(() => Run(pid, ready, cancel.Token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(16), token); }
        catch { await DisposeAsync(); throw; }
    }
    private void Run(int pid, TaskCompletionSource ready, CancellationToken token)
    {
        int co = AudioNative.CoInitializeEx(IntPtr.Zero, 0);
        AudioNative.IAudioClient? client = null;
        AudioNative.IAudioCaptureClient? capture = null;
        object? enumerator = null, device = null;
        try
        {
            if (pid != 0)
                client = new AudioNative.Activation().Open((uint)pid, token);
            else
            {
                enumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!);
                AudioNative.Check(((AudioNative.IMMDeviceEnumerator)enumerator!).GetDefaultAudioEndpoint(0, 1, out var endpoint));
                device = endpoint;
                var iid = AudioNative.AudioClientId;
                AudioNative.Check(endpoint.Activate(ref iid, 23, IntPtr.Zero, out var obj));
                client = (AudioNative.IAudioClient)obj;
            }
            var format = AudioNative.WaveFormat.Pcm16;
            // Windows performs channel mixing and resampling. No temporary recording is written.
            using var audioEvent = new AutoResetEvent(false);
            AudioNative.Check(client.Initialize(0, 0x00020000 | 0x00040000 | 0x80000000 | 0x08000000, 0, 0, ref format, IntPtr.Zero));
            AudioNative.Check(client.SetEventHandle(audioEvent.SafeWaitHandle.DangerousGetHandle()));
            var cid = AudioNative.CaptureClientId;
            AudioNative.Check(client.GetService(ref cid, out var service));
            capture = (AudioNative.IAudioCaptureClient)service;
            AudioNative.Check(client.Start()); ready.TrySetResult();
            var handles = new WaitHandle[] { audioEvent, token.WaitHandle };
            var clock = Stopwatch.StartNew();
            long lastPacket = clock.ElapsedMilliseconds;
            while (!token.IsCancellationRequested)
            {
                if (WaitHandle.WaitAny(handles, 40) == 1) break;
                AudioNative.Check(capture.GetNextPacketSize(out uint available));
                while (available > 0 && !token.IsCancellationRequested)
                {
                    AudioNative.Check(capture.GetBuffer(out var data, out uint frames, out uint flags, out _, out _));
                    try
                    {
                        var samples = new short[frames];
                        if ((flags & 2) == 0) Marshal.Copy(data, samples, 0, samples.Length);
                        Samples?.Invoke(samples); lastPacket = clock.ElapsedMilliseconds;
                    }
                    finally { AudioNative.Check(capture.ReleaseBuffer(frames)); }
                    AudioNative.Check(capture.GetNextPacketSize(out available));
                }
                // WASAPI stops issuing packets when the app stops playing. Supply silence
                // so that the final utterance still reaches the segmenter's end detector.
                if (clock.ElapsedMilliseconds - lastPacket >= 40)
                { Samples?.Invoke(new short[640]); lastPacket = clock.ElapsedMilliseconds; }
                if (pid != 0)
                {
                    try { using var process = Process.GetProcessById(pid); if (process.HasExited) throw new InvalidOperationException(); }
                    catch (ArgumentException) { throw new InvalidOperationException("所选应用已退出，请重新选择音频来源。"); }
                }
            }
        }
        catch (OperationCanceledException) { ready.TrySetCanceled(token); }
        catch (Exception ex)
        {
            Exception userError = ex;
            if (ex is COMException com)
                userError = new InvalidOperationException(pid == 0
                    ? $"无法捕获系统声音（0x{com.HResult:X8}）。请确认有可用的扬声器或耳机输出设备，设备未断开，再重新开始。"
                    : $"无法捕获此进程（0x{com.HResult:X8}）。请确认应用仍在运行；部分 Windows 10 版本不支持此功能，可手动选择系统全部声音。", ex);
            if (!ready.TrySetException(userError) && !token.IsCancellationRequested) Failed?.Invoke(userError);
        }
        finally
        {
            if (client != null) { try { client.Stop(); } catch { } }
            foreach (var item in new object?[] { capture, client, device, enumerator })
                if (item != null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
            if (co >= 0) AudioNative.CoUninitialize();
        }
    }
    public async ValueTask DisposeAsync()
    {
        cancel?.Cancel();
        if (worker != null) await worker;
        cancel?.Dispose(); cancel = null; worker = null;
    }
}
