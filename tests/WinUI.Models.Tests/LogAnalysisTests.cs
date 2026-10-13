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
        Check(new LogEntry(new(now, "First seen", browser with { State = "SynSent" }), LogCategory.Connection, true).Message.Contains("attempting"), "Attempts must not claim a successful connection.");
        Check(new LogEntry(new(now, "First seen", browser with { RemotePort = null, Protocol = "UDPv4" }), LogCategory.Connection, true).Host.StartsWith("Local "), "UDP must not invent a remote hostname.");
        var directory = Path.Combine(Path.GetTempPath(), "zikwall-log-test-" + Guid.NewGuid());
        try
        {
            var store = new LogReadStateStore(directory);
            Check(store.Load() == DateTimeOffset.MinValue, "Missing read state must leave events unread.");
            store.Save(now);
            Check(store.Load() == now, "Read state must survive reopening.");
            File.WriteAllText(Path.Combine(directory, "log-read-through.txt"), "invalid");
            Check(store.Load() == DateTimeOffset.MinValue, "Invalid timestamp must fail to unread rather than hide events.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        Console.WriteLine($"PASS: {checks} log analysis checks.");
    }
}
