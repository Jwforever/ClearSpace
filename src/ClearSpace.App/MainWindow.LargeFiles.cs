using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ClearSpace.Core;
using Microsoft.Win32;

namespace ClearSpace.App;

public partial class MainWindow
{
    public LargeFilesViewModel LargeFiles { get; } = new();
    private LargeFileService CreateLargeFileService()
    {
        var registeredLocations = appsService.ReadRegistrations().Select(a => a.InstallLocation)
            .Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p) && !p.StartsWith(@"\\"))
            .Where(p => !PathSafety.Normalize(p).Equals(Path.GetPathRoot(p), StringComparison.OrdinalIgnoreCase)).ToList();
        return new(new LargeFilePolicy(additionalProtectedRoots: registeredLocations), new WindowsRecycleBin());
    }

    private void LoadLargeFileDrives()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.DriveType == DriveType.Fixed)
                    LargeFiles.Drives.Add(new(drive.RootDirectory.FullName, $"{drive.Name}  {Format.Bytes(drive.AvailableFreeSpace)} 可用"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private void ChooseLargeFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Model.Busy || launchArgs.Contains("--ui-smoke")) return;
        var dialog = new OpenFolderDialog { Title = "选择大文件扫描位置", Multiselect = false };
        if (Directory.Exists(LargeFiles.TargetPath)) dialog.InitialDirectory = LargeFiles.TargetPath;
        if (dialog.ShowDialog(this) == true) LargeFiles.TargetPath = dialog.FolderName;
    }

    private async void ScanLargeFiles_Click(object sender, RoutedEventArgs e)
    {
        if (Model.Busy || launchArgs.Contains("--ui-smoke")) return;
        string root = LargeFiles.TargetPath; long minimum = LargeFiles.MinimumBytes;
        Model.Busy = true; operation = new CancellationTokenSource();
        LargeFiles.Summary = "正在扫描，只读取文件信息…";
        Model.Status = "正在扫描大文件…";
        var progress = new Progress<LargeFileProgress>(p =>
        {
            if (!Model.Busy || operation?.IsCancellationRequested != false) return;
            Model.Status = $"已检查 {p.InspectedFiles:N0} 个文件 · 匹配 {p.MatchingFiles:N0} 个 · 跳过 {p.SkippedEntries:N0} 项";
            LargeFiles.Summary = "正在扫描：" + p.Directory;
        });
        try
        {
            var scan = await Task.Run(() => CreateLargeFileService().Scan(root, minimum, operation.Token, progress));
            LargeFiles.SetScan(scan); Model.Status = LargeFiles.Summary;
        }
        catch (OperationCanceledException) { Model.Status = LargeFiles.Summary = "扫描已取消，原有结果保留。"; }
        catch (Exception ex) { LargeFiles.Summary = "扫描未完成：" + ex.Message; ShowError(ex); }
        finally { operation?.Dispose(); operation = null; Model.Busy = false; }
    }

    private async void CleanLargeFiles_Click(object sender, RoutedEventArgs e)
    {
        if (Model.Busy || LargeFiles.Scan is not { } scan || launchArgs.Contains("--ui-smoke")) return;
        var selected = LargeFiles.Files.Where(f => f.Selected).ToList();
        if (selected.Count == 0) return;
        string paths = string.Join("\n", selected.Take(8).Select(f => "• " + f.Path));
        if (selected.Count > 8) paths += $"\n…共 {selected.Count:N0} 个文件，完整清单见扫描结果。";
        if (MessageBox.Show(this, $"将把勾选的 {selected.Count:N0} 个文件（{Format.Bytes(selected.Sum(f => f.Length))}）移入回收站，包括筛选后隐藏的已选文件。\n\n{paths}\n\n" +
            "这些文件可能是视频、安装包、备份或个人资料，请确认不再需要。文件可从回收站恢复，清空回收站后才释放空间。\n\n是否继续？",
            "确认清理大文件", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        Model.Busy = true; operation = new CancellationTokenSource();
        var progress = new Progress<string>(text => Model.Status = text);
        try
        {
            var result = await Task.Run(() => CreateLargeFileService().Execute(scan, selected, operation.Token, progress));
            string message = $"移入回收站 {result.Succeeded:N0} 个大文件（{Format.Bytes(result.Bytes)}），跳过 {result.Skipped:N0} 个。";
            var logged = selected.Select(f => new CleanupCandidate { Path = f.Path, Root = scan.Root, CategoryId = "large", CategoryTitle = "大文件", Length = f.Length, LastWriteUtc = f.LastWriteUtc }).ToList();
            AddHistory(new(DateTime.UtcNow, "大文件清理", message, HistoryDetails.Cleanup(logged, result), result.Skipped > 0 ? "部分完成" : "已移入回收站"));
            LargeFiles.Invalidate(); LargeFiles.Summary = message + " 请重新扫描查看剩余文件。";
            Model.RefreshDisk(); Model.Status = message;
        }
        catch (Exception ex)
        {
            LargeFiles.Invalidate();
            AddHistory(new(DateTime.UtcNow, "大文件清理", "任务中断", ex.Message + " 部分文件可能已移入回收站，请重新扫描核对。", "请核对结果"));
            ShowError(ex);
        }
        finally { operation?.Dispose(); operation = null; Model.Busy = false; }
    }

    private void LocateLargeFile_Click(object sender, RoutedEventArgs e)
    {
        if (launchArgs.Contains("--ui-smoke") || sender is not Button { Tag: LargeFileCandidate file }) return;
        try
        {
            if (!File.Exists(file.Path)) throw new FileNotFoundException("文件已不存在，请重新扫描。", file.Path);
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true };
            start.Arguments = "/select,\"" + file.Path + "\"";
            Process.Start(start);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void LargeFilesGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is DataGrid grid && grid.Columns.Count > 1)
            grid.Columns[1].Width = Math.Max(180, grid.ActualWidth - 590);
    }

    private async Task VerifyLargeFilesPage(string output)
    {
        LargeFileScan DemoScan() => new(@"D:\资料", 100L << 20,
        [
            new() { Path = @"D:\资料\视频\旅行原片.mp4", Length = 4800L << 20, LastWriteUtc = DateTime.UtcNow.AddDays(-32) },
            new() { Path = @"D:\资料\备份\旧照片归档.zip", Length = 2400L << 20, LastWriteUtc = DateTime.UtcNow.AddDays(-180) },
            new() { Path = @"D:\资料\下载\旧版安装包.exe", Length = 850L << 20, LastWriteUtc = DateTime.UtcNow.AddDays(-90) },
            new() { Path = @"D:\资料\镜像\系统镜像.iso", Length = 5800L << 20, LastWriteUtc = DateTime.UtcNow.AddDays(-8) },
            new() { Path = @"D:\资料\备份\项目归档.7z", Length = 420L << 20, LastWriteUtc = DateTime.UtcNow.AddDays(-14) }
        ], 6824, 5, 12, false, false, []);
        LargeFiles.Drives.Add(new(@"D:\", @"D:\  演示磁盘"));
        LargeFiles.TargetPath = @"D:\资料";
        LargeFiles.SetScan(DemoScan());
        NavLargeFiles.IsChecked = true;
        await Task.Delay(120);
        if (LargeFilesPage.Visibility != Visibility.Visible || LargeFilesGrid.Items.Count != 5 ||
            LargeFiles.Files.Any(f => f.Selected) || CleanLargeFilesButton.IsEnabled)
            throw new InvalidOperationException("Large-file results must appear without any default selection.");
        var expected = LargeFiles.Files.OrderByDescending(f => f.Length).Select(f => f.Path);
        if (!LargeFiles.View.Cast<LargeFileCandidate>().Select(f => f.Path).SequenceEqual(expected))
            throw new InvalidOperationException("Large-file results must default to largest first.");
        LargeType.SelectedIndex = 2;
        LargeSort.SelectedIndex = 1;
        await Task.Delay(80);
        var archives = LargeFiles.View.Cast<LargeFileCandidate>().ToList();
        if (archives.Count != 2 || archives[0].Length >= archives[1].Length)
            throw new InvalidOperationException("Large-file type filter or ascending size selector failed.");
        LargeFiles.AllVisibleSelected = true;
        if (LargeFiles.Files.Count(f => f.Selected) != 2 || !CleanLargeFilesButton.IsEnabled)
            throw new InvalidOperationException("Large-file selection must only select the visible filtered results.");
        LargeSearch.Text = "旅行";
        LargeType.SelectedIndex = 0;
        await Task.Delay(80);
        if (LargeFilesGrid.Items.Count != 1 || !LargeFiles.SelectionText.Contains("筛选外 2"))
            throw new InvalidOperationException("Hidden selected files must remain visible in the selection summary.");
        Model.Busy = true;
        await Task.Delay(60);
        if (CleanLargeFilesButton.IsEnabled || ScanLargeFilesButton.IsEnabled || ChooseLargeFolderButton.IsEnabled || LargeMinimum.IsEnabled)
            throw new InvalidOperationException("Large-file mutation and scope controls must be disabled while another task runs.");
        Model.Busy = false;
        LargeFiles.TargetPath = @"D:\新的资料";
        if (LargeFiles.Scan is not null || LargeFiles.Files.Count != 0 || CleanLargeFilesButton.IsEnabled)
            throw new InvalidOperationException("Changing scan scope must invalidate earlier selections and snapshots.");
        LargeFiles.TargetPath = @"D:\资料";
        LargeSearch.Text = ""; LargeType.SelectedIndex = 0; LargeSort.SelectedIndex = 0;
        LargeFiles.SetScan(DemoScan());
        LargeMinimum.SelectedIndex = 2;
        if (LargeFiles.Scan is not null || LargeFiles.MinimumBytes != (1L << 30))
            throw new InvalidOperationException("Changing the size threshold must invalidate earlier results.");
        LargeMinimum.SelectedIndex = 0;
        LargeFiles.SetScan(DemoScan());
        LargeFiles.Summary = "演示结果 · 所有文件默认不选 · 未扫描或清理个人文件。";
        Model.Status = "界面预览 · 演示数据 · 未执行卸载或清理";
        await Task.Delay(80);
        CaptureWindow(Path.Combine(output, "08-large-files.png"));
        Width = MinWidth; Height = MinHeight;
        await Task.Delay(80);
        if (LargeFilesGrid.Columns.Sum(c => c.ActualWidth) > LargeFilesGrid.ActualWidth)
            throw new InvalidOperationException("Large-file columns must fit the minimum window size.");
        CaptureWindow(Path.Combine(output, "09-large-files-minimum.png"));
        Width = 1260; Height = 850;
    }
}
