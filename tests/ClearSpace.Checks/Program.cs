using System.Text;
using ClearSpace.Core;

var sandbox = Path.Combine(Path.GetTempPath(), "ClearSpace.Checks." + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sandbox);
string canonicalSandbox = Path.GetFullPath(sandbox);
string canonicalTemp = Path.GetFullPath(Path.GetTempPath());
if (!canonicalSandbox.StartsWith(Path.Combine(canonicalTemp, "ClearSpace.Checks."), StringComparison.OrdinalIgnoreCase) ||
    string.IsNullOrWhiteSpace(Path.GetFileName(canonicalSandbox).Replace("ClearSpace.Checks.", "", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("Refusing to run outside the unique temporary test folder.");
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    try
    {
        string rechecked = Path.GetFullPath(sandbox);
        if (string.Equals(rechecked, canonicalSandbox, StringComparison.OrdinalIgnoreCase) && Directory.Exists(rechecked))
            Directory.Delete(rechecked, recursive: true);
    }
    catch { }
};
int passed = 0, failed = 0;
var lines = new List<string>();

void Check(string name, Action check)
{
    try { check(); passed++; lines.Add("PASS " + name); }
    catch (Exception ex) { failed++; lines.Add("FAIL " + name + ": " + ex.Message); }
}
void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Reject(Action action)
{
    try { action(); }
    catch (InvalidOperationException) { return; }
    throw new Exception("Expected command rejection");
}
string Folder(string name) { string path = Path.Combine(sandbox, name); Directory.CreateDirectory(path); return path; }
string Sample(string root, string name, int days = 10)
{
    string path = Path.Combine(root, name);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, "ClearSpace isolated test fixture");
    File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-days));
    return path;
}
ScanResult Scan(string root, string pattern = "*") => new CleanupScanner([new("test", "测试", root, pattern)])
    .Scan(new HashSet<string> { "test" }, CancellationToken.None);
CleanupResult Execute(string root, IReadOnlyList<CleanupCandidate> files, IRecycleBin bin) =>
    new CleanupExecutor([new("test", "测试", root)], bin).Execute(files, CancellationToken.None);
CleanupCandidate Candidate(string path, string root)
{
    var file = new FileInfo(path);
    return new() { Path = path, Root = root, CategoryId = "test", CategoryTitle = "测试", Length = file.Length, LastWriteUtc = file.LastWriteTimeUtc };
}

