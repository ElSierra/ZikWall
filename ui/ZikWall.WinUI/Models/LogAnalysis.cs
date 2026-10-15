namespace ZikWall.WinUI.Models;

public enum LogCategory { FirstApp, Connection, State, Closed }
public enum LogFilter { Important, All, FirstApp, Connection, State, Closed }
public enum LogRange { All, Today, Day, Week }

public sealed record LogEntry(ConnectionEvent Source, LogCategory Category, bool IsUnread)
{
    public ConnectionEntry Connection => Source.Connection;
    public string Headline => $"{Connection.Application} {Message}";
    public string DetailLine => $"{Host} · {Context}{(Connection.CountryCode.Length == 2 ? " · " + Connection.CountryCode : "")}";
    public string CategoryLabel => Category switch
    {
        LogCategory.FirstApp => "First app observation", LogCategory.Connection => "Connection observed",
        LogCategory.Closed => "No longer observed", _ => "Connection state"
    };
    public string TimeLabel => Source.Time.ToLocalTime().ToString("t");
    public string FullTime => Source.Time.ToLocalTime().ToString("F");
    public string Message => Category switch
    {
        LogCategory.FirstApp => "was first observed in the retained history",
        LogCategory.Closed => "has an endpoint that is no longer observed",
        LogCategory.State => $"changed connection state: {Source.Event}",
        _ => Source.Connection.State switch
        {
            "SynSent" or "SynReceived" => "was observed attempting a connection",
            "Established" => "was observed with an established connection",
            "Listen" => "was observed listening for connections",
            _ => "had a network endpoint observed"
        }
    };
    public string Host => Connection.RemotePort is > 0
        ? Connection.HostNames.Length > 0 ? Connection.HostNames : Connection.RemoteAddress
        : $"Local {Connection.LocalEndpoint}";
    public string Context => Connection.RemotePort is > 0
        ? $"{Connection.Protocol} · {Connection.DirectionLabel} · {Connection.RemoteEndpoint}{(Connection.HostNames.Length > 0 ? " · " + Connection.HostNameSource : "") }"
        : $"{Connection.Protocol} · {Connection.State}";
}

public sealed class LogDayGroup(string title, IEnumerable<LogEntry> entries) : System.Collections.ObjectModel.ObservableCollection<LogEntry>(entries)
{
    public string Title { get; } = title;

