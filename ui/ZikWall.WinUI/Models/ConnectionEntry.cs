using System.Net;
using System.Text.Json.Serialization;

namespace ZikWall.WinUI.Models;

public sealed record ConnectionEntry(string Protocol, string LocalAddress, int LocalPort,
    string RemoteAddress, int? RemotePort, int ProcessId, string State,
    string ProcessName = "", string ProcessPath = "", string Direction = "Unknown",
    string CountryCode = "", string CountryName = "", string NetworkScope = "Unknown",
    string PackageFamily = "", long ProcessStarted = 0, [property: JsonIgnore] string DomainHint = "",
    string NetworkName = "", uint NetworkAsn = 0)
{
    [JsonIgnore] public string AppKey => PackageFamily.Length > 0 ? "package:" + PackageFamily.ToUpperInvariant()
        : ProcessPath.Length > 0 ? "path:" + ProcessPath.ToUpperInvariant() : $"unknown:{ProcessId}:{ProcessStarted}";
    [JsonIgnore] public string ProcessKey => $"{AppKey}|{ProcessId}|{ProcessStarted}";
    [JsonIgnore] public ConnectionIdentity Identity => new(Protocol, ProcessId, LocalAddress, LocalPort, RemoteAddress, RemotePort);
    [JsonIgnore] public string Key => $"{Protocol}|{ProcessId}|{LocalAddress}|{LocalPort}|{RemoteAddress}|{RemotePort}";
    [JsonIgnore] public string Application => string.IsNullOrWhiteSpace(ProcessName) ? $"PID {ProcessId}" : ProcessName;
    [JsonIgnore] public string ProcessLabel => ConnectionContinuity.IsOrphaned(this) ? $"{Application} · closing" : $"{Application} · PID {ProcessId}";
    [JsonIgnore] public string LocalEndpoint => Endpoint(LocalAddress, LocalPort);
    [JsonIgnore] public string RemoteEndpoint => RemotePort.HasValue ? Endpoint(RemoteAddress, RemotePort.Value) : "Not available for UDP";
    [JsonIgnore] public string DestinationLabel => DomainHint.Length > 0 ? DomainHint : RemoteEndpoint;
    [JsonIgnore] public string DomainDescription => DomainHint.Length > 0 ? $"{DomainHint} (Windows DNS cache hint; shared IPs may host other domains)" : "Unknown (no matching Windows DNS cache entry)";
    [JsonIgnore] public string CountryLabel => CountryCode.Length == 2 ? $"{CountryName} · {CountryCode}" : NetworkScope == "Internet" ? "Country unknown" : NetworkScope;
    // Who operates the remote IP (e.g. "Microsoft · GB"); recognisable where a country alone is not.
    [JsonIgnore] public string NetworkLabel => NetworkName.Length == 0 ? CountryLabel : CountryCode.Length == 2 ? $"{NetworkName} · {CountryCode}" : NetworkName;
    [JsonIgnore] public string NetworkDescription => NetworkName.Length > 0 ? $"{NetworkName} (AS{NetworkAsn})" : "Unknown";
    [JsonIgnore] public string PlaceDescription => NetworkName.Length > 0 ? $"{NetworkDescription} · {CountryLabel}" : CountryLabel;
    [JsonIgnore] public string CountryFlag => CountryCode.Length == 2 ? $"ms-appx:///Assets/Flags/{CountryCode.ToLowerInvariant()}.svg" : "ms-appx:///Assets/Flags/unknown.svg";
    [JsonIgnore] public string DirectionLabel => Direction switch { "Incoming" => "↓ Incoming", "Outgoing" => "↑ Outgoing", "Listening" => "Listening", _ => "Unknown" };
    public bool Matches(string query) => string.IsNullOrEmpty(query)
        || Application.Contains(query, StringComparison.OrdinalIgnoreCase)
        || ProcessPath.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Protocol.Contains(query, StringComparison.OrdinalIgnoreCase)
        || State.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Direction.Contains(query, StringComparison.OrdinalIgnoreCase)
        || CountryName.Contains(query, StringComparison.OrdinalIgnoreCase)
        || CountryCode.Contains(query, StringComparison.OrdinalIgnoreCase)
        || NetworkName.Contains(query, StringComparison.OrdinalIgnoreCase)
        || NetworkAsn > 0 && $"AS{NetworkAsn}".Equals(query, StringComparison.OrdinalIgnoreCase)
        || LocalAddress.Contains(query, StringComparison.OrdinalIgnoreCase)
        || RemoteAddress.Contains(query, StringComparison.OrdinalIgnoreCase)
        || DomainHint.Contains(query, StringComparison.OrdinalIgnoreCase)
        || LocalEndpoint.Contains(query, StringComparison.OrdinalIgnoreCase)
        || RemoteEndpoint.Contains(query, StringComparison.OrdinalIgnoreCase)
        || ProcessId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase);
    private static string Endpoint(string address, int port) => address.Contains(':') ? $"[{address}]:{port}" : $"{address}:{port}";
}

public readonly record struct ConnectionIdentity(string Protocol, int ProcessId, string LocalAddress,
    int LocalPort, string RemoteAddress, int? RemotePort);

