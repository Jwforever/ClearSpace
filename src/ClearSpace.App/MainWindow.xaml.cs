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
        HistoryPage.Visibility = page == "history" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = page == "about" ? Visibility.Visible : Visibility.Collapsed;
        (Model.PageTitle, Model.PageDescription) = page switch
        {
            "cleanup" => ("空间清理", "从一次预览开始，为电脑留出更多空间。"),
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
            Model.RefreshDisk();
            int removed = pendingRefresh.Observe(apps);
            if (removed > 0 && !Model.Busy)
                Model.Status = $"已自动刷新 · {removed} 个软件的安装记录已移除 · 刷新于 {DateTime.Now:HH:mm:ss}";
        }
        finally { appReadGate.Release(); }
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
                $"将依次启动以下软件自带的卸载程序：\n\n{list}\n\n卸载窗口可能需要你继续操作。当前版本不会自动删除软件残留，也不提供卸载撤销。\n\n是否继续？",
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
                    using var process = command.Start(app.MachineWide && !admin);
                    started++;
                    TrackUninstall(app.Id);
                    AddHistory(new(DateTime.UtcNow, "卸载", app.Name, "已启动软件自带的卸载程序，等待退出。", "已启动"));
                    try { await process.WaitForExitAsync(operation.Token); }
                    catch (OperationCanceledException)
                    {
                        canceled = true;
                        AddHistory(new(DateTime.UtcNow, "卸载", app.Name, "已停止等待；已启动的卸载程序由其自身控制。", "已停止等待"));
                        break;
                    }
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
        Model.Status = "正在停止任务 · 已启动的卸载程序仍由其自身控制";
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
            {
                AppSort.SelectedIndex = index;
                await Task.Delay(60);
                AssertAppOrder(index);
            }
            AppSort.SelectedIndex = 3;
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
                ok = true, apps = Model.AppCount, searchVerified = true, pages = 4,
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

    private void AssertAppOrder(int index)
    {
        if (Model.AppSortIndex != index) throw new InvalidOperationException("Sorting selector did not update its binding.");
        var actual = Model.AppsView.Cast<InstalledApp>().ToList();
        var names = StringComparer.Create(Model.AppsView.Culture, false);
        IEnumerable<InstalledApp> expected = index switch
        {
            1 => Model.Apps.OrderByDescending(a => a.EstimatedBytes).ThenBy(a => a.Name, names),
            2 => Model.Apps.OrderByDescending(a => a.InstallDate).ThenBy(a => a.Name, names),
            3 => Model.Apps.OrderByDescending(a => a.HasUsageRecord).ThenByDescending(a => a.LastUsedUtc).ThenBy(a => a.Name, names),
            4 => Model.Apps.OrderByDescending(a => a.HasUsageRecord).ThenBy(a => a.LastUsedUtc).ThenBy(a => a.Name, names),
            _ => Model.Apps.OrderBy(a => a.Name, names)
        };
        if (!actual.Select(a => a.Id).SequenceEqual(expected.Select(a => a.Id)))
            throw new InvalidOperationException($"Incorrect application sort order for option {index}: actual={string.Join(',', actual.Select(a => a.Id))}; expected={string.Join(',', expected.Select(a => a.Id))}; sorts={string.Join(',', Model.AppsView.SortDescriptions.Select(s => s.PropertyName + ':' + s.Direction))}.");
        if (index is 3 or 4)
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
        AppSort.SelectedIndex = 4;
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
                Model.Query != "Microsoft" || Model.AppSortIndex != 4 || Model.AppCount != 5)
                throw new InvalidOperationException("Automatic refresh lost row selection, filtering, sorting or app count.");
            AssertUiList("automatic uninstall refresh");
            Model.Query = "";
            AssertAppOrder(4);
            // A second delayed removal happens after the queue has finished, while the user can use the window.
            Model.Busy = false;
            uninstallQueueRunning = false;
            demoAppsReader = () => CreateDemoApps().Where(a => a.Id is not ("demo-2" or "demo-1")).ToList();
            TrackUninstall("demo-1");
            deadline = DateTime.UtcNow.AddSeconds(5);
            while (pendingRefresh.HasPending && DateTime.UtcNow < deadline) await Task.Delay(100);
            if (pendingRefresh.HasPending || Model.AppCount != 4 || !survivor.Selected)
                throw new InvalidOperationException("Automatic refresh stopped before the child uninstaller removed its entry.");
            AssertAppOrder(4);
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
        }
    }

    private void CaptureWindow(string path)
    {
        UpdateLayout();
        var root = (FrameworkElement)Content;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render((System.Windows.Media.Visual)Content);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
