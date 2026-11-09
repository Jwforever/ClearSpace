using Microsoft.Win32;

namespace ClearSpace.Core;

public sealed record ResidueApp(string Id, string Name, string Publisher, string InstallLocation, string DisplayIcon)
{
    public static ResidueApp From(InstalledApp app) => new(app.Id, app.Name, app.Publisher, app.InstallLocation, app.DisplayIcon);
}

public sealed record RegistryAddress(RegistryHive Hive, RegistryView View, string Key)
{
    public string FullName => (Hive == RegistryHive.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE") + "\\" + Key;
    public string DisplayName => FullName + (View == RegistryView.Registry32 ? "（32 位）" : "（64 位）");
}

public sealed class ResiduePlan : ObservableObject
{
    private bool removalObserved;
    public required string Id { get; init; }
    public required ResidueApp App { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public List<string> Directories { get; init; } = [];
    public List<RegistryAddress> RegistryKeys { get; init; } = [];
    public bool RemovalObserved { get => removalObserved; set { if (Set(ref removalObserved, value)) Notify(nameof(DisplayName)); } }
    // Old manually managed plans stay inactive; only new uninstalls opt in to an automatic prompt.
    public bool PromptPending { get; set; }
    public string DisplayName => App.Name + " · " + CreatedUtc.ToLocalTime().ToString("MM-dd HH:mm") +
        (RemovalObserved ? " · 已卸载" : " · 等待卸载完成");
}

public enum ResidueKind { File, RegistryKey }

public sealed class ResidueCandidate
{
    public required ResidueKind Kind { get; init; }
    public required string Path { get; init; }
    public required string Evidence { get; init; }
    public string Root { get; init; } = "";
    public long Length { get; init; }
    public DateTime LastWriteUtc { get; init; }
    public RegistryAddress? Address { get; init; }
    public string Fingerprint { get; init; } = "";
    public string TypeText => Kind == ResidueKind.File ? "残留文件" : "注册表项";
    public string SizeText => Kind == ResidueKind.File ? Format.Bytes(Length) : "先备份再删除";
}

public sealed record ResidueScan(ResiduePlan Plan, IReadOnlyList<ResidueCandidate> Candidates, IReadOnlyList<string> Notes, bool Blocked = false);
public sealed record ResidueResult(int FilesRecycled, int RegistryKeysRemoved, int Skipped, string BackupDirectory, IReadOnlyList<string> Details);

public sealed class ResiduePlanStore
{
    public string DirectoryPath { get; }
    public ResiduePlanStore(string? folder = null) => DirectoryPath = folder ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClearSpace");
    private string FilePath => System.IO.Path.Combine(DirectoryPath, "residue-plans.json");
    public IReadOnlyList<ResiduePlan> Read()
    {
        try
        {
            return File.Exists(FilePath) ? System.Text.Json.JsonSerializer.Deserialize<List<ResiduePlan>>(
                File.ReadAllText(FilePath))?.Where(p => p is { Id: not null, App: { Id: not null, Name: not null,
                    Publisher: not null, InstallLocation: not null, DisplayIcon: not null }, Directories: not null, RegistryKeys: not null } &&
                    p.Directories.All(d => d is not null) && p.RegistryKeys.All(k => k is { Key: not null }))
                    .Take(100).ToList() ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return []; }
    }
    public void Write(IEnumerable<ResiduePlan> plans)
    {
        Directory.CreateDirectory(DirectoryPath);
        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(plans.OrderByDescending(p => p.CreatedUtc).Take(100)));
        File.Move(temporary, FilePath, true);
    }
}
