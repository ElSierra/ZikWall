using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI;

public sealed partial class ActivityOverviewView : UserControl
{
    private IReadOnlyList<ConnectionEntry> current = [];
    private IReadOnlyList<ConnectionEvent> events = [];
    private IReadOnlyCollection<ActivityUsageDelta> samples = [];
    private IReadOnlyList<DailyUsage> daily = [];
    private DateTimeOffset? observedSince;
    private bool collecting;
    private string collectionStatus = "";
    public event Action? ConnectionsRequested;
    public Func<Task>? EnableUsageRequested { get; set; }
    public ActivityOverviewView() => InitializeComponent();
    public static GridLength Portion(double share) => share <= 0 ? new(0) : new(share, GridUnitType.Star);

    public void Update(IReadOnlyList<ConnectionEntry> current, IReadOnlyList<ConnectionEvent> events,
        IReadOnlyCollection<ActivityUsageDelta> samples, IReadOnlyList<DailyUsage> daily, DateTimeOffset? since, bool collecting, string status = "")
    {
        this.current = current; this.events = events; this.samples = samples; this.daily = daily;
        observedSince = since; this.collecting = collecting; collectionStatus = status;
        if (Visibility == Visibility.Visible) Refresh();
    }
    public void Show() => Refresh();
    private void Refresh()
    {
        if (AppsList is null || ActivityRange is null || ActivityScope is null) return;
        var now = DateTimeOffset.Now;
        var since = ActivityRange.SelectedIndex switch { 1 => now.AddHours(-1), 2 => new DateTimeOffset(now.Date, now.Offset), _ => now.AddMinutes(-5) };
        string query = ActivitySearch.Text ?? "";
        var source = current.Concat(events.Where(x => x.Time >= since).Select(x => x.Connection))
            .Where(x => MonitorFilter.Matches(x, query, ActivityScope.SelectedIndex, 0));
        IEnumerable<ActivityUsageDelta> bytes = ActivityRange.SelectedIndex == 2
            ? daily.Where(x => x.Day == DateOnly.FromDateTime(now.Date)).Select(x => new ActivityUsageDelta(now, x.AppKey, x.Path, x.Received, x.Sent))
            : samples.Where(x => x.Time >= since);
        var result = ActivityOverview.Build(source, bytes);
        AppsList.ItemsSource = result.Apps; HostsList.ItemsSource = result.Hosts;
        TypesList.ItemsSource = result.Types; CountriesList.ItemsSource = result.Countries;
        OverviewEmpty.Visibility = result.Connections == 0 ? Visibility.Visible : Visibility.Collapsed;
        AppsCaption.Text = result.Download + result.Upload > 0 ? "Traffic · whole-app totals" : "Observed connections";
        ActivityTotals.Text = $"↓ {UsageLedger.Bytes(result.Download)}     ↑ {UsageLedger.Bytes(result.Upload)}     ·     {result.AppCount:N0} apps     ·     {result.HostCount:N0} hosts";
        bool hasBytes = result.Apps.Any(x => x.Hint.StartsWith("Downloaded", StringComparison.Ordinal));
        if (!hasBytes) ActivityTotals.Text = $"{result.AppCount:N0} apps     ·     {result.HostCount:N0} hosts     ·     {result.Connections:N0} connections";
        string coverage = ActivityRange.SelectedIndex == 2 ? "Today's saved app totals cover collection time only."
            : observedSince is { } start ? $"App traffic observed since {start.ToLocalTime():t}; selected-range totals include completed samples only." : "Enable usage to measure app download and upload.";
        ActivityCoverage.Text = coverage + " Host, type and country bars count connections; UDP destinations are unavailable. Current endpoints are included.";
        if (!collecting && (collectionStatus.StartsWith("Usage unavailable", StringComparison.Ordinal) || collectionStatus.StartsWith("Usage stopped", StringComparison.Ordinal)))
            ActivityCoverage.Text = collectionStatus + " " + ActivityCoverage.Text;
        ActivityUsageButton.Content = collecting ? "Usage collecting" : "Enable usage";
        ActivityUsageButton.IsEnabled = !collecting;
    }
    private void SearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => DispatcherQueue.TryEnqueue(Refresh);
    private void FilterChanged(object sender, SelectionChangedEventArgs args) => Refresh();
    private void ConnectionsClicked(object sender, RoutedEventArgs args) => ConnectionsRequested?.Invoke();
    private async void EnableUsageClicked(object sender, RoutedEventArgs args)
    {
        if (EnableUsageRequested is null) return;
        ActivityUsageButton.IsEnabled = false;
        try { await EnableUsageRequested(); }
        finally { Refresh(); }
    }
    private void OverviewSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (RankColumns is null) return;
        bool compact = args.NewSize.Width < 1050;
        RankColumns.ColumnDefinitions[2].Width = RankColumns.ColumnDefinitions[3].Width = compact ? new(0) : new(1, GridUnitType.Star);
        RankColumns.RowDefinitions[1].Height = compact ? new(1, GridUnitType.Star) : new(0);
        Grid.SetColumn(TypesColumn, compact ? 0 : 2); Grid.SetColumn(CountriesColumn, compact ? 1 : 3);
        Grid.SetRow(TypesColumn, compact ? 1 : 0); Grid.SetRow(CountriesColumn, compact ? 1 : 0);
    }
}
