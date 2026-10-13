using ZikWall.WinUI.Models;

static class ActivityOverviewTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        var entry = new ConnectionEntry("TCPv4", "192.168.1.2", 45000, "8.8.8.8", 443, 10, "Established",
            "Browser", @"C:\Apps\browser.exe", "Outgoing", "US", "United States", "Internet", DomainHint: "dns.google");
        var second = entry with { LocalPort = 45001, RemoteAddress = "1.1.1.1", CountryCode = "AU", CountryName = "Australia", DomainHint = "one.one.one.one" };
        var result = ActivityOverview.Build([entry, entry, second], [new(now, entry.AppKey, entry.ProcessPath, 1024, 512)]);
        Check(result.Connections == 2 && result.AppCount == 1 && result.HostCount == 2, "Current and archived copies must not count the same connection twice.");
        Check(result.Download == 1024 && result.Upload == 512 && result.Apps.Single().Value == "1.5 KB", "One app's bytes must not be multiplied by its connections.");
        Check(result.Hosts.All(x => x.Value == "1" && x.Hint.Contains("observed connections")), "Host rankings must not invent byte attribution.");
        Check(result.Types.Single().Title == "HTTPS (TCP 443)" && result.Types.Single().Value == "2", "Traffic types must group matching ports.");
        Check(result.Countries.Count == 2 && result.Hosts.Any(x => x.Title == "dns.google"), "Country flags and available DNS names must be retained.");
        Check(Math.Abs(result.Apps[0].DownloadShare + result.Apps[0].UploadShare + result.Apps[0].RemainingShare - 1) < .00001, "Byte bars must use one common scale per column.");
        result = ActivityOverview.Build([entry], []);
        Check(result.Apps[0].Value == "1 conn." && result.Download == 0, "Missing measurements must be labelled as connection counts, not measured zero bytes.");
        result = ActivityOverview.Build([], []);
        Check(result.Apps.Count == 0 && result.Hosts.Count == 0 && result.Connections == 0, "Empty filtered activity must be safe.");
        var udp = entry with { Protocol = "UDPv4", RemotePort = null, RemoteAddress = "", DomainHint = "" };
        result = ActivityOverview.Build([udp], []);
        Check(result.Hosts.Count == 0 && result.Types[0].Title.Contains("unavailable"), "UDP bindings must not become fictitious remote hosts.");
        var service = entry with { ProcessId = 20, ProcessName = "esrv_svc", ProcessPath = @"C:\Program Files\Intel\esrv_svc.exe" };
        var hidden = service with { ProcessId = 21, ProcessPath = "", LocalPort = 45010 };
        result = ActivityOverview.Build([service, hidden, hidden with { ProcessId = 22, LocalPort = 45011 }], []);
        Check(result.Apps.Count == 1 && result.Apps[0].Value == "3 conn." && result.Apps[0].ProcessPath == service.ProcessPath, "Path-less processes must merge into the same-named app and keep its icon.");
        result = ActivityOverview.Build([hidden, hidden with { ProcessId = 22, LocalPort = 45011 }], []);
        Check(result.Apps.Count == 1, "Path-less processes with one name must share one row.");
        Check(result.Apps[0].DownloadShare == 1 && result.Apps[0].RemainingShare == 0, "Unmeasured apps must bar by connection count.");
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var host = entry with { ProcessName = "svchost", ProcessPath = Path.Combine(windows, "System32", "svchost.exe") };
        Check(host.Application == "Windows Service Host" && host.ProcessLabel.Contains("svchost") && host.Matches("svchost"), "Known Windows processes must get readable names and stay findable by file name.");
        Check((host with { ProcessPath = "" }).Application == "Windows Service Host", "Protected Windows processes without a path must still get readable names.");
        Check((host with { ProcessPath = @"C:\Users\Someone\Downloads\svchost.exe" }).Application == "svchost", "Look-alike executables outside Microsoft folders must keep their raw name.");
        Check((host with { ProcessName = "WINWORD", ProcessPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft Office\root\Office16\WINWORD.EXE") }).Application == "Word", "Office executables must get product names.");
        Check(entry.Application == "Browser", "Unknown apps must keep their process name.");
        var window = new ActivityUsageWindow();
        var session = Guid.NewGuid();
        UsageSnapshot Sample(DateTimeOffset time, ulong down, ulong up, Guid? id = null) => new(id ?? session, time, [new(entry.ProcessPath, "", down, up)], 0, 0, 0, 0, 1024);
        window.Observe(Sample(now, 1000, 500));
        Check(window.Samples.Count == 0, "Initial cumulative bytes must establish a baseline, not be assigned to a short window.");
        window.Observe(Sample(now.AddSeconds(5), 1600, 800));
        window.Observe(Sample(now.AddSeconds(5), 1600, 800));
        Check(window.Samples.Count == 1 && window.Samples.Single().Download == 600 && window.Samples.Single().Upload == 300, "Repeated samples must not double count and deltas must preserve direction.");
        window.Observe(Sample(now.AddSeconds(10), 10, 5, Guid.NewGuid()));
        Check(window.Samples.Count == 1, "Restarted collector bytes must establish a fresh baseline.");
        window.Observe(Sample(now.AddHours(2), 20, 10));
        Check(window.Samples.All(x => x.Time >= now.AddHours(1)), "Short-window measurements must expire to bound memory.");
        Console.WriteLine($"PASS: {checks} activity overview aggregation and range accounting checks.");
    }
}
