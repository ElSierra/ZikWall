using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

namespace ZikWall.WinUI;

public sealed record CountryRow(string Code, string Name, string Flag, int Ips, int Apps, double Share)
{
    public string IpsLabel => Ips == 1 ? "1 IP" : $"{Ips} IPs";
    public string AppsLabel => Apps == 1 ? "1 app" : $"{Apps} apps";
    public string Label => $"{Name}, {IpsLabel}, {AppsLabel}";
}

public sealed partial class GeoView : UserControl
{
    private static readonly SolidColorBrush MarkerFill = new(Windows.UI.Color.FromArgb(230, 0x00, 0x78, 0xD4));
    private static readonly SolidColorBrush SelectedFill = new(Windows.UI.Color.FromArgb(255, 0xF7, 0x63, 0x0C));
    private IReadOnlyList<ConnectionEntry> connections = [];
    private bool loaded, background;
    private Dictionary<string, GeoLocation> countries = new();
    private readonly Dictionary<string, (Button Button, string Signature)> markers = new();
    private readonly List<Storyboard> animations = [];
    private Dictionary<string, List<ConnectionEntry>> groups = new();
    private string selected = "";
    public Window? Owner { get; set; }
    private bool Active => !background && Visibility == Visibility.Visible && Owner?.AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized };
    public void SetBackground(bool value) { if (background == value) return; background = value; if (value) StopAnimations(); else if (Active) Draw(); }
    private void StopAnimations() { foreach (var animation in animations) animation.Stop(); animations.Clear(); }
    public GeoView()
    {
        InitializeComponent();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => { if (!Active) StopAnimations(); });
    }
    public void Show()
    {
        if (!loaded)
        {
            try { countries = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, GeoLocation>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "Countries", "centroids.json"))) ?? new(); loaded = true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { GeoStatus.Text = ex.Message; return; }
        }
        Draw();
    }
    public void UpdateConnections(IReadOnlyList<ConnectionEntry> entries) { connections = entries; if (Active) Draw(); }
    private void Draw()
    {
        if (!Active) { StopAnimations(); return; }
        StopAnimations();
        var internet = connections.Where(x => x.NetworkScope == "Internet" && x.RemotePort is > 0).ToList();
        groups = internet.GroupBy(x => x.CountryCode).ToDictionary(x => x.Key, x => x.ToList());
        var located = groups.Where(x => x.Key.Length == 2).Select(x => (Code: x.Key, Entries: x.Value, Ips: x.Value.Select(c => c.RemoteAddress).Distinct().Count())).ToList();
        int busiest = located.Select(x => x.Ips).DefaultIfEmpty(1).Max();
        var rows = located.Select(x => new CountryRow(x.Code, x.Entries[0].CountryName, x.Entries[0].CountryFlag, x.Ips, x.Entries.Select(c => c.AppKey).Distinct().Count(), (double)x.Ips / busiest))
            .OrderByDescending(x => x.Ips).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        // Rebinding resets scroll position, so only replace the list when a row changed.
        if (CountryList.ItemsSource is not List<CountryRow> previous || !previous.SequenceEqual(rows)) CountryList.ItemsSource = rows;
        int placed = 0, pulses = 0;
        bool animate = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        var visible = new HashSet<string>();
        foreach (var (code, entries, count) in located)
        {
            if (!countries.TryGetValue(code, out var location)) continue;
            visible.Add(code); placed += count;
            string signature = string.Join('|', entries.Select(x => x.Key).Order());
            if (!markers.TryGetValue(code, out var old))
            {
                var button = new Button { Style = (Style)Resources["MarkerStyle"] };
                button.Click += (_, _) => Select(selected == code ? "" : code);
                Markers.Children.Add(button); old = (button, "");
            }
            // Area grows with the IP count, capped so dense regions stay readable.
            double size = Math.Min(30, 14 + 3 * Math.Sqrt(count));
            old.Button.Width = old.Button.Height = size;
            Canvas.SetLeft(old.Button, 2 * (location.Longitude + 180) - size / 2); Canvas.SetTop(old.Button, 2 * (90 - location.Latitude) - size / 2);
            old.Button.Content = count.ToString();
            ToolTipService.SetToolTip(old.Button, $"{location.Label} · {count} {(count == 1 ? "IP" : "IPs")}");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(old.Button, $"{location.Label}, {count} remote IPs");
            if (old.Signature.Length > 0 && old.Signature != signature && pulses++ < 32 && animate)
            {
                var animation = new DoubleAnimation { From = 0.35, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(450)) };
                Storyboard.SetTarget(animation, old.Button); Storyboard.SetTargetProperty(animation, "Opacity");
                var storyboard = new Storyboard(); storyboard.Children.Add(animation); animations.Add(storyboard); storyboard.Begin();
            }
            markers[code] = (old.Button, signature);
        }
        foreach (var key in markers.Keys.Where(x => !visible.Contains(x)).ToArray()) { Markers.Children.Remove(markers[key].Button); markers.Remove(key); }
        int total = internet.Select(x => x.RemoteAddress).Distinct().Count();
        GeoStatus.Text = total == 0 ? "No Internet connections right now" : $"{markers.Count} {(markers.Count == 1 ? "country" : "countries")} · {placed} of {total} Internet IPs located";
        UpdateMarkerStyles();
        UpdateDetails();
    }
    private void Select(string code)
    {
        selected = code;
        UpdateMarkerStyles();
        UpdateDetails();
    }
    private void UpdateMarkerStyles()
    {
        foreach (var (code, marker) in markers)
        {
            bool isSelected = code == selected;
            marker.Button.Background = isSelected ? SelectedFill : MarkerFill;
            // Smaller dots above larger ones; the selection above everything.
            Canvas.SetZIndex(marker.Button, isSelected ? 1000 : 500 - (int)marker.Button.Width);
        }
    }
    // The map is 2:1. In tall windows, stack the country panel below it instead of leaving empty bands.
    private void LayoutChanged(object sender, SizeChangedEventArgs args)
    {
        double width = args.NewSize.Width, height = args.NewSize.Height;
        bool stacked = height > 480 && (width - 352) / height < 1.4;
        MapLayout.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(340);
        MapLayout.ColumnSpacing = stacked ? 0 : 12;
        MapLayout.RowDefinitions[0].Height = stacked ? new GridLength(Math.Min(height * 0.62, width / 2 + 24)) : new GridLength(1, GridUnitType.Star);
        MapLayout.RowDefinitions[1].Height = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        MapLayout.RowSpacing = stacked ? 12 : 0;
        Grid.SetRow(SidePanel, stacked ? 1 : 0); Grid.SetColumn(SidePanel, stacked ? 0 : 1);
    }
    private void CountryClicked(object sender, ItemClickEventArgs args) { if (args.ClickedItem is CountryRow row) Select(row.Code); }
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
        bool any = CountryList.ItemsSource is List<CountryRow> { Count: > 0 };
        if (!groups.TryGetValue(selected, out var entries) || selected.Length != 2)
        {
            selected = "";
            DestinationTitle.Text = "Countries";
            DestinationSubtitle.Text = "Most remote IPs first";
            BackButton.Visibility = SelectedFlag.Visibility = Destinations.Visibility = Visibility.Collapsed;
            CountryList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            EmptyCountries.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            Destinations.ItemsSource = null;
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
