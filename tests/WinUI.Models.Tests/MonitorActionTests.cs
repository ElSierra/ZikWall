using ZikWall.WinUI.Models;

static class MonitorActionTests
{
    public static void Run()
    {
        int count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        bool Rejected(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
        var entry = new ConnectionEntry("TCPv4", "192.168.1.2", 50123, "8.8.8.8", 443, 10,
            "Established", "Browser", @"C:\Apps\browser.exe", "Outgoing", PackageFamily: "Package_1", ProcessStarted: 100);
        var helper = entry with { ProcessPath = @"C:\Apps\helper.exe", ProcessId = 11 };
        var tree = new ActivityTree();
        tree.Update([entry, helper]);
        var target = MonitorActionTarget.Capture(tree.Rows[0], new[] { entry, helper });
        var app = target.Block(false);
        Check(app.Rules.Count == 2 && app.Rules.Select(x => x.Application).Distinct().Count() == 2, "Package action must include each observed executable, not just its first row.");
        Check(app.Rules.All(x => x.Block && x.Direction == 3 && x.Protocol == 0 && x.RemotePort == 0 && x.RemoteNetwork == ""), "App block covers both directions without accidental endpoint limits.");
        tree.Toggle(tree.Rows[0]); tree.Update([entry, helper]);
        var process = MonitorActionTarget.Capture(tree.Rows[1], new[] { entry, helper });
        Check(process.Block(false).Rules.Single().Application == entry.ProcessPath, "Subprocess action must not include a sibling executable.");
        Check(Rejected(() => process.Block(true)), "A process's first connection is not its whole connection scope.");
        tree.Update([entry], true);
        var leaf = MonitorActionTarget.Capture(tree.Rows[0], new[] { entry });
        var rule = leaf.Block(true).Rules.Single();
        Check(rule.Application == entry.ProcessPath && rule.RemoteNetwork == "8.8.8.8" && rule.RemotePort == 443 && rule.Protocol == 6 && rule.Direction == 1, "Connection block must preserve every supported scope field.");
        MonitorActionTarget For(ConnectionEntry item) { tree.Update([item], true); return MonitorActionTarget.Capture(tree.Rows[0], new[] { item }); }
        Check(For(entry with { Direction = "Incoming" }).Block(true).Rules.Single().Direction == 2, "Incoming connection block must remain inbound.");
        Check(For(entry with { Protocol = "TCPv6", RemoteAddress = "2001:4860:4860::8888" }).Block(true).Rules.Single().RemoteNetwork == "2001:4860:4860::8888", "IPv6 address must survive rule creation.");
        Check(Rejected(() => For(entry with { Direction = "Unknown" }).Block(true)), "Unknown direction must not silently become outbound or both.");
        Check(Rejected(() => For(entry with { Protocol = "UDPv4", RemoteAddress = "", RemotePort = null }).Block(true)), "Unavailable UDP destination must not become an unrestricted app block.");
        Check(Rejected(() => For(entry with { RemoteAddress = "0.0.0.0", RemotePort = 0 }).Block(true)), "Listening wildcard is not a remote target.");
        Check(Rejected(() => For(entry with { RemoteAddress = "8.8.8.8/0" }).Block(true)), "An edited history address must not broaden an endpoint block into a CIDR network.");
        Check(Rejected(() => For(entry with { RemoteAddress = "fe80::1%5", Protocol = "TCPv6" }).Block(true)), "Unsupported scoped IPv6 must not lose its interface scope.");
        Check(Rejected(() => For(entry with { ProcessPath = "" }).Block(false)), "Missing executable must never broaden to all apps.");
        Check(Rejected(() => For(entry with { ProcessPath = @"\\server\app.exe" }).Block(false)), "Remote executable paths remain rejected by firewall validation.");
        tree.Update([entry, helper with { ProcessPath = "" }]);
        Check(Rejected(() => MonitorActionTarget.Capture(tree.Rows[0], new[] { entry, helper with { ProcessPath = "" } }).Block(false)), "Incomplete group identity must not claim a complete app block.");
        tree.Update([helper], true);
        Check(leaf.Block(true).Rules.Single().Application == entry.ProcessPath && target.Block(false).Rules.Count == 2, "Refreshing or replacing rows must not retarget an open menu.");
        Check(For(entry with { ProcessName = new string('x', 200) }).Block(true).Rules.Single().Name.Length == 128, "Long display names must not break otherwise valid rules.");
        Console.WriteLine($"PASS: {count} monitor action scope and stale-target checks.");
    }
}
