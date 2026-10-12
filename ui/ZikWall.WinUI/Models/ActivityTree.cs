using System.Collections.ObjectModel;

namespace ZikWall.WinUI.Models;

public sealed record ActivityRow(string Key, string Title, string Subtitle, string Chevron, double Indent,
    ConnectionEntry Entry, string Direction, string Endpoint, string Country, string Flag, string State,
    string Usage = "", string Total = "")
{
    public bool Expandable => Chevron.Length > 0;
    public string ProcessPath => Entry.ProcessPath;
    public IEnumerable<string> CountryFlags => Expandable && Indent == 0 && Country.Length > 0
        ? Country.Split(", ").Take(3).Select(x => $"ms-appx:///Assets/Flags/{x.ToLowerInvariant()}.svg") : [Flag];
}

// One flat virtualized list: collapsed apps cost one realized row, regardless of
// the number of processes/sockets. Identity never depends on a display name.
public sealed class ActivityTree
{
    private readonly HashSet<string> expanded = new(StringComparer.Ordinal);
    public ObservableCollection<ActivityRow> Rows { get; } = [];
    public void Toggle(ActivityRow row)
    {
        if (!row.Expandable) return;
        if (!expanded.Remove(row.Key)) expanded.Add(row.Key);
    }
    public void Update(IEnumerable<ConnectionEntry> source, bool flat = false, Func<string, (string Rate, string Total)>? usage = null)
    {
        var next = new List<ActivityRow>();
        foreach (var app in source.GroupBy(x => x.AppKey).OrderBy(x => x.First().Application, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Key))
        {
            var entries = app.ToList();
            var first = entries[0];
            if (flat) { next.AddRange(entries.Select(x => Leaf(x, 0))); continue; }
            var processes = entries.GroupBy(x => x.ProcessKey).OrderBy(x => x.First().ProcessId).ToList();
            var countries = entries.Select(x => x.CountryCode).Where(x => x.Length == 2).Distinct().Order().ToList();
            var destinations = entries.Where(x => x.RemotePort is > 0).Select(x => x.RemoteAddress).Distinct().Count();
            var stats = usage?.Invoke(app.Key) ?? ("—", "Enable usage");
            next.Add(new(app.Key, first.PackageFamily.Length > 0 ? first.PackageFamily.Split('_')[0] : first.Application,
                $"{processes.Count} processes · {entries.Count} endpoints", expanded.Contains(app.Key) ? "⌄" : "›", 0, first,
                string.Join(" / ", entries.Select(x => x.Direction).Distinct()), $"{destinations} remote IPs",
                string.Join(", ", countries), countries.Count == 1 ? (first with { CountryCode = countries[0] }).CountryFlag : "ms-appx:///Assets/Flags/unknown.svg",
                "", stats.Item1, stats.Item2));
            if (!expanded.Contains(app.Key)) continue;
            foreach (var process in processes)
            {
                var entry = process.First();
                next.Add(new(process.Key, entry.ProcessLabel, $"{process.Count()} endpoints", expanded.Contains(process.Key) ? "⌄" : "›", 20, entry, "", "", "", "ms-appx:///Assets/Flags/unknown.svg", ""));
                if (expanded.Contains(process.Key)) next.AddRange(process.Select(x => Leaf(x, 40)));
            }
        }
        // Expansion memory is bounded, including vanished process IDs.
        var valid = source.SelectMany(x => new[] { x.AppKey, x.ProcessKey }).ToHashSet();
        expanded.IntersectWith(valid);
        // Preserve equal rows and selection. A reset bounds work under extreme churn.
        if (Math.Abs(Rows.Count - next.Count) > 64) { Rows.Clear(); foreach (var row in next) Rows.Add(row); return; }
        for (int i = 0; i < next.Count; i++)
        {
            if (i == Rows.Count) Rows.Add(next[i]);
            else if (Rows[i] != next[i]) Rows[i] = next[i];
        }
        while (Rows.Count > next.Count) Rows.RemoveAt(Rows.Count - 1);
    }
    private static ActivityRow Leaf(ConnectionEntry entry, double indent) => new("connection:" + entry.Key,
        entry.RemoteEndpoint, entry.LocalEndpoint, "", indent, entry, entry.DirectionLabel,
        entry.Protocol, entry.CountryLabel, entry.CountryFlag, entry.State);
}
