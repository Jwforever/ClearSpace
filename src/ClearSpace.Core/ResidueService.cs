using System.Security;
using System.Text;

namespace ClearSpace.Core;

public sealed class ResidueService
{
    private readonly IResidueRegistry registry;
    private readonly IRecycleBin recycleBin;
    private readonly Func<IReadOnlyList<InstalledApp>> readInstalled;
    private readonly string backupRoot;
    public ResidueService(IResidueRegistry registry, IRecycleBin recycleBin, Func<IReadOnlyList<InstalledApp>> readInstalled, string? backupRoot = null)
    {
        this.registry = registry;
        this.recycleBin = recycleBin;
        this.readInstalled = readInstalled;
        this.backupRoot = backupRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClearSpace", "RegistryBackups");
    }

    // Capture existing locations before handing control to the original uninstaller. Never search all registry keys by a substring.
    public ResiduePlan Capture(InstalledApp app, IReadOnlyList<InstalledApp> registrations)
    {
        var target = ResidueApp.From(app);
        var others = registrations.Where(a => a.Id != app.Id).ToList();
        var plan = new ResiduePlan { Id = Guid.NewGuid().ToString("N"), App = target };
        foreach (string path in ResiduePolicy.CandidateDirectories(target))
            if (Directory.Exists(path) && !ResiduePolicy.DirectoryShared(path, others)) plan.Directories.Add(path);
        foreach (var address in ResiduePolicy.CandidateRegistryKeys(target))
        {
            try
            {
                if (!ResiduePolicy.RegistryShared(address, others) && registry.Read(address) is not null) plan.RegistryKeys.Add(address);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { }
        }
        return plan;
    }

    public ResidueScan Scan(ResiduePlan plan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var apps = readInstalled();
        var found = new List<ResidueCandidate>();
        var notes = new List<string>();
        if (!plan.RemovalObserved || ResiduePolicy.AppStillInstalled(plan.App, apps))
            return new(plan, found, ["该软件的安装记录仍在，尚不能清理残留。请先完成卸载并刷新。"]);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in plan.Directories)
        {
            token.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;
            if (!ResiduePolicy.IsSafeDirectory(root, plan.App) || ResiduePolicy.DirectoryShared(root, apps))
            { notes.Add("已跳过共用或不符合清理范围的目录：" + root); continue; }
            var pending = new Stack<string>(); pending.Push(root);
            int directories = 0;
            while (pending.TryPop(out string? folder))
            {
                token.ThrowIfCancellationRequested();
                if (++directories > 10000 || found.Count >= 20000) { notes.Add("已达到文件扫描上限，请分批处理。"); break; }
                try
                {
                    if (!ResiduePolicy.NoLinks(folder)) continue;
                    foreach (string path in Directory.EnumerateFiles(folder))
                    {
                        token.ThrowIfCancellationRequested();
                        if (found.Count >= 20000) break;
                        var file = new FileInfo(path);
                        if ((file.Attributes & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.ReparsePoint)) != 0 ||
                            !PathSafety.IsRegularFileUnderRoot(path, root) || !seen.Add(path)) continue;
                        found.Add(new() { Kind = ResidueKind.File, Path = file.FullName, Root = root, Length = file.Length,
                            LastWriteUtc = file.LastWriteTimeUtc, Evidence = "卸载前记录的专属目录；可能含设置、存档或个人内容，请核对。" });
                    }
                    foreach (string child in Directory.EnumerateDirectories(folder)) pending.Push(child);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
                { if (notes.Count < 30) notes.Add("无法访问或已变化：" + folder); }
            }
        }
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var address in plan.RegistryKeys)
        {
            token.ThrowIfCancellationRequested();
            if (!ResiduePolicy.IsSafeRegistryKey(address, plan.App) || ResiduePolicy.RegistryShared(address, apps)) continue;
            try
            {
                var snapshot = registry.Read(address);
                if (snapshot is null) continue;
                // Only suppress a shared HKCU view when its actual contents and timestamps match.
                string identity = address.Hive == Microsoft.Win32.RegistryHive.CurrentUser ? address.FullName + snapshot.Fingerprint : address.DisplayName;
                if (!seenKeys.Add(identity)) continue;
                found.Add(new() { Kind = ResidueKind.RegistryKey, Path = address.DisplayName, Address = address,
                    Fingerprint = snapshot.Fingerprint, Evidence = $"卸载前存在的产品专属键；{snapshot.ValueCount} 个值；删除前导出 .reg 备份。" });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            { if (notes.Count < 30) notes.Add("无法读取注册表项：" + address.DisplayName); }
        }
        return new(plan, found, notes);
    }

