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
        var retainedHistory = HistoryList.ItemsSource;
        UpdateLists();
        if (!ReferenceEquals(retainedHistory, HistoryList.ItemsSource))
            throw new InvalidOperationException("An unchanged history refresh rebuilt the native list.");
        archive.Events.Add(new(DateTimeOffset.UtcNow, "Closed", entry));
        UpdateLists();
        if (HistoryList.Items.Count != 2 || ReferenceEquals(retainedHistory, HistoryList.ItemsSource))
            throw new InvalidOperationException("Changed history did not refresh.");
        archive.Events.RemoveAt(1);
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
        if (hostsWindow is not null || hostButton.Flyout is not null)
            throw new InvalidOperationException("Unopened host rows eagerly created their details popup.");
        HostsClicked(hostButton, new RoutedEventArgs());
        var hostWindow = hostsWindow!;
        await Task.Delay(200);
        var hosts = HostVisuals((DependencyObject)hostWindow.Content).OfType<ListView>().Single();
        if (hosts.Items.Count != 2 || activityTree.Rows[0].Hosts.More != "+1 more"
            || hosts.Items.Cast<HostEntry>().All(x => x.Label != "website.example"))
            throw new InvalidOperationException("Hosts window did not bind the name, fallback IP and additional-host count.");
        HostsClicked(hostButton, new RoutedEventArgs());
        if (!ReferenceEquals(hostWindow, hostsWindow)) throw new InvalidOperationException("Repeated host clicks duplicated the window.");
        hostWindow.Close();
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
        hosts = hostsList!;
        if (ReferenceEquals(hostWindow, hostsWindow) || hosts.Items.Count != 1 || ((HostEntry)hosts.Items[0]).Address != "192.0.2.99")
            throw new InvalidOperationException("Reopened host window showed the previous row's details.");
        hostsWindow!.Close();
        await Task.Delay(100);
        var scroll = OptionsContent;
        OptionsClicked(OptionsButton, new RoutedEventArgs());
        var window = optionsWindow ?? throw new InvalidOperationException("Options window did not open.");
        await Task.Delay(200);
        OptionsClicked(OptionsButton, new RoutedEventArgs());
        if (!ReferenceEquals(window, optionsWindow) || window.Content.XamlRoot == XamlRoot)
            throw new InvalidOperationException("Options must reuse a separate native window.");
        var windowPresenter = (Microsoft.UI.Windowing.OverlappedPresenter)window.AppWindow.Presenter;
        windowPresenter.Maximize();
        await Task.Delay(100);
        OptionsClicked(OptionsButton, new RoutedEventArgs());
        if (windowPresenter.State != Microsoft.UI.Windowing.OverlappedPresenterState.Maximized)
            throw new InvalidOperationException("Raising options changed its maximized state.");
        windowPresenter.Minimize();
        await Task.Delay(100);
        OptionsClicked(OptionsButton, new RoutedEventArgs());
        await Task.Delay(100);
        if (windowPresenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
            throw new InvalidOperationException("Raising options did not restore its minimized window.");
        windowPresenter.Restore();
        var ownerRoot = (FrameworkElement)Owner!.Content;
        var originalTheme = ownerRoot.RequestedTheme;
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            ownerRoot.RequestedTheme = theme;
            await Task.Delay(150);
            if (((FrameworkElement)window.Content).ActualTheme != theme)
                throw new InvalidOperationException("Tool window did not follow the main theme.");
        }
        ownerRoot.RequestedTheme = originalTheme;
        await Task.Delay(150);
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await bitmap.RenderAsync(window.Content);
        var pixels = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(await bitmap.GetPixelsAsync());
        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(AppContext.BaseDirectory);
        var file = await folder.CreateFileAsync("options-window-preview.png", Windows.Storage.CreationCollisionOption.ReplaceExisting);
        using (var output = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite))
        {
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync();
        }
        foreach (double width in new[] { 600d, 440d, 360d })
        {
            double scale = window.Content.XamlRoot.RasterizationScale;
            window.AppWindow.Resize(new((int)(width * scale), (int)(480 * scale)));
            await Task.Delay(200);
            if (scroll.ScrollableWidth > 1 || scroll.ActualHeight > 480)
                throw new InvalidOperationException($"Options overflow at {width} DIP.");
            foreach (var grid in new[] { ViewOptionsGrid, AdapterOptionsGrid, HistoryOptionsGrid, NotificationOptionsGrid, UsageOptionsGrid })
                foreach (FrameworkElement child in grid.Children)
                {
                    var position = child.TransformToVisual(grid).TransformPoint(new Point());
                    if (position.X < -1 || position.X + child.ActualWidth > grid.ActualWidth + 1)
                        throw new InvalidOperationException($"Options control {child.Name} is clipped at width {width}.");
                }
            scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
            await Task.Delay(100);
            if (Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) > 1)
                throw new InvalidOperationException("Options bottom is unreachable.");
        }
        bool setting = HideSystemApps.IsOn;
        HideSystemApps.IsOn = !setting;
        window.Close();
        await Task.Delay(100);
        if (optionsWindow is not null || OptionsParking.Children.Count != 1)
            throw new InvalidOperationException("Closed options did not release its window and return its controls.");
        OptionsClicked(OptionsButton, new RoutedEventArgs());
        await Task.Delay(100);
        if (ReferenceEquals(window, optionsWindow) || HideSystemApps.IsOn == setting)
            throw new InvalidOperationException("Reopening options lost its setting or reused a closed window.");
        HideSystemApps.IsOn = setting;
        optionsWindow!.Close();
        if (Presentation.ToolWindows.For(Owner!).Count != 0)
            throw new InvalidOperationException("Closed tools retained windows.");
        var testOwner = new Window { Content = new Grid() };
        testOwner.Activate();
        var tools = Presentation.ToolWindows.For(testOwner);
        var childWindow = tools.Text("lifetime", "Window lifecycle fixture", "Fixture");
        bool childClosed = false;
        childWindow.Closed += (_, _) => childClosed = true;
        testOwner.Close();
        await Task.Delay(100);
        if (tools.Count != 0 || !childClosed)
            throw new InvalidOperationException("Closing the owner retained a child window.");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "monitor-options-selftest.txt"), "PASS: separate native options window fits 600/440/360 DIP, controls fit, bottom reachable, repeat clicks reuse the window, close releases it, reopen preserves settings, themes synchronize, owner exit closes children. Unchanged history retains its native list; changed history refreshes. Async website hints display and search without entering saved activity. Host windows bind current snapshots, are lazy and release their lists on close; live row bindings preserve containers and flag images.");
    }
}
