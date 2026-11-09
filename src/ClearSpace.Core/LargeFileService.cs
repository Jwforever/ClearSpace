using System.Diagnostics;
using System.Security;

namespace ClearSpace.Core;

public sealed class LargeFileCandidate : ObservableObject
{
    private bool selected;
    public bool Selected { get => selected; set => Set(ref selected, value); }
    public required string Path { get; init; }
    public required long Length { get; init; }
    public required DateTime LastWriteUtc { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string SizeText => Format.Bytes(Length);
    public string ModifiedText => LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public int TypeIndex => LargeFileTypes.Classify(Path);
    public string TypeText => LargeFileTypes.Labels[TypeIndex];
}

public static class LargeFileTypes
{
    public static readonly string[] Labels = ["全部类型", "音视频", "压缩包", "程序 / 安装包", "镜像 / 虚拟磁盘", "其他"];
    public static int Classify(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".mkv" or ".mov" or ".avi" or ".wmv" or ".webm" or ".m4v" or ".mpeg" or ".mpg" or ".ts" or
        ".mp3" or ".flac" or ".wav" or ".aac" or ".m4a" or ".ogg" => 1,
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".bz2" or ".xz" or ".zst" => 2,
        ".exe" or ".msi" or ".msix" or ".appx" or ".msixbundle" or ".appxbundle" => 3,
        ".iso" or ".img" or ".vhd" or ".vhdx" or ".vmdk" or ".qcow2" => 4,
        _ => 5
    };
}

public sealed record LargeFileProgress(int InspectedFiles, int MatchingFiles, int SkippedEntries, string Directory);
public sealed record LargeFileScan(string Root, long MinimumBytes, IReadOnlyList<LargeFileCandidate> Files,
    int InspectedFiles, int MatchingFiles, int SkippedEntries, bool Canceled, bool Truncated, IReadOnlyList<string> Notes);

public sealed class LargeFilePolicy
{
    private readonly IReadOnlyList<string> protectedRoots;
    private const FileAttributes Unavailable = FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Offline;
    private static readonly string[] ReservedDirectories = ["$Recycle.Bin", "System Volume Information", "Recovery", "$WINDOWS.~BT", "$WINDOWS.~WS", "Config.Msi"];

    public LargeFilePolicy(IEnumerable<string>? protectedRoots = null, IEnumerable<string>? additionalProtectedRoots = null)
    {
        this.protectedRoots = (protectedRoots ?? DefaultProtectedRoots()).Concat(additionalProtectedRoots ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(PathSafety.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> DefaultProtectedRoots()
    {
        foreach (var special in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData })
            yield return Environment.GetFolderPath(special);
        yield return AppContext.BaseDirectory;
    }

    public bool IsProtected(string path)
    {
        string full = PathSafety.Normalize(path);
        if (protectedRoots.Any(p => SameOrWithin(full, p))) return true;
        string volume = Path.GetPathRoot(full)!;
        return ReservedDirectories.Any(name => SameOrWithin(full, Path.Combine(volume, name))) ||
            new[] { "pagefile.sys", "swapfile.sys", "hiberfil.sys" }.Any(name => full.Equals(Path.Combine(volume, name), StringComparison.OrdinalIgnoreCase));
    }

    public string ValidateRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\"))
            throw new IOException("请选择本机磁盘或文件夹的完整路径。");
        string root = PathSafety.Normalize(path);
        var drive = new DriveInfo(Path.GetPathRoot(root)!);
        if (!drive.IsReady || drive.DriveType != DriveType.Fixed) throw new IOException("当前支持本地固定磁盘，请选择电脑中的磁盘或文件夹。");
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("找不到扫描文件夹，请重新选择。");
        if (IsProtected(root)) throw new IOException("该位置属于系统或应用目录，请选择下载、视频等个人文件夹，或扫描磁盘中的其他位置。");
        if (!DirectoryAllowed(root)) throw new IOException("该文件夹包含链接、系统或脱机属性，无法扫描。");
        return root;
    }

    public bool DirectoryAllowed(string path)
    {
        try
        {
            if (IsProtected(path) || new[] { ".git", ".svn", ".hg" }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)) return false;
            bool volumeRoot = PathSafety.Normalize(path).Equals(Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase);
            var excludedAttributes = volumeRoot ? Unavailable & ~FileAttributes.System : Unavailable;
            if ((File.GetAttributes(path) & excludedAttributes) != 0) return false;
            for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException) { return false; }
    }

