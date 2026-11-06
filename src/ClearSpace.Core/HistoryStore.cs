using System.Text;
using System.Text.Json;

namespace ClearSpace.Core;

public sealed class HistoryStore
{
    public string DirectoryPath { get; }
    public string FilePath => Path.Combine(DirectoryPath, "history.json");
    private readonly object gate = new();
    public HistoryStore(string? directory = null) => DirectoryPath = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClearSpace");

    public IReadOnlyList<OperationEntry> Read()
    {
        lock (gate)
        {
            try
            {
                return File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<List<OperationEntry>>(File.ReadAllText(FilePath)) ?? [] : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
        }
    }

    public void Add(OperationEntry entry)
    {
        lock (gate)
        {
            Directory.CreateDirectory(DirectoryPath);
            var entries = Read().Prepend(entry).Take(500).ToList();
            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(temporary, FilePath, true);
        }
    }
}

public static class HistoryDetails
{
    public const int MaximumListedPaths = 100;

    public static string Cleanup(IReadOnlyList<CleanupCandidate> selected, CleanupResult result)
    {
        var detail = new System.Text.StringBuilder();
        detail.Append("本次选择的文件（最多展示 ").Append(MaximumListedPaths).AppendLine(" 条）：");
        foreach (var candidate in selected.Take(MaximumListedPaths)) detail.AppendLine(candidate.Path);
        if (selected.Count > MaximumListedPaths)
            detail.Append("……另有 ").Append(selected.Count - MaximumListedPaths).AppendLine(" 个已选择文件未写入操作记录。");

        if (result.Errors.Count > 0)
        {
            detail.AppendLine().Append("跳过原因（最多展示 ").Append(MaximumListedPaths).AppendLine(" 条）：");
            foreach (var error in result.Errors.Take(MaximumListedPaths)) detail.AppendLine(error);
            if (result.Errors.Count > MaximumListedPaths)
                detail.Append("……另有 ").Append(result.Errors.Count - MaximumListedPaths).AppendLine(" 项原因未写入操作记录。");
        }
        return detail.ToString().TrimEnd();
    }
}
