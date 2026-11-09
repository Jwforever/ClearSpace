using ClearSpace.Core;
using Microsoft.Win32;
using System.Diagnostics;
using System.Text;

static class ResidueChecks
{
    public static void Run(Action<string, Action> check, string temporary)
    {
        void Assert(bool condition, string message = "Residue assertion failed") { if (!condition) throw new Exception(message); }
        string name = "ClearSpace.Checks." + Guid.NewGuid().ToString("N");
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name);
        string registryPath = @"Software\" + name;
        var address = new RegistryAddress(RegistryHive.CurrentUser, RegistryView.Registry64, registryPath);
        var app = new InstalledApp { Id = "residue-test", Name = name, Publisher = "ClearSpace fixture publisher", InstallLocation = local };
        var registry = new WindowsResidueRegistry();
        string backups = Path.Combine(temporary, "residue-backups");
        string fixture = Path.Combine(local, "settings.json");
        Directory.CreateDirectory(local);
        File.WriteAllText(fixture, "isolated residue fixture");
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(registryPath))
            {
                key.SetValue("", "默认值 中文", RegistryValueKind.String);
                key.SetValue("escaped\\name\"", "line1\r\nline2\0embedded", RegistryValueKind.String);
                key.SetValue("expand", "%TEMP%\\example", RegistryValueKind.ExpandString);
                key.SetValue("multi", new[] { "one", "第二行" }, RegistryValueKind.MultiString);
                key.SetValue("binary", Enumerable.Range(0, 80).Select(i => (byte)i).ToArray(), RegistryValueKind.Binary);
                key.SetValue("dword", unchecked((int)0xf1234567), RegistryValueKind.DWord);
                key.SetValue("qword", 0x1020304050607080L, RegistryValueKind.QWord);
                key.SetValue("none", new byte[] { 1, 2 }, RegistryValueKind.None);
                using var child = key.CreateSubKey("nested"); child.SetValue("value", "child");
            }
            var installed = new List<InstalledApp>();
            var bin = new FixtureBin();
            var service = new ResidueService(registry, bin, () => installed, backups);
            var plan = service.Capture(app, [app]); plan.RemovalObserved = true;

            check("Residue capture records exact pre-uninstall locations and defaults to no selection", () =>
            {
                Assert(plan.Directories.Contains(local) && plan.RegistryKeys.Contains(address));
                var scan = service.Scan(plan, CancellationToken.None);
                Assert(scan.Candidates.Any(c => c.Path == fixture));
                Assert(scan.Candidates.Any(c => c.Kind == ResidueKind.RegistryKey));
                Assert(scan.Candidates.All(c => !c.Selected));
            });
            check("Residue policy rejects system, personal, shared and unrelated roots", () =>
            {
                Assert(!ResiduePolicy.IsSafeDirectory(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), plan.App));
                Assert(!ResiduePolicy.IsSafeDirectory(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), plan.App));
                Assert(!ResiduePolicy.IsSafeDirectory(Path.GetPathRoot(local)!, plan.App));
                Assert(!ResiduePolicy.IsSafeDirectory(Path.Combine(Path.GetDirectoryName(local)!, "UnrelatedApp"), plan.App));
                Assert(!ResiduePolicy.IsSafeDirectory(local + "Unrelated", plan.App));
                Assert(!ResiduePolicy.IsSafeRegistryKey(new(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Microsoft"), plan.App));
                Assert(!ResiduePolicy.IsSafeRegistryKey(new(RegistryHive.LocalMachine, RegistryView.Registry64, @"SYSTEM\CurrentControlSet"), plan.App));
                Assert(!ResiduePolicy.IsSafeRegistryKey(address with { Key = @"Software\" + name + @"\..\Other" }, plan.App));
            });
            check("Residue scan excludes folders used by a remaining hidden component", () =>
            {
                installed.Add(new() { Id = "other", Name = "Shared component", InstallLocation = Path.Combine(local, "plugin") });
                try { Assert(!service.Scan(plan, CancellationToken.None).Candidates.Any(c => c.Kind == ResidueKind.File)); }
                finally { installed.Clear(); }
            });
            check("Residue removal refuses software still installed or reinstalled", () =>
            {
                var scan = service.Scan(plan, CancellationToken.None);
                installed.Add(app);
                try
                {
                    Assert(service.Scan(plan, CancellationToken.None).Candidates.Count == 0);
                    var result = service.Execute(scan, scan.Candidates, CancellationToken.None);
                    Assert(result.FilesRecycled == 0 && result.RegistryKeysRemoved == 0 && registry.Read(address) is not null);
                }
                finally { installed.Clear(); }
            });
            check("Residue cleanup protects shared product variants and AppData folders", () =>
            {
                installed.Add(new() { Id = "remaining-edition", Name = name + " (x64)", Publisher = app.Publisher });
                try
                {
                    Assert(ResiduePolicy.AppStillInstalled(plan.App, installed));
                    Assert(ResiduePolicy.RegistryShared(address, installed));
                    Assert(service.Scan(plan, CancellationToken.None).Candidates.Count == 0);
                }
                finally { installed.Clear(); }
                var settings = new ResidueApp("settings", "Editor", name, "", "");
                string sharedData = Path.Combine(local, "Editor");
                installed.Add(new() { Id = "other", Name = settings.Name, Publisher = settings.Publisher });
                try { Assert(ResiduePolicy.DirectoryShared(sharedData, installed)); }
                finally { installed.Clear(); }
            });
            check("Residue cleanup skips changed files and changed registry keys", () =>
            {
                var scan = service.Scan(plan, CancellationToken.None);
                File.AppendAllText(fixture, " changed");
                using (var key = Registry.CurrentUser.OpenSubKey(registryPath, true)) key!.SetValue("changed", "new value");
                int before = bin.Paths.Count;
                var result = service.Execute(scan, scan.Candidates, CancellationToken.None);
                Assert(result.FilesRecycled == 0 && result.RegistryKeysRemoved == 0 && result.Skipped == scan.Candidates.Count);
                Assert(bin.Paths.Count == before && File.Exists(fixture) && registry.Read(address) is not null);
            });
            check("Residue cleanup refuses forged candidates and links", () =>
            {
                var scan = service.Scan(plan, CancellationToken.None);
                var forged = new ResidueCandidate { Kind = ResidueKind.File, Path = fixture, Root = local, Evidence = "forged" };
                Assert(service.Execute(scan, [forged], CancellationToken.None).Skipped == 1);
                string link = Path.Combine(local, "linked.txt");
                try { File.CreateSymbolicLink(link, fixture); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
                try { Assert(!service.Scan(plan, CancellationToken.None).Candidates.Any(c => c.Path == link)); }
                finally { File.Delete(link); }
            });
            check("Registry backup failure prevents registry deletion", () =>
            {
                string blocked = Path.Combine(temporary, "backup-is-file"); File.WriteAllText(blocked, "fixture");
                var blockedService = new ResidueService(registry, bin, () => installed, blocked);
                var scan = blockedService.Scan(plan, CancellationToken.None);
                var chosen = scan.Candidates.Where(c => c.Kind == ResidueKind.RegistryKey).ToList();
                var result = blockedService.Execute(scan, chosen, CancellationToken.None);
                Assert(chosen.Count > 0 && result.RegistryKeysRemoved == 0 && result.Skipped == chosen.Count && registry.Read(address) is not null);
            });
            check("Registry export, verified deletion and reg import restore all tested value types", () =>
            {
                var before = registry.Read(address)!;
                var scan = service.Scan(plan, CancellationToken.None);
                var chosen = scan.Candidates.Where(c => c.Kind == ResidueKind.RegistryKey).Take(1).ToList();
                var result = service.Execute(scan, chosen, CancellationToken.None);
                Assert(result.RegistryKeysRemoved == 1 && registry.Read(address) is null, string.Join(";", result.Details));
                string backup = Directory.EnumerateFiles(result.BackupDirectory, "*.reg").Single();
                Assert(File.ReadAllBytes(backup).Take(2).SequenceEqual(new byte[] { 0xff, 0xfe }));
                var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("import"); start.ArgumentList.Add(backup); start.ArgumentList.Add("/reg:64");
                using var process = Process.Start(start)!;
                string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                Assert(process.WaitForExit(10000) && process.ExitCode == 0, output);
                var after = registry.Read(address)!;
                Assert(after.ExportText == before.ExportText, "Restored registry contents differ from the backed-up values.");
            });
            check("Residue file cleanup sends only the checked snapshot to recycle bin", () =>
            {
                var scan = service.Scan(plan, CancellationToken.None);
                var selected = scan.Candidates.Where(c => c.Path == fixture).ToList();
                int before = bin.Paths.Count;
                var result = service.Execute(scan, selected, CancellationToken.None);
                Assert(result.FilesRecycled == 1 && result.RegistryKeysRemoved == 0 && bin.Paths.Count == before + 1 && File.Exists(fixture));
            });
            check("Residue cancellation changes no files or registry keys", () =>
            {
                var scan = service.Scan(plan, CancellationToken.None);
                using var cts = new CancellationTokenSource(); cts.Cancel();
                var result = service.Execute(scan, scan.Candidates, cts.Token);
                Assert(result.Skipped == scan.Candidates.Count && result.FilesRecycled == 0 && result.RegistryKeysRemoved == 0);
            });
            check("Residue plan store survives restart with full paths and registry view", () =>
            {
                var store = new ResiduePlanStore(Path.Combine(temporary, "residue-store")); store.Write([plan]);
                var restored = store.Read().Single();
                Assert(restored.App == plan.App && restored.RemovalObserved && restored.RegistryKeys.Contains(address) && restored.Directories.Contains(local));
            });
            check("Residue plan store ignores incomplete records without losing valid plans", () =>
            {
                string folder = Path.Combine(temporary, "incomplete-residue-store");
                Directory.CreateDirectory(folder);
                string valid = System.Text.Json.JsonSerializer.Serialize(plan);
                File.WriteAllText(Path.Combine(folder, "residue-plans.json"), "[null,{\"Id\":\"broken\",\"App\":null}," + valid + "]");
                Assert(new ResiduePlanStore(folder).Read().Single().Id == plan.Id);
            });
        }
        finally
        {
            // Resolve and check the exact two fixture roots before removing anything recursively.
            string expectedLocal = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name));
            if (Path.GetFullPath(local) == expectedLocal && Path.GetFileName(expectedLocal) == name && name.StartsWith("ClearSpace.Checks."))
            {
                if (Directory.Exists(local)) Directory.Delete(local, true);
            }
            if (registryPath == @"Software\" + name && name.StartsWith("ClearSpace.Checks."))
                Registry.CurrentUser.DeleteSubKeyTree(registryPath, false);
        }
    }

    private sealed class FixtureBin : IRecycleBin
    {
        public List<string> Paths { get; } = [];
        public void Send(string path) => Paths.Add(path);
    }
}
