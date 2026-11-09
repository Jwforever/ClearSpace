namespace ClearSpace.Core;

/// <summary>Tracks registry removals, never assumes that a successful launcher exit means uninstall success.</summary>
public sealed class PendingUninstallRefresh
{
    private readonly HashSet<string> ids = new(StringComparer.Ordinal);
    private DateTime pollUntilUtc;
    public bool HasPending => ids.Count > 0;

    public void Track(string id, DateTime nowUtc)
    {
        ids.Add(id);
        Extend(nowUtc);
    }

    public void Extend(DateTime nowUtc) => pollUntilUtc = nowUtc.AddMinutes(3);

    public bool ShouldPoll(DateTime nowUtc, bool queueRunning) =>
        HasPending && (queueRunning || nowUtc <= pollUntilUtc);

    public int Observe(IReadOnlyList<InstalledApp> apps)
    {
        var installed = apps.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        return ids.RemoveWhere(id => !installed.Contains(id));
    }
}