Check("Nested file stays inside allowed root", () =>
{
    string root = Folder("containment");
    Assert(PathSafety.IsWithin(Path.Combine(root, "nested", "a.tmp"), root));
});
Check("Sibling with the same prefix is rejected", () =>
{
    string root = Folder("prefix");
    Assert(!PathSafety.IsWithin(Path.Combine(root + "-outside", "a.tmp"), root));
});
Check("Parent traversal is rejected", () =>
{
    string root = Folder("traversal");
    Assert(!PathSafety.IsWithin(Path.Combine(root, "..", "escape.tmp"), root));
});
Check("Root itself is never a file candidate", () =>
{
    string root = Folder("root-itself");
    Assert(!PathSafety.IsWithin(root, root));
});
Check("Scanner excludes recent, read-only, and wrong-extension files", () =>
{
    string root = Folder("scanner");
    Sample(root, "old.tmp"); Sample(root, "recent.tmp", 1); Sample(root, "wrong.txt");
    string readOnly = Sample(root, "readonly.tmp");
    File.SetAttributes(readOnly, FileAttributes.ReadOnly);
    try
    {
        var scan = Scan(root, "*.tmp");
        Assert(scan.Files.Count == 1 && scan.Files[0].Name == "old.tmp");
    }
    finally { File.SetAttributes(readOnly, FileAttributes.Normal); }
});
Check("Scanner recurses through ordinary directories", () =>
{
    string root = Folder("nested"); Sample(root, Path.Combine("a", "b", "old.tmp"));
    Assert(Scan(root).Files.Count == 1);
});
Check("Unselected categories are not scanned", () =>
{
    string root = Folder("unselected"); Sample(root, "old.tmp");
    var scan = new CleanupScanner([new("test", "测试", root)]).Scan(new HashSet<string>(), CancellationToken.None);
    Assert(scan.Files.Count == 0);
});
Check("Cancellation stops a scan", () =>
{
    using var cts = new CancellationTokenSource(); cts.Cancel();
    try { new CleanupScanner([new("test", "测试", sandbox)]).Scan(new HashSet<string> { "test" }, cts.Token); }
    catch (OperationCanceledException) { return; }
    throw new Exception("Scan did not cancel");
});
Check("Executor passes only verified snapshots to recycle bin", () =>
{
    string root = Folder("execute"); string path = Sample(root, "old.tmp");
    var bin = new RecordingBin(); var result = Execute(root, Scan(root).Files, bin);
    Assert(result.Succeeded == 1 && result.Bytes > 0 && bin.Paths.SequenceEqual([path]));
    Assert(File.Exists(path), "Fake bin should not delete a file");
});
Check("Changed file is not recycled", () =>
{
    string root = Folder("changed"); string path = Sample(root, "old.tmp"); var files = Scan(root).Files;
    File.AppendAllText(path, "changed"); var bin = new RecordingBin();
    var result = Execute(root, files, bin);
    Assert(result.Skipped == 1 && bin.Paths.Count == 0);
});
Check("Disappeared file is skipped", () =>
{
    string root = Folder("disappeared"); string path = Sample(root, "old.tmp"); var files = Scan(root).Files;
    File.Move(path, path + ".moved"); var bin = new RecordingBin();
    Assert(Execute(root, files, bin).Skipped == 1 && bin.Paths.Count == 0);
});
Check("Outside-root candidate is never recycled", () =>
{
    string root = Folder("allowed"); string outside = Sample(Folder("outside"), "old.tmp");
    var bin = new RecordingBin(); var result = Execute(root, [Candidate(outside, root)], bin);
    Assert(result.Skipped == 1 && bin.Paths.Count == 0);
});
Check("Forged cleanup root is rejected", () =>
{
    string root = Folder("real-rule"); string other = Folder("forged-rule"); string path = Sample(other, "old.tmp");
    var bin = new RecordingBin(); Assert(Execute(root, [Candidate(path, other)], bin).Skipped == 1 && bin.Paths.Count == 0);
});
Check("Read-only file is rechecked before cleanup", () =>
{
    string root = Folder("changed-attributes"); string path = Sample(root, "old.tmp"); var files = Scan(root).Files;
    File.SetAttributes(path, FileAttributes.ReadOnly);
    try { var bin = new RecordingBin(); Assert(Execute(root, files, bin).Skipped == 1 && bin.Paths.Count == 0); }
    finally { File.SetAttributes(path, FileAttributes.Normal); }
});
Check("Linked file is never treated as a regular cleanable file", () =>
{
    string root = Folder("links"); string targetRoot = Folder("link-target");
    string target = Sample(targetRoot, "real.tmp");
    string linked = Path.Combine(root, "linked.tmp");
    try { File.CreateSymbolicLink(linked, target); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
    { return; }
    Assert(!PathSafety.IsRegularFileUnderRoot(linked, root));
    Assert(Scan(root).Files.Count == 0);
});
Check("Files inside a junction-linked directory are never scanned", () =>
{
    string root = Folder("junction-source"); string target = Folder("junction-target");
    Sample(target, "old.tmp");
    string link = Path.Combine(root, "linked-directory");
    try { Directory.CreateSymbolicLink(link, target); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
    { return; }
    Assert(Scan(root).Files.Count == 0);
});
Check("Exclusive lock is respected", () =>
{
    string root = Folder("locked"); string path = Sample(root, "old.tmp"); var files = Scan(root).Files;
    using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
    var bin = new RecordingBin(); Assert(Execute(root, files, bin).Skipped == 1 && bin.Paths.Count == 0);
});
Check("Duplicate candidates are recycled once", () =>
{
    string root = Folder("duplicates"); Sample(root, "old.tmp"); var candidate = Scan(root).Files.Single();
    var bin = new RecordingBin(); var result = Execute(root, [candidate, candidate], bin);
    Assert(result.Succeeded == 1 && result.Skipped == 1 && bin.Paths.Count == 1);
});
Check("Recycle errors preserve an unsuccessful result", () =>
{
    string root = Folder("bin-error"); Sample(root, "old.tmp");
    var result = Execute(root, Scan(root).Files, new FailingBin());
    Assert(result.Succeeded == 0 && result.Skipped == 1 && result.Errors.Count == 1);
});
Check("Cancellation before cleanup leaves every file unprocessed", () =>
{
    string root = Folder("cleanup-cancel"); Sample(root, "old.tmp"); var bin = new RecordingBin();
    using var cts = new CancellationTokenSource(); cts.Cancel();
    var result = new CleanupExecutor([new("test", "测试", root)], bin).Execute(Scan(root).Files, cts.Token);
    Assert(result.Succeeded == 0 && result.Skipped == 1 && bin.Paths.Count == 0);
});
Check("Quoted executable preserves arguments", () =>
{
    var command = UninstallCommand.Parse("\"C:\\Program Files\\Example\\uninstall.exe\" /S /mode=remove", @"C:\Windows");
    Assert(command.Executable == @"C:\Program Files\Example\uninstall.exe" && command.Arguments == "/S /mode=remove");
});
Check("Unquoted absolute executable supports spaces", () =>
{
    var command = UninstallCommand.Parse(@"C:\Program Files\Example\uninstall.exe /remove", @"C:\Windows");
    Assert(command.Executable == @"C:\Program Files\Example\uninstall.exe" && command.Arguments == "/remove");
});
Check("MSI maintenance command is changed to uninstall", () =>
{
    var command = UninstallCommand.Parse("MsiExec.exe /I{12345678-1234-1234-1234-123456789012}", @"C:\Windows");
    Assert(command.Executable == @"C:\Windows\System32\msiexec.exe" && command.Arguments.StartsWith("/X{"));
});
Check("Already-uninstalling MSI command is preserved", () =>
{
    var command = UninstallCommand.Parse("MsiExec.exe /X {12345678-1234-1234-1234-123456789012}", @"C:\Windows");
    Assert(command.Arguments.StartsWith("/X {"));
});
Check("Quoted MSI executable is normalized to Windows system executable", () =>
{
    var command = UninstallCommand.Parse("\"C:\\Windows\\SysWOW64\\msiexec.exe\" /I {12345678-1234-1234-1234-123456789012} /qn", @"C:\Windows");
    Assert(command.Executable == @"C:\Windows\System32\msiexec.exe" && command.Arguments.Contains("/X {") && command.Arguments.EndsWith("/qn"));
});
Check("Shell, relative, and malformed commands are rejected", () =>
{
    Reject(() => UninstallCommand.Parse(@"C:\Windows\System32\cmd.exe /c erase *", @"C:\Windows"));
    Reject(() => UninstallCommand.Parse("uninstall.exe /S", @"C:\Windows"));
    Reject(() => UninstallCommand.Parse("\"C:\\broken.exe", @"C:\Windows"));
    Reject(() => UninstallCommand.Parse("", @"C:\Windows"));
});
Check("History round-trips Chinese, file paths and newest-first order", () =>
{
    var store = new HistoryStore(Folder("history"));
    store.Add(new(DateTime.UtcNow, "清理", "第一次", @"C:\用户\临时\文件.tmp", "完成"));
    store.Add(new(DateTime.UtcNow, "卸载", "第二次", "明细\n换行", "已启动"));
    var entries = store.Read();
    Assert(entries.Count == 2 && entries[0].Title == "第二次" && entries[1].Detail.Contains("用户"));
});
Check("Corrupted history does not break startup", () =>
{
    var store = new HistoryStore(Folder("corrupt-history")); File.WriteAllText(store.FilePath, "not json");
    Assert(store.Read().Count == 0);
});
Check("History is capped at 500 entries", () =>
{
    var store = new HistoryStore(Folder("history-cap"));
    for (int i = 0; i < 510; i++) store.Add(new(DateTime.UnixEpoch.AddMinutes(i), "测试", i.ToString(), "", "完成"));
    var entries = store.Read();
    Assert(entries.Count == 500 && entries[0].Title == "509" && entries[^1].Title == "10");
});
Check("Cleanup history limits logged file paths and skipped reasons", () =>
{
    string root = Folder("bounded-history-detail");
    var candidates = Enumerable.Range(0, HistoryDetails.MaximumListedPaths + 5).Select(i => new CleanupCandidate
    {
        Path = Path.Combine(root, $"file-{i}.tmp"), Root = root,
        CategoryId = "test", CategoryTitle = "测试", Length = 1, LastWriteUtc = DateTime.UtcNow
    }).ToList();
    var errors = Enumerable.Range(0, HistoryDetails.MaximumListedPaths + 4)
        .Select(i => $"{candidates[i].Path}：fixture error {i}").ToList();
    string detail = HistoryDetails.Cleanup(candidates, new CleanupResult(0, candidates.Count, 0, errors));
    Assert(detail.Contains("另有 5 个已选择文件未写入"));
    Assert(detail.Contains("另有 4 项原因未写入"));
    Assert(!detail.Contains("file-104.tmp"));
    Assert(Encoding.UTF8.GetByteCount(detail) < 60000, "Operation log detail should stay bounded.");
});
Check("Installed apps can be enumerated without launching an uninstaller", () =>
{
    var apps = new InstalledAppsService().Read();
    Assert(apps.Count > 0 && apps.All(a => !string.IsNullOrWhiteSpace(a.Name)));
});
Check("Real recycle-bin integration uses only a newly-created fixture", () =>
{
    string root = Folder("recycle-integration"); string path = Sample(root, "clearspace-recycle-check.tmp");
    var result = Execute(root, Scan(root).Files, new WindowsRecycleBin());
    Assert(result.Succeeded == 1 && !File.Exists(path), string.Join("; ", result.Errors));
});

foreach (string line in lines) Console.WriteLine(line);
Console.WriteLine($"\n{passed} passed, {failed} failed. Isolated fixtures: {sandbox}");
if (args.Length > 0) File.WriteAllText(args[0], string.Join(Environment.NewLine, lines) + $"\n{passed} passed, {failed} failed\nFixtures: {sandbox}");
Environment.ExitCode = failed > 0 ? 1 : 0;

sealed class RecordingBin : IRecycleBin
{
    public List<string> Paths { get; } = [];
    public void Send(string path) => Paths.Add(path);
}
sealed class FailingBin : IRecycleBin
{
    public void Send(string path) => throw new IOException("Simulated recycle-bin error");
}
