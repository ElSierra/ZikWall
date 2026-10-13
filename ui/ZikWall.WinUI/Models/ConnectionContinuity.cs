namespace ZikWall.WinUI.Models;

// Windows hands closing TCP sockets to PID 0, reports unreliable states for them
// (even Listen/Closed on a :443 peer) and stops answering SYN-option queries.
// Remember each connected 4-tuple's owner and direction while it is live.
public sealed class ConnectionContinuity(Func<ConnectionEntry, string> readDirection)
{
    public const string Closing = "Closing";
    public const string Unowned = "Unowned";
    private const long RetentionMs = 5 * 60 * 1000;
    private readonly record struct Known(ConnectionEntry Owner, string Direction, bool Measured, long Seen);
    private readonly Dictionary<ConnectionIdentity, Known> known = new();

    public static ConnectionIdentity Tuple(ConnectionEntry entry) => entry.Identity with { ProcessId = 0 };
    public static bool IsOrphaned(ConnectionEntry entry) => entry.ProcessId == 0 && entry.RemotePort is > 0;

    public IReadOnlyList<ConnectionEntry> Apply(IReadOnlyList<ConnectionEntry> rows, long nowMs)
    {
        if (known.Count > 0)
            foreach (var (tuple, entry) in known)
                if (nowMs - entry.Seen > RetentionMs) known.Remove(tuple);
        // An accepted connection shares its local port with a listener; an
        // outgoing one gets its own port. Dual-stack listeners on :: accept IPv4 too.
        var bound = new HashSet<(string Address, int Port)>();
        var wildcard = new HashSet<int>();
        foreach (var row in rows)
            if (row.Protocol.StartsWith("TCP", StringComparison.Ordinal) && row.RemotePort == 0)
                if (row.LocalAddress is "0.0.0.0" or "::") wildcard.Add(row.LocalPort); else bound.Add((row.LocalAddress, row.LocalPort));
        string Infer(ConnectionEntry row) => wildcard.Contains(row.LocalPort) || bound.Contains((row.LocalAddress, row.LocalPort)) ? "Incoming" : "Outgoing";

        var result = new ConnectionEntry[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!row.Protocol.StartsWith("TCP", StringComparison.Ordinal) || row.RemotePort is not > 0)
            {
                result[i] = row with { Direction = readDirection(row) };
                continue;
            }
            var tuple = Tuple(row);
            bool hasKnown = known.TryGetValue(tuple, out var previous);
            if (IsOrphaned(row))
            {
                row = row with { State = Closing };
                if (hasKnown && previous.Owner.ProcessId != 0)
                    row = row with { ProcessName = previous.Owner.ProcessName, ProcessPath = previous.Owner.ProcessPath,
                        PackageFamily = previous.Owner.PackageFamily, ProcessStarted = previous.Owner.ProcessStarted };
            }
            string direction; bool measured;
            if (hasKnown && previous.Measured) (direction, measured) = (previous.Direction, true);
            else
            {
                direction = readDirection(row);
                measured = direction is "Incoming" or "Outgoing";
                if (!measured) direction = hasKnown ? previous.Direction : Infer(row);
            }
            row = row with { Direction = direction };
            known[tuple] = new(row.ProcessId != 0 || !hasKnown ? row : previous.Owner, direction, measured, nowMs);
            result[i] = row;
        }
        return result;
    }
}
