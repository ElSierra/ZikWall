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
        var stableRows = new ActivityTree(); stableRows.Update([entry, other]);
        var retainedApp = stableRows.Rows[0]; var retainedOther = stableRows.Rows[1];
        var changedProperties = new HashSet<string>(); int rowChanges = 0;
        retainedApp.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName!);
        stableRows.Rows.CollectionChanged += (_, _) => rowChanges++;
        var namedEntry = entry with { DomainHint = "updated.example", CountryCode = "US", CountryName = "United States" };
        stableRows.Update([namedEntry, other]);
        Check(ReferenceEquals(retainedApp, stableRows.Rows[0]) && rowChanges == 0 && retainedApp.Entry == namedEntry
            && retainedApp.Hosts.Primary == "updated.example" && changedProperties.Contains("Hosts") && changedProperties.Contains("CountryFlags"),
            "Changing host details must update bindings without replacing the realized app row.");
        changedProperties.Clear();
        stableRows.Update([namedEntry, namedEntry with { LocalPort = 45099 }, other]);
        Check(changedProperties.Contains("Hosts") && !changedProperties.Contains("HostFlag") && !changedProperties.Contains("HostPrimary")
            && retainedApp.Hosts.Items.Single().Connections == 2, "Socket-count changes must not reload the unchanged native flag or hostname label.");
        stableRows.Update([other]);
        Check(ReferenceEquals(retainedOther, stableRows.Rows[0]), "Removing an earlier app must preserve surviving row identity.");
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
        var sortLedger = new UsageLedger();
        var small = entry with { ProcessName = "Small", ProcessPath = @"C:\Apps\small.exe" };
        var large = entry with { ProcessName = "Large", ProcessPath = @"C:\Apps\large.exe", ProcessId = 12 };
        var unknown = entry with { ProcessName = "Unknown", ProcessPath = @"C:\Apps\unknown.exe", ProcessId = 13 };
        var zero = entry with { ProcessName = "Zero", ProcessPath = @"C:\Apps\zero.exe", ProcessId = 14 };
        sortLedger.Load([new(day, small.AppKey, small.ProcessPath, 900 * 1024, 0), new(day, large.AppKey, large.ProcessPath, 0, 2 * 1024 * 1024), new(day, zero.AppKey, zero.ProcessPath, 0, 0)], day);
        AppUsageDisplay Display(string key) => sortLedger.Display(key, day);
        Check(Display(large.AppKey).Bytes == 2 * 1024 * 1024 && Display(unknown.AppKey).Bytes is null && Display(zero.AppKey).Bytes == 0,
            "Usage must combine upload and download without confusing unobserved apps with zero traffic.");
        tree = new();
        tree.Update([small, large, unknown, zero], showUsage: true, sort: UsageSort.Descending, usageDisplay: Display);
        Check(tree.Rows.Select(x => x.Entry.AppKey).SequenceEqual(new[] { large.AppKey, small.AppKey, zero.AppKey, unknown.AppKey }), "Usage sort must compare bytes numerically and put unknowns last.");
        tree.Toggle(tree.Rows[0]); tree.Update([small, large, unknown, zero], showUsage: true, sort: UsageSort.Descending, usageDisplay: Display);
        var processRow = tree.Rows[1]; tree.Toggle(processRow);
        tree.Update([small, large, unknown, zero], showUsage: true, sort: UsageSort.Descending, usageDisplay: Display);
        Check(tree.Rows[1].Total == "" && tree.Rows[2].Total == "" && tree.Rows.All(x => x.UsageVisible), "Process/socket rows must align without inventing per-connection usage.");
        tree.ApplyUsage(true, UsageSort.Ascending, Display);
        Check(tree.Rows[0].Entry.AppKey == zero.AppKey && tree.Rows[1].Entry.AppKey == small.AppKey && tree.Rows.Skip(2).Take(3).All(x => x.Entry.AppKey == large.AppKey) && tree.Rows[^1].Entry.AppKey == unknown.AppKey,
            "Reversing usage sort must preserve expanded app blocks and keep unknowns last.");
        int changes = 0; tree.Rows.CollectionChanged += (_, _) => changes++;
        tree.ApplyUsage(true, UsageSort.Ascending, Display);
        Check(changes == 0, "An unchanged usage sample must not replace native list rows.");
        var retained = tree.Rows.Single(x => x.Key == small.AppKey);
        int notifications = 0; retained.PropertyChanged += (_, _) => notifications++;
        sortLedger.Load([new(day, small.AppKey, small.ProcessPath, 950 * 1024, 0), new(day, large.AppKey, large.ProcessPath, 0, 2 * 1024 * 1024), new(day, zero.AppKey, zero.ProcessPath, 0, 0)], day);
        tree.ApplyUsage(true, UsageSort.Ascending, Display);
        Check(changes == 0 && ReferenceEquals(retained, tree.Rows.Single(x => x.Key == small.AppKey)) && retained.Total == "950.0 KB" && notifications > 0,
            "Changing usage values must notify cells in place without replacing connection rows.");
        tree.ApplyUsage(false, UsageSort.AppName, Display);
        Check(tree.Rows.All(x => !x.UsageVisible && x.Total.Length == 0) && tree.Rows[0].Entry.AppKey == large.AppKey, "Disabling usage must remove its column and restore name order.");
        tree.Update([small, large], flat: true, showUsage: true, sort: UsageSort.Descending, usageDisplay: Display);
        Check(tree.Rows[0].Total == "2.0 MB" && tree.Rows[0].UsageHint.Contains("Whole-app"), "Flat connections must label displayed usage as whole-app totals.");
        sortLedger.Load([new(day, small.AppKey, small.ProcessPath, ulong.MaxValue, ulong.MaxValue)], day);
        Check(Display(small.AppKey).Bytes == (decimal)ulong.MaxValue * 2 && sortLedger.Display(small.AppKey, day.AddDays(1)).Bytes is null, "Combined counters must not overflow, and yesterday's totals must not appear as today's.");
        var locations = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, ZikWall.WinUI.Services.GeoLocation>>(File.ReadAllText("ui/ZikWall.WinUI/Assets/Countries/centroids.json"))!;
        Check(locations.Count > 230 && locations["AU"].Label == "Australia" && locations["AU"].Latitude < -20, "Shared country codes must select mainland Australia rather than an outlying territory.");
        Check(locations.Values.All(x => double.IsFinite(x.Latitude) && double.IsFinite(x.Longitude) && x.Latitude is >= -90 and <= 90 && x.Longitude is >= -180 and <= 180), "Bundled country positions must fit the map projection.");
        Console.WriteLine($"PASS: {count} activity grouping and traffic accounting checks.");
    }
}
