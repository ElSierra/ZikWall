using System.Xml.Linq;
using ZikWall.WinUI.Models;

static class NotificationTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        var row = new ConnectionEntry("TCPv4", "192.168.1.2", 50000, "8.8.8.8", 443, 1,
            "Established", "Chrome", Direction: "Outgoing", CountryCode: "US", CountryName: "United States", NetworkScope: "Internet");
        ConnectionNotification Format(ConnectionEntry entry, int count = 1) => ConnectionNotification.From(new([entry], count));
        var outgoing = Format(row);
        Check(outgoing.Title == "Network activity" && outgoing.Body == "Chrome\n8.8.8.8", "Notification must use a short title, app line and address line.");
        Check(Format(row with { Direction = "Incoming" }).Body == outgoing.Body, "Incoming activity keeps the same compact layout.");
        Check(Format(row with { State = "SynSent" }).Title == "Network activity" && Format(row with { State = "SynReceived" }).Title == "Network activity", "An attempted connection must not claim success.");
        Check(Format(row with { State = "TimeWait" }).Title == "Network activity", "First observed teardown must not claim a newly successful connection.");
        Check(ConnectionNotification.From(new([row], 1, ConnectionNotificationType.NewApps)).Title == "First network activity", "Only first-app notifications use the first-activity title.");
        var hinted = Format(row with { DomainHint = "dns.google", RemoteAddress = "2001:db8::1" }, 11);
        Check(hinted.Body == "Chrome\n2001:db8::1 · +10 more", "Keep the address and batch count on one destination line without extra detail.");
        Check(Format(row, 2).Body.EndsWith(" · +1 more"), "Additional connections must stay compact.");
        Check(Format(row with { CountryCode = "", CountryName = "" }).Country == "Country unknown", "Unknown countries must retain their accessible description.");
        string assets = Path.GetFullPath("ui/ZikWall.WinUI/Assets");
        var xml = XDocument.Parse(outgoing.ToToastXml(assets));
        Check(xml.Descendants("text").First().Value == outgoing.Title && !xml.ToString().Contains("Recent connections"), "Toast headline must contain the event, without repeated branding.");
        var icon = xml.Descendants("image").Single();
        Check(icon.Attribute("placement")?.Value == "appLogoOverride" && new Uri(icon.Attribute("src")!.Value).LocalPath == Path.Combine(assets, "ZikWall.png"), "Toast must fall back to the app logo, never a country flag.");
        var withPath = Format(row with { ProcessPath = @"C:\Apps\browser.exe" });
        Check(withPath.ProcessPath == @"C:\Apps\browser.exe", "Notification must retain the first app's executable for icon extraction.");
        string customIcon = Path.Combine(Path.GetTempPath(), "zikwall-notification-" + Guid.NewGuid() + " & icon.png");
        try
        {
            File.Copy(Path.Combine(assets, "ZikWall.png"), customIcon);
            var custom = XDocument.Parse(Format(row with { ProcessName = "A&B <Browser>" }).ToToastXml(assets, customIcon)).Descendants("image").Single();
            Check(new Uri(custom.Attribute("src")!.Value).LocalPath == customIcon && custom.Attribute("alt")?.Value == "A&B <Browser>", "Extracted app icon and accessible label must survive XML escaping.");
            Check(custom.Attribute("hint-crop") is null, "Square app icons must not be cropped into a circle.");
        }
        finally { File.Delete(customIcon); }
        foreach (string unavailable in new[] { Path.Combine(assets, "missing-icon.png"), "relative.png", @"\\server\share\icon.png" })
            Check(new Uri(XDocument.Parse(outgoing.ToToastXml(assets, unavailable)).Descendants("image").Single().Attribute("src")!.Value).LocalPath == Path.Combine(assets, "ZikWall.png"), "Unavailable or nonlocal icons must use the bundled app logo.");
        Check(xml.Descendants("audio").Single().Attribute("silent")?.Value == "true", "Alerts must remain silent.");
        foreach (string country in new[] { "", "../us", "ZZ", "<x", "12" })
        {
            var unknown = XDocument.Parse(Format(row with { CountryCode = country }).ToToastXml(assets));
            Check(new Uri(unknown.Descendants("image").Single().Attribute("src")!.Value).LocalPath.EndsWith("ZikWall.png"), "Country metadata must not select a notification icon.");
        }
        var escaped = Format(row with { ProcessName = "A&B <Browser>", DomainHint = "a&b.example" });
        var escapedXml = XDocument.Parse(escaped.ToToastXml(assets));
        Check(escapedXml.Descendants("text").ElementAt(1).Value == "A&B <Browser>" && escapedXml.Descendants("text").Last().Value == escaped.Destination, "App and address text must survive XML escaping without injection.");
        var longText = Format(row with { ProcessName = new string('A', 500), DomainHint = new string('b', 1000) });
        Check(longText.Title.Length < 110 && longText.Body.Length < 250, "Long app/domain labels must not overwhelm the notification.");
        Check(!XDocument.Parse(outgoing.ToToastXml(Path.Combine(assets, "missing"))).Descendants("image").Any(), "Missing assets must still produce a usable text notification.");
        var now = DateTimeOffset.UtcNow;
        ConnectionEvent Seen(ConnectionEntry entry) => new(now, "First seen", entry);
        var app = row with { ProcessPath = @"C:\Apps\browser.exe" };
        var fresh = app with { ProcessPath = @"C:\Apps\new.exe" };
        var alerts = new RecentConnections();
        Check(alerts.Observe([Seen(app)], now, true, type: ConnectionNotificationType.NewApps) is null, "Startup apps must seed the new-app baseline silently.");
        Check(alerts.Observe([Seen(app with { RemotePort = 80, ProcessId = 2 })], now.AddSeconds(1), true, type: ConnectionNotificationType.NewApps) is null, "Known app with a new PID or destination is not a new app.");
        Check(alerts.Observe([Seen(fresh), Seen(fresh with { RemotePort = 80 })], now.AddSeconds(2), true, type: ConnectionNotificationType.NewApps)?.Count == 1, "Only the first connection per new app must alert.");
        Check(alerts.Observe([Seen(fresh with { ProcessPath = @"c:\apps\NEW.exe", RemotePort = 8080 })], now.AddMinutes(6), true, type: ConnectionNotificationType.NewApps) is null, "Windows app identity must be case-insensitive and outlive the endpoint cooldown.");
        alerts.Observe([Seen(app with { ProcessPath = @"C:\Apps\muted.exe" })], now.AddMinutes(7), false, type: ConnectionNotificationType.NewApps);
        Check(alerts.Observe([Seen(app with { ProcessPath = @"C:\Apps\muted.exe", RemotePort = 80 })], now.AddMinutes(8), true, type: ConnectionNotificationType.NewApps) is null, "Apps observed while muted must not be announced as new later.");
        alerts.ResetBaseline();
        Check(alerts.Observe([Seen(app)], now.AddMinutes(9), true, type: ConnectionNotificationType.NewApps) is null, "Resume must stay quiet.");
        foreach (var type in new[] { ConnectionNotificationType.Outgoing, ConnectionNotificationType.Incoming })
        {
            var filter = new RecentConnections();
            filter.Observe([], now, true, type: type);
            var incomingRow = row with { Direction = "Incoming" };
            var batch = filter.Observe([Seen(row), Seen(incomingRow), Seen(row with { Direction = "Unknown" })], now.AddSeconds(1), true, type: type);
            Check(batch?.Count == 1 && batch.Connections[0].Direction == (type == ConnectionNotificationType.Outgoing ? "Outgoing" : "Incoming"), "Direction choice must apply before batching, excluding unknown direction.");
            filter.Observe([Seen(row with { RemotePort = 80 }), Seen(incomingRow with { RemotePort = 80 })], now.AddSeconds(2), true, type: type);
            Check(filter.Observe([], now.AddSeconds(32), true, type: ConnectionNotificationType.NewApps) is null, "Changing notification type must discard the old queued batch.");
        }
        var hidden = new RecentConnections();
        hidden.Observe([], now, true);
        Check(hidden.Observe([Seen(fresh)], now.AddSeconds(1), true, _ => false, ConnectionNotificationType.NewApps) is null &&
            hidden.Observe([Seen(fresh with { RemotePort = 80 })], now.AddSeconds(32), true, type: ConnectionNotificationType.NewApps) is null, "Hidden apps stay silent and are still remembered.");
        var packaged = new RecentConnections(); packaged.Observe([], now, true);
        Check(packaged.Observe([Seen(fresh with { PackageFamily = "fixture.package" }), Seen(app with { PackageFamily = "fixture.package" })], now.AddSeconds(1), true, type: ConnectionNotificationType.NewApps)?.Count == 1, "Package identity must group executables from the same app.");
        Console.WriteLine($"PASS: {checks} event notification checks.");
    }
}
