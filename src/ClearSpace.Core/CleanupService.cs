using System.Diagnostics;

namespace ClearSpace.Core;

public static class CleanupRules
{
    public static IReadOnlyList<CleanupRule> CreateDefault()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var rules = new List<CleanupRule>
        {
            new("temp", "用户临时文件", Path.GetTempPath()),
            new("crashes", "崩溃转储", Path.Combine(local, "CrashDumps"), "*.dmp")
        };
        foreach (string browser in new[] { @"Google\Chrome\User Data", @"Microsoft\Edge\User Data" })
        {
            string data = Path.Combine(local, browser);
            if (!Directory.Exists(data)) continue;
            try
            {
                foreach (string profile in Directory.EnumerateDirectories(data))
                {
                    string name = Path.GetFileName(profile);
                    if (name != "Default" && !name.StartsWith("Profile ", StringComparison.Ordinal)) continue;
                    foreach (string cache in new[] { @"Cache\Cache_Data", "Code Cache", "GPUCache" })
                        rules.Add(new CleanupRule("browser", "浏览器缓存", Path.Combine(profile, cache)));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }
        return rules;
    }

    public static bool BrowserIsRunning()
    {
        foreach (string name in new[] { "chrome", "msedge" })
        {
            var processes = Process.GetProcessesByName(name);
            bool running = processes.Length > 0;
            foreach (var process in processes) process.Dispose();
            if (running) return true;
        }
        return false;
    }
}

public static class PathSafety
{
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static bool IsWithin(string path, string root)
    {
        try
        {
            string fullRoot = Normalize(root);
            string fullPath = Path.GetFullPath(path);
            string prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
            return !string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase) &&
                fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    public static bool IsRegularFileUnderRoot(string path, string root)
    {
        if (!IsWithin(path, root)) return false;
        try
        {
            string fullRoot = Normalize(root);
            string? current = Path.GetFullPath(path);
            while (current is not null)
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                if (string.Equals(Normalize(current), fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    // Check ancestors too: the allowed root may itself live below a junction.
                    current = Path.GetDirectoryName(current);
                    while (current is not null)
                    {
                        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                        current = Path.GetDirectoryName(current);
                    }
                    return File.Exists(path);
                }
                current = Path.GetDirectoryName(current);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return false;
    }
}

public sealed class CleanupScanner
{
    public const int MinimumAgeDays = 7;
    public const int MaximumCandidates = 20000;
    private readonly IReadOnlyList<CleanupRule> rules;
    public CleanupScanner(IReadOnlyList<CleanupRule> rules) => this.rules = rules;

    public ScanResult Scan(IReadOnlySet<string> categories, CancellationToken token, IProgress<string>? progress = null)
    {
        var files = new List<CleanupCandidate>();
        var notes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTime cutoff = DateTime.UtcNow.AddDays(-MinimumAgeDays);
        bool truncated = false;
        bool browserRunning = categories.Contains("browser") && CleanupRules.BrowserIsRunning();
        if (browserRunning) notes.Add("Chrome / Edge 正在运行，已跳过浏览器缓存。关闭浏览器后可重新扫描。");
        foreach (var rule in rules.Where(r => categories.Contains(r.CategoryId)))
        {
            token.ThrowIfCancellationRequested();
            if (rule.CategoryId == "browser" && browserRunning) continue;
            string root = PathSafety.Normalize(rule.Root);
            if (!Directory.Exists(root)) continue;
            progress?.Report("正在扫描 " + rule.Title + "…");
            var pending = new Stack<string>();
            pending.Push(root);
            int directories = 0;
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                string directory = pending.Pop();
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                    if (++directories > 100000) { truncated = true; break; }
                    foreach (string path in Directory.EnumerateFiles(directory, rule.Pattern))
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            var info = new FileInfo(path);
                            if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly | FileAttributes.System)) != 0 ||
                                info.LastWriteTimeUtc >= cutoff || !seen.Add(info.FullName) ||
                                !PathSafety.IsRegularFileUnderRoot(info.FullName, root)) continue;
                            files.Add(new CleanupCandidate
                            {
                                Path = info.FullName, Root = root, CategoryId = rule.CategoryId,
                                CategoryTitle = rule.Title, Length = info.Length, LastWriteUtc = info.LastWriteTimeUtc
                            });
                            if (files.Count >= MaximumCandidates) { truncated = true; break; }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    }
                    if (truncated) break;
                    foreach (string child in Directory.EnumerateDirectories(directory)) pending.Push(child);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (notes.Count < 8) notes.Add($"已跳过无法访问的目录：{directory}");
                }
            }
            if (truncated) break;
        }
        if (truncated) notes.Add("本次扫描已达到数量上限，可整理后再次扫描。");
        return new ScanResult(files.OrderByDescending(f => f.Length).ToList(), notes, truncated);
    }
}

public interface IRecycleBin
{
    void Send(string path);
}

public sealed class WindowsRecycleBin : IRecycleBin
{
    public void Send(string path) => Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
        Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
}

public sealed class CleanupExecutor
{
    private readonly IReadOnlyList<CleanupRule> rules;
    private readonly IRecycleBin recycleBin;
    public CleanupExecutor(IReadOnlyList<CleanupRule> rules, IRecycleBin recycleBin)
    {
        this.rules = rules;
        this.recycleBin = recycleBin;
    }

    public CleanupResult Execute(IReadOnlyList<CleanupCandidate> candidates, CancellationToken token,
        IProgress<string>? progress = null)
    {
        int succeeded = 0, skipped = 0;
        long bytes = 0;
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (token.IsCancellationRequested) break;
            try
            {
                if (!seen.Add(Path.GetFullPath(candidate.Path))) { skipped++; continue; }
                var rule = rules.FirstOrDefault(r => r.CategoryId == candidate.CategoryId &&
                    string.Equals(PathSafety.Normalize(r.Root), PathSafety.Normalize(candidate.Root), StringComparison.OrdinalIgnoreCase));
                if (rule is null || !PathSafety.IsRegularFileUnderRoot(candidate.Path, rule.Root))
                    throw new IOException("路径已变化或不在允许的清理目录内");
                if (candidate.CategoryId == "browser" && CleanupRules.BrowserIsRunning())
                    throw new IOException("浏览器正在运行，已跳过缓存文件");
                var file = new FileInfo(candidate.Path);
                if ((file.Attributes & (FileAttributes.ReadOnly | FileAttributes.System)) != 0 ||
                    file.Length != candidate.Length || file.LastWriteTimeUtc != candidate.LastWriteUtc ||
                    file.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-CleanupScanner.MinimumAgeDays))
                    throw new IOException("文件在扫描后发生变化，已跳过");
                if (!System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(rule.Pattern, file.Name, true))
                    throw new IOException("文件不符合当前清理规则");
                // An exclusively opened file is treated as in use. Close before invoking the shell.
                using (var handle = new FileStream(candidate.Path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                progress?.Report($"正在移入回收站 {succeeded + skipped + 1} / {candidates.Count}…");
                recycleBin.Send(candidate.Path);
                succeeded++;
                bytes += candidate.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or
                                       System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
            {
                skipped++;
                if (errors.Count < 100) errors.Add($"{candidate.Path}：{ex.Message}");
            }
        }
        int unprocessed = candidates.Count - succeeded - skipped;
        if (unprocessed > 0) { skipped += unprocessed; errors.Add($"操作已取消，另有 {unprocessed} 个文件未处理。"); }
        return new CleanupResult(succeeded, skipped, bytes, errors);
    }
}
