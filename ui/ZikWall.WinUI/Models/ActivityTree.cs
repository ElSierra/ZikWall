using System.Collections.ObjectModel;
using System.ComponentModel;

namespace ZikWall.WinUI.Models;

public enum UsageSort { AppName, Ascending, Descending }

public sealed class ActivityRow(string Key, string Title, string Subtitle, string Chevron, double Indent,
    ConnectionEntry Entry, string Direction, string Endpoint, string Country, string Flag, string State,
    string Usage = "", string Total = "", bool IsNew = false) : INotifyPropertyChanged
{
    public string Key { get; } = Key;
    private string title = Title, subtitle = Subtitle, chevron = Chevron, direction = Direction,
        endpoint = Endpoint, country = Country, flag = Flag, state = State;
    private double indent = Indent;
    private ConnectionEntry entry = Entry;
    private bool isNew = IsNew;
    private HostSummary hosts = HostSummary.Empty;
    public string Title => title;
    public string Subtitle => subtitle;
    public string Chevron => chevron;
    public double Indent => indent;
    public ConnectionEntry Entry => entry;
    public string Direction => direction;
    public string Endpoint => endpoint;
    public string Country => country;
    public string Flag => flag;
    public string State => state;
    public bool IsNew => isNew;
    public HostSummary Hosts { get => hosts; init => hosts = value; }
    public string HostPrimary => hosts.Primary;
    public string HostFlag => hosts.Flag;
    public string HostMore => hosts.More;
    public string HostTitle => hosts.Title;
    public string HostDescription => hosts.Description;
    public bool HasHosts => hosts.Available;
    private string usage = Usage, total = Total, usageHint = "";
    private bool usageVisible;
    public string Usage => usage;
    public string Total => total;
    public string UsageHint => usageHint;
    public bool UsageVisible => usageVisible;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void SetUsage(bool visible, string total, string rate, string hint)
    {
        Set(ref usageVisible, visible, nameof(UsageVisible));
        Set(ref this.total, total, nameof(Total));
        Set(ref usage, rate, nameof(Usage));
        Set(ref usageHint, hint, nameof(UsageHint));
    }
    private void Set<T>(ref T field, T value, string property)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value; PropertyChanged?.Invoke(this, new(property));
    }
    internal void UpdateFrom(ActivityRow other)
    {
        string oldHint = TitleHint, oldPath = ProcessPath;
        bool flagsChanged = country != other.Country || flag != other.Flag || Expandable != other.Expandable || indent != other.Indent;
        Set(ref title, other.Title, nameof(Title));
        if (subtitle != other.Subtitle) { Set(ref subtitle, other.Subtitle, nameof(Subtitle)); Changed(nameof(Details)); }
        if (chevron != other.Chevron) { Set(ref chevron, other.Chevron, nameof(Chevron)); Changed(nameof(ChevronGlyph)); Changed(nameof(Expandable)); }
        Set(ref indent, other.Indent, nameof(Indent));
        Set(ref entry, other.Entry, nameof(Entry));
        Set(ref direction, other.Direction, nameof(Direction));
        Set(ref endpoint, other.Endpoint, nameof(Endpoint));
        Set(ref country, other.Country, nameof(Country));
        Set(ref flag, other.Flag, nameof(Flag));
        Set(ref state, other.State, nameof(State));
        Set(ref isNew, other.IsNew, nameof(IsNew));
        if (!hosts.SameContent(other.Hosts))
        {
            var before = hosts;
            hosts = other.Hosts; Changed(nameof(Hosts));
            if (before.Primary != hosts.Primary) Changed(nameof(HostPrimary));
            if (before.Flag != hosts.Flag) Changed(nameof(HostFlag));
            if (before.More != hosts.More) Changed(nameof(HostMore));
            if (before.Title != hosts.Title) Changed(nameof(HostTitle));
            if (before.Description != hosts.Description) Changed(nameof(HostDescription));
            if (before.Available != hosts.Available) Changed(nameof(HasHosts));
        }
        if (flagsChanged) Changed(nameof(CountryFlags));
        if (oldPath != ProcessPath) Changed(nameof(ProcessPath));
        if (oldHint != TitleHint) Changed(nameof(TitleHint));
    }
    private void Changed(string property) => PropertyChanged?.Invoke(this, new(property));
    public bool Expandable => Chevron.Length > 0;
    public string Details => Subtitle;
    public string ChevronGlyph => Chevron == "›" ? "" : Chevron.Length > 0 ? "" : "";
    public string ProcessPath => Entry.ProcessPath;
    public string TitleHint => Expandable ? ProcessPath : Entry.DomainDescription + "\n" + Entry.RemoteEndpoint;
    public IEnumerable<string> CountryFlags => Expandable && Indent == 0 && Country.Length > 0
        ? Country.Split(", ").Take(3).Select(x => $"ms-appx:///Assets/Flags/{x.ToLowerInvariant()}.svg") : [Flag];
}

