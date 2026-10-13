namespace ZikWall.WinUI.Models;

public sealed record ActivityUsageDelta(DateTimeOffset Time, string AppKey, string Path, decimal Download, decimal Upload);

// Keep exact counter deltas for short windows; never assign whole-app bytes to a host.
public sealed class ActivityUsageWindow
{
    private Guid session;
    private DateTimeOffset? previousTime;
    private Dictionary<string, (decimal Down, decimal Up)> previous = [];
    private readonly Queue<ActivityUsageDelta> samples = new();
    public IReadOnlyCollection<ActivityUsageDelta> Samples => samples;
    public DateTimeOffset? ObservedSince { get; private set; }
    public void Observe(UsageSnapshot snapshot)
    {
        if (snapshot.Session == session && previousTime is { } time && snapshot.Time <= time) return;
        var apps = snapshot.Apps.GroupBy(x => x.AppKey).ToDictionary(x => x.Key,
            x => (Path: x.First().Path, Down: x.Sum(a => (decimal)a.Received), Up: x.Sum(a => (decimal)a.Sent)));
        bool continuation = session == snapshot.Session && previousTime.HasValue;
        // The initial sample may include bytes from before the selected range.
        // Establish a baseline instead of labelling those bytes as recent traffic.
        ObservedSince ??= snapshot.Time;
        if (continuation)
            foreach (var (key, value) in apps)
            {
                var old = previous.GetValueOrDefault(key);
                decimal down = Math.Max(0, value.Down - old.Down), up = Math.Max(0, value.Up - old.Up);
                if (down + up > 0) samples.Enqueue(new(snapshot.Time, key, value.Path, down, up));
            }
        while (samples.TryPeek(out var sample) && (sample.Time < snapshot.Time.AddHours(-1) || samples.Count > 65536))
        {
            samples.Dequeue();
            if (sample.Time > ObservedSince) ObservedSince = sample.Time;
        }
        previous = apps.ToDictionary(x => x.Key, x => (x.Value.Down, x.Value.Up));
        previousTime = snapshot.Time; session = snapshot.Session;
    }
}

public sealed record ActivityRank(string Title, string Value, string Hint, string ProcessPath,
    string Flag, string Filter, double DownloadShare, double UploadShare, double RemainingShare,
    bool IsApp, bool HasFlag);
public sealed record ActivityOverviewResult(IReadOnlyList<ActivityRank> Apps, IReadOnlyList<ActivityRank> Hosts,
    IReadOnlyList<ActivityRank> Types, IReadOnlyList<ActivityRank> Countries, decimal Download, decimal Upload,
    int Connections, int AppCount, int HostCount);

