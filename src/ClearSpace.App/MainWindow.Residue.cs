using System.IO;
using System.Windows;
using ClearSpace.Core;

namespace ClearSpace.App;

public partial class MainWindow
{
    public ResidueViewModel Residues { get; } = new();
    private readonly ResiduePlanStore residueStore = new();
    private ResidueService CreateResidueService() => new(new WindowsResidueRegistry(), new WindowsRecycleBin(), appsService.ReadRegistrations);

    private void LoadResiduePlans()
    {
        foreach (var plan in residueStore.Read()) Residues.Plans.Add(plan);
        Residues.SelectedPlan = Residues.Plans.FirstOrDefault();
    }

    private void SaveResiduePlans()
    {
        try { residueStore.Write(Residues.Plans); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Model.Status = "残留扫描记录保存失败：" + ex.Message; }
    }

    private async Task<ResiduePlan?> PrepareResiduePlan(InstalledApp app)
    {
        try
        {
            if (launchArgs.Contains("--ui-smoke")) return null;
            return await Task.Run(() => CreateResidueService().Capture(app, appsService.ReadRegistrations()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { Model.Status = "无法记录该软件的残留位置：" + ex.Message; return null; }
    }

    private void AddResiduePlan(ResiduePlan? plan)
    {
        if (plan is null) return;
        foreach (var prior in Residues.Plans.Where(p => p.App.Id == plan.App.Id).ToList()) Residues.Plans.Remove(prior);
        Residues.Plans.Insert(0, plan);
        while (Residues.Plans.Count > 100) Residues.Plans.RemoveAt(Residues.Plans.Count - 1);
        Residues.SelectedPlan = plan;
        SaveResiduePlans();
    }

    private void ObserveResiduePlans(IReadOnlyList<InstalledApp> apps)
    {
        bool changed = false;
        foreach (var plan in Residues.Plans.Where(p => !p.RemovalObserved))
            if (!ResiduePolicy.AppStillInstalled(plan.App, apps)) { plan.RemovalObserved = true; changed = true; }
        if (!changed) return;
        if (!launchArgs.Contains("--ui-smoke")) SaveResiduePlans();
        if (Residues.SelectedPlan is null) Residues.SelectedPlan = Residues.Plans.FirstOrDefault(p => p.RemovalObserved);
        if (!Model.Busy) Model.Status = "卸载记录已移除，可点击“清理残留”扫描文件和注册表。";
    }

    private async void OpenResidues_Click(object sender, RoutedEventArgs e)
    {
        NavResidues.IsChecked = true;
        Residues.SelectedPlan ??= Residues.Plans.FirstOrDefault(p => p.RemovalObserved) ?? Residues.Plans.FirstOrDefault();
        if (Residues.CanScan) await ScanResidues();
    }

    private async void ScanResidues_Click(object sender, RoutedEventArgs e) => await ScanResidues();

    private async Task ScanResidues()
    {
        if (Model.Busy || Residues.SelectedPlan is not { } plan) return;
        if (launchArgs.Contains("--ui-smoke")) return;
        Model.Busy = true; Residues.Busy = true;
        operation = new CancellationTokenSource();
        Residues.SetScan(null);
        Residues.Summary = "正在扫描卸载前记录的位置…";
        Model.Status = "正在扫描 " + plan.App.Name + " 的卸载残留…";
        try
        {
            var scan = await Task.Run(() => CreateResidueService().Scan(plan, operation.Token));
            Residues.SetScan(scan);
            Residues.Summary = $"扫描完成 · {scan.Candidates.Count} 项候选，未执行清理。" +
                (scan.Notes.Count > 0 ? "  " + string.Join("  ", scan.Notes.Take(5)) : " 请逐项核对后勾选。");
            Model.Status = $"残留扫描完成 · {scan.Candidates.Count} 项候选 · 请核对后勾选";
        }
        catch (OperationCanceledException) { Model.Status = Residues.Summary = "残留扫描已取消。"; }
        catch (Exception ex) { Model.Status = Residues.Summary = "残留扫描未完成：" + ex.Message; }
        finally { operation?.Dispose(); operation = null; Residues.Busy = false; Model.Busy = false; }
    }

    private async void CleanResidues_Click(object sender, RoutedEventArgs e)
    {
        if (Model.Busy || Residues.Scan is not { } scan || launchArgs.Contains("--ui-smoke")) return;
        var selected = Residues.Candidates.Where(c => c.Selected).ToList();
        if (selected.Count == 0) return;
        int files = selected.Count(c => c.Kind == ResidueKind.File), keys = selected.Count - files;
        string items = string.Join("\n", selected.Take(8).Select(c => "• " + c.Path));
        if (selected.Count > 8) items += $"\n…共 {selected.Count} 项，完整清单见预览。";
        if (MessageBox.Show(this, $"{scan.Plan.App.Name}\n\n{items}\n\n" +
            $"将把 {files} 个文件移入回收站；先备份再删除 {keys} 个注册表项。\n" +
            "残留可能包含软件设置、存档或个人内容，请确认选中的项目不再需要。\n\n是否清理选中项？",
            "确认清理卸载残留", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        Model.Busy = true; Residues.Busy = true; operation = new CancellationTokenSource();
        Model.Status = "正在清理 " + scan.Plan.App.Name + " 的选中残留…";
        try
        {
            var result = await Task.Run(() => CreateResidueService().Execute(scan, selected, operation.Token));
            string message = $"已移入回收站 {result.FilesRecycled} 个文件，备份并删除 {result.RegistryKeysRemoved} 个注册表项，跳过 {result.Skipped} 项。";
            string backupDetail = result.BackupDirectory.Length > 0 ? "\n注册表备份目录：" + result.BackupDirectory : "";
            AddHistory(new(DateTime.UtcNow, "清理残留", scan.Plan.App.Name, message + backupDetail + "\n" + string.Join("\n", result.Details), result.Skipped > 0 ? "部分完成" : "已处理"));
            Residues.SetScan(null);
            Residues.Summary = message + (result.BackupDirectory.Length > 0 ? "  备份：" + result.BackupDirectory : "") + " 请重新扫描查看剩余项。";
            Model.RefreshDisk(); Model.Status = message;
        }
        catch (Exception ex)
        {
            Residues.SetScan(null);
            Model.Status = Residues.Summary = "任务未完成，请重新扫描核对：" + ex.Message;
            AddHistory(new(DateTime.UtcNow, "清理残留", scan.Plan.App.Name, Residues.Summary + "\n注册表备份可在“卸载残留 → 备份文件夹”查看。", "请核对结果"));
        }
        finally { operation?.Dispose(); operation = null; Residues.Busy = false; Model.Busy = false; }
    }

    private void OpenRegistryBackups_Click(object sender, RoutedEventArgs e)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClearSpace", "RegistryBackups");
        try { Directory.CreateDirectory(folder); OpenShell(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError(ex); }
    }
}
