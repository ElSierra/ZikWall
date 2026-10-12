namespace ZikWall.WinUI.Models;

public sealed record MonitorBlockPlan(string Title, string Explanation, IReadOnlyList<FirewallRule> Rules);

// Captured when the menu opens. A refresh, recycled row or changed selection must
// never retarget an action to a different application or endpoint.
public sealed record MonitorActionTarget(ActivityRow Row, IReadOnlyList<ConnectionEntry> Entries)
{
    public bool IsApp => Row.Expandable && Row.Indent == 0;
    public bool IsConnection => !Row.Expandable;
    public static MonitorActionTarget Capture(ActivityRow row, IEnumerable<ConnectionEntry> snapshot)
    {
        var entries = row.Expandable
            ? snapshot.Where(x => row.Indent == 0 ? x.AppKey == row.Entry.AppKey : x.ProcessKey == row.Entry.ProcessKey).ToArray()
            : [row.Entry];
        return new(row, Array.AsReadOnly(entries));
    }

    public MonitorBlockPlan Block(bool connection)
    {
        var entry = Row.Entry;
        if (connection && !IsConnection) throw new InvalidDataException("Expand the program and select an individual connection first.");
        var paths = (IsApp && !connection ? Entries.Select(x => x.ProcessPath) : [entry.ProcessPath])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0 || paths.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("An executable path is unavailable. Expand the app and choose a process with a known path.");
        if (paths.Length > 256) throw new InvalidDataException("This group has too many executables. Choose a specific process.");
        int direction = 3, protocol = 0, port = 0;
        string address = "";
        if (connection)
        {
            direction = entry.Direction switch { "Outgoing" => 1, "Incoming" => 2, _ => 0 };
            protocol = entry.Protocol switch { "TCPv4" or "TCPv6" => 6, "UDPv4" or "UDPv6" => 17, _ => 0 };
            if (direction == 0 || protocol == 0 || entry.RemotePort is not (> 0 and <= 65535) ||
                !System.Net.IPAddress.TryParse(entry.RemoteAddress, out _) || entry.RemoteAddress is "0.0.0.0" or "::")
                throw new InvalidDataException("This row has no known remote endpoint or direction. You can block its executable instead.");
            address = entry.RemoteAddress;
            port = entry.RemotePort.Value;
        }
        var rules = paths.Select(path =>
        {
            string name = connection ? $"Block {entry.Application} to {entry.RemoteEndpoint}" : $"Block {Path.GetFileName(path)}";
            return new FirewallRule(Guid.NewGuid(), name.Length > 128 ? name[..128] : name,
                path, address, protocol, port, direction, true, true, null);
        }).ToArray();
        foreach (var rule in rules) rule.Validate();
        string explanation = connection
            ? $"{entry.Protocol} · {entry.Direction}\nRemote: {entry.RemoteEndpoint}\n\nMatches this executable and remote endpoint, across all its processes and local ports. It is not a command to close just this socket."
            : "Blocks incoming and outgoing traffic for every process using the listed executable paths. It does not isolate one PID or one hosted service.";
        if (IsApp && !connection) explanation += "\nOnly paths observed in this app group are included; new or unobserved package executables are not covered.";
        explanation += "\n\n" + string.Join("\n", paths);
        return new(connection ? "Block this connection?" : IsApp ? "Block this app?" : "Block this executable?", explanation, Array.AsReadOnly(rules));
    }
}
