using System.Net.Sockets;
using System.Text.Json;
using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

static class HostNameTests
{
    public static async Task RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        var row = new ConnectionEntry("TCPv4", "192.168.1.2", 40000, "8.8.8.8", 443, 2, "Established", "Browser", @"C:\Browser.exe");
        var hints = row with { DomainHint = "site.example, shared.example", ReverseDnsName = "server.example" };
        var summary = HostSummary.Create([hints, hints with { LocalPort = 40001 }, row with { RemoteAddress = "1.1.1.1" }, row with { RemotePort = null }]);
        Check(summary.Count == 2 && summary.More == "+1 more" && summary.Items[0].Connections == 2, "Host count must deduplicate addresses, not count sockets or aliases.");
        Check(summary.Items[0].Names.Contains("site.example") && !summary.Items[0].Names.Contains("server.example"), "Cache hints take priority over reverse lookup.");
        Check(HostSummary.Create([row, row with { RemoteAddress = "::ffff:8.8.8.8" }]).Count == 1, "Mapped IPv4 must identify the same host.");
        Check(HostSummary.Create([row with { RemotePort = 0 }]).Count == 0, "Listening endpoints have no remote host.");
        Check(HostSummary.Create([row, row with { State = "CloseWait", DomainHint = "updated.example" }, row with { LocalPort = 40001 }]).Items.Single().Connections == 2,
            "Host socket counts must deduplicate transport identities while preserving distinct ports.");
        var reversed = row with { ReverseDnsName = "server.example" };
        Check(reversed.DestinationLabel == "server.example" && reversed.DomainDescription.Contains("Reverse DNS") && reversed.Matches("SERVER.EXAMPLE"), "Reverse DNS must display, search and identify its source.");
        Check(reversed.Identity == row.Identity && !JsonSerializer.Serialize(reversed).Contains("server.example"), "Lookup names must not change identity or persist in history.");
        var tree = new ActivityTree(); tree.Update([row]); tree.Update([reversed]);
        Check(tree.Rows[0].Hosts.Primary == "server.example", "Live rows must update when hostnames arrive.");
        var retained = tree.Rows[0];
        tree.Update([reversed], fresh: new HashSet<string> { reversed.Key });
        Check(tree.Rows[0].IsNew, "New connection markers must appear for matching identities.");
        tree.Update([reversed], fresh: new HashSet<string>());
        Check(!tree.Rows[0].IsNew && ReferenceEquals(retained, tree.Rows[0]), "An empty fresh set must clear the marker without replacing its row.");
        long now = 0; int calls = 0;
        using var resolver = new ReverseDnsHints((_, _) => { calls++; return Task.FromResult("SERVER.Example."); }, () => now);
        resolver.RequestRefresh([row], false); await resolver.Pending;
        Check(calls == 0, "Reverse DNS is off by default.");
        resolver.SetEnabled(true);
        resolver.RequestRefresh([row with { RemoteAddress = "192.168.1.1" }, hints], false); await resolver.Pending;
        Check(calls == 0, "Do not resolve local addresses or replace available cache hints.");
        resolver.RequestRefresh([row], false); await resolver.Pending;
        Check(calls == 1 && resolver.Find(row.RemoteAddress) == "server.example", "Valid answers normalize and publish.");
        resolver.RequestRefresh([row with { RemoteAddress = "1.1.1.1" }], false); await resolver.Pending;
        Check(calls == 1, "Lookups are throttled across monitor ticks.");
        now = 60000; resolver.RequestRefresh([row], false); await resolver.Pending;
        Check(calls == 1, "Positive cache avoids repeated queries.");
        now = 600001; Check(resolver.Find(row.RemoteAddress) == "", "Positive hints expire.");
        resolver.SetEnabled(false); Check(resolver.Find(row.RemoteAddress) == "", "Turning off immediately clears hints.");
        using var negative = new ReverseDnsHints((_, _) => { calls++; throw new SocketException(); }, () => now);
        negative.SetEnabled(true); negative.RequestRefresh([row], false); await negative.Pending;
        int afterFailure = calls; now += 60000; negative.RequestRefresh([row], false); await negative.Pending;
        Check(calls == afterFailure && negative.Find(row.RemoteAddress) == "", "Failed lookups are negative-cached and fall back to IP.");
        int batchCalls = 0;
        using var batch = new ReverseDnsHints((_, _) => { batchCalls++; return Task.FromResult("8.8.8.8"); }, () => 0);
        batch.SetEnabled(true); batch.RequestRefresh(Enumerable.Range(1, 30).Select(i => row with { RemoteAddress = $"8.8.8.{i}" }).ToArray(), false); await batch.Pending;
        Check(batchCalls == 8 && batch.Find(row.RemoteAddress) == "", "Each batch is bounded and numeric responses are not hostnames.");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pending = new ReverseDnsHints((_, _) => { started.TrySetResult(); return release.Task; }, () => 0);
        pending.SetEnabled(true); pending.RequestRefresh([row], false); await started.Task;
        pending.SetEnabled(false); release.TrySetResult("late.example"); await pending.Pending;
        Check(pending.Find(row.RemoteAddress) == "", "Late queries cannot republish after disable.");
        Console.WriteLine($"PASS: {checks} hostname summary and reverse DNS checks.");
    }
}
