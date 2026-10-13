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
        current = [enrichment.WithDomainHint(entry), entry with { RemoteAddress = "192.0.2.3", LocalPort = 50001 }];
        Show(false);
        if (!activityTree.Rows.Single().Details.Contains("website.example"))
            throw new InvalidOperationException("Collapsed app did not show its website name.");
        await Task.Delay(200);
        static IEnumerable<DependencyObject> HostVisuals(DependencyObject root)
        {
            yield return root;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var child in HostVisuals(VisualTreeHelper.GetChild(root, i))) yield return child;
        }
        var container = ConnectionList.ContainerFromIndex(0) as DependencyObject
            ?? throw new InvalidOperationException("Host summary row was not realized.");
        var hostButton = HostVisuals(container).OfType<Button>().Single(x => x.Tag is HostSummary);
        if (hostsFlyout is not null || hostButton.Flyout is not null)
            throw new InvalidOperationException("Unopened host rows eagerly created their details popup.");
        HostsClicked(hostButton, new RoutedEventArgs());
        var hostFlyout = hostsFlyout!;
        hostFlyout.AreOpenCloseAnimationsEnabled = false;
        await Task.Delay(200);
        var hosts = HostVisuals((DependencyObject)hostFlyout.Content).OfType<ListView>().Single();
        if (hosts.Items.Count != 2 || activityTree.Rows[0].Hosts.More != "+1 more"
            || hosts.Items.Cast<HostEntry>().All(x => x.Label != "website.example"))
            throw new InvalidOperationException("Hosts flyout did not bind the name, fallback IP and additional-host count.");
        hostFlyout.Hide();
        for (int attempt = 0; attempt < 40 && hosts.ItemsSource is not null; attempt++) await Task.Delay(50);
        if (hosts.ItemsSource is not null) throw new InvalidOperationException("Closed popup retained its previous host snapshot.");
        current = [entry with { RemoteAddress = "192.0.2.99", DomainHint = "updated.example", CountryCode = "GB" }];
        UpdateLists();
        await Task.Delay(100);
        if (!ReferenceEquals(container, ConnectionList.ContainerFromIndex(0)) || hostButton.Tag is not HostSummary { Primary: "updated.example" }
            || !HostVisuals(hostButton).OfType<TextBlock>().Any(x => x.Text == "updated.example"))
            throw new InvalidOperationException("A live host change replaced its container or failed to refresh the native bindings.");
        var hostImage = HostVisuals(hostButton).OfType<Image>().Single();
        if (hostImage.Source is not Microsoft.UI.Xaml.Media.Imaging.SvgImageSource { RasterizePixelWidth: 64, RasterizePixelHeight: 48 } flag
            || !flag.UriSource.AbsoluteUri.EndsWith("/gb.svg", StringComparison.Ordinal))
            throw new InvalidOperationException("Host flag did not update at the bounded raster size.");
        var flagSource = hostImage.Source;
        current.Add(current[0] with { LocalPort = 51000 });
        UpdateLists();
        await Task.Delay(100);
        if (!ReferenceEquals(flagSource, hostImage.Source))
            throw new InvalidOperationException("A socket-count update recreated an unchanged flag image.");
        HostsClicked(hostButton, new RoutedEventArgs());
        await Task.Delay(100);
        if (!ReferenceEquals(hostFlyout, hostsFlyout) || hosts.Items.Count != 1 || ((HostEntry)hosts.Items[0]).Address != "192.0.2.99")
            throw new InvalidOperationException("Reused host popup showed the previous row's details.");
        hostFlyout.Hide();
        await Task.Delay(100);
        var scroll = (ScrollViewer)MonitorOptions.Content;
        var originalStyle = MonitorOptions.FlyoutPresenterStyle;
        bool originalAnimations = MonitorOptions.AreOpenCloseAnimationsEnabled;
        MonitorOptions.AreOpenCloseAnimationsEnabled = false;
        var style = new Style(typeof(FlyoutPresenter)) { BasedOn = originalStyle };
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 600d));
        style.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 480d));
        MonitorOptions.FlyoutPresenterStyle = style;
        MonitorOptions.ShowAt(PauseButton);
        DependencyObject? parent = null;
        for (int attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(100);
            parent = VisualTreeHelper.GetParent(scroll);
            while (parent is not null && parent is not FlyoutPresenter) parent = VisualTreeHelper.GetParent(parent);
            if (parent is FlyoutPresenter { ActualWidth: > 0 }) break;
        }
        var presenter = parent as FlyoutPresenter ?? throw new InvalidOperationException("Options flyout did not open for layout verification.");
        // Resize the live presenter instead of destroying/recreating it between widths.
        // Replacing the style during popup teardown can dismiss the next popup.
        foreach (double width in new[] { 600d, 440d, 360d })
        {
            presenter.MaxWidth = width;
            presenter.UpdateLayout();
            await Task.Delay(200);
            var origin = scroll.TransformToVisual(presenter).TransformPoint(new Point());
            if (origin.X + scroll.ActualWidth > presenter.ActualWidth + 1 || origin.Y + scroll.ActualHeight > presenter.ActualHeight + 1 || scroll.ScrollableWidth > 1)
                throw new InvalidOperationException($"Options overflow: content {scroll.ActualWidth}, presenter {presenter.ActualWidth}, horizontal scroll {scroll.ScrollableWidth}.");
            foreach (var grid in new[] { ViewOptionsGrid, AdapterOptionsGrid, HistoryOptionsGrid, NotificationOptionsGrid, UsageOptionsGrid })
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
        }
        MonitorOptions.Hide();
        MonitorOptions.FlyoutPresenterStyle = originalStyle;
        MonitorOptions.AreOpenCloseAnimationsEnabled = originalAnimations;
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "monitor-options-selftest.txt"), "PASS: native options fit 600/440/360 DIP presenters, controls fit, no horizontal overflow, bottom reachable. Async website hints display and search in history and collapsed apps without entering saved activity. Host flyout binds two hosts, DNS name, fallback IP and +1 more. Popup is lazy, reused, cleared on close and reflects live host changes without replacing the row container.");
    }
}
