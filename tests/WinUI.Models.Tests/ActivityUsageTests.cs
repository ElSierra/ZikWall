using ZikWall.WinUI.Models;

static class ActivityUsageTests
{
    public static void Run()
    {
        int count = 0;
        void Check(bool value, string why) { if (!value) throw new Exception(why); count++; }
        var entry = new ConnectionEntry("TCPv4", "127.0.0.1", 45000, "8.8.8.8", 443, 10, "Established", "Same name", @"C:\Apps\one.exe", ProcessStarted: 100);
        var other = entry with { ProcessId = 11, ProcessPath = @"C:\Apps\two.exe" };
        var tree = new ActivityTree();
        tree.Update([entry, other]);
        Check(tree.Rows.Count == 2, "Equal names must not merge unrelated executables.");
        var helper = entry with { ProcessId = 11, LocalPort = 45001 };
        tree.Update([entry, helper]);
        Check(tree.Rows.Count == 1 && tree.Rows[0].Subtitle.Contains("2 processes"), "Identical executable processes collapse into one row.");
        tree.Toggle(tree.Rows[0]); tree.Update([entry, helper]);
        Check(tree.Rows.Count == 3, "Expanding the app reveals processes, not every socket.");
        tree.Toggle(tree.Rows[1]); tree.Update([entry, helper]);
        Check(tree.Rows.Count == 4 && tree.Rows[2].Entry == entry && !tree.Rows[2].Expandable, "Process expansion reveals its connection.");
        tree.Update([entry with { ProcessStarted = 101 }, helper]);
        Check(tree.Rows.Count == 3, "Reused PID must not inherit old process expansion.");
        tree.Update([entry with { PackageFamily = "Family_123" }, other with { PackageFamily = "Family_123" }]);
        Check(tree.Rows.Count == 1, "Packaged helpers with different paths share a package group.");
        tree.Update([entry, helper], true); Check(tree.Rows.Count == 2 && tree.Rows.All(x => !x.Expandable), "Flat mode keeps all connections.");
        var now = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        var day = DateOnly.FromDateTime(now.LocalDateTime);
        var session = Guid.NewGuid();
        var app = new AppTraffic(entry.ProcessPath, "", 100, 20);
        var ledger = new UsageLedger();
        UsageSnapshot Snapshot(DateTimeOffset time, AppTraffic traffic, Guid? id = null) => new(id ?? session, time, [traffic], 1, 0, 0, 0, 1024);
        ledger.Observe(Snapshot(now, app)); ledger.Observe(Snapshot(now, app));
        Check(ledger.Days.Single().Received == 100, "Repeated snapshots must not double-count bytes.");
        ledger.Observe(Snapshot(now.AddSeconds(5), app with { Received = 150, Sent = 30 }));
        Check(ledger.Days.Single().Received == 150 && ledger.Rates[app.AppKey] == (10, 2), "Rates come from cumulative counter deltas and elapsed time.");
        ledger.Observe(Snapshot(now.AddSeconds(10), app with { Received = 10, Sent = 5 }, Guid.NewGuid()));
        Check(ledger.Days.Single().Received == 160, "Restarted collectors add only the new session.");
        ledger.Load([new(day.AddDays(-30), app.AppKey, app.Path, 1, 1), new(day, app.AppKey, app.Path, 1, 1)], day);
        Check(ledger.Days.Count == 1, "Daily history retains exactly the current day and previous 29 days.");
        Check(ledger.Labels("missing", false, day).Total == "No usage recorded", "Missing usage must never imply a measured zero.");
        var casing = new UsageLedger();
        casing.Observe(new(Guid.NewGuid(), now, [app, app with { Path = app.Path.ToUpperInvariant() }], 1, 0, 0, 0, 1024));
        Check(casing.Days.Count == 1 && casing.Days[0].Received == 200, "Differently cased Windows paths must combine without dropping counters or duplicate-key failures.");
        var locations = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, ZikWall.WinUI.Services.GeoLocation>>(File.ReadAllText("ui/ZikWall.WinUI/Assets/Countries/centroids.json"))!;
        Check(locations.Count > 230 && locations["AU"].Label == "Australia" && locations["AU"].Latitude < -20, "Shared country codes must select mainland Australia rather than an outlying territory.");
        Check(locations.Values.All(x => double.IsFinite(x.Latitude) && double.IsFinite(x.Longitude) && x.Latitude is >= -90 and <= 90 && x.Longitude is >= -180 and <= 180), "Bundled country positions must fit the map projection.");
        Console.WriteLine($"PASS: {count} activity grouping and traffic accounting checks.");
    }
}
