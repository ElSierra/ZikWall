using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

namespace ZikWall.WinUI;

public sealed partial class LogAnalysisView : UserControl
{
    private readonly LogReadStateStore readStore;
    private MonitoringView? monitor;
    private LogReadState readState = LogReadState.Empty();
    private DateTimeOffset readThrough { get => readState.ReadThrough; set => readState = new(value, []); }
    private bool ready;
    public Window? Owner { get; set; }
    private string persistenceError = "";
    private CancellationTokenSource? queryCancellation;
    private Task savedQuery = Task.CompletedTask;
    private readonly List<LogCursor?> pages = [null];
    private SavedLogResult? savedResult;
    private DateTimeOffset lastQueryStarted;
    private readonly LogTimelineCollection timeline = new();
    private ScrollViewer? timelineScroll;
    private bool scrolling;
    private bool automaticQuery;
    private bool resetScroll;
    private Window? logDetailWindow;
    private MenuFlyout? logMenu;

    public LogAnalysisView() : this(null) { }
    internal LogAnalysisView(string? directory)
    {
        directory ??= Environment.GetCommandLineArgs().Any(x => x.StartsWith("--test-", StringComparison.Ordinal) || x == "--benchmark")
            ? Path.Combine(AppContext.BaseDirectory, "log-diagnostic", Guid.NewGuid().ToString("N"))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZikWall");
        readStore = new(directory);
        InitializeComponent();
        GroupedEvents.Source = timeline;
        EventList.Loaded += (_, _) => AttachScrollViewer();
        Unloaded += (_, _) => logMenu?.Hide();
        try { readState = readStore.LoadState(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { persistenceError = $"Read status unavailable: {ex.Message}"; }
        ready = true;
    }

    public void Connect(MonitoringView source)
    {
        if (monitor is not null) monitor.LogChanged -= LogsChanged;
        monitor = source;
        monitor.LogChanged += LogsChanged;
    }

    private void LogsChanged()
    {
        // Archive scans run only for the visible newest page, at most every 30 seconds.
        // Browsing an older page must not jump as new connections arrive.
        if (ReadingTimeline) return;
        if (monitor?.SavedLogDirectory is not null && (pages.Count > 1 || !savedQuery.IsCompleted
            || DateTimeOffset.UtcNow - lastQueryStarted < TimeSpan.FromSeconds(30))) return;
        Refresh(automatic: true);
    }

    private bool ReadingTimeline => scrolling || timelineScroll?.VerticalOffset > 1;
    private void AttachScrollViewer()
    {
        static ScrollViewer? Find(DependencyObject root)
        {
            if (root is ScrollViewer viewer) return viewer;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                if (Find(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
            return null;
        }
        if (timelineScroll is not null) return;
        timelineScroll = Find(EventList);
        if (timelineScroll is null) return;
        timelineScroll.ViewChanged += (_, args) =>
        {
            scrolling = args.IsIntermediate;
            if (ReadingTimeline && automaticQuery) queryCancellation?.Cancel();
        };
    }

    public void Show() => Refresh();

    private void Refresh(bool automatic = false)
    {
        if (!ready || monitor is null || Visibility != Visibility.Visible) return;
        if (monitor.SavedLogDirectory is { } directory)
        {
            savedQuery = RefreshSavedAsync(directory, automatic);
            return;
        }
        var result = LogAnalysis.Build(monitor.LogEvents, DateTimeOffset.UtcNow, readThrough,
            (LogFilter)Math.Max(0, CategoryChoice.SelectedIndex), (LogRange)Math.Max(0, RangeChoice.SelectedIndex),
            LogSearch.Text ?? "", UnreadOnly.IsChecked == true, monitor.IsLogEntryVisible, monitor.EnrichLogEntry, readState: readState);
        Display(result, saved: false);
    }

    private async Task RefreshSavedAsync(string directory, bool automatic)
    {
        queryCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        queryCancellation = cancellation;
        automaticQuery = automatic;
        lastQueryStarted = DateTimeOffset.UtcNow;
        var now = lastQueryStarted;
        var live = monitor!.LogEvents.ToArray();
        var state = new LogReadState(readThrough, new(readState.ReadEventIds));
        var filter = (LogFilter)Math.Max(0, CategoryChoice.SelectedIndex);
        var range = (LogRange)Math.Max(0, RangeChoice.SelectedIndex);
        string search = LogSearch.Text ?? "";
        bool unreadOnly = UnreadOnly.IsChecked == true;
        var visible = monitor.CaptureLogVisibility();
        Func<ConnectionEntry, ConnectionEntry> enrich = monitor.EnrichLogEntry;
        var before = pages[^1];
        // Background refresh can be canceled by scrolling; keep the displayed page usable.
        if (!automatic)
        {
            NewerButton.IsEnabled = OlderButton.IsEnabled = MarkReadButton.IsEnabled = false;
            LogStatus.Text = "Loading saved logs…";
        }
        try
        {
            // Debounce search/filter changes; canceled queries cannot replace newer results.
            await Task.Delay(150, cancellation.Token);
            var result = await Task.Run(() => new SavedLogStore(directory).Query(live, now, state,
                filter, range, search, unreadOnly, visible, enrich, before, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (automatic && ReadingTimeline) return;
            savedResult = result;
            Display(result.Timeline, saved: true);
            NewerButton.IsEnabled = pages.Count > 1;
            OlderButton.IsEnabled = result.Next is not null;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { LogStatus.Text = "Could not load saved logs: " + ex.Message; NewerButton.IsEnabled = pages.Count > 1; }
        finally
        {
            if (ReferenceEquals(queryCancellation, cancellation))
            { queryCancellation = null; automaticQuery = false; }
        }
    }

    private void Display(LogAnalysisResult result, bool saved)
    {
        // Keep native rows stable when nothing visible changed.
        timeline.Update(result.Groups);
        if (resetScroll)
        {
            resetScroll = false;
            AttachScrollViewer();
            timelineScroll?.ChangeView(null, 0, null, true);
        }
        EmptyState.Visibility = result.MatchingCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        MarkReadButton.IsEnabled = result.UnreadCount > 0;
        string count = result.MatchingCount > LogAnalysis.DisplayLimit ? $"Showing newest {LogAnalysis.DisplayLimit:N0} of {result.MatchingCount:N0} matching events" : $"{result.MatchingCount:N0} matching events";
        if (saved) count = $"{result.MatchingCount:N0} matching events · Page {pages.Count:N0}";
        LogStatus.Text = $"{count} · {result.UnreadCount:N0} unread · "
            + (saved ? "Saved history and current session. " : "Current session. ")
            + (monitor!.IsHistorySaved ? "Saved logs stay on this PC until you clear them." : "History saving is off; new events stay in memory.")
            + (persistenceError.Length > 0 ? " " + persistenceError : "");
    }

    private void ResetPages() { pages.Clear(); pages.Add(null); resetScroll = true; }
    private void FilterChanged(object sender, SelectionChangedEventArgs args) { ResetPages(); Refresh(); }
    private void UnreadChanged(object sender, RoutedEventArgs args) { ResetPages(); Refresh(); }
    private void SearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => DispatcherQueue.TryEnqueue(() => { ResetPages(); Refresh(); });
    private void RefreshClicked(object sender, RoutedEventArgs args) { ResetPages(); Refresh(); }
    private void OlderClicked(object sender, RoutedEventArgs args) { if (savedResult?.Next is { } next) { pages.Add(next); resetScroll = true; Refresh(); } }
    private void NewerClicked(object sender, RoutedEventArgs args) { if (pages.Count > 1) { pages.RemoveAt(pages.Count - 1); resetScroll = true; Refresh(); } }

    private void MarkReadClicked(object sender, RoutedEventArgs args)
    {
        if (monitor is null) return;
        // Include archived events, not only the bounded live cache or current page.
        readThrough = savedResult?.LatestTime ?? monitor.LogEvents.Select(x => x.Time).DefaultIfEmpty(readThrough).Max();
        SaveReadState();
        Refresh();
    }

    private void SaveReadState()
    {
        try { readStore.SaveState(readState); persistenceError = ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { persistenceError = $"Read state changed for this session only: {ex.Message}"; }
    }

    private void EventClicked(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not LogEntry row) return;
        OpenLog(row);
    }

    private void LogActionNotice(LogEntry row, string message, bool failed)
    {
        LogActionBanner.Title = row.Connection.Application;
        LogActionBanner.Message = message;
        LogActionBanner.Severity = failed ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        LogActionBanner.IsOpen = true;
    }

    private MenuFlyout CreateLogMenu(LogEntry row) => monitor!.CreateLogMenu(row.Connection,
        () => OpenLog(row), (message, failed) => LogActionNotice(row, message, failed));

    private void LogContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs args)
    {
        bool pointer = args.TryGetPosition(EventList, out var point);
        if (monitor is null || MonitoringView.ContextItem(EventList, args.OriginalSource as DependencyObject, pointer) is not LogEntry row) return;
        EventList.SelectedItem = row;
        logMenu?.Hide();
        var menu = CreateLogMenu(row);
        logMenu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(logMenu, menu)) logMenu = null; };
        args.Handled = true;
        if (pointer) menu.ShowAt(EventList, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = point });
        else menu.ShowAt(EventList.ContainerFromItem(row) as FrameworkElement ?? EventList);
    }

    private void OpenLog(LogEntry row)
    {
        logMenu?.Hide();
        readState.MarkRead(row.Source);
        SaveReadState();
        Refresh();
        if (Owner is null) return;
        logDetailWindow?.Close();
        var window = Presentation.ToolWindows.For(Owner).Details("log-details", row.CategoryLabel, sheet =>
        {
            sheet
            .Header(row.Connection.Application, $"{char.ToUpper(row.Message[0])}{row.Message[1..]}.", row.Connection.ProcessPath, row.CategoryLabel);
            if (monitor is not null)
            {
                var status = new InfoBar { IsClosable = true };
                void Notice(string message, bool failed)
                {
                    LogActionNotice(row, message, failed);
                    status.Title = row.Connection.Application; status.Message = message;
                    status.Severity = failed ? InfoBarSeverity.Warning : InfoBarSeverity.Informational; status.IsOpen = true;
                }
                sheet.Actions(monitor.CreateLogBlockButton(row.Connection, false, Notice))
                    .Actions(monitor.CreateLogBlockButton(row.Connection, true, Notice),
                        new Button { Content = "More actions", Flyout = monitor.CreateLogMenu(row.Connection, () => OpenLog(row), Notice) })
                    .Add(status);
            }
            return sheet.Section("Event", ("Time", row.FullTime), ("Event", row.Source.Event))
                .Connection(row.Connection)
                .Note("Recorded from a connection snapshot. Blocking matches the recorded executable path, not a historical process ID. It does not establish the firewall decision or the hostname requested by the app.");
        }, height: 740);
        logDetailWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(logDetailWindow, window)) logDetailWindow = null; };
    }

    private async void OpenSavedLogsClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZikWall", "SavedHistory");
            Directory.CreateDirectory(directory);
            await Windows.System.Launcher.LaunchFolderPathAsync(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        { persistenceError = $"Could not open saved logs: {ex.Message}"; Refresh(); }
    }
}
