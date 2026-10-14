using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

namespace ZikWall.WinUI;

public sealed partial class LogAnalysisView : UserControl
{
    private readonly LogReadStateStore readStore = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZikWall"));
    private MonitoringView? monitor;
    private DateTimeOffset readThrough;
    private bool ready;
    public Window? Owner { get; set; }
    private string persistenceError = "";

    public LogAnalysisView()
    {
        InitializeComponent();
        try { readThrough = readStore.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { persistenceError = $"Read status unavailable: {ex.Message}"; }
        ready = true;
    }

    public void Connect(MonitoringView source)
    {
        if (monitor is not null) monitor.LogChanged -= Refresh;
        monitor = source;
        monitor.LogChanged += Refresh;
    }

    public void Show() => Refresh();

    private void Refresh()
    {
        if (!ready || monitor is null || Visibility != Visibility.Visible) return;
        var result = LogAnalysis.Build(monitor.LogEvents, DateTimeOffset.UtcNow, readThrough,
            (LogFilter)Math.Max(0, CategoryChoice.SelectedIndex), (LogRange)Math.Max(0, RangeChoice.SelectedIndex),
            LogSearch.Text ?? "", UnreadOnly.IsChecked == true, monitor.IsLogEntryVisible, monitor.EnrichLogEntry);
        // Monitor ticks usually change nothing visible; re-binding the grouped source re-rendered every card.
        if (GroupedEvents.Source is not IReadOnlyList<LogDayGroup> shown || !SameGroups(shown, result.Groups)) GroupedEvents.Source = result.Groups;
        EmptyState.Visibility = result.MatchingCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        MarkReadButton.IsEnabled = result.UnreadCount > 0;
        string count = result.MatchingCount > LogAnalysis.DisplayLimit ? $"Showing newest {LogAnalysis.DisplayLimit:N0} of {result.MatchingCount:N0} matching events" : $"{result.MatchingCount:N0} matching events";
        LogStatus.Text = $"{count} · {result.UnreadCount:N0} unread · Up to 7 days / 10,000 events. "
            + (monitor.IsHistorySaved ? "History saved on this PC." : "History saving is off; new events stay in memory.")
            + (persistenceError.Length > 0 ? " " + persistenceError : "");
    }

    private static bool SameGroups(IReadOnlyList<LogDayGroup> left, IReadOnlyList<LogDayGroup> right) =>
        left.Count == right.Count && left.Zip(right).All(x => x.First.Title == x.Second.Title && x.First.SequenceEqual(x.Second));

    private void FilterChanged(object sender, SelectionChangedEventArgs args) => Refresh();
    private void UnreadChanged(object sender, RoutedEventArgs args) => Refresh();
    private void SearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => DispatcherQueue.TryEnqueue(Refresh);

    private void MarkReadClicked(object sender, RoutedEventArgs args)
    {
        if (monitor is null || monitor.LogEvents.Count == 0) return;
        // Mark all retained events, including currently filtered-out entries.
        readThrough = monitor.LogEvents.Max(x => x.Time);
        try { readStore.Save(readThrough); persistenceError = ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { persistenceError = $"Read state changed for this session only: {ex.Message}"; }
        Refresh();
    }

    private void EventClicked(object sender, ItemClickEventArgs args)
    {
        if (Owner is null || args.ClickedItem is not LogEntry row) return;
        Presentation.ToolWindows.For(Owner).Text("log:" + row.Source.Time.UtcTicks + ":" + row.Source.Event + ":" + row.Connection.Key, row.CategoryLabel,
            $"{row.FullTime}\n{row.Connection.Application} {row.Message}.\n\n{row.Host}\n{row.Context}\n{row.Connection.CountryLabel}\n\n{row.Source.Details}\n{row.Connection.ProcessPath}\n\n{row.Connection.DomainDescription}");
    }
}
