using System.Net;

using System.Text.Json;
using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

static class CachedDomainTests
{
    public static async Task RunAsync(bool native)
    {
        int checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        var row = new ConnectionEntry("TCPv4", "127.0.0.1", 1234, "192.0.2.1", 443, 1, "Established");
        long now = 0; int calls = 0;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var hints = new CachedDomainHints(wanted =>
        {
            Interlocked.Increment(ref calls); entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return [new(row.RemoteAddress, "WWW.Example.COM.", 30), new(row.RemoteAddress, "b.example", 60),
                new(row.RemoteAddress, "c.example", 60), new(row.RemoteAddress, "d.example", 60),
                new(row.RemoteAddress, "bad/name.example", 60), new("192.0.2.99", "unrelated.example", 60)];
        }, () => now);
        hints.RequestRefresh([row], false);
        Check(entered.Wait(TimeSpan.FromSeconds(10)), "Background reader must start.");
        Check(hints.Find(row.RemoteAddress) == "", "Pending work must not block reads.");
        now = 20000; hints.RequestRefresh([row], false);
        Check(calls == 1, "A blocked scan must remain single flight.");
        release.Set(); await hints.Pending;
        Check(hints.Find(row.RemoteAddress) == "b.example, c.example, www.example.com", "Names must normalize, sort and cap at three.");
        Check(hints.Find("::ffff:192.0.2.1") == hints.Find(row.RemoteAddress), "Mapped IPv4 must match.");
        Check(hints.Find("192.0.2.99") == "", "Only observed endpoints may be cached.");
        now = 60000;
        Check(hints.Find(row.RemoteAddress) == "", "The earliest answer TTL must expire the hint.");
        hints.RequestRefresh([row], true); await hints.Pending;
        now = 179999; hints.RequestRefresh([row], true);
        Check(calls == 2, "Background scans must wait 120 seconds.");
        now = 180000; hints.RequestRefresh([row], true); await hints.Pending;
        Check(calls == 3, "Background scan must resume when due.");

        now = 0; int reads = 0; bool fail = false;
        using var partial = new CachedDomainHints(_ => { reads++; if (fail) throw new NotSupportedException(); return reads == 1 ? [new(row.RemoteAddress, "one.example", 300)] : []; }, () => now);
        partial.RequestRefresh([row], false); await partial.Pending;
        now = 59999; partial.RequestRefresh([row], false);
        Check(reads == 1, "Foreground scans must wait 60 seconds.");
        now = 60000; partial.RequestRefresh([row], false); await partial.Pending;
        Check(partial.Find(row.RemoteAddress) == "one.example", "Partial scans preserve unexpired hints.");
        now = 120000;
        Check(partial.Find(row.RemoteAddress) == "", "TTL must be capped at 120 seconds.");
        fail = true; partial.RequestRefresh([row], false); await partial.Pending;
        Check(partial.Find(row.RemoteAddress) == "", "Unavailable resolver must gracefully fall back to IPs.");
        Check(partial.State == DomainCacheState.Unavailable, "Failed cache reads must be distinguishable from an empty cache.");
        using var denied = new CachedDomainHints(_ => throw new UnauthorizedAccessException(), () => 0);
        denied.RequestRefresh([row], false); await denied.Pending;
        Check(denied.State == DomainCacheState.AccessDenied, "Cache access failures must explain why website names are missing.");
        using var startup = new CachedDomainHints(_ => [new(row.RemoteAddress, "startup.example", 60)], () => 0);
        startup.RequestRefresh([], false);
        startup.RequestRefresh([row], false); await startup.Pending;
        Check(startup.Find(row.RemoteAddress) == "startup.example" && startup.State == DomainCacheState.Ready,
            "An empty startup capture must not delay the first real website lookup by a minute.");

        int count = 0;
        using var bounded = new CachedDomainHints(wanted => { count = wanted.Count; return []; }, () => 0);
        bounded.RequestRefresh(Enumerable.Range(1, 5000).Select(i => row with { RemoteAddress = $"10.0.{i / 256}.{i % 256}" }).ToArray(), false);
        await bounded.Pending;
        Check(count == CachedDomainHints.MaxAddresses, "Snapshot must bound distinct addresses.");
        using var empty = new CachedDomainHints(_ => throw new Exception("No lookup expected"), () => 0);
        empty.RequestRefresh([row with { RemotePort = null }, row with { RemoteAddress = "0.0.0.0" }], false);
        await empty.Pending;
        Check(empty.Find(row.RemoteAddress) == "", "Local and unspecified endpoints must not trigger scans.");

        using var disposalEntered = new ManualResetEventSlim();
        using var disposalRelease = new ManualResetEventSlim();
        var disposed = new CachedDomainHints(_ => { disposalEntered.Set(); if (!disposalRelease.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); return [new(row.RemoteAddress, "late.example", 60)]; }, () => 0);
        disposed.RequestRefresh([row], false);
        Check(disposalEntered.Wait(TimeSpan.FromSeconds(10)), "Disposal fixture must start.");
        disposed.Dispose(); disposalRelease.Set(); await disposed.Pending;
        Check(disposed.Find(row.RemoteAddress) == "", "Disposed workers must not publish.");

        var named = row with { DomainHint = "www.example.com" };
        Check(new ConnectionEvent(DateTimeOffset.UtcNow, "First seen", named).Endpoints.Contains("www.example.com"),
            "Activity history must show available website names alongside IP endpoints.");
        var tree = new ActivityTree();
        tree.Update([named]);
        Check(tree.Rows.Single().Details.Contains("www.example.com"), "Website hints must be visible on collapsed app rows.");
        tree.Toggle(tree.Rows.Single()); tree.Update([named]);
        Check(tree.Rows[1].Details.Contains("www.example.com"), "Website hints must be visible on collapsed process rows.");
        tree.Toggle(tree.Rows[1]); tree.Update([named]);
        Check(tree.Rows[2].Title == named.DomainHint && tree.Rows[2].Details.Contains(row.RemoteEndpoint), "Expanded connections must retain both the website hint and IP endpoint.");
        Check(named.Identity == row.Identity && named.Key == row.Key && named.RemoteEndpoint == row.RemoteEndpoint, "Hints must not change endpoint identity or rule target.");
        var tracker = new ConnectionTracker(); tracker.Observe([row], DateTimeOffset.UtcNow);
        Check(tracker.Observe([named], DateTimeOffset.UtcNow).Count == 0, "Hints must not generate connection events.");
        Check(named.Matches("EXAMPLE.COM") && named.DestinationLabel == named.DomainHint, "Hints must display and search.");
        string json = JsonSerializer.Serialize(named);
        var restored = JsonSerializer.Deserialize<ConnectionEntry>(json)!;
        Check(!json.Contains("example.com") && restored.DomainHint == "" && restored.DestinationLabel == row.RemoteEndpoint, "Domain hints must not persist and old history must keep safe defaults.");
        foreach (string bad in new[] { "", "localhost", "192.0.2.1", "a..example", "-a.example", "a-.example", "a/b.example", new string('a', 64) + ".example" })
            Check(!CachedDomainHints.ValidName(bad), "Invalid DNS name accepted.");
        Check(CachedDomainHints.ValidName("xn--bcher-kva.example."), "ASCII IDNs and trailing dots must work.");

        var wantedFixture = new HashSet<string> { row.RemoteAddress, "2001:db8::1" };
        Check(WindowsDnsCache.Decode(row.RemoteAddress, "answer.example", 12, wantedFixture) is { TtlSeconds: 12 }, "Provider records must retain their TTL.");
        Check(WindowsDnsCache.Decode("::ffff:192.0.2.1", "answer.example", 12, wantedFixture)?.Address == row.RemoteAddress, "Mapped provider addresses must normalize.");
        Check(WindowsDnsCache.Decode("2001:db8::1", "v6.example", 60, wantedFixture) is not null, "IPv6 cache records must match.");
        Check(WindowsDnsCache.Decode(row.RemoteAddress, "answer.example", 0, wantedFixture) is null
            && WindowsDnsCache.Decode("192.0.2.99", "answer.example", 60, wantedFixture) is null
            && WindowsDnsCache.Decode(row.RemoteAddress, "bad/name.example", 60, wantedFixture) is null, "Expired, unrelated and malformed records must be rejected.");
        Check(WindowsDnsCache.Query.Contains("Status=0 AND Section=1") && WindowsDnsCache.Query.Contains("Type=1 OR Type=28"), "Provider must request successful A/AAAA answers only.");
        if (native && OperatingSystem.IsWindows())
        {
            var wanted = ConnectionMonitor.Capture().Where(x => x.RemotePort > 0).Select(x => x.RemoteAddress).ToHashSet();
            var cache = new WindowsDnsCache();
            int matched = 0;
            for (int i = 0; i < 20; i++) matched += cache.Read(wanted).Count;
            Console.WriteLine($"Native cache-only smoke: 20 scans, {wanted.Count} remote IPs, {matched} total hints (names omitted).");
        }
        Console.WriteLine($"PASS: {checks} passive domain hint checks.");
    }
}