public static class ActivityOverview
{
    public static ActivityOverviewResult Build(IEnumerable<ConnectionEntry> source, IEnumerable<ActivityUsageDelta> usage)
    {
        var entries = source.DistinctBy(x => x.Key).ToList();
        var bytes = usage.GroupBy(x => x.AppKey).ToDictionary(x => x.Key,
            x => (Down: x.Sum(a => a.Download), Up: x.Sum(a => a.Upload)));
        var appKey = AppGroups(entries);
        var apps = entries.GroupBy(appKey).Select(group =>
        {
            // Prefer a row with a path so the merged app keeps its real icon.
            var first = group.FirstOrDefault(x => x.ProcessPath.Length > 0) ?? group.First();
            var keys = group.Select(x => x.AppKey).Distinct().Where(bytes.ContainsKey).ToList();
            decimal down = keys.Sum(x => bytes[x].Down), up = keys.Sum(x => bytes[x].Up);
            return (Entry: first, Count: group.Count(), Measured: keys.Count > 0, Down: down, Up: up);
        }).OrderByDescending(x => x.Measured).ThenByDescending(x => x.Down + x.Up)
            .ThenByDescending(x => x.Count).ThenBy(x => x.Entry.Application).ToList();
        decimal peak = apps.Select(x => x.Down + x.Up).DefaultIfEmpty(0).Max();
        // Without measured bytes, bar apps by connections like the other columns instead of leaving them blank.
        int countPeak = apps.Select(x => x.Count).DefaultIfEmpty(1).Max();
        var appRows = apps.Select(x => new ActivityRank(x.Entry.Application,
            x.Measured ? UsageLedger.Bytes(x.Down + x.Up) : $"{x.Count:N0} conn.",
            x.Measured ? $"Downloaded {UsageLedger.Bytes(x.Down)} · uploaded {UsageLedger.Bytes(x.Up)}. Whole-app totals, including local traffic."
                : $"{x.Count:N0} observed connections. No measured usage in this range.",
            x.Entry.ProcessPath, x.Entry.CountryFlag, x.Entry.Application,
            peak > 0 ? (double)(x.Down / peak) : x.Count / (double)countPeak, peak > 0 ? (double)(x.Up / peak) : 0,
            peak > 0 ? (double)(1 - (x.Down + x.Up) / peak) : 1 - x.Count / (double)countPeak, true, false)).ToList();
        var remote = entries.Where(x => x.RemotePort is > 0).ToList();
        var hosts = Rank(remote.GroupBy(x => x.RemoteAddress), group =>
        {
            var first = group.First();
            string names = string.Join(", ", group.Select(x => x.HostNames).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
            return (names.Length > 0 ? names : first.RemoteAddress, first.RemoteAddress,
                $"{first.RemoteAddress} · {first.DomainDescription}", first.CountryFlag, true);
        });
        var types = Rank(entries.GroupBy(TrafficType), group => (group.Key, group.First().RemotePort?.ToString() ?? "UDP", "Identified from transport and port; encrypted payloads are not inspected.", "", false));
        var countries = Rank(remote.GroupBy(x => x.NetworkScope != "Internet" ? "Local network" : x.CountryName.Length > 0 ? x.CountryName : "Unknown location"),
            group => (group.Key, group.Key == "Local network" ? group.First().RemoteAddress : group.Key, "Approximate offline IP location; ranked by observed connections.", group.First().CountryFlag, true));
        return new(appRows, hosts, types, countries, apps.Sum(x => x.Down), apps.Sum(x => x.Up), entries.Count, apps.Count, hosts.Count);
    }
    // Windows withholds the path of protected processes, which made one app appear twice
    // (e.g. esrv_svc with and without a path). Fold path-less processes into the single
    // pathed app of the same name, or into one row per name when no path is known.
    internal static Func<ConnectionEntry, string> AppGroups(IReadOnlyList<ConnectionEntry> entries)
    {
        var pathed = entries.Where(x => x.PackageFamily.Length == 0 && x.ProcessPath.Length > 0)
            .GroupBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Keys: g.Select(x => x.AppKey).Distinct().Take(2).ToList()))
            .Where(x => x.Keys.Count == 1).ToDictionary(x => x.Name, x => x.Keys[0], StringComparer.OrdinalIgnoreCase);
        return x => x.PackageFamily.Length > 0 || x.ProcessPath.Length > 0 || x.ProcessName.Length == 0
            || x.ProcessName == ConnectionContinuity.Unowned || x.ProcessName.StartsWith("PID ", StringComparison.Ordinal) ? x.AppKey
            : pathed.TryGetValue(x.ProcessName, out string? key) ? key : "name:" + x.ProcessName.ToUpperInvariant();
    }
    private static List<ActivityRank> Rank(IEnumerable<IGrouping<string, ConnectionEntry>> groups,
        Func<IGrouping<string, ConnectionEntry>, (string Title, string Filter, string Hint, string Flag, bool HasFlag)> label)
    {
        var rows = groups.Select(g => (Group: g, Count: g.Count(), Label: label(g)))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Label.Title, StringComparer.OrdinalIgnoreCase).ToList();
        int peak = rows.Select(x => x.Count).DefaultIfEmpty(1).Max();
        return rows.Select(x => new ActivityRank(x.Label.Title, $"{x.Count:N0}",
            $"{x.Count:N0} observed connections. {x.Label.Hint}", "", x.Label.Flag.Length > 0 ? x.Label.Flag : "ms-appx:///Assets/Flags/unknown.svg", x.Label.Filter,
            x.Count / (double)peak, 0, 1 - x.Count / (double)peak, false, x.Label.HasFlag)).ToList();
    }
    public static string TrafficType(ConnectionEntry entry) => entry.RemotePort switch
    {
        443 => entry.Protocol.StartsWith("UDP", StringComparison.Ordinal) ? "QUIC / HTTPS (UDP 443)" : "HTTPS (TCP 443)",
        80 => "HTTP (80)", 53 => "DNS (53)", 853 => "DNS over TLS (853)", 5353 => "Multicast DNS (5353)",
        1900 => "SSDP (1900)", 123 => "NTP (123)", 22 => "SSH (22)", 445 => "SMB (445)",
        null => "UDP · destination unavailable", 0 => "Listening", _ => $"{entry.Protocol} · port {entry.RemotePort}"
    };
}
