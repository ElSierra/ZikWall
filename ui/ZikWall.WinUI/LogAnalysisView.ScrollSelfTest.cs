using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI;

public sealed partial class LogAnalysisView
{
    internal async Task ExerciseScrollAsync()
    {
        static T? Find<T>(DependencyObject root) where T : DependencyObject
        {
            if (root is T match) return match;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                if (Find<T>(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
            return null;
        }
        var now = DateTimeOffset.UtcNow;
        var events = Enumerable.Range(0, 1000).Select(i => new ConnectionEvent(now.AddMinutes(-i), "First seen",
            new ConnectionEntry("TCPv4", "127.0.0.1", 10000 + i, "192.0.2.1", 443, i % 30,
                "Established", $"Browser {i % 30}", Environment.ProcessPath!, "Outgoing", "US", "United States", "Internet"))).ToArray();
        var initial = LogAnalysis.Build(events, now, DateTimeOffset.MinValue, LogFilter.All);
        Display(initial, saved: false);
        EventList.UpdateLayout();
        await Task.Delay(500);
        var scroll = Find<ScrollViewer>(EventList) ?? throw new InvalidOperationException("Timeline has no native scroll viewer.");
        var steps = new List<double>();
        var frames = new List<double>();
        long lastFrame = 0;
        void Rendered(object? sender, object args)
        {
            long current = Stopwatch.GetTimestamp();
            if (lastFrame != 0) frames.Add(Stopwatch.GetElapsedTime(lastFrame, current).TotalMilliseconds);
            lastFrame = current;
        }
        CompositionTarget.Rendering += Rendered;
        int maxRealized = 0;
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        long allocations = GC.GetTotalAllocatedBytes();
        try
        {
            for (int i = 0; i < 180; i++)
            {
                double offset = Math.Min(scroll.ScrollableHeight, (i < 90 ? i : 180 - i) * 180);
                long start = Stopwatch.GetTimestamp();
                scroll.ChangeView(null, offset, null, true);
                EventList.UpdateLayout();
                steps.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                if (EventList.ItemsPanelRoot is { } panel) maxRealized = Math.Max(maxRealized, VisualTreeHelper.GetChildrenCount(panel));
                await Task.Delay(16);
            }
        }
        finally { CompositionTarget.Rendering -= Rendered; }
        double cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        long allocated = GC.GetTotalAllocatedBytes() - allocations;
        scroll.ChangeView(null, Math.Min(8000, scroll.ScrollableHeight / 2), null, true);
        await Task.Delay(250);
        double before = scroll.VerticalOffset;
        double RowY(LogEntry row) => EventList.ContainerFromItem(row) is ListViewItem container
            ? container.TransformToVisual(EventList).TransformPoint(new(0, 0)).Y : double.NaN;
        var anchor = EventList.Items.OfType<LogEntry>().First(row => RowY(row) >= 0 && RowY(row) < EventList.ActualHeight / 2);
        double anchorBefore = RowY(anchor);
        var updated = LogAnalysis.Build(new[] { events[0] with { Time = now.AddSeconds(1) } }.Concat(events), now, DateTimeOffset.MinValue, LogFilter.All);
        Display(updated, saved: false);
        await Task.Delay(300);
        double after = scroll.VerticalOffset;
        double anchorDrift = Math.Abs(RowY(anchor) - anchorBefore);
        double P95(List<double> values) => values.Order().ElementAt((int)((values.Count - 1) * .95));
        bool passed = before > 1000 && Math.Abs(after - before) < 150 && anchorDrift < 2 && maxRealized < 150;
        var report = new { Passed = passed, ScrollSteps = steps.Count, UiStepP95Ms = P95(steps), UiStepMaxMs = steps.Max(),
            FrameP95Ms = frames.Count > 0 ? P95(frames) : 0, LongFrames = frames.Count(x => x > 50), Frames = frames.Count,
            CpuMs = cpuMs, AllocatedBytes = allocated, MaxRealizedChildren = maxRealized,
            OffsetBeforeRefresh = before, OffsetAfterRefresh = after, ScrollJumpPixels = Math.Abs(after - before), VisibleRowDriftPixels = anchorDrift };
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "log-scroll-selftest.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        if (!passed) throw new InvalidOperationException("Timeline scrolling lost its position on refresh or realized too many rows; see log-scroll-selftest.json.");
    }
}

public sealed partial class MainWindow
{
    internal async Task ExerciseLogScrollAsync()
    {
        await MonitorPanel.PauseForDiagnosticAsync();
        Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(x => x.Tag?.ToString() == "logs");
        await (LogPanel ?? throw new InvalidOperationException("Timeline not created.")).ExerciseScrollAsync();
    }
}
