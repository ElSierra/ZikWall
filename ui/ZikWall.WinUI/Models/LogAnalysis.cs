namespace ZikWall.WinUI.Models;

public enum LogCategory { FirstApp, Connection, State, Closed }
public enum LogFilter { Important, All, FirstApp, Connection, State, Closed }
public enum LogRange { All, Today, Day, Week }

public sealed record LogEntry(ConnectionEvent Source, LogCategory Category, bool IsUnread)
{
    public ConnectionEntry Connection => Source.Connection;
    public string CategoryLabel => Category switch
    {
        LogCategory.FirstApp => "First app observation", LogCategory.Connection => "Connection observed",
        LogCategory.Closed => "No longer observed", _ => "Connection state"
    };
    public string Badge => Category switch { LogCategory.FirstApp => "NEW", LogCategory.Connection => "NET", LogCategory.Closed => "END", _ => "TCP" };
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

public sealed class LogDayGroup(string title, IEnumerable<LogEntry> entries) : List<LogEntry>(entries)
{
    public string Title { get; } = title;
}

public sealed record LogAnalysisResult(IReadOnlyList<LogDayGroup> Groups, int MatchingCount, int UnreadCount);

public static class LogAnalysis
{
    public const int DisplayLimit = 1000;

    public static LogAnalysisResult Build(IEnumerable<ConnectionEvent> events, DateTimeOffset now,
        DateTimeOffset readThrough, LogFilter filter = LogFilter.Important, LogRange range = LogRange.All,
        string query = "", bool unreadOnly = false, Func<ConnectionEntry, bool>? visible = null,
        Func<ConnectionEntry, ConnectionEntry>? enrich = null, TimeZoneInfo? timeZone = null)
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
            var row = new LogEntry(enrich is null ? source : source with { Connection = enrich(source.Connection) }, category,
                category == LogCategory.FirstApp && source.Time > readThrough);
            if (row.IsUnread) unreadCount++;
            if (unreadOnly && !row.IsUnread) continue;
            if (filter == LogFilter.Important && category != LogCategory.FirstApp) continue;
            if (filter >= LogFilter.FirstApp && (int)category != (int)filter - (int)LogFilter.FirstApp) continue;
            var day = TimeZoneInfo.ConvertTime(source.Time, timeZone).Date;
            if (range == LogRange.Today && day != today || range == LogRange.Day && source.Time < now.AddDays(-1)
                || range == LogRange.Week && source.Time < now.AddDays(-7)) continue;
            if (query.Length > 0 && !row.Connection.Matches(query) && !row.CategoryLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
                && !source.Event.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            rows.Add(row);
        }
        var groups = rows.OrderByDescending(x => x.Source.Time).Take(DisplayLimit)
            .GroupBy(x => TimeZoneInfo.ConvertTime(x.Source.Time, timeZone).Date)
            .Select(g => new LogDayGroup(g.Key == today ? "Today" : g.Key == today.AddDays(-1) ? "Yesterday" : g.Key.ToString("dddd, MMMM d, yyyy"), g))
            .ToList();
        return new(groups, rows.Count, unreadCount);
    }
}
