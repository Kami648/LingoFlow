using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LingoFlow.Services;

public static class AppPaths
{
    public static string Root { get; } = Environment.GetEnvironmentVariable("LINGOFLOW_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LingoFlow");
    public static string DefaultModelsDirectory => ChooseDefaultModelsDirectory(AppContext.BaseDirectory,
        DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).Select(d => d.RootDirectory.FullName));
    public static bool IsSystemDrive(string path) => string.Equals(Path.GetPathRoot(Path.GetFullPath(path)), Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase);
    public static string ChooseDefaultModelsDirectory(string programDirectory, IEnumerable<string> fixedDrives)
    {
        string adjacent = Path.Combine(Path.GetFullPath(programDirectory), "models");
        if (!IsSystemDrive(adjacent)) return adjacent;
        string? dataDrive = fixedDrives.Where(d => !IsSystemDrive(d)).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        return dataDrive == null ? adjacent : Path.Combine(dataDrive, "LingoFlow", "models");
    }
    public static string ModelsFor(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ModelDirectory)) return DefaultModelsDirectory;
        string path = Environment.ExpandEnvironmentVariables(settings.ModelDirectory.Trim());
        return Path.GetFullPath(path, AppContext.BaseDirectory);
    }
    public static string EnginesFor(AppSettings settings) => Path.Combine(ModelsFor(settings), ".engines");
    public static void ValidateModelLocation(AppSettings settings)
    {
        string directory = ModelsFor(settings);
        if (string.IsNullOrWhiteSpace(settings.ModelDirectory) && IsSystemDrive(directory))
            throw new IOException("未找到非系统盘默认目录。请先在“设备与模型”中手动选择模型保存位置，再下载模型。");
        try
        {
            Directory.CreateDirectory(directory);
            using var probe = new FileStream(Path.Combine(directory, ".lingoflow-write-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            probe.WriteByte(0);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        { throw new IOException($"模型目录不可写：{directory}。请在“设备与模型”中选择可写入的其他位置。", ex); }
    }
    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(string path, out ulong available, out ulong total, out ulong free);
    public static long FreeBytes(string directory)
    {
        string? existing = Path.GetFullPath(directory);
        while (existing != null && !Directory.Exists(existing)) existing = Path.GetDirectoryName(existing);
        if (existing == null || !GetDiskFreeSpaceEx(existing, out ulong available, out _, out _))
            throw new IOException($"无法检测所选模型目录的剩余空间：{directory}");
        return (long)Math.Min(available, (ulong)long.MaxValue);
    }
    public static string Settings => Path.Combine(Root, "settings.json");
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    public static AppSettings Load()
    {
        if (!File.Exists(AppPaths.Settings)) return new();
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.Settings), Json) ?? new();
        settings.Translation.Key = Unprotect(settings.Translation.ProtectedKey);
        settings.SubtitleSize = Math.Clamp(settings.SubtitleSize, 18, 54);
        settings.SubtitleOpacity = Math.Clamp(settings.SubtitleOpacity, .2, 1);
        settings.VoiceThreshold = Math.Clamp(settings.VoiceThreshold, .001, .05);
        settings.Colors ??= new(); Theme.Normalize(settings.Colors);
        settings.TranslationPrompt ??= AppSettings.DefaultTranslationPrompt;
        LocalTranslationModels.Migrate(settings);
        if (settings.DownloadSourceVersion < 1)
        { settings.DownloadHost = "https://hf-mirror.com"; settings.DownloadSourceVersion = 1; }
        if (settings.DownloadHost is not ("https://hf-mirror.com" or "https://huggingface.co")) settings.DownloadHost = "https://hf-mirror.com";
        settings.OverlayLines = Math.Clamp(settings.OverlayLines, 1, 6);
        if (settings.OverlayLayoutVersion < 1)
        { if (settings.OverlayHeight == 220) settings.OverlayHeight = 340; settings.OverlayLayoutVersion = 1; }
        if (settings.Acceleration is not ("auto" or "cpu")) settings.Acceleration = "auto";
        if (settings.RecognitionQualityVersion < 1)
        {
            settings.Accurate = true; settings.WhisperModel = "small"; settings.StreamTranslation = false;
            settings.RecognitionQualityVersion = 1;
        }
        if (settings.RealtimeCaptionsVersion < 1)
        { settings.StreamTranslation = true; settings.RealtimeCaptionsVersion = 1; }
        if (!Languages.All.Any(x => x.Code == settings.TargetLanguage)) settings.TargetLanguage = "zh";
        if (settings.SourceLanguage != "auto" && !Languages.All.Any(x => x.Code == settings.SourceLanguage)) settings.SourceLanguage = "auto";
        if (!new HotkeyBinding(settings.HotkeyModifiers, settings.HotkeyKey).IsValid)
        { settings.HotkeyModifiers = HotkeyBinding.Default.Modifiers; settings.HotkeyKey = HotkeyBinding.Default.Key; }
        return settings;
    }
    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppPaths.Root);
        var safe = settings.Copy();
        safe.LocalModelDefaultsVersion = 1;
        safe.RecognitionQualityVersion = 1;
        safe.DownloadSourceVersion = 1; safe.OverlayLayoutVersion = 1;
        safe.RealtimeCaptionsVersion = 1;
        safe.Translation.ProtectedKey = Protect(safe.Translation.Key);
        var temp = AppPaths.Settings + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(safe, Json), new UTF8Encoding(false));
        File.Move(temp, AppPaths.Settings, true);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);
    private static string Protect(string key) => string.IsNullOrEmpty(key) ? "" : Convert.ToBase64String(Crypt(Encoding.UTF8.GetBytes(key), false));
    private static string Unprotect(string key) => string.IsNullOrEmpty(key) ? "" : Encoding.UTF8.GetString(Crypt(Convert.FromBase64String(key), true));
    private static byte[] Crypt(byte[] data, bool decrypt)
    {
        var input = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        Marshal.Copy(data, 0, input.Data, data.Length);
        Blob output = default;
        try
        {
            bool ok = decrypt ? CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptProtectData(ref input, "LingoFlow", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法读取或保存此 Windows 用户的 API 密钥。");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }
}