    internal void Update(IReadOnlyList<LogEntry> desired)
    {
        var keys = desired.Select(LogEventKey.For).ToHashSet();
        // A different page needs one notification, not thousands of removals.
        if (Count > 0 && this.Count(x => keys.Contains(LogEventKey.For(x))) < Math.Min(Count, desired.Count) / 2)
        {
            Items.Clear();
            foreach (var entry in desired) Items.Add(entry);
            OnPropertyChanged(new(nameof(Count)));
            OnPropertyChanged(new("Item[]"));
            OnCollectionChanged(new(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
            return;
        }
        for (int i = Count - 1; i >= 0; i--) if (!keys.Contains(LogEventKey.For(this[i]))) RemoveAt(i);
        for (int i = 0; i < desired.Count; i++)
        {
            var key = LogEventKey.For(desired[i]);
            if (i < Count && LogEventKey.For(this[i]) == key)
            {
                if (this[i] != desired[i]) this[i] = desired[i];
                continue;
            }
            int existing = -1;
            for (int j = i + 1; j < Count; j++) if (LogEventKey.For(this[j]) == key) { existing = j; break; }
            if (existing >= 0) { Move(existing, i); if (this[i] != desired[i]) this[i] = desired[i]; }
            else Insert(i, desired[i]);
        }
    }
}

internal readonly record struct LogEventKey(DateTimeOffset Time, string Event, ConnectionIdentity Connection, string App, long Started)
{
    internal static LogEventKey For(LogEntry entry) => new(entry.Source.Time, entry.Source.Event,
        entry.Connection.Identity, entry.Connection.AppKey, entry.Connection.ProcessStarted);
}

public sealed class LogTimelineCollection : System.Collections.ObjectModel.ObservableCollection<LogDayGroup>
{
    public void Update(IReadOnlyList<LogDayGroup> desired)
    {
        for (int i = Count - 1; i >= 0; i--) if (!desired.Any(x => x.Title == this[i].Title)) RemoveAt(i);
        for (int i = 0; i < desired.Count; i++)
        {
            int existing = -1;
            for (int j = i; j < Count; j++) if (this[j].Title == desired[i].Title) { existing = j; break; }
            if (existing < 0) Insert(i, new(desired[i].Title, desired[i]));
            else { if (existing != i) Move(existing, i); this[i].Update(desired[i]); }
        }
    }
}

public sealed record LogAnalysisResult(IReadOnlyList<LogDayGroup> Groups, int MatchingCount, int UnreadCount);

public static class LogAnalysis
{
    public const int DisplayLimit = 1000;

    public static LogAnalysisResult Build(IEnumerable<ConnectionEvent> events, DateTimeOffset now,
        DateTimeOffset readThrough, LogFilter filter = LogFilter.Important, LogRange range = LogRange.All,
        string query = "", bool unreadOnly = false, Func<ConnectionEntry, bool>? visible = null,
        Func<ConnectionEntry, ConnectionEntry>? enrich = null, TimeZoneInfo? timeZone = null,
        LogReadState? readState = null)
    {
        timeZone ??= TimeZoneInfo.Local;
        var today = TimeZoneInfo.ConvertTime(now, timeZone).Date;
        var firstApps = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<LogEntry>();
        query = query.Trim();
        int unreadCount = 0;
        foreach (var source in events.OrderBy(x => x.Time))
        {
            // Resolve first-app identity before filtering, so search/range changes
            // cannot reclassify a later connection as the app's first observation.
            bool first = source.Event == "First seen" && firstApps.Add(source.Connection.AppKey);
            if (visible is not null && !visible(source.Connection)) continue;
            if (source.IsTcpDetail) continue;
            var category = first ? LogCategory.FirstApp : source.Event == "First seen" ? LogCategory.Connection
                : source.Event == "No longer observed" ? LogCategory.Closed : LogCategory.State;
            bool isUnread = category == LogCategory.FirstApp && source.Time > readThrough && !(readState?.IsRead(source) ?? false);
            if (isUnread) unreadCount++;
            if (unreadOnly && !isUnread) continue;
            if (filter == LogFilter.Important && category != LogCategory.FirstApp) continue;
            if (filter >= LogFilter.FirstApp && (int)category != (int)filter - (int)LogFilter.FirstApp) continue;
            var day = TimeZoneInfo.ConvertTime(source.Time, timeZone).Date;
            if (range == LogRange.Today && day != today || range == LogRange.Day && source.Time < now.AddDays(-1)
                || range == LogRange.Week && source.Time < now.AddDays(-7)) continue;
            // Search needs current hostnames, but category/date exclusions do not.
            // With no query, defer enrichment until after the display limit below.
            var row = new LogEntry(query.Length > 0 && enrich is not null
                ? source with { Connection = enrich(source.Connection) } : source, category, isUnread);
            if (query.Length > 0 && !row.Connection.Matches(query) && !row.CategoryLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !source.Event.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            rows.Add(row);
        }
        var groups = rows.OrderByDescending(x => x.Source.Time).Take(DisplayLimit)
            .Select(row => query.Length == 0 && enrich is not null
                ? row with { Source = row.Source with { Connection = enrich(row.Connection) } } : row)
            .GroupBy(x => TimeZoneInfo.ConvertTime(x.Source.Time, timeZone).Date)
            .Select(g => new LogDayGroup(g.Key == today ? "Today" : g.Key == today.AddDays(-1) ? "Yesterday" : g.Key.ToString("dddd, MMMM d, yyyy"), g))
            .ToList();
        return new(groups, rows.Count, unreadCount);
    }
}
