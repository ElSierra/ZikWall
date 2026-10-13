using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI;

public sealed partial class MonitoringView
{
    internal async Task ExerciseSystemAppsAsync()
    {
        await PauseForDiagnosticAsync();
        void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
        var system = new ConnectionEntry("TCPv4", "192.0.2.2", 50000, "192.0.2.1", 443, 123, "Established", "Windows service",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "svchost.exe"), NetworkScope: "Internet");
        var user = system with { ProcessName = "Browser", ProcessPath = @"C:\Fixture\browser.exe", ProcessId = 456 };
        current = [system, user]; CaptureCount = 1;
        archive.Events.Clear();
        archive.Events.AddRange(current.Select(x => new ConnectionEvent(DateTimeOffset.UtcNow, "First seen", x)));
        IReadOnlyList<ConnectionEntry>? published = null;
        SnapshotChanged += rows => published = rows;
        Show(false); UpdateLists();
        Check(activityTree.Rows.Count == 2, "Default view must include both apps.");
        RecentBanner.IsOpen = true;
        var peer = new ToggleSwitchAutomationPeer(HideSystemApps);
        var toggle = (IToggleProvider)peer.GetPattern(PatternInterface.Toggle);
        toggle.Toggle(); await Task.Delay(100);
        Check(HideSystemApps.IsOn && activityTree.Rows.Count == 1 && activityTree.Rows[0].Entry == user, "Native toggle must immediately hide the Windows app.");
        Check(StatApps.Text == "1" && StatConnections.Text == "1" && !RecentBanner.IsOpen, "Counters and the current banner must follow the toggle.");
        Check(published?.Count == 1 && published[0] == user && !MatchesFilters(system, ""), "Map snapshots and export filters must exclude the system app.");
        Show(true);
        Check(HistoryList.Items.Count == 1 && archive.Events.Count == 2 && current.Count == 2, "Activity must filter display without discarding captured history or connections.");
        toggle.Toggle(); await Task.Delay(100);
        Check(HistoryList.Items.Count == 2 && published?.Count == 2, "Turning off must restore history and map immediately.");
        Show(false);
        Check(activityTree.Rows.Count == 2 && StatApps.Text == "2", "Turning off must restore both app rows and counts.");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "system-apps-selftest.txt"), "PASS: native toggle hides/restores connections, counts, history and map snapshots, clears banner and preserves captured data. Preferences untouched in diagnostic mode.");
    }
}
