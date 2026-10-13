using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

namespace ZikWall.WinUI;

public sealed partial class MonitoringView
{
    internal async Task ExerciseOptionsAsync()
    {
        await PauseForDiagnosticAsync();
        var entry = new ConnectionEntry("TCPv4", "192.0.2.2", 50000, "192.0.2.1", 443, Environment.ProcessId, "Established", "Website fixture", NetworkScope: "Internet");
        enrichment?.Dispose();
        enrichment = new(Path.Combine(AppContext.BaseDirectory, "Assets", "Countries"));
        enrichment.SetDomainReader(_ => [new(entry.RemoteAddress, "website.example", 120)]);
        archive.Events.Clear();
        archive.Events.Add(new(DateTimeOffset.UtcNow, "First seen", entry));
        enrichment.Enrich([entry]);
        for (int i = 0; i < 100 && enrichment.DomainState == DomainCacheState.Reading; i++) await Task.Delay(20);
        Show(true);
        MonitorSearch.Text = "website.example";
        UpdateLists();
        if (HistoryList.Items.Count != 1 || !((ConnectionEvent)HistoryList.Items[0]).Endpoints.Contains("website.example"))
            throw new InvalidOperationException("History did not display or search the asynchronously resolved website name.");
        if (archive.Events[0].Connection.DomainHint.Length != 0)
            throw new InvalidOperationException("Website names leaked into saved activity.");
        MonitorSearch.Text = "";
        current = [enrichment.WithDomainHint(entry)];
        Show(false);
        if (!activityTree.Rows.Single().Details.Contains("website.example"))
            throw new InvalidOperationException("Collapsed app did not show its website name.");
        var scroll = (ScrollViewer)MonitorOptions.Content;
        var originalStyle = MonitorOptions.FlyoutPresenterStyle;
        foreach (double width in new[] { 600d, 440d, 360d })
        {
            var style = new Style(typeof(FlyoutPresenter)) { BasedOn = originalStyle };
            style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, width));
            style.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 480d));
            MonitorOptions.FlyoutPresenterStyle = style;
            MonitorOptions.ShowAt(PauseButton);
            await Task.Delay(300);
            var parent = VisualTreeHelper.GetParent(scroll);
            while (parent is not null && parent is not FlyoutPresenter) parent = VisualTreeHelper.GetParent(parent);
            var presenter = (FlyoutPresenter)parent!;
            var origin = scroll.TransformToVisual(presenter).TransformPoint(new Point());
            if (origin.X + scroll.ActualWidth > presenter.ActualWidth + 1 || origin.Y + scroll.ActualHeight > presenter.ActualHeight + 1 || scroll.ScrollableWidth > 1)
                throw new InvalidOperationException($"Options overflow: content {scroll.ActualWidth}, presenter {presenter.ActualWidth}, horizontal scroll {scroll.ScrollableWidth}.");
            foreach (var grid in new[] { ViewOptionsGrid, AdapterOptionsGrid, HistoryOptionsGrid, UsageOptionsGrid })
                foreach (FrameworkElement child in grid.Children)
                {
                    var position = child.TransformToVisual(grid).TransformPoint(new Point());
                    if (position.X < -1 || position.X + child.ActualWidth > grid.ActualWidth + 1)
                        throw new InvalidOperationException($"Options control {child.Name} is clipped at width {width}.");
                }
            scroll.ChangeView(null, 0, null, true);
            await Task.Delay(100);
            scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
            await Task.Delay(100);
            if (Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) > 1)
                throw new InvalidOperationException("Options bottom is unreachable.");
            MonitorOptions.Hide();
            await Task.Delay(100);
        }
        MonitorOptions.FlyoutPresenterStyle = originalStyle;
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "monitor-options-selftest.txt"), "PASS: native options fit 600/440/360 DIP presenters, controls fit, no horizontal overflow, bottom reachable. Async website hints display and search in history and collapsed apps without entering saved activity.");
    }
}
