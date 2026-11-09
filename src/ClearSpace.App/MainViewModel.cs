using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using ClearSpace.Core;

namespace ClearSpace.App;

public sealed class MainViewModel : ObservableObject
{
    public ObservableCollection<InstalledApp> Apps { get; } = [];
    public ICollectionView AppsView { get; }
    public ObservableCollection<CleanupCandidate> Candidates { get; } = [];
    public ObservableCollection<OperationEntry> History { get; } = [];
    public ObservableCollection<CleanupCategory> Categories { get; } =
    [
        new() { Id = "temp", Title = "用户临时文件", Description = "应用运行留下的旧临时文件", Glyph = "", Selected = true },
        new() { Id = "crashes", Title = "崩溃转储", Description = "故障诊断用的旧 .dmp 转储文件", Glyph = "", Selected = true },
        new() { Id = "browser", Title = "浏览器缓存", Description = "Chrome / Edge 缓存，需先关闭浏览器", Glyph = "", Selected = false }
    ];

    private bool busy;
    private string query = "";
    private string status = "就绪 · 所有数据保存在本机";
    private string freeSpace = "—";
    private double diskUsedPercent;
    private string pageTitle = "软件管理";
    private string pageDescription = "看清每一款应用，让卸载更从容。";
    private bool hasScanned;
    private bool appsLoaded;
    private int appSortIndex;
    private int appSortDirectionIndex;
    private string cleanupSummary = "扫描只读取文件信息。选中具体文件后才能执行清理。";

    public MainViewModel()
    {
        AppsView = CollectionViewSource.GetDefaultView(Apps);
        AppsView.Culture = CultureInfo.CurrentCulture;
        AppsView.Filter = o => o is InstalledApp app && (string.IsNullOrWhiteSpace(Query) ||
            app.Name.Contains(Query, StringComparison.CurrentCultureIgnoreCase) ||
            app.Publisher.Contains(Query, StringComparison.CurrentCultureIgnoreCase));
        AppsView.SortDescriptions.Add(new SortDescription(nameof(InstalledApp.Name), ListSortDirection.Ascending));
        RefreshDisk();
    }

    public bool Busy
    {
        get => busy;
        set { if (Set(ref busy, value)) { Notify(nameof(CanAct)); Notify(nameof(CanClean)); Notify(nameof(CanUninstallSelected)); } }
    }
    public bool CanAct => !Busy;
    public string Query
    {
        get => query;
        set { if (Set(ref query, value)) { AppsView.Refresh(); Notify(nameof(AppListSummary)); } }
    }
    public int AppSortIndex
    {
        get => appSortIndex;
        set
        {
            if (!Set(ref appSortIndex, value)) return;
            ApplyAppSort();
        }
    }
    public int AppSortDirectionIndex
    {
        get => appSortDirectionIndex;
        set { if (Set(ref appSortDirectionIndex, value)) ApplyAppSort(); }
    }
    private void ApplyAppSort()
    {
        var direction = AppSortDirectionIndex == 0 ? ListSortDirection.Ascending : ListSortDirection.Descending;
        using (AppsView.DeferRefresh())
        {
            AppsView.SortDescriptions.Clear();
            string? known = AppSortIndex switch
            {
                1 => nameof(InstalledApp.HasSizeEstimate), 2 => nameof(InstalledApp.HasInstallDate),
                3 => nameof(InstalledApp.HasUsageRecord), _ => null
            };
            if (known is not null) AppsView.SortDescriptions.Add(new(known, ListSortDirection.Descending));
            AppsView.SortDescriptions.Add(new(AppSortIndex switch
            {
                1 => nameof(InstalledApp.EstimatedBytes), 2 => nameof(InstalledApp.InstallDate),
                3 => nameof(InstalledApp.LastUsedUtc), 4 => nameof(InstalledApp.Publisher), _ => nameof(InstalledApp.Name)
            }, direction));
            if (AppSortIndex != 0) AppsView.SortDescriptions.Add(new(nameof(InstalledApp.Name), ListSortDirection.Ascending));
        }
    }
    public string Status { get => status; set => Set(ref status, value); }
    public string FreeSpace { get => freeSpace; private set => Set(ref freeSpace, value); }
    public double DiskUsedPercent { get => diskUsedPercent; private set => Set(ref diskUsedPercent, value); }
    public string PageTitle { get => pageTitle; set => Set(ref pageTitle, value); }
    public string PageDescription { get => pageDescription; set => Set(ref pageDescription, value); }
    public string CleanupSummary { get => cleanupSummary; set => Set(ref cleanupSummary, value); }
    public int AppCount => Apps.Count;
    public string AppsEmptyText => appsLoaded ? "没有找到匹配的软件" : "正在读取本机安装记录…";
    public string AppListSummary => $"显示 {Apps.Count(IsVisible)} / {Apps.Count} 个应用 · 已选择 {Apps.Count(a => a.Selected)} 个";
    public string UninstallLabel => Apps.Any(a => a.Selected) ? $"卸载选中 ({Apps.Count(a => a.Selected)})" : "卸载选中";
    public bool CanUninstallSelected => !Busy && Apps.Any(a => a.Selected && a.CanUninstall);
    public string ScannedSize => hasScanned ? Format.Bytes(Candidates.Sum(f => f.Length)) : "尚未扫描";
    public string ScannedHint => hasScanned ? $"共 {Candidates.Count:N0} 个文件 · 清理前可逐项选择" : "前往空间清理，开始一次扫描";
    public string ScanLabel => hasScanned ? "重新扫描" : "开始扫描";
    public string CleanLabel => $"移入回收站 · {Format.Bytes(Candidates.Where(f => f.Selected).Sum(f => f.Length))}";
    public bool CanClean => !Busy && Candidates.Any(f => f.Selected);
    public string CleanupEmptyText => hasScanned ? "本次没有可整理的文件" : "先看清，再整理";

