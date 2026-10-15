using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI;

public sealed partial class GeoView
{
    internal void SetDiagnosticMotion(bool enabled) => MotionToggle.IsChecked = enabled;
    internal async Task ExerciseAtlasAsync()
    {
        // Exercise shipped lookup tables, not only pre-located fixture records.
        using (var enrichment = new Services.MonitorEnrichment(Path.Combine(AppContext.BaseDirectory, "Assets", "Countries")))
        {
            var actual = enrichment.Enrich([new ConnectionEntry("UDPv4", "192.168.1.2", 1234, "8.8.8.8", 443, 1, "Established")]);
            UpdateConnections(actual);
            if (!markers.ContainsKey("US")) throw new InvalidOperationException("Packaged country tables failed to locate a real Internet IP.");
            SetCountryDataStatus("Country data unavailable");
            UpdateConnections([actual[0] with { CountryCode = "", CountryName = "" }]);
            if (!GeoStatus.Text.Contains("Repair") || Destinations.Items.Count != 1 || MapHint.Text.Contains("Waiting"))
                throw new InvalidOperationException("Unavailable location data must explain repair and retain endpoints.");
            SetCountryDataStatus(enrichment.CountryDataStatus);
        }
        string[] codes = ["CA", "US", "GB", "DE", "FR", "IT", "NG", "ZA", "JP"];
        var fixture = Enumerable.Range(0, 10000).Select(i => new ConnectionEntry("TCPv4", "192.0.2.1", 1000 + i,
            $"198.51.{i / 256}.{i % 256}", 443, i % 100, "Established", $"Fixture app {i % 100}",
            $@"C:\Fixture\app{i % 100}.exe", "Outgoing", codes[i % codes.Length], countries[codes[i % codes.Length]].Label, "Internet")).ToArray();
        UpdateConnections(fixture);
        selectedApp = appRows[0].Key; lastDrawn = null; Draw();
        if (AppCount.Text != "1" || groups.Values.SelectMany(x => x).Any(x => x.AppKey != selectedApp))
            throw new InvalidOperationException("Application filter did not restrict map destinations.");
        AllAppsClicked(this, new RoutedEventArgs());
        await Task.Delay(700);
        var originalMarker = markers["US"].Button;
        var results = new List<object>();
        async Task Measure(string name, Func<int, ConnectionEntry[]> input)
        {
            var samples = new List<double>();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 60; i++)
            {
                var data = input(i);
                long start = Stopwatch.GetTimestamp();
                UpdateConnections(data); UpdateLayout();
                samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            results.Add(new { Name = name, MedianMs = samples.Order().ElementAt(30), P95Ms = samples.Order().ElementAt(56), AllocatedBytesPerRefresh = bytes / 60 });
            if (name.Contains("unchanged") && bytes / 60 > 200000) throw new InvalidOperationException("Unchanged map refresh allocated more than 200 KB (including the 80 KB input array).");
            await Task.Delay(500);
        }
        await Measure("10k unchanged endpoints / fresh snapshot", _ => fixture.ToArray());
        await Measure("10k endpoints / one state change", i => { var data = fixture.ToArray(); data[0] = data[0] with { State = i % 2 == 0 ? "SynSent" : "Established" }; return data; });
        var retainedCountry = displayedCountries.Single(x => x.Code == fixture[^1].CountryCode);
        var retainedApp = displayedApps.Single(x => x.Key == fixture[^1].AppKey);
        int oldAppIps = retainedApp.Value.Ips;
        int oldIpCount = retainedCountry.Value.Ips;
        UpdateConnections(fixture[..^1]);
        if (!ReferenceEquals(retainedApp, displayedApps.Single(x => x.Key == retainedApp.Key)) || retainedApp.Value.Ips != oldAppIps - 1)
            throw new InvalidOperationException("Application count change replaced its row or left its value stale.");
        if (!ReferenceEquals(retainedCountry, displayedCountries.Single(x => x.Code == retainedCountry.Code)) || retainedCountry.Value.Ips != oldIpCount - 1)
            throw new InvalidOperationException("Country count change replaced its row or left its value stale.");
        UpdateConnections(fixture);
        var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(markers["US"].Button);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        if (Destinations.Items.Count != 1000 || selected != "US") throw new InvalidOperationException("Country selection or display cap failed.");
        await Measure("10k endpoints / selected country unchanged", _ => fixture.ToArray());
        if (!ReferenceEquals(originalMarker, markers["US"].Button)) throw new InvalidOperationException("Refresh replaced a stable marker.");
        SetBackground(true);
        if (animations.Count != 0) throw new InvalidOperationException("Background map retained animations.");
        await Measure("10k endpoints / hidden", _ => fixture.ToArray());
        SetBackground(false);
        UpdateConnections([]);
        if (markers.Count != 0 || links.Count != 0 || LinkLayer.Children.Count != 0 || travelers?.Children.Count > 0 || selected.Length != 0) throw new InvalidOperationException("Empty snapshot retained a stale marker, link or selection.");
        var display = fixture.Take(53).ToArray();
        UpdateConnections(display); Select("NG");
        fitWorld = false; SizeMap();
        if (MapBox.Height + 1 < MapViewport.ActualHeight) throw new InvalidOperationException("Default map leaves unused vertical space.");
        if (MapViewport.ScrollableWidth > 10)
        {
            MapViewport.ChangeView(10, 0, null, true);
            for (int attempt = 0; attempt < 20 && MapViewport.HorizontalOffset < 9; attempt++) await Task.Delay(30);
            if (MapViewport.HorizontalOffset < 9) throw new InvalidOperationException("Native map pan failed.");
        }
        MotionToggle.IsChecked = false; MotionClicked(MotionToggle, new RoutedEventArgs());
        Select("US");
        if (animations.Count != 0) throw new InvalidOperationException("Motion toggle did not suppress animations.");
        MapViewport.UpdateLayout();
        MapViewport.ChangeView(0, 0, 2, true);
        for (int attempt = 0; attempt < 40 && Math.Abs(MapViewport.ZoomFactor - 2) > .01; attempt++) await Task.Delay(50);
        if (Math.Abs(MapViewport.ZoomFactor - 2) > .01) throw new InvalidOperationException($"Native map zoom failed: {MapViewport.ZoomFactor}.");
        ZoomClicked(new Button { Tag = "fit" }, new RoutedEventArgs());
        for (int attempt = 0; attempt < 40 && Math.Abs(MapViewport.ZoomFactor - 1) > .01; attempt++) await Task.Delay(50);
        if (MapViewport.ZoomFactor != 1) throw new InvalidOperationException("Fit did not reset map zoom.");
        MotionToggle.IsChecked = true;
        StopAnimations();
        var reconnect = display.ToArray(); reconnect[0] = reconnect[0] with { LocalPort = 54321 };
        UpdateConnections(reconnect);
        if (animations.Count != 0) throw new InvalidOperationException("Socket churn to the same remote IP restarted map animations.");
        reconnect[0] = reconnect[0] with { RemoteAddress = "203.0.113.99" };
        UpdateConnections(reconnect);
        if (uiSettings.AnimationsEnabled && animations.Count != 2) throw new InvalidOperationException("A changed remote IP did not animate its marker and trail.");
        var frameIntervals = new List<double>(); long lastFrame = 0;
        void Rendered(object? sender, object args)
        {
            long now = Stopwatch.GetTimestamp();
            if (lastFrame != 0) frameIntervals.Add(Stopwatch.GetElapsedTime(lastFrame, now).TotalMilliseconds);
            lastFrame = now;
        }
        CompositionTarget.Rendering += Rendered;
        using var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        try
        {
            for (int i = 0; i < 90; i++)
            {
                var changed = display.ToArray(); changed[i % changed.Length] = changed[i % changed.Length] with { LocalPort = 30000 + i };
                UpdateConnections(changed); Select(codes[i % codes.Length]);
                if (animations.Count > 28 || Markers.Children.Count != markers.Count * 4 || links.Count != markers.Count || LinkLayer.Children.Count != links.Count || travelers?.Children.Count != links.Count)
                    throw new InvalidOperationException("Animation or visual population is unbounded.");
                foreach (var (code, link) in links)
                    if (Math.Abs(link.Destination.X - markers[code].X) > .01 || Math.Abs(link.Destination.Y - markers[code].Y) > .01)
                        throw new InvalidOperationException("Country link is detached from its marker.");
                await Task.Delay(32);
            }
        }
        finally { CompositionTarget.Rendering -= Rendered; }
        results.Add(new { Name = "90 animated updates and country selections", CpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds,
            Frames = frameIntervals.Count, FrameP95Ms = frameIntervals.Count == 0 ? 0 : frameIntervals.Order().ElementAt((int)((frameIntervals.Count - 1) * .95)), LongFrames = frameIntervals.Count(x => x > 50) });
        UpdateConnections(display); Select("NG");
        await Task.Delay(900);
        fitWorld = false; SizeMap();
        await Task.Delay(200);
        await CaptureAtlasAsync("geo-preview.png");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "geo-selftest.json"), JsonSerializer.Serialize(new { Passed = true, Results = results }, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal async Task CaptureAtlasAsync(string name)
    {
        AtlasLayout.Background = new SolidColorBrush(ActualTheme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 250, 250, 250));
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(this);
        var file = await (await StorageFolder.GetFolderFromPathAsync(AppContext.BaseDirectory)).CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, (await bitmap.GetPixelsAsync()).ToArray());
            await encoder.FlushAsync();
        }
    }

    internal void CheckStackedLayout()
    {
        if (Grid.GetRow(SidePanel) != 1 || SidePanel.ActualHeight < 80 || MapViewport.ActualHeight < 80)
            throw new InvalidOperationException($"Narrow map did not retain a usable map and country panel: layout {MapLayout.ActualWidth} x {MapLayout.ActualHeight}, row {Grid.GetRow(SidePanel)}, panel {SidePanel.ActualHeight}, viewport {MapViewport.ActualHeight}.");
    }
}

public sealed partial class MainWindow
{
    internal void ShowMapForBenchmark(bool motion)
    {
        GeoPanel.SetDiagnosticMotion(motion);
        Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(x => x.Tag?.ToString() == "map");
    }
    internal async Task ExerciseGeoAsync()
    {
        await MonitorPanel.PauseForDiagnosticAsync();
        Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(x => x.Tag?.ToString() == "map");
        await GeoPanel.ExerciseAtlasAsync();
        Root.RequestedTheme = ElementTheme.Light;
        await Task.Delay(200);
        await GeoPanel.CaptureAtlasAsync("geo-light-preview.png");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(800, 900));
        await Task.Delay(300);
        await GeoPanel.CaptureAtlasAsync("geo-narrow-preview.png");
        GeoPanel.CheckStackedLayout();
    }
}
