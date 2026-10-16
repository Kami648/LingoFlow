using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace LingoFlow.Services;

public sealed partial class ModelInstaller
{
    private sealed record InstalledFile(string Path, long Size, string Sha256);
    private sealed record InstalledArchive(int Version, string ArchiveSha256, List<InstalledFile> Files);
    private const string Manifest = ".lingoflow-installed.json";
    private static string ArchiveDirectory(DownloadAsset asset) => Path.ChangeExtension(asset.Path, null);
    private static string[] ArchiveExecutables(DownloadAsset asset) =>
        [Path.GetFileName(asset.Path).StartsWith("whisper-", StringComparison.OrdinalIgnoreCase) ? "whisper-server.exe" : "llama-server.exe"];
    private static string RootFor(DownloadAsset asset, string destination)
    {
        string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);
        if (!asset.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(asset.Path).Equals(root + ".zip", StringComparison.OrdinalIgnoreCase))
            throw new IOException("压缩包必须位于对应的组件目录旁。");
        return root + Path.DirectorySeparatorChar;
    }
    private static string SafeFile(string root, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("组件清单包含不安全的路径。");
        for (string? parent = Path.GetDirectoryName(path); parent != null && parent.Length >= root.TrimEnd(Path.DirectorySeparatorChar).Length; parent = Path.GetDirectoryName(parent))
            if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new IOException("组件目录不能使用目录链接。");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("组件文件不能使用文件链接。");
        return path;
    }
    internal static async Task<bool> InstalledArchiveValidAsync(DownloadAsset asset, string destination, string[] executables, CancellationToken token)
    {
        try
        {
            string root = RootFor(asset, destination), manifest = SafeFile(root, Manifest);
            if (!File.Exists(manifest) || new FileInfo(manifest).Length > 4_000_000) return false;
            var installed = JsonSerializer.Deserialize<InstalledArchive>(await File.ReadAllTextAsync(manifest, token));
            if (installed == null || installed.Version != 1 || installed.ArchiveSha256 != asset.Sha256 || installed.Files == null || installed.Files.Count == 0 ||
                installed.Files.Any(f => f == null || string.IsNullOrWhiteSpace(f.Path) || f.Size < 0 || f.Sha256 == null) ||
                installed.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != installed.Files.Count) return false;
            foreach (string name in executables)
                if (!installed.Files.Any(f => Path.GetFileName(f.Path).Equals(name, StringComparison.OrdinalIgnoreCase))) return false;
            foreach (var entry in installed.Files)
            {
                token.ThrowIfCancellationRequested(); string path = SafeFile(root, entry.Path);
                if (!File.Exists(path) || new FileInfo(path).Length != entry.Size) return false;
                using var file = File.OpenRead(path);
                if (!Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return false; }
    }
    internal static async Task PrepareArchiveAsync(DownloadAsset asset, string destination, string[] executables,
        IProgress<long>? progress, CancellationToken token, bool verifiedInstalled = false)
    {
        string root = RootFor(asset, destination);
        if (!verifiedInstalled && !await InstalledArchiveValidAsync(asset, destination, executables, token))
        {
            await DownloadAsync(asset, progress, token);
            // Reject directory links before extraction, including links left in an old install.
            using (var inspect = ZipFile.OpenRead(asset.Path))
                foreach (var entry in inspect.Entries) SafeFile(root, entry.FullName);
            await ExtractAsync(asset.Path, destination, executables[0], token);
            var files = new List<InstalledFile>();
            using (var zip = ZipFile.OpenRead(asset.Path))
            {
                foreach (var entry in zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)))
                {
                    token.ThrowIfCancellationRequested(); string path = SafeFile(root, entry.FullName);
                    using var source = entry.Open(); using var actual = File.OpenRead(path);
                    string expected = Convert.ToHexString(await SHA256.HashDataAsync(source, token));
                    if (actual.Length != entry.Length || !Convert.ToHexString(await SHA256.HashDataAsync(actual, token)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("解压后的组件校验失败，压缩包已保留供重试。");
                    files.Add(new(entry.FullName, entry.Length, expected));
                }
            }
            foreach (string name in executables)
                if (!files.Any(f => Path.GetFileName(f.Path).Equals(name, StringComparison.OrdinalIgnoreCase))) throw new IOException($"组件缺少 {name}，压缩包已保留供重试。");
            string manifest = SafeFile(root, Manifest), temporary = manifest + ".part";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new InstalledArchive(1, asset.Sha256, files)), token);
            token.ThrowIfCancellationRequested(); File.Move(temporary, manifest, true);
        }
        token.ThrowIfCancellationRequested();
        // Delete only the pinned archive, after extraction AND all-file validation.
        // An interrupted extraction keeps the archive; interrupted downloads keep .part.
        try { if (File.Exists(asset.Path) && await ValidAsync(asset.Path, asset, token)) File.Delete(Path.GetFullPath(asset.Path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* A locked ZIP is retried at the next preparation. */ }
        progress?.Report(asset.Size);
    }
}
