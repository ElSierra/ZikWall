namespace ZikWall.WinUI.Models;

public sealed record RecentConnectionBatch(IReadOnlyList<ConnectionEntry> Connections, int Count);

public sealed class RecentConnections
{
    private bool baseline;
    private DateTimeOffset lastAlert;
    private readonly Dictionary<(string App, string Ip, int? Port, string Direction), DateTimeOffset> seen = new();
    private readonly List<ConnectionEntry> pending = [];
    private int pendingCount;
    public RecentConnectionBatch? Observe(IReadOnlyList<ConnectionEvent> events, DateTimeOffset now, bool enabled, Func<ConnectionEntry, bool>? include = null)
    {
        if (!baseline || !enabled) { baseline = true; pending.Clear(); pendingCount = 0; return null; }
        foreach (var entry in events.Where(x => x.Event == "First seen").Select(x => x.Connection))
        {
            if (entry.NetworkScope != "Internet" || entry.RemotePort is not > 0 || include?.Invoke(entry) == false) continue;
            var key = (entry.ProcessPath.Length > 0 ? entry.ProcessPath : entry.Application, entry.RemoteAddress, entry.RemotePort, entry.Direction);
            if (seen.TryGetValue(key, out var previous) && now - previous < TimeSpan.FromMinutes(5)) continue;
            if (seen.Count >= 4096) seen.Clear();
            seen[key] = now;
            if (pending.Count < 3) pending.Add(entry);
            pendingCount = Math.Min(10000, pendingCount + 1);
        }
        if (pendingCount == 0 || now - lastAlert < TimeSpan.FromSeconds(30)) return null;
        var batch = new RecentConnectionBatch(pending.ToArray(), pendingCount);
        pending.Clear(); pendingCount = 0; lastAlert = now;
        return batch;
    }
    public void ClearPending() { pending.Clear(); pendingCount = 0; }
    public void ResetBaseline() { baseline = false; ClearPending(); }
}
