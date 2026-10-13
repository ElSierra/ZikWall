using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI;

public sealed partial class MonitoringView
{
    internal async Task ExerciseUsageViewAsync(bool benchmark)
    {
        await PauseForDiagnosticAsync();
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        var today = DateOnly.FromDateTime(DateTime.Now);
        current = Enumerable.Range(0, 1000).Select(i => new ConnectionEntry("TCPv4", "192.0.2.2", 50000 + i,
            "192.0.2.1", 443, i % 100, "Established", $"App {i % 100:D3}", $@"C:\Fixture\app{i % 100}.exe",
            "Outgoing", "US", "United States", "Internet")).ToList();
        usage.Load(current.GroupBy(x => x.AppKey).Select((x, i) => new DailyUsage(today, x.Key, x.First().ProcessPath, (ulong)(i + 1) * 1024 * 1024, (ulong)i * 1024)), today);
        usageEnabled = false;
        Show(false);
        Check(UsageSortButton.Visibility == Visibility.Collapsed && UsageHeaderColumn.Width.Value == 0, "Usage column must be hidden while collection is off.");
        usageEnabled = true;
        UpdateLists();
        Check(UsageSortButton.Visibility == Visibility.Visible && activityTree.Rows.All(x => x.UsageVisible), "Enabling usage must reveal the column in every app row.");
        var selected = activityTree.Rows[50];
        ConnectionList.SelectedItem = selected;
        var peer = new ButtonAutomationPeer(UsageSortButton);
        var invoke = (IInvokeProvider)peer.GetPattern(PatternInterface.Invoke);
        invoke.Invoke(); await Task.Delay(100);
        Check(usageSort == UsageSort.Descending && activityTree.Rows[0].Title == "App 099", "First heading click must sort highest usage first.");
        Check((ConnectionList.SelectedItem as ActivityRow)?.Key == selected.Key, "Sorting must preserve selection.");
        invoke.Invoke(); await Task.Delay(100);
        Check(usageSort == UsageSort.Ascending && activityTree.Rows[0].Title == "App 000", "Second heading click must reverse usage order.");
        activityTree.Toggle(activityTree.Rows[0]); UpdateLists();
        Check(activityTree.Rows[1].Indent == 20 && activityTree.Rows[1].Total == "", "Expanding an app must not duplicate its total on processes.");
        var retained = activityTree.Rows[0];
        usage.Load(usage.Days.Select(x => x.AppKey == retained.Key ? x with { Received = x.Received + 512 * 1024 } : x), today);
        RefreshUsageRows(true); await Task.Delay(150);
        Check(ReferenceEquals(retained, activityTree.Rows[0]) && retained.Total == "1.5 MB", "Usage refresh must keep the row object while changing its number.");
        static IEnumerable<TextBlock> Labels(DependencyObject root)
        {
            if (root is TextBlock text) yield return text;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var label in Labels(VisualTreeHelper.GetChild(root, i))) yield return label;
        }
        ConnectionList.ScrollIntoView(retained); await Task.Delay(150);
        Check(Labels((DependencyObject)ConnectionList.ContainerFromItem(retained)).Any(x => x.Text == "1.5 MB"), "Native one-way binding must update the visible usage cell.");
        usageEnabled = false; RefreshUsageRows(false);
        Check(UsageHeaderColumn.Width.Value == 0 && activityTree.Rows.All(x => !x.UsageVisible), "Stopping usage must collapse the column.");
        if (benchmark)
        {
            usageEnabled = true; usageSort = UsageSort.Descending; UpdateLists();
            var apps = current.GroupBy(x => x.AppKey).Select(x => new AppTraffic(x.First().ProcessPath, "", 0, 0)).ToArray();
            var session = Guid.NewGuid(); ulong tick = 0;
            void Sample()
            {
                tick++;
                usage.Observe(new(session, DateTimeOffset.Now, apps.Select((x, i) => x with { Received = tick * (ulong)(i + 1) * 1024, Sent = tick * (ulong)i * 512 }).ToList(), tick, 0, 0, 0, 1024));
                RefreshUsageRows(true);
            }
            Sample(); await Task.Delay(5000); Sample(); await Task.Delay(5000);
            using var process = Process.GetCurrentProcess();
            double cpuStart = process.TotalProcessorTime.TotalMilliseconds;
            var watch = Stopwatch.StartNew();
            var samples = new List<object>();
            long peakPrivate = 0, peakWorking = 0;
            for (int second = 1; second <= 60; second++)
            {
                await Task.Delay(1000);
                if (second % 5 == 0) Sample();
                process.Refresh();
                peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
                peakWorking = Math.Max(peakWorking, process.WorkingSet64);
                samples.Add(new { Seconds = watch.Elapsed.TotalSeconds, PrivateMiB = process.PrivateMemorySize64 / 1048576d });
            }
            double cpu = 100 * (process.TotalProcessorTime.TotalMilliseconds - cpuStart) / watch.Elapsed.TotalMilliseconds;
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "usage-view-benchmark.json"), JsonSerializer.Serialize(new
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Scenario = "Native usage column sorted descending; 1,000 fixture connections, 100 apps, one expanded app; synthetic usage every 5 seconds. ETW collector and live capture off.",
                MeasuredSeconds = watch.Elapsed.TotalSeconds, CpuOneCorePercent = cpu,
                PeakPrivateMiB = peakPrivate / 1048576d, PeakWorkingMiB = peakWorking / 1048576d,
                Samples = samples
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "usage-view-selftest.txt"), "PASS: native usage column enable/disable, two heading clicks, numeric order, selection preservation and expanded rows. Fixture only; no collector or saved usage changes.");
    }
}
