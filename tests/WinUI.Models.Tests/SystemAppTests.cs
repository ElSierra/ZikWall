using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

static class SystemAppTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool value, string why) { if (!value) throw new Exception(why); checks++; }
        var classifier = new WindowsSystemApps(@"D:\OS", @"D:\Programs", @"D:\Data");
        var row = new ConnectionEntry("TCPv4", "192.0.2.2", 50000, "192.0.2.1", 443, 123, "Established", "fixture", NetworkScope: "Internet");
        foreach (string path in new[] { @"D:\OS\System32\svchost.exe", @"d:\os\syswow64\service.exe", @"D:\OS\SystemApps\Shell\host.exe", @"D:\OS\explorer.exe", @"D:\Programs\Windows Defender\MsMpEng.exe", @"D:\Data\Microsoft\Windows Defender\Platform\1.0\MsMpEng.exe", @"\\?\D:\OS\System32\service.exe" })
            Check(classifier.IsSystem(row with { ProcessPath = path }), "System component must match its Windows location: " + path);
        foreach (string path in new[] { @"D:\OS-old\System32\svchost.exe", @"D:\OS\System32-other\host.exe", @"D:\OS\System32\..\..\Apps\game.exe", @"D:\OS\Temp\download.exe", @"D:\Programs\Microsoft\Edge\msedge.exe", @"D:\Programs\Microsoft Office\WINWORD.EXE", "" })
            Check(!classifier.IsSystem(row with { ProcessPath = path, ProcessName = "svchost" }), "Ordinary/unknown identities must remain visible: " + path);
        foreach (string family in new[] { "Microsoft.Windows.ShellExperienceHost_cw5n1h2txyewy", "MicrosoftWindows.Client.CBS_cw5n1h2txyewy", "Microsoft.WindowsCalculator_8wekyb3d8bbwe" })
            Check(classifier.IsSystem(row with { PackageFamily = family }), "Known Windows package must match.");
        foreach (string family in new[] { "Microsoft.Windows.ShellExperienceHost_otherpublisher", "Microsoft.Office.Desktop_8wekyb3d8bbwe", "Microsoft.MicrosoftEdge_8wekyb3d8bbwe", "Microsoft.RandomApp_8wekyb3d8bbwe" })
            Check(!classifier.IsSystem(row with { PackageFamily = family }), "Microsoft publisher alone must not hide an app.");
        Check(classifier.IsSystem(row with { ProcessId = 4 }) && !classifier.IsSystem(row with { ProcessId = 0 }), "System PID is known; unowned PID zero is ambiguous.");

        var system = row with { ProcessPath = @"D:\OS\System32\svchost.exe" };
        var ordinary = row with { ProcessPath = @"D:\Apps\browser.exe", ProcessId = 456 };
        bool hide = true;
        bool Include(ConnectionEntry item) => !hide || !classifier.IsSystem(item);
        var alerts = new RecentConnections();
        var now = DateTimeOffset.UtcNow;
        ConnectionEvent Seen(ConnectionEntry item) => new(now, "First seen", item);
        alerts.Observe([], now, true, Include);
        Check(alerts.Observe([Seen(system)], now.AddSeconds(1), true, Include) is null, "Hidden system connections must never enqueue a banner/toast.");
        var mixed = alerts.Observe([Seen(system), Seen(ordinary)], now.AddSeconds(2), true, Include);
        Check(mixed is { Count: 1 } && mixed.Connections.Single() == ordinary, "Mixed batches must count and name only visible apps.");
        Check(alerts.Observe([], now.AddSeconds(40), true, Include) is null, "A hidden system event must not leak after notification cooldown.");
        hide = false; alerts.ClearPending();
        Check(alerts.Observe([Seen(system)], now.AddSeconds(42), true, Include)?.Connections.Single() == system, "Turning hiding off must restore future system notifications.");
        alerts.Observe([Seen(system with { RemotePort = 80 })], now.AddSeconds(43), true, Include);
        hide = true; alerts.ClearPending();
        Check(alerts.Observe([], now.AddSeconds(90), true, Include) is null, "Toggling must clear already queued system alerts.");

        string directory = Path.Combine(Path.GetTempPath(), "ZikWall-monitor-preferences-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MonitorPreferencesStore(directory);
            Check(!store.Load().HideWindowsSystemApps, "The new toggle defaults off.");
            store.Save(new(true)); Check(store.Load().HideWindowsSystemApps, "Hide preference must survive reopening.");
            store.Save(new(false)); Check(!store.Load().HideWindowsSystemApps, "Turning hiding off must persist too.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        Console.WriteLine($"PASS: {checks} system-app visibility, alert suppression and preference checks.");
    }
}
