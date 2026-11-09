using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using ClearSpace.Core;

namespace ClearSpace.App;

public sealed record LargeFileDrive(string Path, string Label);

public sealed class LargeFilesViewModel : ObservableObject
{
    public ObservableCollection<LargeFileCandidate> Files { get; private set; } = [];
    public ObservableCollection<LargeFileDrive> Drives { get; } = [];
    public ICollectionView View { get; private set; }
    public IReadOnlyList<string> Types => LargeFileTypes.Labels;
    private string targetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    private string query = "";
    private string summary = "选择磁盘或文件夹后开始扫描。文件默认不勾选，请核对后选择。";
    private int minimumIndex, typeIndex, sortIndex;
    private bool busy;
    private bool selectingVisible;
    private LargeFileDrive? selectedDrive;
    public LargeFileScan? Scan { get; private set; }
    public LargeFilesViewModel()
    {
        View = CollectionViewSource.GetDefaultView(Files);
        View.Filter = o => o is LargeFileCandidate file && Matches(file);
        ApplySort();
    }
    public bool Busy { get => busy; set { if (Set(ref busy, value)) { Notify(nameof(CanAct)); Notify(nameof(CanClean)); } } }
    public bool CanAct => !Busy;
    public bool CanClean => !Busy && Scan is not null && Files.Any(f => f.Selected);
    public string TargetPath
    {
        get => targetPath;
        set
        {
            if (!Set(ref targetPath, value)) return;
            if (selectedDrive is not null && !string.Equals(value, selectedDrive.Path, StringComparison.OrdinalIgnoreCase))
            { selectedDrive = null; Notify(nameof(SelectedDrive)); }
            Invalidate();
        }
    }
    public LargeFileDrive? SelectedDrive
    {
        get => selectedDrive;
        set { if (Set(ref selectedDrive, value) && value is not null) TargetPath = value.Path; }
    }
    public int MinimumIndex { get => minimumIndex; set { if (Set(ref minimumIndex, value)) Invalidate(); } }
    public long MinimumBytes => MinimumIndex switch { 1 => 500L << 20, 2 => 1L << 30, 3 => 5L << 30, _ => 100L << 20 };
    public string Query { get => query; set { if (Set(ref query, value)) { View.Refresh(); RefreshSelection(); } } }
    public int TypeIndex { get => typeIndex; set { if (Set(ref typeIndex, value)) { View.Refresh(); RefreshSelection(); } } }
    public int SortIndex { get => sortIndex; set { if (Set(ref sortIndex, value)) ApplySort(); } }
    public string Summary { get => summary; set => Set(ref summary, value); }
    public string EmptyText => Scan is null ? "选择扫描位置，找出占空间的大文件" : Files.Count == 0 ? "本次没有找到符合条件的大文件" : "没有符合当前筛选的文件";
    public string TotalText => Scan is null ? "尚未扫描" : $"{Files.Count:N0} 个文件 · {Format.Bytes(Files.Sum(f => f.Length))}";
    public string TotalSizeText => Scan is null ? "尚未扫描" : Format.Bytes(Files.Sum(f => f.Length));
    public string TotalHint => Scan is null ? "选择磁盘或文件夹，开始扫描" : $"共 {Files.Count:N0} 个文件 · 默认不勾选";
    public string SelectionText
    {
        get
        {
            int hidden = Files.Count(f => f.Selected && !Matches(f));
            return $"显示 {Files.Count(Matches):N0} / {Files.Count:N0} 个 · 已选择 {Files.Count(f => f.Selected):N0} 个（{Format.Bytes(Files.Where(f => f.Selected).Sum(f => f.Length))}）" +
                (hidden > 0 ? $" · 含筛选外 {hidden} 个" : "");
        }
    }
    public string CleanLabel => $"移入回收站 · {Format.Bytes(Files.Where(f => f.Selected).Sum(f => f.Length))}";
    public bool? AllVisibleSelected
    {
        get
        {
            var visible = Files.Where(Matches).ToList();
            int count = visible.Count(f => f.Selected);
            return count == 0 ? false : count == visible.Count ? true : null;
        }
        set
        {
            if (Busy) return;
            selectingVisible = true;
            try { foreach (var file in Files.Where(Matches)) file.Selected = value == true; }
            finally { selectingVisible = false; }
            RefreshSelection();
        }
    }
    private bool Matches(LargeFileCandidate file) => (TypeIndex == 0 || file.TypeIndex == TypeIndex) &&
        (string.IsNullOrWhiteSpace(Query) || file.Path.Contains(Query, StringComparison.CurrentCultureIgnoreCase));
    private void ApplySort()
    {
        using (View.DeferRefresh())
        {
            View.SortDescriptions.Clear();
            View.SortDescriptions.Add(SortIndex switch
            {
                1 => new(nameof(LargeFileCandidate.Length), ListSortDirection.Ascending),
                2 => new(nameof(LargeFileCandidate.LastWriteUtc), ListSortDirection.Descending),
                3 => new(nameof(LargeFileCandidate.Name), ListSortDirection.Ascending),
                _ => new(nameof(LargeFileCandidate.Length), ListSortDirection.Descending)
            });
            View.SortDescriptions.Add(new(nameof(LargeFileCandidate.Path), ListSortDirection.Ascending));
        }
    }
    public void SetScan(LargeFileScan scan)
    {
        foreach (var file in Files) file.PropertyChanged -= FileChanged;
        Scan = scan;
        Files = new(scan.Files);
        View = CollectionViewSource.GetDefaultView(Files);
        View.Filter = o => o is LargeFileCandidate file && Matches(file);
        foreach (var file in Files) file.PropertyChanged += FileChanged;
        Notify(nameof(Files)); Notify(nameof(View));
        // DataGrid resets sort descriptions when its source changes; apply the chosen order after binding.
        ApplySort();
        RefreshSelection(); Notify(nameof(EmptyText)); Notify(nameof(TotalText)); Notify(nameof(TotalSizeText)); Notify(nameof(TotalHint));
        Summary = (scan.Canceled ? "已停止，显示部分结果" : "扫描完成") + $" · 检查 {scan.InspectedFiles:N0} 个文件。" +
            (scan.Notes.Count > 0 ? "  " + string.Join("  ", scan.Notes) : " 文件默认不选，核对后勾选；清空回收站后才释放空间。");
    }
    public void Invalidate()
    {
        foreach (var file in Files) file.PropertyChanged -= FileChanged;
        Files.Clear(); Scan = null;
        RefreshSelection(); Notify(nameof(EmptyText)); Notify(nameof(TotalText)); Notify(nameof(TotalSizeText)); Notify(nameof(TotalHint));
        Summary = "扫描位置或大小门槛已更改，请重新扫描。";
    }
    private void FileChanged(object? sender, PropertyChangedEventArgs e) { if (!selectingVisible && e.PropertyName == nameof(LargeFileCandidate.Selected)) RefreshSelection(); }
    private void RefreshSelection()
    {
        Notify(nameof(SelectionText)); Notify(nameof(CleanLabel)); Notify(nameof(CanClean)); Notify(nameof(AllVisibleSelected));
    }
}
