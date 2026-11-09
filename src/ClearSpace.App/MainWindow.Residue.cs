using System.IO;
using System.Windows;
using System.Windows.Threading;
using ClearSpace.Core;

namespace ClearSpace.App;

public partial class MainWindow
{
    private readonly ResiduePlanStore residueStore = new();
    private readonly List<ResiduePlan> residuePlans = [];
    private readonly HashSet<string> waitingUninstallIds = new(StringComparer.Ordinal);
    private bool processingResiduePrompts;
    private Func<ResiduePlan, CancellationToken, ResidueScan>? demoResidueScanner;
    private Func<ResidueScan, CancellationToken, ResidueResult>? demoResidueExecutor;
    private Action<ResidueDialog>? demoResidueDialog;
    private ResidueService CreateResidueService() => new(new WindowsResidueRegistry(), new WindowsRecycleBin(), appsService.ReadRegistrations);

    private void LoadResiduePlans()
    {
        residuePlans.AddRange(residueStore.Read());
        foreach (var plan in residuePlans.Where(p => p.PromptPending)) TrackUninstall(plan.App.Id);
    }

    private bool SaveResiduePlans()
    {
        if (launchArgs.Contains("--ui-smoke")) return true;
        try { residueStore.Write(residuePlans); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Model.Status = "残留扫描记录保存失败：" + ex.Message; return false; }
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
        residuePlans.RemoveAll(p => p.App.Id == plan.App.Id);
        plan.PromptPending = true;
        residuePlans.Insert(0, plan);
        if (residuePlans.Count > 100) residuePlans.RemoveRange(100, residuePlans.Count - 100);
        SaveResiduePlans();
    }

    private void ObserveResiduePlans(IReadOnlyList<InstalledApp> apps)
    {
        bool changed = false;
        foreach (var plan in residuePlans.Where(p => p.PromptPending && !p.RemovalObserved))
            if (!ResiduePolicy.AppStillInstalled(plan.App, apps)) { plan.RemovalObserved = true; changed = true; }
        if (changed) SaveResiduePlans();
    }

    private void ScheduleResiduePrompts()
    {
        if (windowClosed || !IsLoaded || Model.Busy || processingResiduePrompts || !residuePlans.Any(p => p.PromptPending && p.RemovalObserved)) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () => await ProcessReadyResiduePrompts()));
    }

    private async Task ProcessReadyResiduePrompts(bool duringUninstall = false)
    {
        if (windowClosed || processingResiduePrompts || (Model.Busy && !duringUninstall)) return;
        // The demo path can only run with explicitly injected synthetic scanners and executors.
        if (launchArgs.Contains("--ui-smoke") && (demoResidueScanner is null || demoResidueExecutor is null || demoResidueDialog is null)) return;
        processingResiduePrompts = true;
        bool ownsOperation = !duringUninstall;
        if (ownsOperation) { Model.Busy = true; operation = new CancellationTokenSource(); }
        try
        {
            foreach (var plan in residuePlans.Where(p => p.PromptPending && p.RemovalObserved && !waitingUninstallIds.Contains(p.App.Id)).ToList())
            {
                var token = operation?.Token ?? CancellationToken.None;
                if (token.IsCancellationRequested || windowClosed) break;
                try { await ScanAndPromptResidues(plan, token); }
                catch (OperationCanceledException)
                {
                    plan.PromptPending = false; SaveResiduePlans();
                    Model.Status = "残留扫描已停止，未继续清理。";
                    break;
                }
                catch (Exception ex)
                {
                    plan.PromptPending = false; SaveResiduePlans();
                    Model.Status = plan.App.Name + " 的残留处理未完成：" + ex.Message;
                    RecordResidueHistory(new(DateTime.UtcNow, "清理残留", plan.App.Name,
                        Model.Status + "\n注册表备份位于：" + RegistryBackupRoot, "请核对结果"));
                }
            }
        }
        finally
        {
            if (operation?.IsCancellationRequested == true)
            {
                foreach (var plan in residuePlans.Where(p => p.RemovalObserved && p.PromptPending)) plan.PromptPending = false;
                SaveResiduePlans();
            }
            processingResiduePrompts = false;
            // A timer can observe another delayed removal while this modal flow is active.
            // Releasing Busy after the guard lets that pending dialog be scheduled normally.
            if (ownsOperation) { operation?.Dispose(); operation = null; Model.Busy = false; }
        }
    }

    private static string RegistryBackupRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClearSpace", "RegistryBackups");

    private async Task ScanAndPromptResidues(ResiduePlan plan, CancellationToken token)
    {
        Model.Status = "正在扫描 " + plan.App.Name + " 的卸载残留…";
        var scan = await Task.Run(() => demoResidueScanner is not null ? demoResidueScanner(plan, token) : CreateResidueService().Scan(plan, token));
        token.ThrowIfCancellationRequested();
        // Persist the handled state before opening the modal window, so Cancel or Close never causes a repeat prompt.
        plan.PromptPending = false;
        if (!SaveResiduePlans()) throw new IOException("无法保存弹窗处理状态，本次未继续清理。");
        if (scan.Blocked)
        {
            Model.Status = "检测到软件仍在安装列表中或已重新安装，已跳过残留清理。";
            return;
        }
        var dialog = new ResidueDialog(scan) { Owner = this };
        demoResidueDialog?.Invoke(dialog);
        bool confirmed = dialog.ShowDialog() == true;
        if (!confirmed)
        {
            Model.Status = "已跳过 " + plan.App.Name + " 的残留清理。";
            RecordResidueHistory(new(DateTime.UtcNow, "清理残留", plan.App.Name, "已取消残留清理，列出的文件和注册表项均保留。", "已跳过"));
            return;
        }
        if (scan.Candidates.Count == 0)
        {
            Model.Status = plan.App.Name + " 已卸载 · 未发现可清理的残留。";
            return;
        }
        Model.Status = "正在清理 " + plan.App.Name + " 的残留…";
        var result = await Task.Run(() => demoResidueExecutor is not null ? demoResidueExecutor(scan, token) :
            CreateResidueService().Execute(scan, scan.Candidates, token));
        string message = $"已移入回收站 {result.FilesRecycled} 个文件，备份并删除 {result.RegistryKeysRemoved} 个注册表项，跳过 {result.Skipped} 项。";
        string backupDetail = result.BackupDirectory.Length > 0 ? "\n注册表备份目录：" + result.BackupDirectory : "";
        RecordResidueHistory(new(DateTime.UtcNow, "清理残留", plan.App.Name, message + backupDetail + "\n" + string.Join("\n", result.Details), result.Skipped > 0 ? "部分完成" : "已处理"));
        Model.RefreshDisk();
        Model.Status = message + (result.BackupDirectory.Length > 0 ? "  备份：" + result.BackupDirectory : "");
    }

    private void RecordResidueHistory(OperationEntry entry)
    {
        if (launchArgs.Contains("--ui-smoke")) Model.History.Insert(0, entry);
        else AddHistory(entry);
    }
}