    public ResidueResult Execute(ResidueScan scan, IReadOnlyList<ResidueCandidate> selected, CancellationToken token)
    {
        int files = 0, keys = 0, skipped = 0;
        var details = new List<string>();
        if (token.IsCancellationRequested) return new(0, 0, selected.Count, "", ["已取消，未执行残留清理。"]);
        var apps = readInstalled();
        if (!scan.Plan.RemovalObserved || ResiduePolicy.AppStillInstalled(scan.Plan.App, apps))
            return new(0, 0, selected.Count, "", ["软件仍在安装列表中或已重新安装，未执行残留清理。"]);
        string backupFolder = "";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in selected)
        {
            if (token.IsCancellationRequested) break;
            try
            {
                if (!scan.Candidates.Contains(item) || !ResiduePolicy.IsInPlan(scan.Plan, item))
                    throw new IOException("项目不属于本次残留预览。");
                if (!seen.Add(item.Kind + ":" + item.Path)) { skipped++; continue; }
                // Re-read registration state before each mutation, including hidden components and reinstalls.
                apps = readInstalled();
                if (ResiduePolicy.AppStillInstalled(scan.Plan.App, apps)) throw new IOException("检测到软件仍在或已重新安装。");
                if (item.Kind == ResidueKind.File)
                {
                    if (ResiduePolicy.DirectoryShared(item.Root, apps) || !PathSafety.IsRegularFileUnderRoot(item.Path, item.Root))
                        throw new IOException("目录已变化或正被其他软件使用。");
                    var file = new FileInfo(item.Path);
                    if (file.Length != item.Length || file.LastWriteTimeUtc != item.LastWriteUtc ||
                        (file.Attributes & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.ReparsePoint)) != 0)
                        throw new IOException("文件在扫描后发生变化。");
                    using (var handle = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                    recycleBin.Send(item.Path);
                    files++;
                    if (details.Count < 100) details.Add("已移入回收站：" + item.Path);
                }
                else
                {
                    var address = item.Address!;
                    if (ResiduePolicy.RegistryShared(address, apps)) throw new IOException("注册表项仍被其他安装记录引用。");
                    var snapshot = registry.Read(address);
                    if (snapshot is null || snapshot.Fingerprint != item.Fingerprint) throw new IOException("注册表项在扫描后发生变化。");
                    if (backupFolder.Length == 0)
                    {
                        Directory.CreateDirectory(backupRoot);
                        if (!ResiduePolicy.NoLinks(backupRoot)) throw new IOException("备份目录含链接，无法安全备份。");
                        string preparedFolder = Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(preparedFolder);
                        File.WriteAllText(Path.Combine(preparedFolder, "恢复说明.txt"),
                            "备份在每个注册表项删除前写入。恢复会重新写入对应键和值。\r\n" +
                            "64 位备份使用：reg.exe import \"文件名.reg\" /reg:64\r\n" +
                            "32 位备份使用：reg.exe import \"文件名.reg\" /reg:32\r\n" +
                            "HKLM 恢复通常需要管理员权限。请先关闭相关软件再恢复。\r\n", Encoding.UTF8);
                        backupFolder = preparedFolder;
                    }
                    if (!ResiduePolicy.NoLinks(backupFolder)) throw new IOException("备份目录发生变化。");
                    string backup = Path.Combine(backupFolder, $"{keys + skipped + 1:D3}-{address.Hive}-{address.View}.reg");
                    using (var stream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(snapshot.ExportText)).ToArray();
                        stream.Write(bytes); stream.Flush(flushToDisk: true);
                    }
                    registry.DeleteVerified(address, item.Fingerprint);
                    keys++;
                    if (details.Count < 100) details.Add("已备份并删除：" + address.DisplayName + "；备份：" + backup);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or
                System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or OperationCanceledException)
            {
                skipped++;
                if (details.Count < 100) details.Add("已跳过：" + item.Path + "；" + ex.Message);
            }
        }
        int unprocessed = selected.Count - files - keys - skipped;
        if (unprocessed > 0) { skipped += unprocessed; details.Add($"已取消，另有 {unprocessed} 项未处理。"); }
        return new(files, keys, skipped, backupFolder, details);
    }
}