    public bool FileAllowed(string path, string root)
    {
        try
        {
            if (IsProtected(path) || !PathSafety.IsWithin(path, root)) return false;
            var attributes = File.GetAttributes(path);
            return (attributes & (Unavailable | FileAttributes.Directory | FileAttributes.ReadOnly)) == 0 &&
                PathSafety.IsRegularFileUnderRoot(path, root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException) { return false; }
    }

    private static bool SameOrWithin(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || PathSafety.IsWithin(path, root);
}

public sealed class LargeFileService
{
    private readonly LargeFilePolicy policy;
    private readonly IRecycleBin recycleBin;
    private readonly int maximumCandidates;
    public LargeFileService(LargeFilePolicy policy, IRecycleBin recycleBin, int maximumCandidates = 20000)
    {
        this.policy = policy; this.recycleBin = recycleBin;
        this.maximumCandidates = Math.Clamp(maximumCandidates, 1, 20000);
    }

    public LargeFileScan Scan(string requestedRoot, long minimumBytes, CancellationToken token, IProgress<LargeFileProgress>? progress = null)
    {
        if (minimumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(minimumBytes));
        token.ThrowIfCancellationRequested();
        string root = policy.ValidateRoot(requestedRoot);
        var largest = new PriorityQueue<LargeFileCandidate, long>();
        var pending = new Stack<string>(); pending.Push(root);
        int inspected = 0, matching = 0, skipped = 0, directories = 0;
        bool truncated = false;
        var watch = Stopwatch.StartNew();
        progress?.Report(new(0, 0, 0, root));
        while (pending.TryPop(out string? folder) && !token.IsCancellationRequested)
        {
            if (++directories > 200000 || inspected >= 2000000) { truncated = true; break; }
            if (!policy.DirectoryAllowed(folder)) { skipped++; continue; }
            try
            {
                foreach (string path in Directory.EnumerateFiles(folder))
                {
                    if (token.IsCancellationRequested) break;
                    if (++inspected > 2000000) { truncated = true; break; }
                    if (watch.ElapsedMilliseconds >= 150)
                    { progress?.Report(new(inspected, matching, skipped, folder)); watch.Restart(); }
                    try
                    {
                        var info = new FileInfo(path);
                        if (info.Length < minimumBytes) continue;
                        if (!policy.FileAllowed(path, root)) { skipped++; continue; }
                        matching++;
                        if (largest.Count >= maximumCandidates && largest.Peek().Length >= info.Length) continue;
                        if (largest.Count >= maximumCandidates) largest.Dequeue();
                        largest.Enqueue(new() { Path = info.FullName, Length = info.Length, LastWriteUtc = info.LastWriteTimeUtc }, info.Length);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { skipped++; }
                }
                if (truncated || token.IsCancellationRequested) break;
                foreach (string child in Directory.EnumerateDirectories(folder))
                {
                    if (token.IsCancellationRequested) break;
                    if (pending.Count >= 200000) { truncated = true; break; }
                    pending.Push(child);
                }
                if (truncated) break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { skipped++; }
            if (watch.ElapsedMilliseconds >= 150)
            { progress?.Report(new(inspected, matching, skipped, folder)); watch.Restart(); }
        }
        var notes = new List<string>();
        if (matching > maximumCandidates) notes.Add($"匹配 {matching:N0} 个文件，仅显示最大的 {maximumCandidates:N0} 个；可提高大小门槛缩小范围。");
        if (truncated) notes.Add("已达到本次遍历上限，请选择较小的文件夹继续扫描。");
        if (token.IsCancellationRequested) notes.Add("扫描已停止，当前为已扫描部分的结果。");
        if (skipped > 0) notes.Add($"跳过 {skipped:N0} 个系统、应用、链接或无法访问的项目。");
        return new(root, minimumBytes, largest.UnorderedItems.Select(i => i.Element).OrderByDescending(f => f.Length)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(), inspected, matching, skipped,
            token.IsCancellationRequested, truncated || matching > maximumCandidates, notes);
    }

    public CleanupResult Execute(LargeFileScan scan, IReadOnlyList<LargeFileCandidate> selected, CancellationToken token, IProgress<string>? progress = null)
    {
        int succeeded = 0, skipped = 0; long bytes = 0;
        var errors = new List<string>();
        var allowed = scan.Files.ToHashSet();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in selected)
        {
            if (token.IsCancellationRequested) break;
            try
            {
                if (!allowed.Contains(item)) throw new IOException("文件不属于本次扫描预览。");
                if (!seen.Add(item.Path)) { skipped++; continue; }
                string root = policy.ValidateRoot(scan.Root);
                if (!policy.FileAllowed(item.Path, root)) throw new IOException("路径或属性已变化，或文件不在扫描范围内。");
                var info = new FileInfo(item.Path);
                if (info.Length != item.Length || info.LastWriteTimeUtc != item.LastWriteUtc || info.Length < scan.MinimumBytes)
                    throw new IOException("文件在扫描后发生变化，请重新扫描核对。");
                using (var handle = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                progress?.Report($"正在移入回收站 {succeeded + skipped + 1} / {selected.Count}…");
                recycleBin.Send(item.Path);
                succeeded++; bytes += item.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or OperationCanceledException or
                System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
            {
                skipped++;
                if (errors.Count < 100) errors.Add(item.Path + "：" + ex.Message);
            }
        }
        int remaining = selected.Count - succeeded - skipped;
        if (remaining > 0) { skipped += remaining; errors.Add($"操作已取消，另有 {remaining} 个文件未处理。"); }
        return new(succeeded, skipped, bytes, errors);
    }
}