public sealed record ConnectionEvent(DateTimeOffset Time, string Event, ConnectionEntry Connection)
{
    // Set on the displayed copy only, so a run of same-minute events shows one time.
    [JsonIgnore] public bool SameMinuteAsPrevious { get; init; }
    [JsonIgnore] public string TimeLabel => SameMinuteAsPrevious ? "" : Time.ToLocalTime() is var local && local.Date == DateTime.Today ? local.ToString("t") : $"{local:MMM d} {local:t}";
    [JsonIgnore] public string FullTime => Time.ToLocalTime().ToString("G");
    [JsonIgnore] public string Summary => $"{Connection.Application} · {Activity}";
    [JsonIgnore] public string Endpoints => Connection.RemotePort is > 0
        ? Connection.DomainHint.Length > 0 ? $"{Connection.DomainHint} · {Connection.RemoteEndpoint} · DNS hint" : Connection.RemoteEndpoint
        : $"Local {Connection.LocalEndpoint}";
    [JsonIgnore] public string Details => $"{Connection.ProcessLabel} · {Connection.Protocol} · {Event}\n{Connection.LocalEndpoint} → {Connection.RemoteEndpoint}";
    [JsonIgnore] public string Activity => Event switch
    {
        "First seen" when Connection.RemotePort is null => $"UDP port {Connection.LocalPort} opened",
        "First seen" when Connection.RemotePort == 0 => $"Listening on port {Connection.LocalPort}",
        "First seen" => Phase(Connection.State) is { Length: > 0 } phase ? phase : "Connection seen",
        "No longer observed" when Connection.RemotePort is null => $"UDP port {Connection.LocalPort} closed",
        "No longer observed" when Connection.RemotePort == 0 => $"Stopped listening on port {Connection.LocalPort}",
        "No longer observed" => "Closed",
        _ when Transition(out _, out string to) && Phase(to).Length > 0 => Phase(to),
        _ => Event
    };
    // TCP teardown steps and sockets whose owner already exited; hidden unless TCP details are on.
    [JsonIgnore] public bool IsTcpDetail => UnknownOwner
        || Transition(out string from, out string to) && (Phase(to) is "" or "Closing" || Phase(from) == Phase(to));
    private bool UnknownOwner => ConnectionContinuity.IsOrphaned(Connection) && Connection.ProcessName is ConnectionContinuity.Unowned or "System / unowned";
    private bool Transition(out string from, out string to)
    {
        int arrow = Event.IndexOf(" → ", StringComparison.Ordinal);
        (from, to) = arrow < 0 ? ("", "") : (Event[..arrow], Event[(arrow + 3)..]);
        return arrow >= 0;
    }
    private static string Phase(string state) => state switch
    {
        "Established" => "Connected",
        "SynSent" or "SynReceived" => "Connecting",
        "Listen" => "Listening",
        "FinWait1" or "FinWait2" or "CloseWait" or "Closing" or "LastAck" or "TimeWait" or "DeleteTcb" => "Closing",
        _ => ""
    };
}

public sealed class ConnectionGroup(string title, IEnumerable<ConnectionEntry> entries) : List<ConnectionEntry>(entries)
{
    public string Title { get; } = title;
}

public sealed class ConnectionTracker
{
    private Dictionary<ConnectionIdentity, ConnectionEntry> previous = new();
    public IReadOnlyList<ConnectionEvent> Observe(IEnumerable<ConnectionEntry> entries, DateTimeOffset now)
    {
        var current = new Dictionary<ConnectionIdentity, ConnectionEntry>(entries.TryGetNonEnumeratedCount(out int count) ? count : 0);
        // A connected TCP 4-tuple is one connection even when Windows moves it to PID 0 while closing.
        foreach (var entry in entries) current.TryAdd(entry.RemotePort is > 0 ? ConnectionContinuity.Tuple(entry) : entry.Identity, entry);
        var events = new List<ConnectionEvent>();
        foreach (var (identity, entry) in current)
        {
            if (!previous.TryGetValue(identity, out var old)) events.Add(new(now, "First seen", entry));
            else if (old.ProcessId != entry.ProcessId && old.ProcessId != 0 && entry.ProcessId != 0)
            {
                events.Add(new(now, "No longer observed", old));
                events.Add(new(now, "First seen", entry));
            }
            else if (old.State != entry.State) events.Add(new(now, $"{old.State} → {entry.State}", entry));
        }
        foreach (var (identity, entry) in previous)
            if (!current.ContainsKey(identity)) events.Add(new(now, "No longer observed", entry));
        previous = current;
        return events;
    }
    public void Reset() => previous.Clear();
}

public sealed record AdapterCounters(string Id, string Name, long Received, long Sent);
public sealed record TrafficSample(DateTimeOffset Time, string AdapterId, string AdapterName, double Download, double Upload)
{
    public static string Rate(double bytes) => bytes >= 1048576 ? $"{bytes / 1048576:0.00} MB/s" : bytes >= 1024 ? $"{bytes / 1024:0.0} KB/s" : $"{bytes:0} B/s";
}

public sealed class TrafficTracker
{
    private DateTimeOffset? lastTime;
    private Dictionary<string, AdapterCounters> previous = new();
    public IReadOnlyList<TrafficSample> Observe(IEnumerable<AdapterCounters> counters, DateTimeOffset now)
    {
        var current = counters.ToDictionary(x => x.Id);
        var samples = new List<TrafficSample>();
        double seconds = lastTime.HasValue ? (now - lastTime.Value).TotalSeconds : 0;
        if (seconds > 0)
            foreach (var item in current.Values)
                if (previous.TryGetValue(item.Id, out var old) && item.Received >= old.Received && item.Sent >= old.Sent)
                    samples.Add(new(now, item.Id, item.Name, (item.Received - old.Received) / seconds, (item.Sent - old.Sent) / seconds));
        previous = current;
        lastTime = now;
        return samples;
    }
    public void Reset() { previous.Clear(); lastTime = null; }
}