// One flat virtualized list: collapsed apps cost one realized row, regardless of
// the number of processes/sockets. Identity never depends on a display name.
public sealed class ActivityTree
{
    private readonly HashSet<string> expanded = new(StringComparer.Ordinal);
    private List<ActivityRow> baseRows = [];
    private readonly List<(string Key, int Start, int Count)> blocks = [];
    public ObservableCollection<ActivityRow> Rows { get; } = [];
    public void Toggle(ActivityRow row)
    {
        if (!row.Expandable) return;
        if (!expanded.Remove(row.Key)) expanded.Add(row.Key);
    }
    public void Update(IEnumerable<ConnectionEntry> source, bool flat = false, Func<string, (string Rate, string Total)>? usage = null,
        IReadOnlySet<string>? fresh = null, bool showUsage = false, UsageSort sort = UsageSort.AppName,
        Func<string, AppUsageDisplay>? usageDisplay = null)
    {
        bool IsNew(ConnectionEntry entry) => fresh?.Contains(entry.Key) == true;
        var next = new List<ActivityRow>();
        var valid = new HashSet<string>(StringComparer.Ordinal);
        blocks.Clear();
        foreach (var app in source.GroupBy(x => x.AppKey).OrderBy(x => x.First().Application, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Key))
        {
            var entries = app.ToList();
            var first = entries[0];
            valid.Add(app.Key);
            var processes = entries.GroupBy(x => x.ProcessKey).OrderBy(x => x.First().ProcessId).ToList();
            foreach (var process in processes) valid.Add(process.Key);
            int start = next.Count;
            if (flat) { next.AddRange(entries.Select(x => Leaf(x, 0, IsNew(x)))); blocks.Add((app.Key, start, next.Count - start)); continue; }
            var countries = entries.Select(x => x.CountryCode).Where(x => x.Length == 2).Distinct().Order().ToList();
            var destinations = entries.Where(x => x.RemotePort is > 0).Select(x => x.RemoteAddress).Distinct().Count();
            var stats = usage?.Invoke(app.Key) ?? ("", "");
            next.Add(new(app.Key, first.PackageFamily.Length > 0 ? first.PackageFamily.Split('_')[0] : first.Application,
                WithDomains($"{Count(processes.Count, "process", "processes")} · {Count(entries.Count, "endpoint", "endpoints")}", entries), expanded.Contains(app.Key) ? "⌄" : "›", 0, first,
                string.Join(" / ", entries.Select(x => x.Direction).Distinct()), Count(destinations, "remote IP", "remote IPs"),
                string.Join(", ", countries), countries.Count == 1 ? (first with { CountryCode = countries[0] }).CountryFlag : "ms-appx:///Assets/Flags/unknown.svg",
                "", stats.Item1, stats.Item2, entries.Any(IsNew)) { Hosts = HostSummary.Create(entries) });
            if (!expanded.Contains(app.Key)) { blocks.Add((app.Key, start, next.Count - start)); continue; }
            foreach (var process in processes)
            {
                var entry = process.First();
                next.Add(new(process.Key, entry.ProcessLabel, WithDomains(Count(process.Count(), "endpoint", "endpoints"), process), expanded.Contains(process.Key) ? "⌄" : "›", 20, entry, "", "", "", "ms-appx:///Assets/Flags/unknown.svg", "", IsNew: process.Any(IsNew)) { Hosts = HostSummary.Create(process) });
                if (expanded.Contains(process.Key)) next.AddRange(process.Select(x => Leaf(x, 40, IsNew(x))));
            }
            blocks.Add((app.Key, start, next.Count - start));
        }
        // Expansion memory is bounded, including vanished process IDs.
        expanded.IntersectWith(valid);
        // Socket churn changes an app's summary, not its identity. Keep its
        // native controls and notify only the cells whose displayed values changed.
        var retained = new Dictionary<string, ActivityRow>(StringComparer.Ordinal);
        foreach (var row in Rows) retained.TryAdd(row.Key, row);
        for (int i = 0; i < next.Count; i++)
            if (retained.Remove(next[i].Key, out var row)) { row.UpdateFrom(next[i]); next[i] = row; }
        baseRows = next;
        ApplyUsage(showUsage, sort, usageDisplay);
    }
    // Usage ticks reuse connection grouping and DNS/country summaries. Sort whole
    // app blocks so expanded processes and connections remain attached to the app.
    public void ApplyUsage(bool enabled, UsageSort sort, Func<string, AppUsageDisplay>? display)
    {
        if (!enabled)
        {
            foreach (var row in baseRows) row.SetUsage(false, "", "", "");
            Publish(baseRows); return;
        }
        var values = blocks.ToDictionary(x => x.Key, x => display?.Invoke(x.Key) ?? AppUsageDisplay.Unknown, StringComparer.Ordinal);
        IEnumerable<(string Key, int Start, int Count)> ordered = blocks;
        if (sort != UsageSort.AppName)
        {
            var knownFirst = blocks.OrderBy(x => values[x.Key].Bytes is null);
            ordered = sort == UsageSort.Descending
                ? knownFirst.ThenByDescending(x => values[x.Key].Bytes).ThenBy(x => x.Start)
                : knownFirst.ThenBy(x => values[x.Key].Bytes).ThenBy(x => x.Start);
        }
        var next = new List<ActivityRow>(baseRows.Count);
        foreach (var block in ordered)
        {
            var value = values[block.Key];
            for (int i = block.Start; i < block.Start + block.Count; i++)
            {
                var row = baseRows[i];
                // The collector has app totals, not per-process or per-socket totals.
                bool appTotal = row.Indent == 0;
                row.SetUsage(true, appTotal ? value.Total : "", appTotal ? value.Rate : "",
                    appTotal ? value.Hint : "Usage is collected for the whole app, not individual processes or connections.");
                next.Add(row);
            }
        }
        Publish(next);
    }
    private void Publish(List<ActivityRow> next)
    {
        // Remove vanished identities first; shifting a surviving app must not
        // recreate every row below it. Usage sorting also moves existing rows.
        var wanted = next.ToHashSet();
        for (int i = Rows.Count - 1; i >= 0; i--) if (!wanted.Contains(Rows[i])) Rows.RemoveAt(i);
        for (int i = 0; i < next.Count; i++)
        {
            if (i == Rows.Count) Rows.Add(next[i]);
            else if (!ReferenceEquals(Rows[i], next[i]))
            {
                int existing = Rows.IndexOf(next[i]);
                if (existing >= 0) Rows.Move(existing, i); else Rows.Insert(i, next[i]);
            }
        }
    }
    private static string Count(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";
    private static string WithDomains(string summary, IEnumerable<ConnectionEntry> entries)
    {
        var names = entries.Where(x => x.HostNames.Length > 0).SelectMany(x => x.HostNames.Split(", "))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Take(4).ToArray();
        return names.Length == 0 ? summary : $"{string.Join(", ", names.Take(3))}{(names.Length > 3 ? ", …" : "")} · DNS hints · {summary}";
    }
    private static ActivityRow Leaf(ConnectionEntry entry, double indent, bool fresh) => new("connection:" + entry.Key,
        entry.DestinationLabel, entry.HostNames.Length > 0 ? $"{entry.RemoteEndpoint} · {entry.HostNameSource} · {entry.Protocol}" : $"{entry.Protocol} · domain unknown · local {entry.LocalEndpoint}", "", indent, entry, entry.DirectionLabel,
        "", entry.NetworkLabel, entry.CountryFlag, entry.State, IsNew: fresh) { Hosts = HostSummary.Create([entry]) };
}
