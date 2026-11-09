using ClearSpace.Core;

static class LargeFileChecks
{
    public static void Run(Action<string, Action> check, string sandbox)
    {
        void Assert(bool value, string message = "Large file assertion failed") { if (!value) throw new Exception(message); }
        string root = Path.Combine(sandbox, "large-files"); Directory.CreateDirectory(root);
        string Folder(string name) { string path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
        string FileOfSize(string folder, string name, int size)
        {
            string path = Path.Combine(folder, name);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write); stream.SetLength(size);
            return path;
        }
        LargeFilePolicy Policy(params string[] protectedRoots) => new(protectedRoots);
        LargeFileService Service(IRecycleBin? bin = null, LargeFilePolicy? policy = null, int maximum = 20000) => new(policy ?? Policy(), bin ?? new RecordingBin(), maximum);
        check("Drive root containment supports ordinary files without crossing volume boundaries", () =>
        {
            string volume = Path.GetPathRoot(root)!;
            Assert(PathSafety.IsWithin(root, volume));
            Assert(!PathSafety.IsWithin(volume, volume));
            Assert(!PathSafety.IsWithin(@"Z:\elsewhere\file.bin", @"C:\"));
            Assert(!PathSafety.IsWithin(root + "-other\\file.bin", root));
            Assert(Policy().ValidateRoot(volume) == volume, "A fixed drive root can have the System attribute and must remain scannable.");
        });
        check("Large-file scan honors inclusive size threshold, recent files, nested files and no default selection", () =>
        {
            string folder = Folder("threshold");
            FileOfSize(folder, "small.mp4", 1023);
            string boundary = FileOfSize(folder, "boundary.zip", 1024);
            string nested = FileOfSize(Folder("threshold\\nested"), "recent.iso", 2048);
            var scan = Service().Scan(folder, 1024, CancellationToken.None);
            Assert(scan.Files.Select(f => f.Path).SequenceEqual([nested, boundary]));
            Assert(scan.Files.All(f => !f.Selected) && scan.MatchingFiles == 2 && scan.InspectedFiles == 3);
        });
        check("Large-file scan skips protected, read-only, system and offline files", () =>
        {
            string folder = Folder("protection");
            string protectedFolder = Folder("protection\\Protected"); FileOfSize(protectedFolder, "private.bin", 2048);
            string readOnly = FileOfSize(folder, "readonly.bin", 2048), system = FileOfSize(folder, "system.bin", 2048), offline = FileOfSize(folder, "offline.bin", 2048);
            string allowed = FileOfSize(folder, "allowed.bin", 2048);
            try
            {
                File.SetAttributes(readOnly, FileAttributes.ReadOnly);
                File.SetAttributes(system, FileAttributes.System);
                File.SetAttributes(offline, FileAttributes.Offline);
                var scan = Service(policy: Policy(protectedFolder)).Scan(folder, 1024, CancellationToken.None);
                Assert(scan.Files.Select(f => f.Path).SequenceEqual([allowed]) && scan.SkippedEntries >= 3);
            }
            finally
            {
                foreach (string file in new[] { readOnly, system, offline }) File.SetAttributes(file, FileAttributes.Normal);
            }
        });
        check("Large-file result cap retains the largest files even if smaller matches are found first", () =>
        {
            string folder = Folder("top-results");
            for (int i = 1; i <= 12; i++) FileOfSize(folder, $"{i:D2}.bin", i * 1024);
            var scan = Service(maximum: 3).Scan(folder, 1024, CancellationToken.None);
            Assert(scan.Files.Select(f => f.Length).SequenceEqual([12288L, 11264L, 10240L]));
            Assert(scan.Truncated && scan.MatchingFiles == 12 && scan.Notes.Count > 0);
        });
        check("Large-file scan cancellation reports partial state without cleanup", () =>
        {
            string folder = Folder("cancel-scan"); FileOfSize(folder, "fixture.bin", 2048);
            using var cts = new CancellationTokenSource();
            var scan = Service().Scan(folder, 1024, cts.Token, new ImmediateProgress(_ => cts.Cancel()));
            Assert(scan.Canceled && scan.Files.Count == 0 && File.Exists(Path.Combine(folder, "fixture.bin")));
            try { Service().Scan(folder, 1024, cts.Token); }
            catch (OperationCanceledException) { return; }
            throw new Exception("Pre-canceled scan was not rejected.");
        });
        check("Large-file cleanup uses only requested preview objects and recycles a duplicate once", () =>
        {
            string folder = Folder("selection"); FileOfSize(folder, "one.bin", 2048); FileOfSize(folder, "two.bin", 3072);
            var bin = new RecordingBin(); var service = Service(bin); var scan = service.Scan(folder, 1024, CancellationToken.None);
            var chosen = scan.Files[0];
            var result = service.Execute(scan, [chosen, chosen], CancellationToken.None);
            Assert(result.Succeeded == 1 && result.Skipped == 1 && bin.Paths.SequenceEqual([chosen.Path]));
            var forged = new LargeFileCandidate { Path = chosen.Path, Length = chosen.Length, LastWriteUtc = chosen.LastWriteUtc };
            Assert(service.Execute(scan, [forged], CancellationToken.None).Skipped == 1 && bin.Paths.Count == 1);
        });
        check("Large-file cleanup rejects changed, missing, read-only and exclusively locked snapshots", () =>
        {
            string folder = Folder("changed");
            string changed = FileOfSize(folder, "changed.bin", 2048), missing = FileOfSize(folder, "missing.bin", 2048),
                locked = FileOfSize(folder, "locked.bin", 2048), readOnly = FileOfSize(folder, "readonly.bin", 2048);
            var bin = new RecordingBin(); var service = Service(bin); var scan = service.Scan(folder, 1024, CancellationToken.None);
            File.AppendAllText(changed, "changed"); File.Delete(missing); File.SetAttributes(readOnly, FileAttributes.ReadOnly);
            try
            {
                using var handle = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
                var result = service.Execute(scan, scan.Files, CancellationToken.None);
                Assert(result.Succeeded == 0 && result.Skipped == 4 && bin.Paths.Count == 0);
            }
            finally { File.SetAttributes(readOnly, FileAttributes.Normal); }
        });
        check("Large-file cleanup rechecks newly protected paths and scope boundaries", () =>
        {
            string folder = Folder("scope"); FileOfSize(folder, "fixture.bin", 2048);
            var scan = Service().Scan(folder, 1024, CancellationToken.None);
            var bin = new RecordingBin();
            Assert(Service(bin, Policy(folder)).Execute(scan, scan.Files, CancellationToken.None).Skipped == 1);
            var wrongRoot = scan with { Root = Folder("scope-other") };
            Assert(Service(bin).Execute(wrongRoot, scan.Files, CancellationToken.None).Skipped == 1 && bin.Paths.Count == 0);
        });
        check("Large-file links and linked roots are excluded from scan and execution", () =>
        {
            string folder = Folder("links"), outside = Folder("links-outside"); string file = FileOfSize(outside, "fixture.bin", 2048);
            string link = Path.Combine(folder, "file.bin"), directoryLink = Path.Combine(folder, "linked-dir");
            try { File.CreateSymbolicLink(link, file); Directory.CreateSymbolicLink(directoryLink, outside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return; }
            try
            {
                Assert(Service().Scan(folder, 1024, CancellationToken.None).Files.Count == 0);
                try { Policy().ValidateRoot(directoryLink); }
                catch (IOException) { return; }
                throw new Exception("Linked scan root was accepted.");
            }
            finally { File.Delete(link); Directory.Delete(directoryLink); }
        });
        check("Large-file cleanup cancellation and recycle failure retain unsuccessful results", () =>
        {
            string folder = Folder("cancel-clean"); string path = FileOfSize(folder, "fixture.bin", 2048);
            var scan = Service().Scan(folder, 1024, CancellationToken.None);
            var bin = new RecordingBin(); using var cts = new CancellationTokenSource(); cts.Cancel();
            Assert(Service(bin).Execute(scan, scan.Files, cts.Token) is { Succeeded: 0, Skipped: 1 } && bin.Paths.Count == 0);
            var failed = Service(new FailingBin()).Execute(scan, scan.Files, CancellationToken.None);
            Assert(failed.Succeeded == 0 && failed.Skipped == 1 && failed.Errors.Count == 1 && File.Exists(path));
        });
        check("Large-file default policy protects Windows and application folders while allowing personal folders", () =>
        {
            var policy = new LargeFilePolicy();
            foreach (var special in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.CommonApplicationData })
                Assert(policy.IsProtected(Path.Combine(Environment.GetFolderPath(special), "fixture.bin")));
            string volume = Path.GetPathRoot(root)!;
            Assert(policy.IsProtected(Path.Combine(volume, "$Recycle.Bin", "fixture.bin")));
            Assert(!policy.IsProtected(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "movie.mp4")));
        });
        check("Large-file classification covers media, archives, installers, disk images and other files", () =>
        {
            Assert(LargeFileTypes.Classify("movie.MKV") == 1 && LargeFileTypes.Classify("archive.7z") == 2 &&
                LargeFileTypes.Classify("setup.msi") == 3 && LargeFileTypes.Classify("backup.vhdx") == 4 && LargeFileTypes.Classify("backup.bin") == 5);
        });
    }

    private sealed class ImmediateProgress(Action<LargeFileProgress> action) : IProgress<LargeFileProgress>
    {
        public void Report(LargeFileProgress value) => action(value);
    }
}
