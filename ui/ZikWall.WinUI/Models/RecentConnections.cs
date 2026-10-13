namespace ZikWall.WinUI.Models;

public sealed record RecentConnectionBatch(IReadOnlyList<ConnectionEntry> Connections, int Count, ConnectionNotificationType Type = ConnectionNotificationType.AllConnections);
public enum ConnectionNotificationType { AllConnections, NewApps, Outgoing, Incoming }

public sealed class RecentConnections
{
    private bool baseline;
    private DateTimeOffset lastAlert;
    private readonly Dictionary<(string App, string Ip, int? Port, string Direction), DateTimeOffset> seen = new();
    private readonly List<ConnectionEntry> pending = [];
    private int pendingCount;
    // Session-only, bounded memory. On saturation stop classifying apps as new rather than
    // evicting identities and repeatedly announcing old apps as newly observed.
    private readonly HashSet<string> knownApps = new(StringComparer.OrdinalIgnoreCase);
    private ConnectionNotificationType notificationType;
    public RecentConnectionBatch? Observe(IReadOnlyList<ConnectionEvent> events, DateTimeOffset now, bool enabled, Func<ConnectionEntry, bool>? include = null,
        ConnectionNotificationType type = ConnectionNotificationType.AllConnections)
    {
        if (notificationType != type) { notificationType = type; ClearPending(); }
        bool suppress = !baseline || !enabled;
        baseline = true;
        if (suppress) ClearPending();
        foreach (var entry in events.Where(x => x.Event == "First seen").Select(x => x.Connection))
        {
            if (entry.NetworkScope != "Internet" || entry.RemotePort is not > 0) continue;
            bool newApp = knownApps.Count < 16384 && knownApps.Add(entry.AppKey);
            if (suppress || include?.Invoke(entry) == false || type switch
                {
                    ConnectionNotificationType.NewApps => !newApp,
                    ConnectionNotificationType.Outgoing => entry.Direction != "Outgoing",
                    ConnectionNotificationType.Incoming => entry.Direction != "Incoming",
                    _ => false
                }) continue;
            var key = (entry.ProcessPath.Length > 0 ? entry.ProcessPath : entry.Application, entry.RemoteAddress, entry.RemotePort, entry.Direction);
            if (seen.TryGetValue(key, out var previous) && now - previous < TimeSpan.FromMinutes(5)) continue;
            if (seen.Count >= 4096) seen.Clear();
            seen[key] = now;
            if (pending.Count < 3) pending.Add(entry);
            pendingCount = Math.Min(10000, pendingCount + 1);
        }
        if (suppress) return null;
        if (pendingCount == 0 || now - lastAlert < TimeSpan.FromSeconds(30)) return null;
        var batch = new RecentConnectionBatch(pending.ToArray(), pendingCount, type);
        pending.Clear(); pendingCount = 0; lastAlert = now;
        return batch;
    }
    public void ClearPending() { pending.Clear(); pendingCount = 0; }
    public void ResetBaseline() { baseline = false; ClearPending(); }
}
