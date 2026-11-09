using System.Collections.ObjectModel;
using System.ComponentModel;
using ClearSpace.Core;

namespace ClearSpace.App;

public sealed class ResidueViewModel : ObservableObject
{
    public ObservableCollection<ResiduePlan> Plans { get; } = [];
    public ObservableCollection<ResidueCandidate> Candidates { get; } = [];
    private ResiduePlan? selectedPlan;
    private bool busy;
    private string summary = "从本版本发起卸载后，可在这里扫描该软件的残留。所有项目默认不勾选。";
    public ResiduePlan? SelectedPlan
    {
        get => selectedPlan;
        set { if (Set(ref selectedPlan, value)) { SetScan(null); Notify(nameof(CanScan)); } }
    }
    public bool Busy
    {
        get => busy;
        set { if (Set(ref busy, value)) { Notify(nameof(CanScan)); Notify(nameof(CanClean)); } }
    }
    public bool CanScan => !Busy && SelectedPlan is not null;
    public bool CanClean => !Busy && Scan is not null && Candidates.Any(c => c.Selected);
    public string Summary { get => summary; set => Set(ref summary, value); }
    public string SelectionSummary => $"{Candidates.Count} 项残留候选 · 已选择 {Candidates.Count(c => c.Selected)} 项";
    public ResidueScan? Scan { get; private set; }
    public void SetScan(ResidueScan? scan)
    {
        foreach (var item in Candidates) item.PropertyChanged -= SelectionChanged;
        Candidates.Clear(); Scan = scan;
        if (scan is not null) foreach (var item in scan.Candidates) { item.PropertyChanged += SelectionChanged; Candidates.Add(item); }
        Notify(nameof(CanClean)); Notify(nameof(SelectionSummary));
    }
    private void SelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResidueCandidate.Selected)) { Notify(nameof(CanClean)); Notify(nameof(SelectionSummary)); }
    }
}
