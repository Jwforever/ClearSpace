using System.Windows;
using ClearSpace.Core;

namespace ClearSpace.App;

public partial class ResidueDialog : Window
{
    public IReadOnlyList<ResidueCandidate> Candidates { get; }
    public string Heading { get; }
    public string Summary { get; }
    public string ActionHint { get; }
    public string Notes { get; }

    public ResidueDialog(ResidueScan scan)
    {
        Candidates = scan.Candidates;
        Heading = scan.Plan.App.Name + " 已卸载";
        int files = Candidates.Count(c => c.Kind == ResidueKind.File), keys = Candidates.Count - files;
        Summary = Candidates.Count == 0 ? "扫描完成，没有发现可清理的残留。" :
            $"发现 {Candidates.Count} 项残留 · {files} 个文件（{Format.Bytes(Candidates.Sum(c => c.Length))}） · {keys} 个注册表项";
        ActionHint = Candidates.Count == 0 ? "点击“确定”返回软件列表。" :
            "点击“确定”清理列表中的全部残留；点击“取消”保留。文件移入回收站，注册表先备份再删除。\n残留可能包含设置、存档或个人内容，请核对后确认。";
        Notes = string.Join("  ", scan.Notes.Take(5));
        InitializeComponent();
        DataContext = this;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Dismiss_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
