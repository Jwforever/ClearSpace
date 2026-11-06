using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ClearSpace.Core;

public sealed class InstalledAppsService
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<InstalledApp> Read()
    {
        var apps = new List<InstalledApp>();
        foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var root = baseKey.OpenSubKey(UninstallKey);
                if (root is null) continue;
                foreach (string subName in root.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = root.OpenSubKey(subName);
                        if (sub is null) continue;
                        string name = Text(sub, "DisplayName");
                        if (string.IsNullOrWhiteSpace(name) || Text(sub, "SystemComponent") == "1" ||
                            !string.IsNullOrEmpty(Text(sub, "ParentKeyName"))) continue;
                        string uninstall = Text(sub, "UninstallString");
                        string date = Text(sub, "InstallDate");
                        if (DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture,
                                DateTimeStyles.None, out var parsed)) date = parsed.ToString("yyyy-MM-dd");
                        long.TryParse(Text(sub, "EstimatedSize"), out long kb);
                        // DWORD values larger than Int32.MaxValue may be returned as a signed int.
                        if (sub.GetValue("EstimatedSize") is int signed && signed < 0) kb = (uint)signed;
                        apps.Add(new InstalledApp
                        {
                            Id = $"{hive}|{view}|{subName}", Name = name.Trim(),
                            Publisher = EmptyFallback(Text(sub, "Publisher"), "未知发布者"),
                            Version = EmptyFallback(Text(sub, "DisplayVersion"), "—"),
                            InstallDate = EmptyFallback(date, "—"),
                            InstallLocation = Text(sub, "InstallLocation").Trim('"'),
                            UninstallString = uninstall, MachineWide = hive == RegistryHive.LocalMachine,
                            RegistryPath = $"{hive} ({view})\\{UninstallKey}\\{subName}",
                            EstimatedBytes = kb > 0 && kb < long.MaxValue / 1024 ? kb * 1024 : 0
                        });
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return apps.GroupBy(a => (a.Name.ToUpperInvariant(), a.Version, a.UninstallString, a.MachineWide))
            .Select(g => g.First()).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string Text(RegistryKey key, string name) => key.GetValue(name)?.ToString()?.Trim() ?? "";
    private static string EmptyFallback(string text, string fallback) => string.IsNullOrWhiteSpace(text) ? fallback : text;
}

public sealed record UninstallCommand(string Executable, string Arguments)
{
    public static UninstallCommand Parse(string input, string windowsDirectory)
    {
        string command = Environment.ExpandEnvironmentVariables(input.Trim());
        if (command.Length == 0) throw new InvalidOperationException("此软件没有提供卸载命令，可在 Windows 设置中尝试卸载。");
        string executable;
        string arguments;
        if (command.StartsWith('"'))
        {
            int closing = command.IndexOf('"', 1);
            if (closing < 2) throw new InvalidOperationException("卸载命令的引号不完整。");
            executable = command[1..closing];
            arguments = command[(closing + 1)..].Trim();
        }
        else
        {
            var match = Regex.Match(command, @"^(.+?\.exe)(?=\s|$)", RegexOptions.IgnoreCase);
            if (!match.Success) throw new InvalidOperationException("暂不支持这种卸载命令，可在 Windows 设置中卸载。");
            executable = match.Groups[1].Value;
            arguments = command[match.Length..].Trim();
        }
        string file = Path.GetFileName(executable);
        if (file.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
        {
            executable = Path.Combine(windowsDirectory, "System32", "msiexec.exe");
            arguments = Regex.Replace(arguments, @"(?<!\S)[/-]I(?=\s*\{[0-9a-f-]{36}\})", "/X", RegexOptions.IgnoreCase);
        }
        else if (!Path.IsPathFullyQualified(executable))
        {
            throw new InvalidOperationException("卸载程序未提供完整路径，请使用 Windows 设置卸载。");
        }
        string[] shells = ["cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe"];
        if (shells.Contains(file, StringComparer.OrdinalIgnoreCase) ||
            !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("此卸载方式需要脚本解释器，请使用 Windows 设置卸载。");
        return new UninstallCommand(executable, arguments);
    }

    public Process Start(bool elevate)
    {
        if (!File.Exists(Executable)) throw new FileNotFoundException("找不到卸载程序，安装记录可能已失效。", Executable);
        var start = new ProcessStartInfo
        {
            FileName = Executable, Arguments = Arguments, UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(Executable)!,
            Verb = elevate ? "runas" : "open"
        };
        return Process.Start(start) ?? throw new InvalidOperationException("未能启动卸载程序。");
    }
}