    public void SetApps(IReadOnlyList<InstalledApp> apps)
    {
        var current = Apps.ToDictionary(a => a.Id);
        var incomingIds = apps.Select(a => a.Id).ToHashSet();
        // Keep collection change notifications live: a bound DataGrid reads the current row during each update.
        foreach (var app in Apps.Where(a => !incomingIds.Contains(a.Id)).ToList())
        {
            app.PropertyChanged -= AppChanged;
            Apps.Remove(app);
        }
        foreach (var app in apps)
        {
            if (current.TryGetValue(app.Id, out var prior))
            {
                // Keep selection and row instances during background polling; update changed installation metadata.
                if (prior.Name == app.Name && prior.Version == app.Version && prior.Publisher == app.Publisher &&
                    prior.InstallDate == app.InstallDate && prior.EstimatedBytes == app.EstimatedBytes &&
                    prior.InstallLocation == app.InstallLocation && prior.DisplayIcon == app.DisplayIcon &&
                    prior.UninstallString == app.UninstallString && prior.RegistryPath == app.RegistryPath &&
                    prior.MachineWide == app.MachineWide)
                {
                    prior.LastUsedUtc = app.LastUsedUtc;
                    continue;
                }
                app.Selected = prior.Selected;
                prior.PropertyChanged -= AppChanged;
                int index = Apps.IndexOf(prior);
                app.PropertyChanged += AppChanged;
                Apps[index] = app;
            }
            else { app.PropertyChanged += AppChanged; Apps.Add(app); }
        }
        AppsView.Refresh();
        appsLoaded = true;
        Notify(nameof(AppCount)); Notify(nameof(AppsEmptyText)); RefreshSelections();
    }

    private void AppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstalledApp.Selected)) RefreshSelections();
    }

    public void RefreshSelections()
    {
        Notify(nameof(AppListSummary)); Notify(nameof(UninstallLabel)); Notify(nameof(CanUninstallSelected));
    }

    private bool IsVisible(InstalledApp app) => string.IsNullOrWhiteSpace(Query) ||
        app.Name.Contains(Query, StringComparison.CurrentCultureIgnoreCase) ||
        app.Publisher.Contains(Query, StringComparison.CurrentCultureIgnoreCase);

    public void SetScan(ScanResult scan, IReadOnlySet<string> selectedCategories)
    {
        foreach (var item in Candidates) item.PropertyChanged -= CandidateChanged;
        Candidates.Clear();
        foreach (var item in scan.Files) { item.PropertyChanged += CandidateChanged; Candidates.Add(item); }
        hasScanned = true;
        foreach (var category in Categories)
        {
            var files = scan.Files.Where(f => f.CategoryId == category.Id).ToList();
            category.Summary = selectedCategories.Contains(category.Id)
                ? $"{Format.Bytes(files.Sum(f => f.Length))} · {files.Count:N0} 个文件" : "本次未选择";
        }
        RefreshCleanup();
        CleanupSummary = scan.Notes.Count > 0 ? string.Join("  ", scan.Notes) :
            $"已找到 {Candidates.Count:N0} 个超过 7 天的文件。移入回收站后可恢复，清空回收站后才释放空间。";
    }

    private void CandidateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanupCandidate.Selected)) { Notify(nameof(CleanLabel)); Notify(nameof(CanClean)); }
    }

    public void RefreshCleanup()
    {
        Notify(nameof(ScannedSize)); Notify(nameof(ScannedHint)); Notify(nameof(ScanLabel));
        Notify(nameof(CleanupEmptyText)); Notify(nameof(CleanLabel)); Notify(nameof(CanClean));
    }

    public void InvalidateScan()
    {
        foreach (var item in Candidates) item.PropertyChanged -= CandidateChanged;
        Candidates.Clear();
        hasScanned = false;
        foreach (var category in Categories) category.Summary = "请重新扫描";
        RefreshCleanup();
    }

    public void RefreshDisk()
    {
        try
        {
            string root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
            var drive = new DriveInfo(root);
            FreeSpace = Format.Bytes(drive.AvailableFreeSpace);
            DiskUsedPercent = 100 * (1 - (double)drive.AvailableFreeSpace / drive.TotalSize);
        }
        catch (IOException) { FreeSpace = "暂不可用"; }
    }
}
