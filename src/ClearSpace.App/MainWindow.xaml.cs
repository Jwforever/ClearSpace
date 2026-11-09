using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClearSpace.Core;

namespace ClearSpace.App;

public partial class MainWindow : Window
{
    public MainViewModel Model { get; } = new();
    private readonly InstalledAppsService appsService = new();
    private readonly HistoryStore history = new();
    private IReadOnlyList<CleanupRule> scanRules = [];
    private CancellationTokenSource? operation;
    private readonly string[] launchArgs = Environment.GetCommandLineArgs();
    private readonly SemaphoreSlim appReadGate = new(1, 1);
    private readonly PendingUninstallRefresh pendingRefresh = new();
    private readonly DispatcherTimer uninstallRefreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool uninstallQueueRunning;
    private bool windowClosed;
    private Func<IReadOnlyList<InstalledApp>>? demoAppsReader;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = Model;
        Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(Model.Busy)) return;
            LargeFiles.Busy = Model.Busy;
            if (!Model.Busy) ScheduleResiduePrompts();
        };
        uninstallRefreshTimer.Tick += UninstallRefresh_Tick;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // The UI smoke switch uses invented display data; it never reads registry entries or executes destructive actions.
        if (launchArgs.Contains("--ui-smoke"))
        {
            demoAppsReader = CreateDemoApps;
            Model.SetApps(CreateDemoApps());
            Model.History.Add(new(DateTime.UtcNow.AddMinutes(-12), "清理", "用户临时文件 · 6.4 MB", "示例：3 个文件已移入系统回收站。", "已移入回收站"));
            Model.History.Add(new(DateTime.UtcNow.AddMinutes(-47), "卸载", "示例工具 2.0", "示例：软件自带的卸载程序已退出。", "进程已退出"));
            Model.Status = "界面预览 · 演示数据 · 未执行卸载或清理";
            _ = RunUiSmoke();
            return;
        }
        LoadHistory();
        LoadResiduePlans();
        LoadLargeFileDrives();
        await RefreshApps();
    }

    private static IReadOnlyList<InstalledApp> CreateDemoApps() =>
    [
        new() { Id = "demo-1", Name = "轻笔记", Publisher = "轻笔记工作室", Version = "2.4.0", InstallDate = "2026-09-12", EstimatedBytes = 145L * 1024 * 1024, LastUsedUtc = DateTime.UtcNow.AddHours(-2) },
        new() { Id = "demo-2", Name = "星野音乐", Publisher = "星野科技", Version = "3.8.1", InstallDate = "2026-08-23", EstimatedBytes = 760L * 1024 * 1024, LastUsedUtc = DateTime.UtcNow.AddDays(-15) },
        new() { Id = "demo-3", Name = "Microsoft 示例组件", Publisher = "Microsoft", Version = "1.2.0", InstallDate = "2026-07-02", EstimatedBytes = 48L * 1024 * 1024 },
        new() { Id = "demo-4", Name = "Code Studio", Publisher = "Microsoft", Version = "2026.1", InstallDate = "2026-06-15", EstimatedBytes = 1270L * 1024 * 1024, LastUsedUtc = DateTime.UtcNow.AddMinutes(-25) },
        new() { Id = "demo-5", Name = "拾光相册", Publisher = "拾光软件", Version = "5.3.2", InstallDate = "2026-05-28", EstimatedBytes = 234L * 1024 * 1024, LastUsedUtc = DateTime.UtcNow.AddDays(-60) },
        new() { Id = "demo-6", Name = "云端日历", Publisher = "云端团队", Version = "4.1.0", InstallDate = "2026-04-19", EstimatedBytes = 96L * 1024 * 1024 }
    ];

    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (AppsPage is null || sender is not RadioButton item) return;
        string page = item.Tag?.ToString() ?? "apps";
        AppsPage.Visibility = page == "apps" ? Visibility.Visible : Visibility.Collapsed;
        CleanupPage.Visibility = page == "cleanup" ? Visibility.Visible : Visibility.Collapsed;
        LargeFilesPage.Visibility = page == "large" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == "history" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = page == "about" ? Visibility.Visible : Visibility.Collapsed;
        (Model.PageTitle, Model.PageDescription) = page switch
        {
            "cleanup" => ("空间清理", "从一次预览开始，为电脑留出更多空间。"),
            "large" => ("大文件清理", "找出占空间的文件，核对后再整理。"),
            "history" => ("操作记录", "每一次整理，都有迹可循。"),
            "about" => ("关于清屿", "一个简单、透明的本地整理工具。"),
            _ => ("软件管理", "看清每一款应用，让卸载更从容。")
        };
    }

    private async Task<bool> RefreshApps()
    {
        if (Model.Busy) return false;
        Model.Busy = true;
        Model.Status = "正在读取本机安装记录…";
        try
        {
            await ReloadApps();
            Model.Status = $"已读取 {Model.AppCount} 个应用 · 刷新于 {DateTime.Now:HH:mm} · 数据仅保存在本机";
            return true;
        }
        catch (Exception ex) { ShowError(ex); return false; }
        finally { Model.Busy = false; }
    }

    private async void RefreshApps_Click(object sender, RoutedEventArgs e) => await RefreshApps();

    private async Task ReloadApps()
    {
        await appReadGate.WaitAsync();
        try
        {
            if (windowClosed) return;
            var apps = await Task.Run(demoAppsReader ?? appsService.Read);
            if (windowClosed) return;
            Model.SetApps(apps);
            ObserveResiduePlans(apps);
            Model.RefreshDisk();
            int removed = pendingRefresh.Observe(apps);
            if (removed > 0 && !Model.Busy)
                Model.Status = $"已自动刷新 · {removed} 个软件的安装记录已移除 · 刷新于 {DateTime.Now:HH:mm:ss}";
        }
        finally { appReadGate.Release(); }
        ScheduleResiduePrompts();
    }

    private void TrackUninstall(string id)
    {
        pendingRefresh.Track(id, DateTime.UtcNow);
        uninstallRefreshTimer.Start();
    }

    private async void UninstallRefresh_Tick(object? sender, EventArgs e)
    {
        if (windowClosed || !pendingRefresh.ShouldPoll(DateTime.UtcNow, uninstallQueueRunning))
        {
            uninstallRefreshTimer.Stop();
            return;
        }
        if (appReadGate.CurrentCount == 0) return;
        try { await ReloadApps(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (!Model.Busy) Model.Status = "自动刷新暂未完成，可点击刷新重试。";
        }
    }

    private async void Window_Activated(object? sender, EventArgs e)
    {
        if (windowClosed || !pendingRefresh.HasPending || launchArgs.Contains("--ui-smoke")) return;
        // A child uninstaller can outlive its launcher. Returning to ClearSpace always checks pending removals again.
        pendingRefresh.Extend(DateTime.UtcNow);
        uninstallRefreshTimer.Start();
        if (appReadGate.CurrentCount == 0) return;
        try { await ReloadApps(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        windowClosed = true;
        uninstallRefreshTimer.Stop();
    }

    private void SelectAllApps_Click(object sender, RoutedEventArgs e)
    {
        if (Model.Busy || sender is not CheckBox check) return;
        foreach (var app in Model.AppsView.Cast<InstalledApp>().Where(a => a.CanUninstall)) app.Selected = check.IsChecked == true;
    }

    private void SelectAllFiles_Click(object sender, RoutedEventArgs e)
    {
        if (Model.Busy || sender is not CheckBox check) return;
        foreach (var candidate in Model.Candidates) candidate.Selected = check.IsChecked == true;
    }

    private async void UninstallOne_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: InstalledApp app }) await Uninstall([app]);
    }
    private async void UninstallSelected_Click(object sender, RoutedEventArgs e) =>
        await Uninstall(Model.Apps.Where(a => a.Selected && a.CanUninstall).ToList());

    private async Task Uninstall(IReadOnlyList<InstalledApp> apps)
    {
        if (Model.Busy || apps.Count == 0) return;
        string list = string.Join("\n", apps.Take(10).Select(a => "• " + a.Name));
        if (apps.Count > 10) list += $"\n…共 {apps.Count} 个应用";
        if (MessageBox.Show(this,
                $"将依次启动以下软件自带的卸载程序：\n\n{list}\n\n卸载窗口可能需要你继续操作。卸载完成后自动显示残留清单，点击“确定”清理，点击“取消”保留。软件卸载本身不提供撤销。\n\n是否继续？",
                "确认卸载", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        Model.Busy = true;
        uninstallQueueRunning = true;
        operation = new CancellationTokenSource();
        bool canceled = false;
        int started = 0;
        try
        {
            bool admin = IsAdministrator();
            foreach (var app in apps)
            {
                if (operation.IsCancellationRequested) { canceled = true; break; }
                if (!Model.Apps.Any(a => a.Id == app.Id)) continue;
                Model.Status = "正在卸载 " + app.Name + " · 请完成软件的卸载向导";
                try
                {
                    var command = UninstallCommand.Parse(app.UninstallString,
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                    var residuePlan = await PrepareResiduePlan(app);
                    if (operation.IsCancellationRequested) { canceled = true; break; }
                    using var process = command.Start(app.MachineWide && !admin);
                    AddResiduePlan(residuePlan);
                    waitingUninstallIds.Add(app.Id);
                    started++;
                    TrackUninstall(app.Id);
                    AddHistory(new(DateTime.UtcNow, "卸载", app.Name, "已启动软件自带的卸载程序，等待退出。", "已启动"));
                    try { await process.WaitForExitAsync(operation.Token); }
                    catch (OperationCanceledException)
                    {
                        canceled = true;
                        if (residuePlan is not null) { residuePlan.PromptPending = false; SaveResiduePlans(); }
                        AddHistory(new(DateTime.UtcNow, "卸载", app.Name, "已停止等待；已启动的卸载程序由其自身控制。", "已停止等待"));
                        break;
                    }
                    finally { waitingUninstallIds.Remove(app.Id); }
                    int code = process.ExitCode;
                    AddHistory(new(DateTime.UtcNow, "卸载", app.Name,
                        $"卸载进程已退出，代码 {code}。列表会自动刷新，子进程引起的延迟变化会继续检查。",
                        code switch { 0 => "进程已退出", 3010 => "需要重启", 1602 => "用户已取消", _ => "请核对结果" }));
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    AddHistory(new(DateTime.UtcNow, "卸载", app.Name, "管理员授权已取消，未启动卸载。", "用户已取消"));
                    canceled = true;
                    break;
                }
                catch (Exception ex)
                {
                    AddHistory(new(DateTime.UtcNow, "卸载", app.Name, ex.Message, "未完成"));
                    if (MessageBox.Show(this, $"{app.Name}\n\n{ex.Message}\n\n继续处理剩余软件？", "卸载未完成",
                            MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
                    { canceled = true; break; }
                }
                // Refresh after each item rather than waiting for the whole batch to finish.
                try { await ReloadApps(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                { Model.Status = "卸载后的刷新暂未完成，稍后将自动重试。"; }
                await ProcessReadyResiduePrompts(duringUninstall: true);
            }
        }
        finally
        {
            operation?.Dispose(); operation = null;
            uninstallQueueRunning = false;
            pendingRefresh.Extend(DateTime.UtcNow);
            Model.Busy = false;
        }
        bool refreshed = await RefreshApps();
        Model.Status = canceled ? "已停止卸载队列 · 已启动的卸载程序可能仍在运行" :
            $"队列已处理 · 启动 {started} 个卸载程序" + (refreshed ? " · 列表已自动刷新" : " · 列表刷新暂未完成") +
            (pendingRefresh.HasPending ? " · 继续检查延迟变化，返回窗口时也会刷新" : "");
        ScheduleResiduePrompts();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (Model.Busy) return;
        var categories = Model.Categories.Where(c => c.Selected).Select(c => c.Id).ToHashSet();
        if (categories.Count == 0) { Model.Status = "请先选择至少一个扫描类别。"; return; }
        Model.Busy = true;
        Model.Status = "正在准备扫描…";
        operation = new CancellationTokenSource();
        var progress = new Progress<string>(text => Model.Status = text);
        try
        {
            var rules = await Task.Run(CleanupRules.CreateDefault);
            var scan = await Task.Run(() => new CleanupScanner(rules).Scan(categories, operation.Token, progress));
            scanRules = rules;
            Model.SetScan(scan, categories);
            Model.Status = $"扫描完成 · 找到 {scan.Files.Count:N0} 个文件，共 {Format.Bytes(scan.Files.Sum(f => f.Length))} · 未执行清理";
        }
        catch (OperationCanceledException) { Model.Status = "扫描已取消 · 原有预览保留"; }
        catch (Exception ex) { ShowError(ex); }
        finally { operation?.Dispose(); operation = null; Model.Busy = false; }
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (Model.Busy) return;
        var selected = Model.Candidates.Where(c => c.Selected).ToList();
        if (selected.Count == 0) return;
        if (MessageBox.Show(this,
                $"将把预览中选中的 {selected.Count:N0} 个文件（{Format.Bytes(selected.Sum(f => f.Length))}）移入系统回收站。\n\n可在系统回收站中恢复。磁盘空间在回收站清空后才释放。\n占用中或扫描后发生变化的文件会被跳过。\n\n是否继续？",
                "确认整理文件", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        Model.Busy = true;
        operation = new CancellationTokenSource();
        var progress = new Progress<string>(text => Model.Status = text);
        try
        {
            var executor = new CleanupExecutor(scanRules, new WindowsRecycleBin());
            var result = await Task.Run(() => executor.Execute(selected, operation.Token, progress));
            string detail = $"移入回收站 {result.Succeeded} 个文件（{Format.Bytes(result.Bytes)}），跳过 {result.Skipped} 个。";
            AddHistory(new(DateTime.UtcNow, "清理", detail, HistoryDetails.Cleanup(selected, result),
                result.Skipped > 0 ? "部分完成" : "已移入回收站"));
            Model.InvalidateScan();
            Model.CleanupSummary = detail + "  请重新扫描查看剩余文件。";
            Model.RefreshDisk();
            Model.Status = detail;
        }
        catch (Exception ex)
        {
            Model.InvalidateScan();
            AddHistory(new(DateTime.UtcNow, "清理", "清理任务中断", ex.Message + " 部分文件可能已移入回收站，请重新扫描核对。", "请核对结果"));
            ShowError(ex);
        }
        finally { operation?.Dispose(); operation = null; Model.Busy = false; }
    }

    private void LoadHistory()
    {
        Model.History.Clear();
        foreach (var entry in history.Read()) Model.History.Add(entry);
    }

    private void AddHistory(OperationEntry entry)
    {
        try { history.Add(entry); LoadHistory(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Model.Status = "操作记录保存失败：" + ex.Message; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        operation?.Cancel();
        Model.Status = uninstallQueueRunning ? "正在停止任务 · 已启动的卸载程序仍由其自身控制" : "正在停止任务，请稍候…";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (Model.Busy)
        {
            e.Cancel = true;
            Model.Status = "请等待任务完成，或先点击“停止当前任务”。";
        }
    }

    private void OpenHistory_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(history.DirectoryPath);
        OpenShell(history.DirectoryPath);
    }
    private void WindowsApps_Click(object sender, RoutedEventArgs e) => OpenShell("ms-settings:appsfeatures");
    private void OpenRecycleBin_Click(object sender, RoutedEventArgs e) => OpenShell("shell:RecycleBinFolder");
    private void OpenShell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ShowError(Exception ex)
    {
        Model.Status = "操作未完成：" + ex.Message;
        MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private async Task RunUiSmoke()
    {
        string output = Path.GetTempPath();
        try
        {
            int flag = Array.IndexOf(launchArgs, "--ui-smoke");
            if (flag < 0 || flag + 1 >= launchArgs.Length)
                throw new InvalidOperationException("UI smoke output directory was not provided.");
            output = Path.GetFullPath(launchArgs[flag + 1]);
            Directory.CreateDirectory(output);
            await Task.Delay(650);
            if (App.LastUnhandledException is not null)
                throw new InvalidOperationException("Application startup raised an unhandled exception.", App.LastUnhandledException);
            var diskBinding = System.Windows.Data.BindingOperations.GetBinding(DiskUsageBar, System.Windows.Controls.Primitives.RangeBase.ValueProperty);
            if (diskBinding?.Mode != System.Windows.Data.BindingMode.OneWay)
                throw new InvalidOperationException("Disk usage must bind one-way to the read-only view model property.");
            if (Icon is null)
                throw new InvalidOperationException("The application window icon was not loaded.");
            AssertUiList("initial");
            foreach (int index in new[] { 3, 4, 1, 2, 0 })
            foreach (int direction in new[] { 0, 1 })
            {
                AppSort.SelectedIndex = index;
                AppSortDirection.SelectedIndex = direction;
                await Task.Delay(60);
                AssertAppOrder(index, direction);
            }
            AppSort.SelectedIndex = 3;
            AppSortDirection.SelectedIndex = 1;
            await Task.Delay(100);
            CaptureWindow(Path.Combine(output, "01-apps.png"));
            Width = MinWidth;
            Height = MinHeight;
            await Task.Delay(100);
            CaptureWindow(Path.Combine(output, "05-minimum-size.png"));
            Width = 1260;
            Height = 850;
            var demoScan = new ScanResult(
            [
                new() { Path = @"C:\Users\Demo\AppData\Local\Temp\preview-cache-a.tmp", Root = @"C:\Users\Demo\AppData\Local\Temp", CategoryId = "temp", CategoryTitle = "用户临时文件", Length = 2_621_440, LastWriteUtc = DateTime.UtcNow.AddDays(-14) },
                new() { Path = @"C:\Users\Demo\AppData\Local\Temp\preview-cache-b.tmp", Root = @"C:\Users\Demo\AppData\Local\Temp", CategoryId = "temp", CategoryTitle = "用户临时文件", Length = 4_089_856, LastWriteUtc = DateTime.UtcNow.AddDays(-10) },
                new() { Path = @"C:\Users\Demo\AppData\Local\CrashDumps\preview-app.dmp", Root = @"C:\Users\Demo\AppData\Local\CrashDumps", CategoryId = "crashes", CategoryTitle = "崩溃转储", Length = 12_582_912, LastWriteUtc = DateTime.UtcNow.AddDays(-9) }
            ], [], false);
            Model.SetScan(demoScan, new HashSet<string> { "temp", "crashes" });
            NavCleanup.IsChecked = true;
            await Task.Delay(200);
            CaptureWindow(Path.Combine(output, "02-cleanup.png"));
            NavHistory.IsChecked = true;
            await Task.Delay(200);
            CaptureWindow(Path.Combine(output, "03-history.png"));
            NavAbout.IsChecked = true;
            await Task.Delay(200);
            CaptureWindow(Path.Combine(output, "04-about.png"));
            await VerifyAutomaticResidueDialog(output);
            await VerifyLargeFilesPage(output);
            NavApps.IsChecked = true;
            Model.Query = "Microsoft";
            await Task.Delay(250);
            AssertUiList("filtered search");
            if (Model.AppsView.Cast<InstalledApp>().Any(a =>
                !a.Name.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) &&
                !a.Publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Application search returned a non-matching row.");
            Model.Query = "";
            await Task.Delay(200);
            AssertUiList("search reset");
            await VerifyAutomaticRefresh();
            if (App.LastUnhandledException is not null)
                throw new InvalidOperationException("Application raised an unhandled exception.", App.LastUnhandledException);
            File.WriteAllText(Path.Combine(output, "ui-smoke.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                ok = true, apps = Model.AppCount, searchVerified = true, pages = 5,
                sortCombinations = 10, automaticResidueDialogVerified = true, residueConfirmVerified = true,
                residueCancelVerified = true, residueDelayVerified = true, residueDuplicatePromptPrevented = true,
                largeFilesPageVerified = true, largeFilesSelectionVerified = true, largeFilesFilterSortVerified = true,
                usageSortingVerified = true, unknownUsageLast = true,
                automaticRefreshVerified = true, refreshPreservesState = true,
                demoDataOnly = true, diskBinding = "OneWay", windowIconLoaded = Icon is not null,
                unhandledException = App.LastUnhandledException?.ToString(),
                destructiveActions = 0
            }));
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "ui-smoke-error.txt"), ex.ToString());
        }
        finally
        {
            Close();
        }
    }

    private void AssertUiList(string phase)
    {
        int expectedCount = Model.Apps.Count(a => string.IsNullOrWhiteSpace(Model.Query) ||
            a.Name.Contains(Model.Query, StringComparison.CurrentCultureIgnoreCase) ||
            a.Publisher.Contains(Model.Query, StringComparison.CurrentCultureIgnoreCase));
        int viewCount = Model.AppsView.Cast<InstalledApp>().Count();
        if (expectedCount != viewCount || viewCount != AppsGrid.Items.Count)
            throw new InvalidOperationException($"Application list mismatch during {phase}: expected={expectedCount}, view={viewCount}, grid={AppsGrid.Items.Count}; status={Model.Status}");
    }

    private void AssertAppOrder(int index, int direction = 0)
    {
        if (Model.AppSortIndex != index || Model.AppSortDirectionIndex != direction)
            throw new InvalidOperationException("Sorting selectors did not update their bindings.");
        var actual = Model.AppsView.Cast<InstalledApp>().ToList();
        var names = StringComparer.Create(Model.AppsView.Culture, false);
        IEnumerable<InstalledApp> expected = (index, direction) switch
        {
            (1, 0) => Model.Apps.OrderByDescending(a => a.HasSizeEstimate).ThenBy(a => a.EstimatedBytes).ThenBy(a => a.Name, names),
            (1, 1) => Model.Apps.OrderByDescending(a => a.HasSizeEstimate).ThenByDescending(a => a.EstimatedBytes).ThenBy(a => a.Name, names),
            (2, 0) => Model.Apps.OrderByDescending(a => a.HasInstallDate).ThenBy(a => a.InstallDate, names).ThenBy(a => a.Name, names),
            (2, 1) => Model.Apps.OrderByDescending(a => a.HasInstallDate).ThenByDescending(a => a.InstallDate, names).ThenBy(a => a.Name, names),
            (3, 0) => Model.Apps.OrderByDescending(a => a.HasUsageRecord).ThenBy(a => a.LastUsedUtc).ThenBy(a => a.Name, names),
            (3, 1) => Model.Apps.OrderByDescending(a => a.HasUsageRecord).ThenByDescending(a => a.LastUsedUtc).ThenBy(a => a.Name, names),
            (4, 0) => Model.Apps.OrderBy(a => a.Publisher, names).ThenBy(a => a.Name, names),
            (4, 1) => Model.Apps.OrderByDescending(a => a.Publisher, names).ThenBy(a => a.Name, names),
            (0, 1) => Model.Apps.OrderByDescending(a => a.Name, names),
            _ => Model.Apps.OrderBy(a => a.Name, names)
        };
        if (!actual.Select(a => a.Id).SequenceEqual(expected.Select(a => a.Id)))
            throw new InvalidOperationException($"Incorrect application sort order for option {index}: actual={string.Join(',', actual.Select(a => a.Id))}; expected={string.Join(',', expected.Select(a => a.Id))}; sorts={string.Join(',', Model.AppsView.SortDescriptions.Select(s => s.PropertyName + ':' + s.Direction))}.");
        if (index == 3)
        {
            bool unknownSeen = false;
            foreach (var app in actual)
            {
                if (!app.HasUsageRecord) unknownSeen = true;
                else if (unknownSeen) throw new InvalidOperationException("Missing usage records must be sorted last.");
            }
        }
    }

    private async Task VerifyAutomaticRefresh()
    {
        var survivor = Model.Apps.Single(a => a.Id == "demo-4");
        survivor.Selected = true;
        Model.Query = "Microsoft";
        AppSort.SelectedIndex = 3;
        AppSortDirection.SelectedIndex = 0;
        int reads = 0;
        demoAppsReader = () =>
        {
            reads++;
            // The first poll still sees the app; the next one models a child uninstaller removing it later.
            return CreateDemoApps().Where(a => reads < 2 || a.Id != "demo-2").ToList();
        };
        Model.Busy = true;
        uninstallQueueRunning = true;
        TrackUninstall("demo-2");
        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(9);
            while (pendingRefresh.HasPending && DateTime.UtcNow < deadline) await Task.Delay(100);
            if (pendingRefresh.HasPending || reads < 2 || Model.Apps.Any(a => a.Id == "demo-2"))
                throw new InvalidOperationException("Automatic uninstall refresh did not pick up the delayed removal.");
            if (!ReferenceEquals(survivor, Model.Apps.Single(a => a.Id == "demo-4")) || !survivor.Selected ||
                Model.Query != "Microsoft" || Model.AppSortIndex != 3 || Model.AppSortDirectionIndex != 0 || Model.AppCount != 5)
                throw new InvalidOperationException("Automatic refresh lost row selection, filtering, sorting or app count.");
            AssertUiList("automatic uninstall refresh");
            Model.Query = "";
            AssertAppOrder(3);
            // A second delayed removal happens after the queue has finished, while the user can use the window.
            Model.Busy = false;
            uninstallQueueRunning = false;
            demoAppsReader = () => CreateDemoApps().Where(a => a.Id is not ("demo-2" or "demo-1")).ToList();
            TrackUninstall("demo-1");
            deadline = DateTime.UtcNow.AddSeconds(5);
            while (pendingRefresh.HasPending && DateTime.UtcNow < deadline) await Task.Delay(100);
            if (pendingRefresh.HasPending || Model.AppCount != 4 || !survivor.Selected)
                throw new InvalidOperationException("Automatic refresh stopped before the child uninstaller removed its entry.");
            AssertAppOrder(3);
            // Metadata and usage updates keep selection and reorder the existing view immediately.
            var updated = CreateDemoApps().Where(a => a.Id is not ("demo-2" or "demo-1")).Select(a => a.Id == "demo-4"
                ? new InstalledApp { Id = a.Id, Name = a.Name, Publisher = a.Publisher, Version = "2026.2", LastUsedUtc = DateTime.UtcNow.AddDays(-90) }
                : a).ToList();
            Model.SetApps(updated);
            if (!Model.Apps.Single(a => a.Id == "demo-4").Selected || Model.AppsView.Cast<InstalledApp>().First().Id != "demo-4")
                throw new InvalidOperationException("Refreshing changed metadata lost selection or failed to reorder usage dates.");
        }
        finally
        {
            uninstallQueueRunning = false;
            Model.Busy = false;
            uninstallRefreshTimer.Stop();
            demoAppsReader = CreateDemoApps;
            Model.Query = "";
            Model.SetApps(CreateDemoApps());
            foreach (var app in Model.Apps) app.Selected = false;
            AppSort.SelectedIndex = 3;
            AppSortDirection.SelectedIndex = 1;
        }
    }

    private async Task VerifyAutomaticResidueDialog(string output)
    {
        if (FindName("NavResidues") is not null || FindName("OpenResiduesButton") is not null || FindName("ResiduePage") is not null)
            throw new InvalidOperationException("Standalone residue menu, button or page must be removed.");
        NavApps.IsChecked = true;
        int scans = 0, dialogs = 0, executions = 0;
        Exception? dialogError = null;
        var demoApp = CreateDemoApps().Single(a => a.Id == "demo-1");
        var plan = new ResiduePlan { Id = "demo-residue", App = ResidueApp.From(demoApp), PromptPending = true };
        var address = new RegistryAddress(Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Registry64, @"Software\轻笔记工作室\轻笔记");
        demoResidueScanner = (target, _) =>
        {
            scans++;
            return new(target,
            [
                new() { Kind = ResidueKind.File, Path = @"C:\Users\Demo\AppData\Roaming\轻笔记\settings.json", Root = @"C:\Users\Demo\AppData\Roaming\轻笔记", Length = 4096, Evidence = "卸载前记录的专属目录 · 演示数据" },
                new() { Kind = ResidueKind.RegistryKey, Path = address.DisplayName, Address = address, Fingerprint = "demo", Evidence = "产品专属键 · 删除前导出备份 · 演示数据" }
            ], []);
        };
        demoResidueExecutor = (scan, _) =>
        {
            if (scan.Candidates.Count != 2) throw new InvalidOperationException("Confirmation must pass the entire preview to cleanup.");
            executions++;
            return new(1, 1, 0, @"C:\Users\Demo\AppData\Local\ClearSpace\RegistryBackups\demo", ["Demo only; no actual files or registry entries were modified."]);
        };
        void ArmDialog(bool confirm, bool capture = false, bool closeWindow = false)
        {
            demoResidueDialog = dialog =>
            {
                dialogs++;
                dialog.Loaded += async (_, _) =>
                {
                    try
                    {
                        await Task.Delay(90);
                        if (!Model.Busy || dialog.Owner != this || dialog.ResidueList.Items.Count != 2 ||
                            dialog.ResidueList.Columns.Count != 3 || !dialog.ConfirmButton.IsEnabled ||
                            dialog.ConfirmButton.Content.ToString() != "确定" || dialog.DismissButton.Content.ToString() != "取消")
                            throw new InvalidOperationException("Residue dialog must show the full list with only OK and Cancel actions.");
                        await ProcessReadyResiduePrompts();
                        if (capture)
                        {
                            CaptureWindow(dialog, Path.Combine(output, "06-residue-dialog.png"));
                            dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight;
                            await Task.Delay(80);
                            CaptureWindow(dialog, Path.Combine(output, "07-residue-dialog-minimum.png"));
                        }
                        if (closeWindow) dialog.Close();
                        else
                        {
                            var button = confirm ? dialog.ConfirmButton : dialog.DismissButton;
                            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        }
                    }
                    catch (Exception ex) { dialogError = ex; dialog.Close(); }
                };
            };
        }
        try
        {
            residuePlans.Add(plan);
            ArmDialog(confirm: true, capture: true);
            ObserveResiduePlans(CreateDemoApps());
            await ProcessReadyResiduePrompts();
            if (scans != 0 || dialogs != 0) throw new InvalidOperationException("Software still installed must not trigger a residue prompt.");
            // During an uninstall, registry removal alone does not interrupt its still-running process.
            Model.Busy = true;
            waitingUninstallIds.Add(plan.App.Id);
            ObserveResiduePlans(CreateDemoApps().Where(a => a.Id != plan.App.Id).ToList());
            await ProcessReadyResiduePrompts(duringUninstall: true);
            if (scans != 0 || dialogs != 0) throw new InvalidOperationException("Residue prompt interrupted a running uninstaller.");
            waitingUninstallIds.Remove(plan.App.Id);
            await ProcessReadyResiduePrompts(duringUninstall: true);
            if (dialogError is not null) throw dialogError;
            if (dialogs != 1 || executions != 1 || plan.PromptPending) throw new InvalidOperationException("OK did not clean the entire preview exactly once.");
            Model.Busy = false;
            await ProcessReadyResiduePrompts();
            if (dialogs != 1 || executions != 1) throw new InvalidOperationException("Completed residue cleanup was prompted twice.");

            // A delayed child-uninstaller removal is detected by the same timer as the real workflow.
            var delayedApp = CreateDemoApps().Single(a => a.Id == "demo-2");
            var delayed = new ResiduePlan { Id = "demo-delayed", App = ResidueApp.From(delayedApp), PromptPending = true };
            residuePlans.Add(delayed);
            ArmDialog(confirm: false);
            demoAppsReader = () => CreateDemoApps().Where(a => a.Id != delayedApp.Id).ToList();
            TrackUninstall(delayedApp.Id);
            DateTime deadline = DateTime.UtcNow.AddSeconds(6);
            while ((delayed.PromptPending || processingResiduePrompts) && DateTime.UtcNow < deadline) await Task.Delay(100);
            if (dialogError is not null) throw dialogError;
            if (dialogs != 2 || executions != 1 || delayed.PromptPending)
                throw new InvalidOperationException("Delayed removal did not prompt automatically, or Cancel executed cleanup.");
            await ReloadApps();
            await ProcessReadyResiduePrompts();
            if (dialogs != 2) throw new InvalidOperationException("Cancel caused a duplicate residue prompt.");

            var closed = new ResiduePlan { Id = "demo-closed", App = plan.App, PromptPending = true, RemovalObserved = true };
            residuePlans.Add(closed);
            ArmDialog(confirm: false, closeWindow: true);
            await ProcessReadyResiduePrompts();
            if (dialogError is not null) throw dialogError;
            await ProcessReadyResiduePrompts();
            if (dialogs != 3 || executions != 1 || closed.PromptPending)
                throw new InvalidOperationException("Closing the dialog must preserve residues and prevent repeat prompts.");

            var empty = new ResiduePlan { Id = "demo-empty", App = plan.App, PromptPending = true, RemovalObserved = true };
            residuePlans.Add(empty);
            demoResidueScanner = (target, _) => new(target, [], []);
            demoResidueDialog = dialog =>
            {
                dialogs++;
                dialog.Loaded += (_, _) => dialog.ConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            await ProcessReadyResiduePrompts();
            if (dialogs != 4 || executions != 1 || empty.PromptPending)
                throw new InvalidOperationException("An empty residue list should close without executing cleanup.");

            // A blocked scan is not an empty successful scan: do not claim that a reinstalled app is uninstalled.
            var blocked = new ResiduePlan { Id = "demo-blocked", App = plan.App, PromptPending = true, RemovalObserved = true };
            residuePlans.Add(blocked);
            demoResidueScanner = (target, _) => new(target, [], ["Demo reinstallation"], Blocked: true);
            await ProcessReadyResiduePrompts();
            if (dialogs != 4 || executions != 1 || blocked.PromptPending)
                throw new InvalidOperationException("Reinstalled software must not show a residue confirmation dialog.");
        }
        finally
        {
            demoResidueDialog = null; demoResidueExecutor = null; demoResidueScanner = null;
            residuePlans.Clear(); waitingUninstallIds.Clear();
            uninstallRefreshTimer.Stop();
            Model.Busy = false;
            demoAppsReader = CreateDemoApps;
            Model.SetApps(CreateDemoApps());
        }
    }

    private void CaptureWindow(string path) => CaptureWindow(this, path);

    private static void CaptureWindow(Window window, string path)
    {
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
