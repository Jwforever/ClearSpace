using Microsoft.Win32;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;

namespace ClearSpace.Core;

public sealed record AppUsageRecord(string ExecutablePath, DateTime LastUsedUtc);

/// <summary>Reads the current user's launch history without changing Windows tracking settings.</summary>
public sealed class WindowsAppUsageReader
{
    private const string UserAssistKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";
    private IReadOnlyList<AppUsageRecord> cached = [];
    private DateTime readAtUtc;

    public IReadOnlyList<AppUsageRecord> Read()
    {
        // Uninstall polling reads the installation list every two seconds; shortcut resolution can be reused briefly.
        DateTime now = DateTime.UtcNow;
        if (now - readAtUtc < TimeSpan.FromSeconds(15)) return cached;
        var result = new List<AppUsageRecord>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(UserAssistKey);
            if (root is not null)
            foreach (string group in root.GetSubKeyNames())
            {
                try
                {
                    using var count = root.OpenSubKey(group + @"\Count");
                    if (count is null) continue;
                    foreach (string encoded in count.GetValueNames())
                    {
                        try
                        {
                            if (count.GetValue(encoded) is not byte[] bytes) continue;
                            DateTime? time = DecodeUserAssistTime(bytes, now);
                            if (time is null) continue;
                            string path = ExpandRecordedPath(Rot13(encoded));
                            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                                path = ResolveShortcut(path);
                            path = AppUsageMatcher.NormalizeExecutable(path);
                            if (path.Length > 0) result.Add(new(path, time.Value));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                            System.Security.SecurityException or ArgumentException or COMException) { }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        cached = result;
        readAtUtc = now;
        return cached;
    }

    internal static DateTime? DecodeUserAssistTime(byte[] data, DateTime nowUtc)
    {
        // Windows 7–11's documented-by-observation 72-byte record: FILETIME at offset 60.
        // Other layouts (including Windows 11's large auxiliary values) are deliberately ignored.
        if (data.Length != 72) return null;
        long value = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(60, 8));
        if (value <= 0) return null;
        try
        {
            DateTime time = DateTime.FromFileTimeUtc(value);
            return time >= new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc) && time <= nowUtc ? time : null;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    internal static string Rot13(string text) => new(text.Select(c => c switch
    {
        >= 'a' and <= 'z' => (char)('a' + (c - 'a' + 13) % 26),
        >= 'A' and <= 'Z' => (char)('A' + (c - 'A' + 13) % 26),
        _ => c
    }).ToArray());

    private static string ExpandRecordedPath(string path)
    {
        foreach (string prefix in new[] { "UEME_RUNPATH:", "UEME_RUNPIDL:" })
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) path = path[prefix.Length..];
        if (path.StartsWith('{') && path.Length >= 38 && Guid.TryParse(path[..38], out Guid folder))
        {
            nint pointer = 0;
            try
            {
                if (SHGetKnownFolderPath(ref folder, 0, 0, out pointer) == 0)
                    path = (Marshal.PtrToStringUni(pointer) ?? "") + path[38..];
            }
            finally { if (pointer != 0) Marshal.FreeCoTaskMem(pointer); }
        }
        return Environment.ExpandEnvironmentVariables(path);
    }

    private static string ResolveShortcut(string path)
    {
        if (!File.Exists(path)) return "";
        object? shellLink = null;
        try
        {
            shellLink = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), true)!);
            ((IPersistFile)shellLink!).Load(path, 0); // Read the saved target only; never resolve or execute the shortcut.
            var target = new StringBuilder(32768);
            ((IShellLinkPath)shellLink!).GetPath(target, target.Capacity, 0, 4 /* SLGP_RAWPATH */);
            return Environment.ExpandEnvironmentVariables(target.ToString());
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or IOException or UnauthorizedAccessException) { return ""; }
        finally { if (shellLink is not null && Marshal.IsComObject(shellLink)) Marshal.FinalReleaseComObject(shellLink); }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, nint token, out nint path);

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkPath
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, nint findData, uint flags);
    }
}

public static class AppUsageMatcher
{
    public static void Apply(IReadOnlyList<InstalledApp> apps, IEnumerable<AppUsageRecord> records)
    {
        var executables = new Dictionary<string, List<InstalledApp>>(StringComparer.OrdinalIgnoreCase);
        var roots = new Dictionary<string, List<InstalledApp>>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            app.LastUsedUtc = null;
            string iconExe = NormalizeExecutable(app.DisplayIcon);
            if (iconExe.Length > 0) Add(executables, iconExe, app);
            string installRoot = NormalizeDirectory(app.InstallLocation);
            if (installRoot.Length > 0) Add(roots, installRoot, app);
            if (iconExe.Length > 0)
            {
                string iconRoot = NormalizeDirectory(Path.GetDirectoryName(iconExe) ?? "");
                if (iconRoot.Length > 0) Add(roots, iconRoot, app);
            }
        }
        var orderedRoots = roots.OrderByDescending(p => p.Key.Length).ToArray();
        foreach (var record in records)
        {
            if (record.LastUsedUtc <= DateTime.UnixEpoch || record.LastUsedUtc > DateTime.UtcNow) continue;
            string executable = NormalizeExecutable(record.ExecutablePath);
            if (executable.Length == 0) continue;
            List<InstalledApp>? owners;
            if (!executables.TryGetValue(executable, out owners))
                owners = orderedRoots.FirstOrDefault(p => PathSafety.IsWithin(executable, p.Key)).Value;
            // Shared installation folders and duplicate identities cannot establish which package was used.
            if (owners is not { Count: 1 }) continue;
            var app = owners[0];
            if (!app.LastUsedUtc.HasValue || record.LastUsedUtc > app.LastUsedUtc.Value)
                app.LastUsedUtc = record.LastUsedUtc;
        }
    }

    private static void Add(Dictionary<string, List<InstalledApp>> index, string path, InstalledApp app)
    {
        if (!index.TryGetValue(path, out var owners)) index[path] = owners = [];
        if (!owners.Contains(app)) owners.Add(app);
    }

    internal static string NormalizeExecutable(string input)
    {
        string path = Environment.ExpandEnvironmentVariables(input.Trim());
        if (path.StartsWith('"'))
        {
            int end = path.IndexOf('"', 1);
            if (end < 0) return "";
            path = path[1..end];
        }
        else path = Regex.Replace(path, @",\s*-?\d+\s*$", ""); // DisplayIcon's resource index.
        try
        {
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !Path.IsPathFullyQualified(path)) return "";
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(name, @"^unins\d*$", RegexOptions.IgnoreCase) ||
                new[] { "setup", "install", "msiexec", "maintenancetool", "update" }.Contains(name, StringComparer.OrdinalIgnoreCase)) return "";
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return ""; }
    }

    private static string NormalizeDirectory(string input)
    {
        try
        {
            string path = Environment.ExpandEnvironmentVariables(input.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(path)) return "";
            path = PathSafety.Normalize(path);
            if (string.Equals(path, Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase)) return "";
            var shared = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86,
                Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
                Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolder.DesktopDirectory };
            if (shared.Select(Environment.GetFolderPath).Where(p => p.Length > 0)
                .Any(p => string.Equals(PathSafety.Normalize(p), path, StringComparison.OrdinalIgnoreCase))) return "";
            return path;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return ""; }
    }
}
