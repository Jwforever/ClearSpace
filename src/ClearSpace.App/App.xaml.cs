using System.Windows;
using System.IO;

namespace ClearSpace.App;

public partial class App : Application
{
    public static Exception? LastUnhandledException { get; private set; }

    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            LastUnhandledException = e.Exception;
            string[] args = Environment.GetCommandLineArgs();
            int flag = Array.IndexOf(args, "--ui-smoke");
            if (flag >= 0 && flag + 1 < args.Length)
            {
                try
                {
                    string folder = Path.GetFullPath(args[flag + 1]);
                    Directory.CreateDirectory(folder);
                    File.WriteAllText(Path.Combine(folder, "ui-smoke-error.txt"), e.Exception.ToString());
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                e.Handled = true;
                Current.Shutdown(1);
                return;
            }
            MessageBox.Show("操作未完成：" + e.Exception.Message, "ClearSpace 清屿", MessageBoxButton.OK, MessageBoxImage.Information);
            e.Handled = true;
        };
    }
}
