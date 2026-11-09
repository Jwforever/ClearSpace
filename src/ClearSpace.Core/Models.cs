using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClearSpace.Core;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

public sealed class InstalledApp : ObservableObject
{
    private bool selected;
    private DateTime? lastUsedUtc;
    public bool Selected { get => selected; set => Set(ref selected, value); }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Publisher { get; init; } = "未知发布者";
    public string Version { get; init; } = "—";
    public string InstallDate { get; init; } = "—";
    public string InstallLocation { get; init; } = "";
    public string DisplayIcon { get; init; } = "";
    public string UninstallString { get; init; } = "";
    public string RegistryPath { get; init; } = "";
    public bool MachineWide { get; init; }
    public long EstimatedBytes { get; init; }
    public bool HasSizeEstimate => EstimatedBytes > 0;
    public bool HasInstallDate => DateTime.TryParseExact(InstallDate, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out _);
    public DateTime? LastUsedUtc
    {
        get => lastUsedUtc;
        set
        {
            if (Set(ref lastUsedUtc, value)) { Notify(nameof(LastUsedText)); Notify(nameof(HasUsageRecord)); }
        }
    }
    public bool HasUsageRecord => LastUsedUtc.HasValue;
    public string LastUsedText => LastUsedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "无记录";
    public string SizeText => EstimatedBytes > 0 ? Format.Bytes(EstimatedBytes) : "未提供";
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();
    public string Scope => MachineWide ? "所有用户" : "当前用户";
    public bool CanUninstall => !string.IsNullOrWhiteSpace(UninstallString);
}

public sealed class CleanupCategory : ObservableObject
{
    private bool selected;
    private string summary = "尚未扫描";
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Glyph { get; init; }
    public bool Selected { get => selected; set => Set(ref selected, value); }
    public string Summary { get => summary; set => Set(ref summary, value); }
}

public sealed class CleanupCandidate : ObservableObject
{
    private bool selected = true;
    public bool Selected { get => selected; set => Set(ref selected, value); }
    public required string Path { get; init; }
    public required string Root { get; init; }
    public required string CategoryId { get; init; }
    public required string CategoryTitle { get; init; }
    public required long Length { get; init; }
    public required DateTime LastWriteUtc { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string SizeText => Format.Bytes(Length);
    public string ModifiedText => LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

public sealed record CleanupRule(string CategoryId, string Title, string Root, string Pattern = "*");
public sealed record ScanResult(IReadOnlyList<CleanupCandidate> Files, IReadOnlyList<string> Notes, bool Truncated);
public sealed record CleanupResult(int Succeeded, int Skipped, long Bytes, IReadOnlyList<string> Errors);
public sealed record OperationEntry(DateTime Time, string Kind, string Title, string Detail, string Status)
{
    public string TimeText => Time.ToLocalTime().ToString("MM-dd HH:mm");
}

public static class Format
{
    public static string Bytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = Math.Max(0, value);
        int index = 0;
        while (size >= 1024 && index < units.Length - 1) { size /= 1024; index++; }
        return $"{size:0.#} {units[index]}";
    }
}
