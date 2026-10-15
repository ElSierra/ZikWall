using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

internal static class SavedLogTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "zikwall-saved-logs-" + Guid.NewGuid());
        try
        {
            var now = DateTimeOffset.UtcNow.AddMinutes(-1);
            var browser = new ConnectionEntry("TCPv4", "127.0.0.1", 1234, "192.0.2.1", 443, 10,
                "Established", "Browser", @"C:\Tests\browser.exe");
            var yesterday = new ConnectionEvent(now.AddDays(-1), "First seen", browser);
            var events = new[] { yesterday }.Concat(Enumerable.Range(0, ActivityStore.MaximumEvents + 5)
                .Select(i => new ConnectionEvent(now, "First seen", browser with { LocalPort = 2000 + i }))).ToList();
            new ActivityStore(directory).Save(new(events, []));
            var reopened = new ActivityStore(directory).Load();
            if (reopened.Events.Contains(yesterday)) throw new Exception("Fixture did not exceed the recent-view limit.");
            var saved = new SavedLogStore(directory);
            var logs = saved.Query(reopened.Events, DateTimeOffset.UtcNow, LogReadState.Empty()).Timeline;
            if (logs.Groups.SelectMany(x => x).Single().Source != yesterday)
                throw new Exception("After restart, Log analysis lost yesterday's saved observation and relabeled a recent connection as first seen.");
            if (saved.Query(reopened.Events, DateTimeOffset.UtcNow, LogReadState.Empty(), range: LogRange.Today).Timeline.MatchingCount != 0)
                throw new Exception("Changing the date range relabeled a known app as new.");
            var seen = new HashSet<string>();
            LogCursor? cursor = null;
            do
            {
                var page = saved.Query(reopened.Events, DateTimeOffset.UtcNow, LogReadState.Empty(), LogFilter.All, before: cursor);
                if (page.Timeline.MatchingCount != events.Count || page.Timeline.Groups.Sum(x => x.Count) > LogAnalysis.DisplayLimit)
                    throw new Exception("Saved log pages lost their total count or exceeded the display limit.");
                foreach (var row in page.Timeline.Groups.SelectMany(x => x))
                    if (!seen.Add(LogReadState.EventId(row.Source))) throw new Exception("Paging repeated an event.");
                cursor = page.Next;
            } while (cursor is not null);
            if (seen.Count != events.Count) throw new Exception("Paging skipped older events sharing a timestamp.");
            var state = LogReadState.Empty(); state.MarkRead(yesterday);
            var readStore = new LogReadStateStore(directory); readStore.SaveState(state);
            if (new SavedLogStore(directory).Query(reopened.Events, DateTimeOffset.UtcNow, readStore.LoadState(), unreadOnly: true).Timeline.MatchingCount != 0)
                throw new Exception("Reading an archived observation did not persist across restart.");
            foreach (var filter in Enum.GetValues<LogFilter>())
            {
                var expected = LogAnalysis.Build(events, DateTimeOffset.UtcNow, DateTimeOffset.MinValue, filter);
                var actual = saved.Query(reopened.Events, DateTimeOffset.UtcNow, LogReadState.Empty(), filter).Timeline;
                if (expected.MatchingCount != actual.MatchingCount || expected.UnreadCount != actual.UnreadCount)
                    throw new Exception("Saved and live query semantics disagree for " + filter);
            }
            var older = yesterday with { Time = now.AddDays(-90), Connection = browser with { ProcessPath = @"C:\Tests\older.exe" } };
            new ActivityStore(directory).Append(new([older], []));
            var oldResult = saved.Query(reopened.Events, DateTimeOffset.UtcNow, LogReadState.Empty(), query: "older");
            if (oldResult.Timeline.Groups.SelectMany(x => x).Single().Source != older
                || saved.Query(reopened.Events, DateTimeOffset.UtcNow, LogReadState.Empty(), range: LogRange.Week, query: "older").Timeline.MatchingCount != 0)
                throw new Exception("All saved history and the seven-day range did not query their actual dates.");
            Console.WriteLine("PASS: saved logs survive restart/cache limits; same-time pagination, complete counts, stable first observations, search, date ranges and read markers.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    internal static void Inspect(string directory)
    {
        var live = System.Text.Json.JsonSerializer.Deserialize<ActivityArchive>(File.ReadAllText(Path.Combine(directory, "activity.json")))!;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = new SavedLogStore(directory).Query(live.Events, DateTimeOffset.UtcNow, new LogReadStateStore(directory).LoadState(), range: LogRange.Week);
        Console.WriteLine($"Saved-history query: {result.Timeline.MatchingCount} important events; {result.Timeline.Groups.Count} date groups; {watch.ElapsedMilliseconds} ms.");
        foreach (var group in result.Timeline.Groups) Console.WriteLine($"{group.Title}: {group.Count} events");
        watch.Restart();
        var all = new SavedLogStore(directory).Query(live.Events, DateTimeOffset.UtcNow, LogReadState.Empty(), LogFilter.All, LogRange.Week);
        Console.WriteLine($"All-event query: {all.Timeline.MatchingCount} matches; {all.Timeline.Groups.Sum(x => x.Count)} displayed; older page available = {all.Next is not null}; {watch.ElapsedMilliseconds} ms.");
    }
}
