using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

static class LogAnalysisTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        var browser = new ConnectionEntry("TCPv4", "192.168.1.2", 50000, "8.8.8.8", 443, 20,
            "Established", "Browser", @"C:\Apps\Browser.exe", "Outgoing", "US", "United States", "Internet");
        var app = browser with { ProcessPath = @"C:\Apps\Other.exe", ProcessName = "Other" };
        ConnectionEvent[] events = [new(now.AddDays(-1), "First seen", browser), new(now.AddMinutes(-5), "First seen", browser with { LocalPort = 50001 }),
            new(now.AddMinutes(-4), "SynSent → Established", browser), new(now.AddMinutes(-3), "No longer observed", browser), new(now.AddMinutes(-2), "First seen", app)];
        var important = LogAnalysis.Build(events, now, DateTimeOffset.MinValue, timeZone: TimeZoneInfo.Utc);
        Check(important.MatchingCount == 2 && important.Groups.SelectMany(x => x).All(x => x.Category == LogCategory.FirstApp), "Important must summarize each app's first retained observation only.");
        Check(important.Groups[0].Title == "Today" && important.Groups[1].Title == "Yesterday", "Timeline must group newest first by local calendar date.");
        var today = LogAnalysis.Build(events, now, DateTimeOffset.MinValue, range: LogRange.Today, query: "Browser", timeZone: TimeZoneInfo.Utc);
        Check(today.MatchingCount == 0, "Filtering today's events must not reclassify a known app as new.");
        var all = LogAnalysis.Build(events, now, now.AddMinutes(-4), LogFilter.All, timeZone: TimeZoneInfo.Utc);
        Check(all.MatchingCount == 5 && all.UnreadCount == 1, "Important events start unread; ordinary logs start read.");
        Check(LogAnalysis.Build(events, now, now.AddMinutes(-4), LogFilter.All, unreadOnly: true).MatchingCount == 1, "Unread-only must honor watermark and event importance.");
        Check(LogAnalysis.Build(events, now, now, LogFilter.Closed).MatchingCount == 1, "Closed category must match only ended observations.");
        Check(LogAnalysis.Build(events, now, now, LogFilter.State).MatchingCount == 1, "State category must retain substantive TCP transitions.");
        Check(LogAnalysis.Build(events, now, now, LogFilter.Connection).MatchingCount == 1, "Connection category must exclude first-app summaries.");
        var hints = LogAnalysis.Build(events, now, now, LogFilter.All, query: "dns.google", enrich: x => x with { DomainHint = "dns.google" });
        Check(hints.MatchingCount == 5 && hints.Groups[0][0].Host == "dns.google" && events[0].Connection.DomainHint == "", "Search must use current hostname enrichment without modifying the persisted event.");
        Check(LogAnalysis.Build(events, now, now, LogFilter.All, visible: x => x.Application != "Browser").MatchingCount == 1, "Hidden app filter must apply to the timeline.");
        var offset = TimeZoneInfo.CreateCustomTimeZone("test +2", TimeSpan.FromHours(2), "test", "test");
        var midnight = new DateTimeOffset(2026, 10, 13, 0, 30, 0, TimeSpan.FromHours(2));
        var localDay = LogAnalysis.Build([new(midnight.AddHours(-1), "First seen", browser)], midnight, now, timeZone: offset);
        Check(localDay.Groups[0].Title == "Yesterday", "Day grouping must use local midnight, not elapsed 24-hour windows.");
        var many = Enumerable.Range(0, 1002).Select(i => new ConnectionEvent(now.AddSeconds(-i), "First seen", browser with { ProcessPath = $"C:\\Apps\\{i}.exe" }));
        var capped = LogAnalysis.Build(many, now, now, LogFilter.All);
        Check(capped.MatchingCount == 1002 && capped.Groups.Sum(x => x.Count) == 1000, "Display cap must preserve truthful total counts.");
        int enriched = 0;
        ConnectionEntry Enrich(ConnectionEntry entry) { enriched++; return entry with { DomainHint = "dns.google" }; }
        var limited = LogAnalysis.Build(many, now, DateTimeOffset.MinValue, LogFilter.All, enrich: Enrich);
        Check(enriched == LogAnalysis.DisplayLimit && limited.UnreadCount == 1002,
            "Without search, enrich only displayed events while counting all unread entries.");
        enriched = 0;
        var filtered = LogAnalysis.Build(events, now, DateTimeOffset.MinValue, enrich: Enrich);
        Check(enriched == 2 && filtered.Groups.SelectMany(x => x).All(x => x.Host == "dns.google"),
            "Enrich only matching important events and retain their current hostname display.");
        enriched = 0;
        var excluded = LogAnalysis.Build(events, now.AddDays(10), DateTimeOffset.MinValue,
            LogFilter.All, LogRange.Today, enrich: Enrich);
        Check(enriched == 0 && excluded.MatchingCount == 0 && excluded.UnreadCount == 2,
            "Date-excluded events must skip enrichment without changing unread counts.");
        enriched = 0;
        var searched = LogAnalysis.Build(events, now, now, LogFilter.Closed, query: "dns.google", enrich: Enrich);
        Check(enriched == 1 && searched.MatchingCount == 1,
            "Hostname search must enrich category candidates exactly once, including displayed results.");
        Check(new LogEntry(new(now, "First seen", browser with { State = "SynSent" }), LogCategory.Connection, true).Message.Contains("attempting"), "Attempts must not claim a successful connection.");
        Check(new LogEntry(new(now, "First seen", browser with { RemotePort = null, Protocol = "UDPv4" }), LogCategory.Connection, true).Host.StartsWith("Local "), "UDP must not invent a remote hostname.");
        var timeline = new LogTimelineCollection();
        timeline.Update(all.Groups);
        var group = timeline[0];
        var originalRow = group[0];
        var notifications = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        group.CollectionChanged += (_, e) => notifications.Add(e.Action);
        timeline.CollectionChanged += (_, e) => notifications.Add(e.Action);
        timeline.Update(LogAnalysis.Build(events, now, now.AddMinutes(-4), LogFilter.All, timeZone: TimeZoneInfo.Utc).Groups);
        Check(notifications.Count == 0 && ReferenceEquals(group, timeline[0]) && ReferenceEquals(originalRow, timeline[0][0]),
            "Unchanged refresh must preserve native groups and row objects without notifications.");
        var newEvent = new ConnectionEvent(now, "First seen", app with { LocalPort = 50100 });
        var extended = events.Append(newEvent).ToArray();
        timeline.Update(LogAnalysis.Build(extended, now, now.AddMinutes(-4), LogFilter.All, timeZone: TimeZoneInfo.Utc).Groups);
        Check(notifications.SequenceEqual(new[] { System.Collections.Specialized.NotifyCollectionChangedAction.Add })
            && ReferenceEquals(group, timeline[0]) && ReferenceEquals(originalRow, group[1]),
            "A leading event must insert once and retain existing visible row objects.");
        notifications.Clear();
        var markers = LogReadState.Empty();
        markers.MarkRead(originalRow.Source);
        timeline.Update(LogAnalysis.Build(extended, now, now.AddMinutes(-4), LogFilter.All, timeZone: TimeZoneInfo.Utc, readState: markers).Groups);
        Check(notifications.SequenceEqual(new[] { System.Collections.Specialized.NotifyCollectionChangedAction.Replace }) && !group[1].IsUnread,
            "Reading one event must replace only that row without resetting its group.");
        notifications.Clear();
        var replacement = Enumerable.Range(0, 20).Select(i => new LogEntry(new(now.AddSeconds(-i), "First seen", app with { LocalPort = 51000 + i }), LogCategory.Connection, false)).ToArray();
        timeline.Update([new("Today", replacement), all.Groups[1]]);
        Check(notifications.SequenceEqual(new[] { System.Collections.Specialized.NotifyCollectionChangedAction.Reset }) && group.SequenceEqual(replacement),
            "An unrelated page must replace its rows with one notification.");
        timeline.Update([new("Yesterday", replacement.Take(2)), new("Older date", replacement.Skip(2))]);
        Check(timeline.Select(x => x.Title).SequenceEqual(new[] { "Yesterday", "Older date" }) && timeline.Sum(x => x.Count) == replacement.Length,
            "Date changes must remove old groups and retain all requested rows in order.");
        timeline.Update([]);
        Check(timeline.Count == 0, "Filtering to no matches must remove every old group.");
        var directory = Path.Combine(Path.GetTempPath(), "zikwall-log-test-" + Guid.NewGuid());
        try
        {
            Check(new MonitorPreferencesStore(directory).Load().SaveHistory, "New installations must save history on this PC by default.");
            var store = new LogReadStateStore(directory);
            Check(store.Load() == DateTimeOffset.MinValue, "Missing read state must leave events unread.");
            store.Save(now);
            Check(store.Load() == now, "Read state must survive reopening.");
            File.WriteAllText(Path.Combine(directory, "log-read-through.txt"), "invalid");
            Check(store.Load() == DateTimeOffset.MinValue, "Invalid timestamp must fail to unread rather than hide events.");
            var state = LogReadState.Empty();
            state.MarkRead(events[^1] with { Connection = app with { DomainHint = "display.example" } });
            var individuallyRead = LogAnalysis.Build(events, now, state.ReadThrough, readState: state);
            Check(individuallyRead.UnreadCount == 1 && !individuallyRead.Groups[0][0].IsUnread,
                "Reading the newest entry must not mark an older app read; enriched rows must match their original events.");
            store.SaveState(state);
            var reopened = new LogReadStateStore(directory).LoadState();
            Check(reopened.IsRead(events[^1]) && !reopened.IsRead(events[0]), "Individual read markers must survive restart/update.");
            var sameTime = events[^1] with { Connection = browser };
            Check(!reopened.IsRead(sameTime), "Different events at the same time must have independent read markers.");
            Check(LogAnalysis.Build(events, now, reopened.ReadThrough, unreadOnly: true, readState: reopened).MatchingCount == 1,
                "Read entries must disappear from unread-only results after reopening.");
            store.SaveState(new(now, []));
            Check(LogAnalysis.Build(events, now, now, readState: store.LoadState()).UnreadCount == 0,
                "Mark all as read must survive reopening.");
            File.Delete(Path.Combine(directory, "log-read-state.json"));
            store.Save(now.AddMinutes(-4));
            Check(store.LoadState().ReadThrough == now.AddMinutes(-4), "Upgrade must import the legacy read-through timestamp.");
            File.WriteAllText(Path.Combine(directory, "monitor-preferences.json"), "{\"HideWindowsSystemApps\":true}");
            Check(new MonitorPreferencesStore(directory).Load().SaveHistory, "Older settings missing SaveHistory must default to local saving.");
            var preferences = new MonitorPreferencesStore(directory);
            preferences.Save(new(SaveHistory: false));
            Check(!new MonitorPreferencesStore(directory).Load().SaveHistory, "An explicit saving opt-out must survive updates.");
            var observation = new ConnectionEvent(DateTimeOffset.UtcNow, "First seen", browser);
            new ActivityStore(directory).Save(new([observation], []));
            Check(new ActivityStore(directory).Load().Events.Single() == observation,
                "A replacement app instance must restore saved history from the same version-independent directory.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        Console.WriteLine($"PASS: {checks} log analysis checks.");
        HistoryPersistenceTests.Run();
    }
}
