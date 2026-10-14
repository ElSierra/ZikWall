using System.ComponentModel;
using System.Net;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Data;
using Polyline = Microsoft.UI.Xaml.Shapes.Polyline;
using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;
using Windows.Storage.Pickers;
using Windows.Foundation;

namespace ZikWall.WinUI;

public sealed partial class MonitoringView : UserControl
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly ConnectionTracker connections = new();
    private readonly TrafficTracker traffic = new();
    private readonly RecentConnections recent = new();
    private readonly WindowsSystemApps systemApps = new();
    private readonly MonitorPreferencesStore preferencesStore = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZikWall"));
    private readonly bool diagnostic = Environment.GetCommandLineArgs().Skip(1).Any(x => x.StartsWith("--test-", StringComparison.Ordinal) || x == "--benchmark");
    private bool preferencesReady;
    private readonly Presentation.DesktopNotifications desktopNotifications = new();
    private MonitorEnrichment? enrichment;
    private FirewallClient? usageClient;
    private FirewallClient? domainClient;
    private string domainCaptureStatus = "";
    private readonly UsageLedger usage = new();
    private readonly UsageStore usageStore = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZikWall"));
    private DateTimeOffset lastUsageSaved;
    private bool usageBusy, usageDirty;
    internal int CaptureCount { get; private set; }
    private readonly DispatcherTimer usageTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly ActivityStore store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZikWall"));
    private List<ConnectionEntry> current = [];
    private ActivityArchive archive = new([], []);
    private bool busy, started, closed, history;
    private bool updatingAdapters;
    private List<ConnectionEntry>? displayedConnections;
    private readonly ActivityTree activityTree = new();
    private UsageSort usageSort;
    private bool usageEnabled;
    // Connections first seen in the latest snapshot; empty for the baseline snapshot.
    private HashSet<string> fresh = [];
    private bool baselineTaken;
    private bool pageActive = true, background;
    public void SetBackground(bool value) { if (background == value) return; background = value; UpdateInterval(); }
    private bool WindowMinimized => background || Owner?.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized };
    private DateTimeOffset lastSaved;
    public Window? Owner { get; set; }
    public event Action<IReadOnlyList<ConnectionEntry>>? SnapshotChanged;
    public Func<MonitorBlockPlan, Task<string?>>? BlockRequested { get; set; }
    public event Action? ManageRulesRequested;

    internal async Task PauseForDiagnosticAsync() { timer.Stop(); while (busy) await Task.Delay(20); }
    internal async Task ExerciseRowsAsync()
    {
        timer.Stop();
        while (busy) await Task.Delay(20);
        // Deterministic native list stress; no sockets, policy or persisted activity.
        for (int iteration = 0; iteration < 500; iteration++)
        {
            current = Enumerable.Range(iteration % 40, 120).Select(i => new ConnectionEntry(
                "TCPv4", "192.168.1.2", 50000 + i, "8.8.8.8", 443, Environment.ProcessId,
                iteration % 25 == 0 || i == iteration % 120 ? "CloseWait" : "Established", $"Fixture {i:000}", Environment.ProcessPath!,
                i % 2 == 0 ? "Outgoing" : "Incoming", "US", "United States", "Internet", $"Fixture{i / 10}_family", i / 3,
                i % 3 == 0 ? "a-long-cached-website-name.example, second.example, third.example" : "")).ToList();
            UpdateLists();
            if (iteration % 4 == 0 && activityTree.Rows.FirstOrDefault(x => x.Expandable) is { } group) { activityTree.Toggle(group); UpdateLists(); }
            RecentBanner.Message = "Fixture connection batch";
            RecentBanner.IsOpen = iteration % 20 < 10;
            await Task.Delay(20);
        }
    }

    public MonitoringView() : this(null) { }
    internal MonitoringView(string? preferencesDirectory)
    {
        if (preferencesDirectory is not null) { preferencesStore = new(preferencesDirectory); diagnostic = false; }
        InitializeComponent();
        if (!diagnostic)
            try { LoadPreferences(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { MonitorStatus.Text = $"Could not load monitor preferences: {ex.Message}"; }
        preferencesReady = true;
        UpdateSelectionActions();
        desktopNotifications.Invoked = () => DispatcherQueue.TryEnqueue(() => { if (Owner is MainWindow main) main.ShowFromTray(); else Owner?.Activate(); });
        timer.Tick += async (_, _) => await CaptureAsync();
        usageTimer.Tick += async (_, _) => await CaptureUsageAsync();
        try { usage.Load(usageStore.Load(), DateOnly.FromDateTime(DateTime.Now)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { UsageStatus.Text = ex.Message; }
    }

    public void Show(bool showHistory)
    {
        history = showHistory;
        ConnectionHeader.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
        GroupChoice.IsEnabled = !history;
        ConnectionList.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
        HistoryList.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        if (!started)
        {
            started = true;
            try { archive = store.Load(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { MonitorStatus.Text = $"Could not load saved history: {ex.Message}"; }
            timer.Start();
            _ = CaptureAsync();
            _ = RestoreSessionsAsync();
        }
        UpdateLists();
    }

    public void Stop() { closed = true; domainClient?.Dispose(); usageTimer.Stop(); usageClient?.Dispose(); SaveUsage(); timer.Stop(); desktopNotifications.Dispose(); if (!busy) enrichment?.Dispose(); if (SaveHistory.IsOn) _ = PersistAsync(); }
    public void SetPageActive(bool active) { pageActive = active; UpdateInterval(); }
    private void RefreshIntervalChanged(object sender, SelectionChangedEventArgs args) { UpdateInterval(); SavePreferences(); }
    private void UpdateInterval() => timer.Interval = TimeSpan.FromSeconds((!pageActive || WindowMinimized) && !OptionsVisible ? 15 : RefreshChoice?.SelectedIndex switch { 0 => 2, 2 => 10, _ => 5 });

    private async Task CaptureAsync()
    {
        if (busy || closed || !timer.IsEnabled) return;
        UpdateInterval();
        busy = true;
        try
        {
            bool domainBackground = background || !pageActive || WindowMinimized;
            bool reverseDnsEnabled = ReverseDnsLookup.IsOn;
            var snapshot = await Task.Run(() =>
            {
                enrichment ??= new(Path.Combine(AppContext.BaseDirectory, "Assets", "Countries"));
                enrichment.SetReverseDnsEnabled(reverseDnsEnabled);
                return (Connections: enrichment.Enrich(ConnectionMonitor.Capture(), domainBackground), Counters: ConnectionMonitor.CaptureTraffic());
            });
            if (closed || !timer.IsEnabled) return;
            if (domainClient is { Connected: false })
            {
                domainClient.Dispose(); domainClient = null; enrichment?.SetDomainReader(null);
                current = current.Select(x => enrichment?.WithDomainHint(x) ?? x).ToList();
                UpdateLists();
                DomainButton.Content = "Capture website names";
                DomainStatus.Text = "Website-name session ended. Enable capture to reconnect; IP monitoring continues.";
            }
            var now = DateTimeOffset.UtcNow;
            var next = snapshot.Connections.OrderBy(x => x.Application, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.RemoteAddress).ThenBy(x => x.LocalPort).ThenBy(x => x.Protocol).ThenBy(x => x.RemotePort).ThenBy(x => x.ProcessId).ThenBy(x => x.LocalAddress).ToList();
            if (!current.SequenceEqual(next)) current = next;
            UpdateDomainStatus();
            CaptureCount++;
            PublishVisibleSnapshot();
            var observed = connections.Observe(current, now);
            archive.Events.AddRange(observed);
            var nextFresh = baselineTaken ? observed.Where(x => x.Event == "First seen").Select(x => x.Connection.Key).ToHashSet() : [];
            bool freshChanged = fresh.Count > 0 || nextFresh.Count > 0;
            fresh = nextFresh; baselineTaken = true;
            if (recent.Observe(observed, now, RecentAlerts.IsOn || DesktopAlerts.IsOn, IsVisibleApp,
                (ConnectionNotificationType)NotificationTypeChoice.SelectedIndex) is { } batch)
            {
                var message = ConnectionNotification.From(batch);
                RecentBanner.Title = message.Title;
                RecentBanner.Severity = InfoBarSeverity.Informational;
                // One line in the app; the Windows notification keeps its two-line layout.
                RecentBanner.Message = message.Application.Length > 0 ? $"{message.Application}  ·  {message.Destination}" : message.Body;
                RecentBanner.IsOpen = RecentAlerts.IsOn;
                if (DesktopAlerts.IsOn)
                    try { await desktopNotifications.ShowAsync(message); }
                    catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
                    { DesktopAlerts.IsOn = false; RecentBanner.Severity = InfoBarSeverity.Warning; RecentBanner.Message = $"Windows notification unavailable: {ex.Message}"; RecentBanner.IsOpen = true; }
            }
            archive.Traffic.AddRange(traffic.Observe(snapshot.Counters, now));
            ActivityStore.TrimInPlace(archive, now);
            LogChanged?.Invoke();
            if (Visibility != Visibility.Visible && !WindowMinimized) ActivityOverviewChanged?.Invoke();
            if ((Visibility == Visibility.Visible && !WindowMinimized) || OptionsVisible)
            {
                // Counter values are not displayed in the picker. Rebind only if its
                // names/IDs change, avoiding extra selection events and chart redraws.
                var previousAdapters = AdapterChoice.ItemsSource as IReadOnlyList<AdapterCounters>;
                if (previousAdapters is null || !previousAdapters.Select(x => (x.Id, x.Name)).SequenceEqual(snapshot.Counters.Select(x => (x.Id, x.Name))))
                {
                    string? adapterId = preferredAdapterId ?? (AdapterChoice.SelectedItem as AdapterCounters)?.Id;
                    updatingAdapters = true;
                    try
                    {
                        AdapterChoice.ItemsSource = snapshot.Counters;
                        // Default to the busiest adapter rather than whichever Windows lists first.
                        AdapterChoice.SelectedItem = snapshot.Counters.FirstOrDefault(x => x.Id == adapterId) ?? snapshot.Counters.MaxBy(x => x.Received + x.Sent);
                    }
                    finally { updatingAdapters = false; }
                }
                if (Visibility == Visibility.Visible && !WindowMinimized) UpdateLists(freshChanged);
                DrawTraffic();
            }
            MonitorStatus.Text = $"Updated {now.ToLocalTime():T} · {current.Count} endpoints · {archive.Events.Count} history events";
            CountryDataStatus.Text = enrichment is null ? "" : $"{enrichment.CountryDataStatus}. {enrichment.NetworkDataStatus}.";
            if (SaveHistory.IsOn && now - lastSaved > TimeSpan.FromSeconds(15)) await PersistAsync();
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException or System.Net.NetworkInformation.NetworkInformationException)
        { MonitorStatus.Text = $"Refresh failed; displayed data may be stale. {ex.Message}"; }
        finally { busy = false; if (closed) enrichment?.Dispose(); }
    }

    private void UpdateLists(bool force = true)
    {
        if (ConnectionList is null || HistoryList is null || GroupChoice is null || HistoryWindow is null) return;
        ActivityOverviewChanged?.Invoke();
        string query = MonitorSearch.Text ?? "";
        bool showUsage = usageEnabled;
        UpdateUsageHeader(showUsage);
        if (force || !ReferenceEquals(displayedConnections, current)) UpdateStats(query);
        if (history)
        {
            var selectedEvent = HistoryList.SelectedItem as ConnectionEvent;
            var since = HistorySince();
            // Names arrive asynchronously after an event was recorded. Enrich only
            // the displayed copy so activity storage never retains browsing names.
            var events = archive.Events.Where(x => x.Time >= since)
                .Select(x => enrichment is null ? x : x with { Connection = enrichment.WithDomainHint(x.Connection) })
                .Where(x => (TcpDetails.IsOn || !x.IsTcpDetail) && MatchesFilters(x.Connection, query)).Reverse().ToList();
            for (int i = 1; i < events.Count; i++)
                if (events[i].Time.ToLocalTime() is var time && events[i - 1].Time.ToLocalTime() is var newer
                    && time.Date == newer.Date && time.Hour == newer.Hour && time.Minute == newer.Minute)
                    events[i] = events[i] with { SameMinuteAsPrevious = true };
            // A stable refresh must retain native rows and scroll/selection state.
            // Asynchronous hostname hints still participate in record equality.
            if (HistoryList.ItemsSource is not IReadOnlyList<ConnectionEvent> shown || !shown.SequenceEqual(events))
            {
                HistoryList.ItemsSource = events;
                HistoryList.SelectedItem = selectedEvent is null ? null : events.FirstOrDefault(x => x.Time == selectedEvent.Time && x.Event == selectedEvent.Event && x.Connection.Identity == selectedEvent.Connection.Identity);
            }
            ShowEmpty(events.Count == 0, "No activity in this range", query.Length > 0 ? "Try another search, filter or range." : "New, changed and closed connections appear here as they happen.");
            return;
        }
        if (!force && ReferenceEquals(displayedConnections, current)) return;
        displayedConnections = current;
        string? selected = (ConnectionList.SelectedItem as ActivityRow)?.Key;
        var visible = current.Where(x => MatchesFilters(x, query)).ToList();
        var today = DateOnly.FromDateTime(DateTime.Now);
        activityTree.Update(visible, GroupChoice.SelectedIndex == 1, fresh: fresh, showUsage: showUsage,
            sort: usageSort, usageDisplay: key => usage.Display(key, today));
        if (!ReferenceEquals(ConnectionList.ItemsSource, activityTree.Rows)) ConnectionList.ItemsSource = activityTree.Rows;
        ConnectionList.SelectedItem = activityTree.Rows.FirstOrDefault(x => x.Key == selected);
        ShowEmpty(visible.Count == 0, CaptureCount == 0 ? "Reading Windows connection tables…" : "No matching connections",
            CaptureCount == 0 ? "" : query.Length > 0 ? "Try another search or filter." : "Change the filter to include local and listening endpoints.");
    }
    private void UpdateUsageHeader(bool enabled)
    {
        UsageHeaderColumn.Width = Presentation.Ui.UsageColumnWidth(enabled);
        UsageSortButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        UsageSortButton.Content = usageSort switch { UsageSort.Descending => "Usage today ↓", UsageSort.Ascending => "Usage today ↑", _ => "Usage today" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(UsageSortButton,
            usageSort == UsageSort.Descending ? "Usage today, sorted highest first. Sort lowest first" : "Usage today. Sort highest first");
    }
    private void UsageSortClicked(object sender, RoutedEventArgs args)
    {
        usageSort = usageSort == UsageSort.Descending ? UsageSort.Ascending : UsageSort.Descending;
        SavePreferences();
        RefreshUsageRows(usageEnabled);
    }
    private void AppSortClicked(object sender, RoutedEventArgs args) { usageSort = UsageSort.AppName; SavePreferences(); RefreshUsageRows(usageEnabled); }
    private void RefreshUsageRows(bool enabled)
    {
        UpdateUsageHeader(enabled);
        if (history) return;
        string? selected = (ConnectionList.SelectedItem as ActivityRow)?.Key;
        var today = DateOnly.FromDateTime(DateTime.Now);
        activityTree.ApplyUsage(enabled, usageSort, key => usage.Display(key, today));
        ConnectionList.SelectedItem = activityTree.Rows.FirstOrDefault(x => x.Key == selected);
    }
    private void UpdateStats(string query)
    {
        int shown = 0;
        var apps = new HashSet<string>(StringComparer.Ordinal);
        var countries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in current)
        {
            if (!MatchesFilters(entry, query)) continue;
            shown++; apps.Add(entry.AppKey);
            if (entry.CountryCode.Length == 2) countries.Add(entry.CountryCode);
        }
        StatApps.Text = CaptureCount == 0 ? "—" : apps.Count.ToString("N0");
        StatConnections.Text = CaptureCount == 0 ? "—" : shown.ToString("N0");
        StatCountries.Text = CaptureCount == 0 ? "—" : countries.Count.ToString("N0");
    }
    private void ShowEmpty(bool empty, string title, string hint)
    {
        EmptyList.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;
        EmptyTitle.Text = title; EmptyHint.Text = hint;
        EmptyHint.Visibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateSelectionActions();
    private void UpdateSelectionActions()
    {
        bool any = SelectedActionRow() is not null;
        DetailsButton.IsEnabled = any; BlockButton.IsEnabled = any;
    }
    private void SetLive(bool live)
    {
        LiveDot.Fill = live ? Presentation.Ui.Active : Presentation.Ui.Idle;
        LiveText.Text = live ? "Live" : "Paused";
        string tip = live ? "Pause live updates" : "Resume live updates";
        ToolTipService.SetToolTip(PauseButton, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PauseButton, tip);
    }

    private void AllUsageClicked(object sender, RoutedEventArgs args)
    {
        if (Owner is null) return;
        Presentation.ToolWindows.For(Owner).Show("all-usage", "Observed usage · last 30 days", () =>
        {
            var rows = usage.Days.OrderByDescending(x => x.Day).ThenBy(x => x.Path).Select(x => $"{x.Day:yyyy-MM-dd} · {Path.GetFileName(x.Path)}\n↓ {UsageLedger.Bytes(x.Received)}   ↑ {UsageLedger.Bytes(x.Sent)}\n{x.Path}").ToList();
            return rows.Count == 0 ? new TextBlock { Text = "No recorded usage. Enable usage to begin collecting. Totals cover collection time only.", TextWrapping = TextWrapping.Wrap }
                : new ListView { ItemsSource = rows, SelectionMode = ListViewSelectionMode.None, HorizontalContentAlignment = HorizontalAlignment.Stretch, ItemTemplate = (DataTemplate)Resources["UsageHistoryTemplate"] };
        }, scroll: false);
    }
    private async void DomainClicked(object sender, RoutedEventArgs args) => await ToggleDomainAsync();
    private void ReverseDnsChanged(object sender, RoutedEventArgs args)
    {
        if (!preferencesReady) return;
        enrichment?.SetReverseDnsEnabled(ReverseDnsLookup.IsOn);
        current = current.Select(x => enrichment?.WithDomainHint(x) ?? x).ToList();
        UpdateLists(); SavePreferences();
    }
    private async Task ToggleDomainAsync()
    {
        if (!DomainButton.IsEnabled) return;
        DomainButton.IsEnabled = false;
        try
        {
            while (busy && !closed) await Task.Delay(20);
            if (closed) return;
            if (domainClient is not null)
            {
                domainClient.Dispose(); domainClient = null; enrichment?.SetDomainReader(null);
                current = current.Select(x => enrichment?.WithDomainHint(x) ?? x).ToList();
                UpdateLists(); PublishVisibleSnapshot();
                domainCaptureStatus = "";
                DomainButton.Content = "Capture website names";
                DomainStatus.Text = "Website capture stopped. Windows may still provide cached names.";
                rememberDomains = false; SavePreferences();
                return;
            }
            var client = new FirewallClient();
            try
            {
                await client.StartAsync(domainsOnly: true);
                while (busy && !closed) await Task.Delay(20);
                if (closed) { client.Dispose(); return; }
                enrichment ??= new(Path.Combine(AppContext.BaseDirectory, "Assets", "Countries"));
                enrichment.SetDomainReader(wanted =>
                {
                    // Runs exclusively on the hint worker, never blocks UI capture.
                    var result = client.SendAsync(new("domains", RemoteAddresses: wanted.ToList())).GetAwaiter().GetResult();
                    if (!result.Success) { client.Dispose(); throw new IOException(result.Message); }
                    Volatile.Write(ref domainCaptureStatus, result.Message);
                    return result.Domains ?? [];
                });
                domainClient = client;
                rememberDomains = true; SavePreferences();
                DomainButton.Content = "Stop website capture";
                DomainStatus.Text = "Listening for DNS replies and reading Windows cached names. Open an app or website to collect new names.";
                _ = CaptureAsync();
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException or OperationCanceledException)
            { client.Dispose(); rememberDomains = false; SavePreferences(); DomainStatus.Text = $"Domain hints unavailable: {ex.Message}"; }
        }
        finally { DomainButton.IsEnabled = true; }
    }

    private async void UsageClicked(object sender, RoutedEventArgs args) => await ToggleUsageAsync();
    private async Task ToggleUsageAsync()
    {
        if (usageBusy) return;
        if (usageClient is not null)
        {
            usageTimer.Stop(); usageClient.Dispose(); usageClient = null; SaveUsage();
            usageEnabled = false;
            rememberUsage = false; SavePreferences();
            UsageButton.Content = "Enable usage"; UsageStatus.Text = "Usage paused. Saved totals are partial observed traffic."; UpdateLists(); return;
        }
        usageBusy = true; UsageButton.IsEnabled = false;
        var client = new FirewallClient();
        try
        {
            await client.StartAsync();
            var result = await client.SendAsync(new("usage-start"));
            if (!result.Success) throw new IOException(result.Message);
            if (closed) { client.Dispose(); return; }
            usageClient = client;
            usageEnabled = true;
            rememberUsage = true; SavePreferences();
            if (result.Usage is { } sample) { usage.Observe(sample); overviewUsage.Observe(sample); usageDirty = true; }
            UsageButton.Content = "Pause usage"; usageTimer.Start();
            UsageStatus.Text = "Observing TCP and UDP packet sizes. Counts start now and may be partial; VPNs/proxies affect attribution.";
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException or OperationCanceledException)
        { client.Dispose(); rememberUsage = false; SavePreferences(); UsageStatus.Text = $"Usage unavailable: {ex.Message}"; }
        finally { usageBusy = false; UsageButton.IsEnabled = true; UpdateLists(); }
    }
    private async Task CaptureUsageAsync()
    {
        if (usageBusy || closed || usageClient is null) return;
        usageBusy = true;
        try
        {
            var response = await usageClient.SendAsync(new("usage"));
            if (closed) return;
            if (!response.Success || response.Usage is null) throw new IOException(response.Message);
            var sample = response.Usage;
            usage.Observe(sample); overviewUsage.Observe(sample); usageDirty = true;
            ActivityOverviewChanged?.Invoke();
            UsageStatus.Text = $"Observed network traffic · {sample.Events:N0} events · {sample.BufferKiB / 1024d:0.0} MB trace buffers · {sample.LostEvents} lost events · {UsageLedger.Bytes(sample.UnattributedBytes)} unattributed · {sample.RejectedEvents} unsupported events. Totals cover collection time only.";
            if (sample.Events == 0) UsageStatus.Text += " Waiting for supported network events; coverage is unverified.";
            if (sample.LostEvents > 0 || sample.UnattributedBytes > 0 || sample.RejectedEvents > 0) UsageStatus.Text += " Accounting is incomplete.";
            if (!WindowMinimized && Visibility == Visibility.Visible) RefreshUsageRows(true);
            if (DateTimeOffset.UtcNow - lastUsageSaved > TimeSpan.FromSeconds(30)) SaveUsage();
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException or OperationCanceledException or System.Text.Json.JsonException)
        { usageTimer.Stop(); usageClient?.Dispose(); usageClient = null; usageEnabled = false; UsageButton.Content = "Enable usage"; UsageStatus.Text = $"Usage stopped: {ex.Message}"; RefreshUsageRows(false); }
        finally { usageBusy = false; }
    }
    private void SaveUsage()
    {
        if (!usageDirty) return; // Read-only diagnostics must never overwrite the live instance's totals.
        try { usageStore.Save(usage.Days); usageDirty = false; lastUsageSaved = DateTimeOffset.UtcNow; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { UsageStatus.Text = $"Could not save usage: {ex.Message}"; }
    }
    private void ActivityClicked(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is ActivityRow row) { activityTree.Toggle(row); UpdateLists(); }
    }
    private Window? optionsWindow;
    private bool OptionsVisible => optionsWindow is { AppWindow.IsVisible: true } && optionsWindow.AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized };
    private void OptionsClicked(object sender, RoutedEventArgs args)
    {
        if (Owner is null) return;
        optionsWindow = Presentation.ToolWindows.For(Owner).Show("monitor-options", "Monitor options", () =>
        {
            OptionsParking.Children.Remove(OptionsContent);
            return OptionsContent;
        }, scroll: false, closed: () =>
        {
            optionsWindow = null;
            OptionsParking.Children.Add(OptionsContent);
        });
        UpdateDomainStatus(); DrawTraffic();
    }
    private void UpdateDomainStatus()
    {
        int matched = current.Count(x => x.DomainHint.Length > 0);
        if (domainClient?.Connected == true)
        {
            DomainStatus.Text = Volatile.Read(ref domainCaptureStatus) + $" Names available for {matched} connections; updated while monitoring is live.";
            return;
        }
        string access = "";
        DomainStatus.Text = access + (enrichment?.DomainState switch
        {
            DomainCacheState.AccessDenied => "Windows denied cache access. Choose Capture website names and accept the Windows prompt for this session.",
            DomainCacheState.Unavailable => "Windows DNS cache could not be read. IP addresses remain available; lookup will retry automatically.",
            DomainCacheState.Reading => "Reading cached website names… Results appear after the next live refresh.",
            DomainCacheState.Ready when matched > 0 => $"Website hints found for {matched} connections. Names refresh about once a minute while live.",
            DomainCacheState.Ready => "No cached website names match the current connections. Open a website and allow up to a minute while live. Browsers using private DNS may not put names in the Windows cache.",
            _ => "Waiting for remote connections to match against the Windows DNS cache."
        });
    }
    private void OptionsSizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool narrow = args.NewSize.Width < 480;
        ArrangeOptions(ViewOptionsGrid, narrow ? 1 : 3);
        ArrangeOptions(AdapterOptionsGrid, narrow ? 1 : 2);
        ArrangeOptions(HistoryOptionsGrid, narrow ? 1 : 2);
        ArrangeOptions(NotificationOptionsGrid, narrow ? 1 : 2);
        ArrangeOptions(UsageOptionsGrid, narrow ? 1 : 2);
    }
    private static void ArrangeOptions(Grid grid, int columns)
    {
        if (grid is null) return;
        for (int i = 0; i < grid.ColumnDefinitions.Count; i++)
            grid.ColumnDefinitions[i].Width = i < columns ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        for (int i = 0; i < grid.Children.Count; i++)
        {
            Grid.SetColumn((FrameworkElement)grid.Children[i], i % columns);
            Grid.SetRow((FrameworkElement)grid.Children[i], i / columns);
        }
    }
    public void TogglePause() => PauseClicked(this, new RoutedEventArgs());
    private void SearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => UpdateLists();
    private bool IsVisibleApp(ConnectionEntry entry) => HideSystemApps?.IsOn != true || !systemApps.IsSystem(entry);
    private void PublishVisibleSnapshot() => SnapshotChanged?.Invoke(HideSystemApps.IsOn ? current.Where(IsVisibleApp).ToArray() : current);
    private bool MatchesFilters(ConnectionEntry entry, string query) => IsVisibleApp(entry) && MonitorFilter.Matches(entry, query, ScopeChoice?.SelectedIndex ?? 1, DirectionChoice?.SelectedIndex ?? 0);
    private async void HideSystemAppsChanged(object sender, RoutedEventArgs args)
    {
        if (ConnectionList is null || RecentBanner is null) return;
        // Discard queued banners/toasts so a pre-toggle batch cannot leak later.
        // Keep the baseline: the tracker still observes hidden connections, so
        // there is no backlog to replay and the next new app can alert normally.
        recent.ClearPending();
        RecentBanner.IsOpen = false;
        UpdateLists();
        PublishVisibleSnapshot();
        LogChanged?.Invoke();
        SavePreferences();
        if (HideSystemApps.IsOn)
            try { await desktopNotifications.ClearRecentAsync(); }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            { MonitorStatus.Text = $"Future system alerts are muted; Windows could not clear the previous notification: {ex.Message}"; }
    }
    private void TcpDetailsChanged(object sender, RoutedEventArgs args) { if (HistoryList is null) return; UpdateLists(); SavePreferences(); }
    private void RecentAlertsChanged(object sender, RoutedEventArgs args) { recent.ResetBaseline(); if (RecentBanner is not null) RecentBanner.IsOpen = false; SavePreferences(); }
    private void NotificationTypeChanged(object sender, SelectionChangedEventArgs args)
    {
        recent.ClearPending();
        if (RecentBanner is not null) RecentBanner.IsOpen = false;
        SavePreferences();
    }
    private void DesktopAlertsChanged(object sender, RoutedEventArgs args)
    {
        if (DesktopAlerts is null || MonitorStatus is null) return;
        recent.ResetBaseline();
        try { if (DesktopAlerts.IsOn) desktopNotifications.Enable(); else desktopNotifications.Dispose(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        { DesktopAlerts.IsOn = false; RecentBanner.Severity = InfoBarSeverity.Warning; RecentBanner.Message = $"Windows notifications could not be enabled: {ex.Message}"; RecentBanner.IsOpen = true; }
        SavePreferences();
    }
    private DateTimeOffset HistorySince() => DateTimeOffset.UtcNow - (HistoryWindow.SelectedIndex switch { 1 => TimeSpan.FromDays(1), 2 => TimeSpan.FromDays(7), _ => TimeSpan.FromHours(1) });
    private void MonitorOptionsChanged(object sender, SelectionChangedEventArgs args) { if (ConnectionList is null || HistoryList is null) return; UpdateLists(); DrawTraffic(); SavePreferences(); }
    private void PauseClicked(object sender, RoutedEventArgs args)
    {
        if (timer.IsEnabled) { timer.Stop(); SetLive(false); MonitorStatus.Text = "Paused · displayed connections are a snapshot."; }
        else { connections.Reset(); recent.ResetBaseline(); traffic.Reset(); baselineTaken = false; fresh = []; timer.Start(); SetLive(true); _ = CaptureAsync(); }
    }
    private Task PersistAsync()
    {
        // The snapshot cannot be mutated by a later UI refresh while the writer serializes it.
        var snapshot = new ActivityArchive(archive.Events.ToList(), archive.Traffic.ToList());
        try { store.Save(snapshot); lastSaved = DateTimeOffset.UtcNow; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MonitorStatus.Text = $"Could not save activity: {ex.Message}"; }
        return Task.CompletedTask;
    }
    private async void SaveHistoryChanged(object sender, RoutedEventArgs args)
    {
        SavePreferences();
        if (!started) return;
        if (SaveHistory.IsOn) await PersistAsync();
        else MonitorStatus.Text = "Saving is off. Existing saved history remains until you clear it.";
    }
    private async void ClearHistoryClicked(object sender, RoutedEventArgs args)
    {
        var dialog = new ContentDialog { XamlRoot = OptionsContent.XamlRoot, RequestedTheme = ActualTheme, Title = "Clear local activity?", Content = "This removes the displayed activity and the saved ZikWall activity file on this PC.", PrimaryButtonText = "Clear", CloseButtonText = "Cancel" };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try { store.Clear(); archive = new([], []); connections.Reset(); recent.ResetBaseline(); traffic.Reset(); baselineTaken = false; fresh = []; UpdateLists(); LogChanged?.Invoke(); DrawTraffic(); MonitorStatus.Text = "Local activity cleared."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MonitorStatus.Text = ex.Message; }
    }
    private async void DetailsClicked(object sender, RoutedEventArgs args) => await RunRowActionAsync(() => ShowDetailsAsync(SelectedActionRow()));
    private Task ShowDetailsAsync(ActivityRow? row)
    {
        if (Owner is null) return Task.CompletedTask;
        var windows = Presentation.ToolWindows.For(Owner);
        if (row is { Expandable: true } group)
        {
            var days = usage.Days.Where(x => x.AppKey == group.Entry.AppKey).OrderByDescending(x => x.Day).ToList();
            string totals = days.Count == 0 ? "No usage recorded. Enable usage to start observing traffic." : string.Join("\n", days.Select(x => $"{x.Day:yyyy-MM-dd}   ↓ {UsageLedger.Bytes(x.Received)}   ↑ {UsageLedger.Bytes(x.Sent)}"));
            windows.Text("app-details:" + group.Key, group.Title, group.Entry.ProcessPath + "\n\nObserved traffic · retained daily totals\n" + totals + "\n\nSnapshot when opened. Totals cover collection time only, include local traffic and can be incomplete. Expand a process and select a connection for IP details.");
            return Task.CompletedTask;
        }
        var item = row?.Entry;
        if (item is null) { ActionNotice("Select a connection or history event first.", true); return Task.CompletedTask; }
        windows.Show("connection:" + item.Key, "Connection details", () =>
        {
            var content = new StackPanel { Spacing = 16 };
            content.Children.Add(new TextBlock { Text = $"{item.ProcessLabel}\n{item.ProcessPath}\n\n{item.Protocol} · {item.State} · {item.Direction}\nLocal: {item.LocalEndpoint}\nRemote: {item.RemoteEndpoint}\nDomain: {item.DomainDescription}\nNetwork: {item.NetworkDescription}\nCountry: {item.CountryLabel}\nScope: {item.NetworkScope}\n\nSnapshot when opened. A connection snapshot does not establish the firewall decision or the hostname requested by the app.", IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap });
            if (item.ProcessPath.Length > 0)
            {
                var inspect = new Button { Content = "Inspect executable" };
                inspect.Click += async (_, _) => await RunRowActionAsync(() => InspectTargetAsync(item.ProcessPath));
                content.Children.Add(inspect);
            }
            if (item.RemotePort > 0)
            {
                var lookup = new Button { Content = "Reverse DNS lookup" };
                var status = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                lookup.Click += async (_, _) =>
                {
                    lookup.IsEnabled = false;
                    try
                    {
                        var result = await Dns.GetHostEntryAsync(IPAddress.Parse(item.RemoteAddress)).WaitAsync(TimeSpan.FromSeconds(3));
                        status.Text = $"{result.HostName}\n\nThis name comes from your DNS resolver. It is not proof of the domain the app requested and is not used for firewall decisions.";
                    }
                    catch (Exception ex) when (ex is System.Net.Sockets.SocketException or TimeoutException or ArgumentException)
                    { status.Text = $"No reverse-DNS result: {ex.Message}"; }
                    finally { lookup.IsEnabled = true; }
                };
                content.Children.Add(lookup); content.Children.Add(status);
            }
            return content;
        });
        return Task.CompletedTask;
    }

    private async void BlockAppClicked(object sender, RoutedEventArgs args)
    {
        if (SelectedActionRow() is not { } row) { ActionNotice("Select an app, process or connection first.", true); return; }
        var target = MonitorActionTarget.Capture(row, current);
        await RunRowActionAsync(() => BlockTargetAsync(target, false));
    }

    private async void ExportClicked(object sender, RoutedEventArgs args)
    {
        if (Owner is null) return;
        var picker = new FileSavePicker { SuggestedFileName = history ? "ZikWall-activity" : "ZikWall-connections" };
        picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(Owner));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        var rows = history ? archive.Events.Where(x => x.Time >= HistorySince() && MatchesFilters(x.Connection, MonitorSearch.Text)).Select(x => (x.Time.ToString("O"), x.Event, x.Connection))
            : current.Where(x => MatchesFilters(x, MonitorSearch.Text)).Select(x => (DateTimeOffset.UtcNow.ToString("O"), "Snapshot", x));
        static string Cell(string value) => "\"" + (value.Length > 0 && "=+-@\t\r".Contains(value[0]) ? "'" : "") + value.Replace("\"", "\"\"") + "\"";
        var csv = new StringBuilder("Time,Event,Application,PID,Protocol,Local,Remote,State,Path,Direction,CountryCode,Country,Scope,DomainHint,ReverseDnsHint,Network,ASN,HostNameSource\r\n");
        foreach (var (time, action, row) in rows) csv.AppendLine(string.Join(',', new[] { time, action, row.Application, row.ProcessId.ToString(), row.Protocol, row.LocalEndpoint, row.RemoteEndpoint, row.State, row.ProcessPath, row.Direction, row.CountryCode, row.CountryName, row.NetworkScope, row.DomainHint, row.ReverseDnsName, row.NetworkName, row.NetworkAsn > 0 ? $"AS{row.NetworkAsn}" : "", row.HostNameSource }.Select(Cell)));
        try { await Windows.Storage.FileIO.WriteTextAsync(file, csv.ToString()); MonitorStatus.Text = $"Exported {file.Name}."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MonitorStatus.Text = $"Export failed: {ex.Message}"; }
    }

    private void AdapterChanged(object sender, SelectionChangedEventArgs args)
    {
        if (updatingAdapters) return;
        preferredAdapterId = (AdapterChoice.SelectedItem as AdapterCounters)?.Id;
        SavePreferences(); DrawTraffic();
    }
    private void ChartSizeChanged(object sender, SizeChangedEventArgs args) => DrawTraffic();
    private void DrawTraffic()
    {
        if (TrafficChart is null || Sparkline is null) return;
        var adapter = AdapterChoice.SelectedItem as AdapterCounters;
        string? id = adapter?.Id;
        var recentSamples = archive.Traffic.Where(x => x.AdapterId == id).TakeLast(60).ToList();
        TrafficAdapter.Text = adapter?.Name ?? "Network traffic";
        ToolTipService.SetToolTip(TrafficAdapter, adapter is null ? null : $"{adapter.Name} · whole-adapter totals. Change the adapter in Options.");
        if (recentSamples.LastOrDefault() is { } latest) { RateDown.Text = TrafficSample.Rate(latest.Download); RateUp.Text = TrafficSample.Rate(latest.Upload); }
        else RateDown.Text = RateUp.Text = "—";
        Plot(recentSamples, Sparkline, SparkDown, SparkUp);
        if (!OptionsVisible) return;
        var since = HistorySince();
        var samples = archive.Traffic.Where(x => x.AdapterId == id && x.Time >= since).ToList();
        // Bucket older samples instead of dropping the requested history window. Preserve peaks.
        int bucket = Math.Max(1, (int)Math.Ceiling(samples.Count / 120.0));
        var points = samples.Chunk(bucket).Select(x => new TrafficSample(x[0].Time, x[0].AdapterId, x[0].AdapterName, x.Max(s => s.Download), x.Max(s => s.Upload))).ToList();
        double peak = Plot(points, TrafficChart, DownloadLine, UploadLine);
        ChartScale.Text = $"Peak {TrafficSample.Rate(peak)} · {samples.Count} samples" + (samples.Count > 1 ? $" · {samples[0].Time.ToLocalTime():g} – {samples[^1].Time.ToLocalTime():t}" : "");
    }
    // Scales both series to a shared peak and fits them to the canvas; returns that peak.
    private static double Plot(IReadOnlyList<TrafficSample> points, Canvas canvas, Polyline download, Polyline upload)
    {
        double peak = Math.Max(1, points.Select(x => Math.Max(x.Download, x.Upload)).DefaultIfEmpty().Max());
        double width = Math.Max(0, canvas.ActualWidth), span = canvas.Height - 4;
        double seconds = points.Count > 1 ? Math.Max(1, (points[^1].Time - points[0].Time).TotalSeconds) : 1;
        var down = new PointCollection(); var up = new PointCollection();
        foreach (var point in points)
        {
            double x = (point.Time - points[0].Time).TotalSeconds * width / seconds;
            down.Add(new Point(x, 2 + span - point.Download / peak * span)); up.Add(new Point(x, 2 + span - point.Upload / peak * span));
        }
        download.Points = down; upload.Points = up;
        return peak;
    }
}
