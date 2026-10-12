using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

namespace ZikWall.WinUI;

public sealed partial class GeoView : UserControl
{
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
        var choices = groups.Where(x => x.Key.Length == 2).Select(x => new CountryChoice(x.Key, $"{x.Value[0].CountryName} · {x.Value.Select(c => c.RemoteAddress).Distinct().Count()} IPs")).OrderBy(x => x.Label).ToList();
        if (CountryPicker.ItemsSource is not List<CountryChoice> previous || !previous.SequenceEqual(choices))
        {
            string keep = selected;
            CountryPicker.ItemsSource = choices;
            CountryPicker.SelectedItem = choices.FirstOrDefault(x => x.Code == keep);
            selected = keep;
        }
        int located = 0, pulses = 0;
        bool animate = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        var visible = new HashSet<string>();
        foreach (var (code, entries) in groups)
        {
            if (!countries.TryGetValue(code, out var location)) continue;
            visible.Add(code); located += entries.Select(x => x.RemoteAddress).Distinct().Count();
            string signature = string.Join('|', entries.Select(x => x.Key).Order());
            if (!markers.TryGetValue(code, out var old))
            {
                var button = new Button { MinWidth = 0, MinHeight = 0, Padding = new Thickness(4, 2, 4, 2), FontSize = 9, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue), Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) };
                button.Click += (_, _) => { selected = code; CountryPicker.SelectedItem = (CountryPicker.ItemsSource as List<CountryChoice>)?.FirstOrDefault(x => x.Code == code); UpdateDetails(); };
                Canvas.SetLeft(button, 2 * (location.Longitude + 180) - 10); Canvas.SetTop(button, 2 * (90 - location.Latitude) - 8);
                Markers.Children.Add(button); old = (button, "");
            }
            int count = entries.Select(x => x.RemoteAddress).Distinct().Count();
            old.Button.Content = $"{code} {count}";
            ToolTipService.SetToolTip(old.Button, $"{location.Label} · {count} IPs · approximate country center");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(old.Button, $"{location.Label}, {count} remote IPs");
            if (old.Signature != signature && pulses++ < 32 && animate)
            {
                var animation = new DoubleAnimation { From = 0.35, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(450)) };
                Storyboard.SetTarget(animation, old.Button); Storyboard.SetTargetProperty(animation, "Opacity");
                var storyboard = new Storyboard(); storyboard.Children.Add(animation); animations.Add(storyboard); storyboard.Begin();
            }
            markers[code] = (old.Button, signature);
        }
        foreach (var key in markers.Keys.Where(x => !visible.Contains(x)).ToArray()) { Markers.Children.Remove(markers[key].Button); markers.Remove(key); }
        int total = internet.Select(x => x.RemoteAddress).Distinct().Count();
        GeoStatus.Text = $"{located}/{total} Internet IPs located · {markers.Count} countries · live snapshots";
        UpdateDetails();
    }
    public sealed record CountryChoice(string Code, string Label);
    private void CountrySelected(object sender, SelectionChangedEventArgs args)
    {
        if (CountryPicker.SelectedItem is CountryChoice country) { selected = country.Code; UpdateDetails(); }
    }
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
        if (!groups.TryGetValue(selected, out var entries)) { Destinations.ItemsSource = null; DestinationTitle.Text = "Select a country"; return; }
        DestinationTitle.Text = $"{entries[0].CountryName} · {entries.Count} connections";
        Destinations.ItemsSource = entries.OrderBy(x => x.Application).ThenBy(x => x.RemoteAddress).Take(1000).ToList();
    }
}
