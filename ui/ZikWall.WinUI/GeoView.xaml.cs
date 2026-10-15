using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = System.IO.Path;
using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

namespace ZikWall.WinUI;

public sealed record CountryRow(string Code, string Name, string Flag, int Ips, int Apps, double Share)
{
    public string IpsLabel => Ips == 1 ? "1 IP" : $"{Ips} IPs";
    public string AppsLabel => Apps == 1 ? "1 app" : $"{Apps} apps";
    public string Label => $"{Name}, {IpsLabel}, {AppsLabel}";
}
public sealed record MapAppRow(string Key, string Name, string Path, int Ips)
{
    public string Detail => $"{Ips} remote {(Ips == 1 ? "IP" : "IPs")}";
}

public sealed partial class GeoView : UserControl
{
    private static readonly SolidColorBrush MarkerFill = new(Windows.UI.Color.FromArgb(255, 0x08, 0x76, 0x85));
    private static readonly SolidColorBrush SelectedFill = new(Windows.UI.Color.FromArgb(255, 0xB8, 0x56, 0x0C));
    private static readonly SolidColorBrush Teal = new(Windows.UI.Color.FromArgb(255, 0x55, 0xD9, 0xD0));
    private static readonly SolidColorBrush Amber = new(Windows.UI.Color.FromArgb(255, 0xFF, 0xC0, 0x72));
    private sealed record MapMarker(Button Button, Ellipse Halo, Ellipse Glow, Line Leader, HashSet<string> RemoteIps)
    {
        public double X, Y, Size;
    }
    private IReadOnlyList<ConnectionEntry> connections = [];
    private bool loaded, background;
    private Dictionary<string, GeoLocation> countries = new();
    private readonly Dictionary<string, MapMarker> markers = new();
    private readonly HashSet<Microsoft.UI.Composition.Visual> animations = [];
    private readonly Windows.UI.ViewManagement.UISettings uiSettings = new();
    private ConnectionEntry[]? lastDrawn;
    private Dictionary<string, List<ConnectionEntry>> groups = new();
    private readonly Dictionary<string, CountryRow> countryRows = new();
    private string selected = "";
    private string selectedApp = "", countryDataStatus = "Offline country data ready";
    private List<MapAppRow> appRows = [];
    private bool fitWorld;
    private uint activityAnimationCursor;
    public Window? Owner { get; set; }
    private bool Active => !background && Visibility == Visibility.Visible && Owner?.AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized };
    public void SetBackground(bool value)
    {
        if (background == value) return;
        background = value;
        if (value) StopAnimations(); else if (Active) Reveal();
    }
    private void StopAnimations()
    {
        foreach (var visual in animations) { visual.StopAnimation("Scale"); visual.StopAnimation("Offset"); visual.StopAnimation("Opacity"); visual.Opacity = 0; }
        animations.Clear();
        // Entrance scaling must never leave a marker shrunk or hidden.
        foreach (var visual in entrances) { visual.StopAnimation("Scale"); visual.Scale = Vector3.One; }
        entrances.Clear();
        introUntil = 0;
    }
    public GeoView()
    {
        InitializeComponent();
        CountryList.ItemsSource = displayedCountries;
        MapApps.ItemsSource = displayedApps;
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => { if (!Active) StopAnimations(); });
        Unloaded += (_, _) => StopAnimations();
        ElementCompositionPreview.GetElementVisual(HubPulse).Opacity = 0;
        BuildGraticule();
    }
    public void Show()
    {
        if (!loaded)
        {
            try { countries = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, GeoLocation>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "Countries", "centroids.json"))) ?? new(); loaded = true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { GeoStatus.Text = ex.Message; return; }
        }
        Reveal();
    }
    public void UpdateConnections(IReadOnlyList<ConnectionEntry> entries) { connections = entries; if (Active) Draw(); }
    public void SetCountryDataStatus(string status)
    {
        if (countryDataStatus == status) return;
        countryDataStatus = status;
        lastDrawn = null;
        if (Active) Draw();
    }
    private void Draw()
    {
        if (!Active) { StopAnimations(); return; }
        if (!loaded || lastDrawn is not null && connections.SequenceEqual(lastDrawn)) return;
        bool appsChanged = lastDrawn is null || connections.Count != lastDrawn.Length;
        if (!appsChanged)
            for (int i = 0; i < connections.Count; i++)
            {
                var a = connections[i]; var b = lastDrawn![i];
                if (a.RemoteAddress != b.RemoteAddress || a.RemotePort != b.RemotePort || a.NetworkScope != b.NetworkScope ||
                    a.ProcessPath != b.ProcessPath || a.PackageFamily != b.PackageFamily || a.ProcessId != b.ProcessId ||
                    a.ProcessStarted != b.ProcessStarted || a.ProcessName != b.ProcessName) { appsChanged = true; break; }
            }
        // Keep a private snapshot: callers can reuse a mutable list between captures.
        lastDrawn = connections.ToArray();
        // Let the entrance finish; live captures arrive faster than it runs.
        if (!IntroRunning) StopAnimations();
        var internet = connections.Where(x => x.NetworkScope == "Internet" && x.RemotePort is > 0).ToList();
        if (appsChanged)
        {
            var nextApps = internet.GroupBy(x => x.AppKey).Select(g => new MapAppRow(g.Key, g.First().Application,
                g.First().ProcessPath, g.Select(x => x.RemoteAddress).Distinct().Count())).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
            if (!appRows.SequenceEqual(nextApps)) { appRows = nextApps; UpdateAppRows(appRows); }
        }
        if (selectedApp.Length > 0 && !appRows.Any(x => x.Key == selectedApp)) selectedApp = "";
        MapApps.SelectedItem = displayedApps.FirstOrDefault(x => x.Key == selectedApp);
        if (selectedApp.Length > 0) internet = internet.Where(x => x.AppKey == selectedApp).ToList();
        var previousGroups = groups;
        groups = internet.GroupBy(x => x.CountryCode).ToDictionary(x => x.Key, x => x.ToList());
        foreach (var code in countryRows.Keys.Where(x => !groups.ContainsKey(x)).ToArray()) countryRows.Remove(code);
        foreach (var (code, entries) in groups)
        {
            if (code.Length != 2 || !countries.ContainsKey(code)) continue;
            if (countryRows.ContainsKey(code) && previousGroups.TryGetValue(code, out var previousEntries) && entries.SequenceEqual(previousEntries)) continue;
            countryRows[code] = new CountryRow(code, entries[0].CountryName, entries[0].CountryFlag,
                entries.Select(x => x.RemoteAddress).Distinct().Count(), entries.Select(x => x.AppKey).Distinct().Count(), 0);
        }
        var located = groups.Where(x => countryRows.ContainsKey(x.Key)).Select(x => (Code: x.Key, Entries: x.Value, Ips: countryRows[x.Key].Ips)).ToList();
        int busiest = located.Select(x => x.Ips).DefaultIfEmpty(1).Max();
        var rows = countryRows.Values.Select(x => x with { Share = (double)x.Ips / busiest })
            .OrderByDescending(x => x.Ips).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        UpdateCountryRows(rows);
        int placed = 0;
        bool initialPopulation = markers.Count == 0;
        var changedCountries = new List<string>();
        var visible = new HashSet<string>();
        foreach (var (code, entries, count) in located)
        {
            if (!countries.TryGetValue(code, out var location)) continue;
            visible.Add(code); placed += count;
            // This chart represents remote IPs. Socket/port churn to the same
            // destination must not restart decorative map animations.
            bool changed = !markers.TryGetValue(code, out var old) || !old.RemoteIps.SetEquals(entries.Select(x => x.RemoteAddress));
            var remoteIps = changed ? entries.Select(x => x.RemoteAddress).ToHashSet() : old!.RemoteIps;
            if (old is null)
            {
                var button = new Button { Style = (Style)Resources["MarkerStyle"] };
                button.Click += (_, _) => Select(selected == code ? "" : code);
                var halo = new Ellipse { Stroke = Teal, StrokeThickness = 1.5, IsHitTestVisible = false };
                ElementCompositionPreview.GetElementVisual(halo).Opacity = 0;
                var glow = new Ellipse { Fill = GlowFill, IsHitTestVisible = false };
                var leader = new Line { Stroke = Teal, StrokeThickness = 0.6, Opacity = 0.5, IsHitTestVisible = false };
                Canvas.SetZIndex(halo, -1); Canvas.SetZIndex(glow, -2); Canvas.SetZIndex(leader, -3);
                Markers.Children.Add(leader); Markers.Children.Add(glow); Markers.Children.Add(halo); Markers.Children.Add(button);
                old = new(button, halo, glow, leader, remoteIps);
            }
            // Area grows with the IP count, capped so dense regions stay readable.
            double size = Math.Min(32, 18 + 3 * Math.Sqrt(count));
            old.Size = size;
            if (old.Button.Content as string != count.ToString())
            {
                old.Button.Width = old.Button.Height = size;
                old.Halo.Width = old.Halo.Height = size + 10;
                old.Glow.Width = old.Glow.Height = size * 2.4;
                old.Button.Content = count.ToString();
                ToolTipService.SetToolTip(old.Button, $"{location.Label} · {count} {(count == 1 ? "IP" : "IPs")} · select to explore");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(old.Button, $"{location.Label}, {count} remote IPs");
            }
            markers[code] = old with { RemoteIps = remoteIps };
            if (changed) changedCountries.Add(code);
        }
        foreach (var key in markers.Keys.Where(x => !visible.Contains(x)).ToArray())
        {
            var marker = markers[key]; Markers.Children.Remove(marker.Button); Markers.Children.Remove(marker.Halo); Markers.Children.Remove(marker.Glow); Markers.Children.Remove(marker.Leader); markers.Remove(key);
        }
        PositionMarkers();
        int total = internet.Select(x => x.RemoteAddress).Distinct().Count();
        GeoStatus.Text = countryDataStatus.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
            ? "Offline location files are missing or unreadable. Repair ZikWall to restore the map."
            : total == 0 ? "No Internet connections right now" : $"{markers.Count} {(markers.Count == 1 ? "country" : "countries")} · {placed} of {total} Internet IPs located";
        CountryCount.Text = markers.Count.ToString(); IpCount.Text = total.ToString("N0");
        AppCount.Text = internet.Select(x => x.AppKey).Distinct().Count().ToString("N0");
        Coverage.Text = total == 0 ? "—" : $"{100.0 * placed / total:0}%";
        UpdateDetails();
        UpdateMarkerStyles();
        UpdateFocus();
        UpdateLinks();
        // Reveal the map together, then spotlight one changed destination per
        // capture. Round-robin selection avoids starving quieter countries.
        // Counts and geometry always update; only decorative motion is bounded.
        if (introPending || initialPopulation && changedCountries.Count > 0) PlayIntro();
        else if (changedCountries.Count > 0 && !IntroRunning)
        {
            string code = changedCountries[(int)(activityAnimationCursor++ % (uint)changedCountries.Count)];
            // Captures arrive about once a second: keep live spotlights short and concurrent.
            AnimateLink(code); Pulse(markers[code].Halo);
        }
    }
    private void Select(string code)
    {
        selected = code;
        UpdateDetails();
        UpdateMarkerStyles();
        UpdateFocus();
        UpdateLinks();
        StopAnimations();
        AnimateLink(selected, selection: true);
        if (markers.TryGetValue(selected, out var marker))
        {
            Pulse(marker.Halo, selection: true, delay: SelectionTravelMs * .8);
            double x = marker.X / 720 * MapBox.Width * MapViewport.ZoomFactor;
            double y = marker.Y / 360 * MapBox.Height * MapViewport.ZoomFactor;
            bool outside = x < MapViewport.HorizontalOffset + 40 || x > MapViewport.HorizontalOffset + MapViewport.ViewportWidth - 40 ||
                y < MapViewport.VerticalOffset + 40 || y > MapViewport.VerticalOffset + MapViewport.ViewportHeight - 40;
            if (outside) MapViewport.ChangeView(Math.Max(0, x - MapViewport.ViewportWidth / 2), Math.Max(0, y - MapViewport.ViewportHeight / 2), null, true);
        }
    }
    private void UpdateMarkerStyles()
    {
        foreach (var (code, marker) in markers)
        {
            bool isSelected = code == selected;
            marker.Button.Background = isSelected ? SelectedFill : MarkerFill;
            marker.Button.BorderBrush = marker.Halo.Stroke = isSelected ? Amber : Teal;
            marker.Glow.Fill = isSelected ? SelectedGlowFill : GlowFill;
            // Smaller dots above larger ones; the selection above everything.
            Canvas.SetZIndex(marker.Button, isSelected ? 1000 : 500 - (int)marker.Button.Width);
        }
    }
    private void AppClicked(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is MapAppDisplayRow row) { selectedApp = row.Key; selected = ""; lastDrawn = null; Draw(); }
    }
    private void AllAppsClicked(object sender, RoutedEventArgs args) { selectedApp = selected = ""; lastDrawn = null; Draw(); }

    private void LayoutChanged(object sender, SizeChangedEventArgs args)
    {
        double width = args.NewSize.Width, height = args.NewSize.Height;
        bool stacked = width < 700;
        bool showApps = width >= 880;
        AppsPanel.Visibility = showApps ? Visibility.Visible : Visibility.Collapsed;
        MapLayout.ColumnDefinitions[0].Width = new GridLength(showApps ? width < 1100 ? 160 : 200 : 0);
        MapLayout.ColumnDefinitions[2].Width = new GridLength(stacked ? 0 : width < 1100 ? 220 : 260);
        MapLayout.RowDefinitions[0].Height = stacked ? new GridLength(Math.Max(140, height * .58)) : new GridLength(1, GridUnitType.Star);
        MapLayout.RowDefinitions[1].Height = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SummaryCards.Visibility = stacked ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(SidePanel, stacked ? 1 : 0); Grid.SetColumn(SidePanel, stacked ? 1 : 2);
    }
    private void CountryClicked(object sender, ItemClickEventArgs args) { if (args.ClickedItem is CountryDisplayRow row) Select(row.Code); }
    private void BackClicked(object sender, RoutedEventArgs args) => Select("");
    internal async Task ExerciseSelectionAsync()
    {
        if (markers.Count != 2 || !markers.TryGetValue("US", out var marker)) throw new InvalidOperationException("Fixture markers were not rendered.");
        var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(marker.Button);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        await Task.Delay(150);
        if (selected != "US" || Destinations.Items.Count != 1) throw new InvalidOperationException("Country click did not populate connection details.");
    }
    private void UpdateDetails()
    {
        bool any = displayedCountries.Count > 0;
        if (!groups.TryGetValue(selected, out var entries) || selected.Length != 2)
        {
            selected = "";
            DestinationTitle.Text = "Countries";
            DestinationSubtitle.Text = "Most remote IPs first";
            BackButton.Visibility = SelectedFlag.Visibility = Destinations.Visibility = Visibility.Collapsed;
            CountryList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            EmptyCountries.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            Destinations.ItemsSource = null;
            var unknown = groups.Where(x => !countries.ContainsKey(x.Key)).SelectMany(x => x.Value).Take(1000).ToList();
            if (!any && unknown.Count > 0)
            {
                DestinationTitle.Text = "Unlocated connections";
                DestinationSubtitle.Text = "IP addresses are still available";
                Destinations.ItemsSource = unknown;
                Destinations.Visibility = Visibility.Visible;
                EmptyCountries.Visibility = Visibility.Collapsed;
            }
            EmptyCountryMessage.Text = "No Internet connections for this selection";
            return;
        }
        DestinationTitle.Text = entries[0].CountryName;
        int apps = entries.Select(x => x.AppKey).Distinct().Count();
        DestinationSubtitle.Text = $"{entries.Count} {(entries.Count == 1 ? "connection" : "connections")} · {apps} {(apps == 1 ? "app" : "apps")}";
        if (SelectedFlag.Tag as string != selected) { SelectedFlag.Source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri(entries[0].CountryFlag)); SelectedFlag.Tag = selected; }
        BackButton.Visibility = SelectedFlag.Visibility = Destinations.Visibility = Visibility.Visible;
        CountryList.Visibility = EmptyCountries.Visibility = Visibility.Collapsed;
        var list = entries.OrderBy(x => x.Application, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.RemoteAddress).Take(1000).ToList();
        if (Destinations.ItemsSource is not List<ConnectionEntry> previous || !previous.SequenceEqual(list)) Destinations.ItemsSource = list;
    }
}
