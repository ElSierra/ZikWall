using System.Collections.ObjectModel;
using System.ComponentModel;
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
    // Row slots update in place; replacing items recreated containers, icons and flags every refresh and flashed.
    private readonly ObservableCollection<ActivityRankSlot> apps = [], hosts = [], types = [], countries = [];
    private bool? compactLayout;
    private Microsoft.UI.Xaml.Media.Brush? dividerBrush;
    private bool collecting;
    private string collectionStatus = "";
    public event Action? ConnectionsRequested;
    public Func<Task>? EnableUsageRequested { get; set; }
    public ActivityOverviewView()
    {
        InitializeComponent();
        AppsList.ItemsSource = apps; HostsList.ItemsSource = hosts; TypesList.ItemsSource = types; CountriesList.ItemsSource = countries;
    }
    private static void Sync(ObservableCollection<ActivityRankSlot> target, IReadOnlyList<ActivityRank> next)
    {
        for (int i = 0; i < next.Count; i++)
        {
            if (i == target.Count) target.Add(new(next[i]));
            else target[i].Assign(next[i]);
        }
        while (target.Count > next.Count) target.RemoveAt(target.Count - 1);
    }
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
        Sync(apps, result.Apps); Sync(hosts, result.Hosts); Sync(types, result.Types); Sync(countries, result.Countries);
        OverviewEmpty.Visibility = result.Connections == 0 ? Visibility.Visible : Visibility.Collapsed;
        AppsCaption.Text = result.Download + result.Upload > 0 ? "Traffic · whole-app totals" : "Observed connections";
        ActivityTotals.Text = $"↓ {UsageLedger.Bytes(result.Download)}   ↑ {UsageLedger.Bytes(result.Upload)}   ·   {result.AppCount:N0} apps   ·   {result.HostCount:N0} hosts";
        bool hasBytes = result.Apps.Any(x => x.Hint.StartsWith("Downloaded", StringComparison.Ordinal));
        if (!hasBytes) ActivityTotals.Text = $"{result.AppCount:N0} apps   ·   {result.HostCount:N0} hosts   ·   {result.Connections:N0} connections";
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
        if (compactLayout == compact) return;
        compactLayout = compact;
        // Rebuild the definitions instead of zeroing them: spacing still applied around empty tracks and pushed the right edge in.
        RankColumns.ColumnDefinitions.Clear(); RankColumns.RowDefinitions.Clear();
        for (int i = 0; i < (compact ? 2 : 4); i++) RankColumns.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        for (int i = 0; i < (compact ? 2 : 1); i++) RankColumns.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        Place(AppsColumn, 0, 0, true); Place(HostsColumn, 0, 1, !compact);
        Place(TypesColumn, compact ? 1 : 0, compact ? 0 : 2, true); Place(CountriesColumn, compact ? 1 : 0, compact ? 1 : 3, false);
        Grid.SetColumnSpan(OverviewEmpty, compact ? 2 : 4); Grid.SetRowSpan(OverviewEmpty, compact ? 2 : 1);
    }
    // Every column keeps the same 1 DIP border so values line up; only inner columns show the divider.
    private void Place(Grid column, int row, int col, bool divider)
    {
        Grid.SetRow(column, row); Grid.SetColumn(column, col);
        dividerBrush ??= AppsColumn.BorderBrush;
        column.BorderBrush = divider ? dividerBrush : null;
    }
}

public sealed class ActivityRankSlot(ActivityRank rank) : INotifyPropertyChanged
{
    public ActivityRank Rank { get; private set; } = rank;
    public string Title => Rank.Title;
    public string Value => Rank.Value;
    public string Hint => Rank.Hint;
    public string ProcessPath => Rank.ProcessPath;
    public string Flag => Rank.Flag;
    public double DownloadShare => Rank.DownloadShare;
    public double UploadShare => Rank.UploadShare;
    public double RemainingShare => Rank.RemainingShare;
    public bool IsApp => Rank.IsApp;
    public bool HasFlag => Rank.HasFlag;
    public event PropertyChangedEventHandler? PropertyChanged;
    // Raise only what changed so unchanged icons, flags and bars are left alone.
    public void Assign(ActivityRank next)
    {
        var old = Rank;
        if (old == next) return;
        Rank = next;
        if (old.Title != next.Title) Raise(nameof(Title));
        if (old.Value != next.Value) Raise(nameof(Value));
        if (old.Hint != next.Hint) Raise(nameof(Hint));
        if (old.ProcessPath != next.ProcessPath) Raise(nameof(ProcessPath));
        if (old.Flag != next.Flag) Raise(nameof(Flag));
        if (old.DownloadShare != next.DownloadShare) Raise(nameof(DownloadShare));
        if (old.UploadShare != next.UploadShare) Raise(nameof(UploadShare));
        if (old.RemainingShare != next.RemainingShare) Raise(nameof(RemainingShare));
        if (old.IsApp != next.IsApp) Raise(nameof(IsApp));
        if (old.HasFlag != next.HasFlag) Raise(nameof(HasFlag));
    }
    private void Raise(string name) => PropertyChanged?.Invoke(this, new(name));
}
