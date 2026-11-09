using Microsoft.Win32;

namespace ClearSpace.Core;

public static class ResiduePolicy
{
    private static readonly string[] SharedNames = ["microsoft", "windows", "google", "mozilla", "adobe", "intel", "amd",
        "nvidia", "apple", "commonfiles", "common", "shared", "programs", "packages", "temp", "cache", "software",
        "classes", "clients", "policies", "wow6432node", "registeredapplications", "uninstall", "clearspace",
        "windowsdefender", "windowsapps", "windowsnt", "windowsmail", "windowsmedia", "windowssecurity",
        "packagecache", "packagemanifests", "applicationdata", "microsoftshared"];

    internal static string Token(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static bool ProductName(string name) => name.Length is >= 2 and <= 100 && Token(name).Length >= 2 &&
        name.IndexOfAny(['\\', '/', ':', '\0', '\r', '\n', '*', '?', '<', '>', '|', '"']) < 0 &&
        !SharedNames.Contains(Token(name)) && name != "." && name != "..";

    public static IReadOnlyList<string> CandidateDirectories(ResidueApp app)
    {
        var candidates = new List<string> { app.InstallLocation };
        string icon = AppUsageMatcher.NormalizeExecutable(app.DisplayIcon);
        if (icon.Length > 0 && Token(Path.GetFileNameWithoutExtension(icon)) == Token(app.Name))
            candidates.Add(Path.GetDirectoryName(icon)!);
        candidates.AddRange(ProductDataDirectories(app));
        return candidates.Where(p => IsSafeDirectory(p, app)).Select(PathSafety.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> ProductDataDirectories(ResidueApp app)
    {
        if (!ProductName(app.Name)) yield break;
        foreach (var folder in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.CommonApplicationData })
        {
            string root = Environment.GetFolderPath(folder);
            if (root.Length == 0) continue;
            yield return Path.Combine(root, app.Name);
            if (ProductName(app.Publisher) && Token(app.Publisher) != Token(app.Name))
                yield return Path.Combine(root, app.Publisher, app.Name);
        }
    }

    public static bool IsSafeDirectory(string path, ResidueApp app)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\")) return false;
            path = PathSafety.Normalize(path);
            string leaf = Path.GetFileName(path);
            if (!ProductName(leaf) || (Token(leaf) == Token(app.Publisher) && Token(leaf) != Token(app.Name))) return false;
            string icon = AppUsageMatcher.NormalizeExecutable(app.DisplayIcon);
            string leafToken = Token(leaf), nameToken = Token(app.Name);
            bool sameProduct = leafToken == nameToken;
            bool iconConfirms = icon.Length > 0 && string.Equals(Path.GetDirectoryName(icon), path, StringComparison.OrdinalIgnoreCase) &&
                Token(Path.GetFileNameWithoutExtension(icon)) == leafToken;
            if (!sameProduct && !iconConfirms) return false;
            string? protectedBase = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (protectedBase.Length > 0 && SameOrUnder(path, Path.Combine(protectedBase, "Microsoft", "Windows"))) return false;
            string appRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (appRoot.Length > 0 && (SameOrUnder(path, Path.Combine(appRoot, "ClearSpace")) ||
                SameOrUnder(path, Path.Combine(appRoot, "Microsoft", "Windows")) || SameOrUnder(path, Path.Combine(appRoot, "Packages")))) return false;
            var allowed = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.CommonApplicationData };
            if (!allowed.Select(Environment.GetFolderPath).Where(p => p.Length > 0).Any(p => PathSafety.IsWithin(path, p))) return false;
            var shared = new[] { Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86 };
            if (shared.Select(Environment.GetFolderPath).Where(p => p.Length > 0).Any(p => SameOrUnder(path, p))) return false;
            // Personal document and download locations are never cleanup roots, including redirected folders.
            foreach (string personal in new[] { Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") })
                if (personal.Length > 0 && SameOrUnder(path, personal)) return false;
            if (Directory.Exists(path) && !NoLinks(path)) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    internal static bool NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    internal static bool SameOrUnder(string path, string root) =>
        string.Equals(PathSafety.Normalize(path), PathSafety.Normalize(root), StringComparison.OrdinalIgnoreCase) || PathSafety.IsWithin(path, root);

    public static bool DirectoryShared(string root, IReadOnlyList<InstalledApp> installed) => installed.Any(a =>
        new[] { a.InstallLocation, Path.GetDirectoryName(AppUsageMatcher.NormalizeExecutable(a.DisplayIcon)) ?? "" }
            .Concat(ProductDataDirectories(ResidueApp.From(a)))
            .Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p))
            .Any(p => SameOrUnder(p, root) || SameOrUnder(root, p)));

    // A remaining edition or architecture can still own the base product's settings.
    // Use broad name matching only to exclude shared locations, never to propose deletion.
    private static bool NamesMayShareProduct(string first, string second)
    {
        static bool HasNamePrefix(string text, string prefix) => prefix.Length >= 2 && text.Length > prefix.Length &&
            text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !char.IsLetterOrDigit(text[prefix.Length]);
        return Token(first) == Token(second) || HasNamePrefix(first, second) || HasNamePrefix(second, first);
    }

    public static IReadOnlyList<RegistryAddress> CandidateRegistryKeys(ResidueApp app)
    {
        if (!ProductName(app.Name)) return [];
        var keys = new List<string> { @"Software\" + app.Name };
        if (ProductName(app.Publisher) && Token(app.Publisher) != Token(app.Name))
            keys.Add(@"Software\" + app.Publisher + "\\" + app.Name);
        return (from hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine }
                from view in new[] { RegistryView.Registry64, RegistryView.Registry32 }
                from key in keys
                let address = new RegistryAddress(hive, view, key)
                where IsSafeRegistryKey(address, app)
                select address).ToList();
    }

    public static bool IsSafeRegistryKey(RegistryAddress address, ResidueApp app)
    {
        if (address.Hive is not (RegistryHive.CurrentUser or RegistryHive.LocalMachine) ||
            address.View is not (RegistryView.Registry32 or RegistryView.Registry64)) return false;
        string[] parts = address.Key.Split('\\');
        if (parts.Length is not (2 or 3) || !parts[0].Equals("Software", StringComparison.OrdinalIgnoreCase) ||
            !ProductName(parts[^1]) || Token(parts[^1]) != Token(app.Name)) return false;
        if (parts.Length == 3 && (!parts[1].Equals(app.Publisher, StringComparison.OrdinalIgnoreCase) ||
            !ProductName(parts[1]) || Token(parts[1]) == Token(app.Name))) return false;
        return true;
    }

    public static bool RegistryShared(RegistryAddress address, IReadOnlyList<InstalledApp> installed) =>
        installed.Any(a => NamesMayShareProduct(a.Name, address.Key.Split('\\')[^1]));

    public static bool AppStillInstalled(ResidueApp target, IReadOnlyList<InstalledApp> installed) =>
        installed.Any(a => a.Id == target.Id || (NamesMayShareProduct(a.Name, target.Name) &&
            string.Equals(a.Publisher, target.Publisher, StringComparison.OrdinalIgnoreCase)));

    public static bool IsInPlan(ResiduePlan plan, ResidueCandidate item) => item.Kind switch
    {
        ResidueKind.File => plan.Directories.Any(d => string.Equals(PathSafety.Normalize(d), PathSafety.Normalize(item.Root), StringComparison.OrdinalIgnoreCase)) &&
            IsSafeDirectory(item.Root, plan.App) && PathSafety.IsWithin(item.Path, item.Root),
        ResidueKind.RegistryKey => item.Address is not null && plan.RegistryKeys.Contains(item.Address) && IsSafeRegistryKey(item.Address, plan.App),
        _ => false
    };
}
