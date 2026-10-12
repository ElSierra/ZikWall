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
    private readonly Presentation.DesktopNotifications desktopNotifications = new();
    private MonitorEnrichment? enrichment;
    private FirewallClient? usageClient;
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
                i % 2 == 0 ? "Outgoing" : "Incoming", "US", "United States", "Internet", $"Fixture{i / 10}_family", i / 3)).ToList();
            UpdateLists();
            if (iteration % 4 == 0 && activityTree.Rows.FirstOrDefault(x => x.Expandable) is { } group) { activityTree.Toggle(group); UpdateLists(); }
            RecentBanner.Message = "Fixture connection batch";
            RecentBanner.IsOpen = iteration % 20 < 10;
            await Task.Delay(20);
        }
    }

    public MonitoringView()
    {
        InitializeComponent();
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
        }
        UpdateLists();
    }

    public void Stop() { closed = true; usageTimer.Stop(); usageClient?.Dispose(); SaveUsage(); timer.Stop(); desktopNotifications.Dispose(); if (!busy) enrichment?.Dispose(); if (SaveHistory.IsOn) _ = PersistAsync(); }
    public void SetPageActive(bool active) { pageActive = active; UpdateInterval(); }
    private void RefreshIntervalChanged(object sender, SelectionChangedEventArgs args) => UpdateInterval();
    private void UpdateInterval() => timer.Interval = TimeSpan.FromSeconds(!pageActive || WindowMinimized ? 15 : RefreshChoice?.SelectedIndex switch { 0 => 2, 2 => 10, _ => 5 });

    private async Task CaptureAsync()
    {
        if (busy || closed || !timer.IsEnabled) return;
        UpdateInterval();
        busy = true;
        try
        {
            var snapshot = await Task.Run(() =>
            {
                enrichment ??= new(Path.Combine(AppContext.BaseDirectory, "Assets", "Countries"));
                return (Connections: enrichment.Enrich(ConnectionMonitor.Capture()), Counters: ConnectionMonitor.CaptureTraffic());
            });
            if (closed || !timer.IsEnabled) return;
            var now = DateTimeOffset.UtcNow;
            var next = snapshot.Connections.OrderBy(x => x.Application, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.RemoteAddress).ThenBy(x => x.LocalPort).ThenBy(x => x.Protocol).ThenBy(x => x.RemotePort).ThenBy(x => x.ProcessId).ThenBy(x => x.LocalAddress).ToList();
            if (!current.SequenceEqual(next)) current = next;
            CaptureCount++;
            SnapshotChanged?.Invoke(current);
            var observed = connections.Observe(current, now);
            archive.Events.AddRange(observed);
            var nextFresh = baselineTaken ? observed.Where(x => x.Event == "First seen").Select(x => x.Connection.Key).ToHashSet() : [];
            bool freshChanged = fresh.Count > 0 || nextFresh.Count > 0;
            fresh = nextFresh; baselineTaken = true;
            if (recent.Observe(observed, now, RecentAlerts.IsOn || DesktopAlerts.IsOn) is { } batch)
            {
                var first = batch.Connections[0];
                string message = $"{first.Application} · {first.Direction} · {first.RemoteEndpoint} · {first.CountryLabel}";
                if (batch.Count > 1) message += $" · +{batch.Count - 1} more (see Activity)";
                RecentBanner.Title = "New connections";
                RecentBanner.Severity = InfoBarSeverity.Informational;
                RecentBanner.Message = message;
                RecentBanner.IsOpen = RecentAlerts.IsOn;
                if (DesktopAlerts.IsOn)
                    try { desktopNotifications.Show(message); }
                    catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
                    { DesktopAlerts.IsOn = false; RecentBanner.Severity = InfoBarSeverity.Warning; RecentBanner.Message = $"Windows notification unavailable: {ex.Message}"; RecentBanner.IsOpen = true; }
            }
            archive.Traffic.AddRange(traffic.Observe(snapshot.Counters, now));
            ActivityStore.TrimInPlace(archive, now);
            if (Visibility == Visibility.Visible && !WindowMinimized)
            {
                // Counter values are not displayed in the picker. Rebind only if its
                // names/IDs change, avoiding extra selection events and chart redraws.
                var previousAdapters = AdapterChoice.ItemsSource as IReadOnlyList<AdapterCounters>;
                if (previousAdapters is null || !previousAdapters.Select(x => (x.Id, x.Name)).SequenceEqual(snapshot.Counters.Select(x => (x.Id, x.Name))))
                {
                    string? adapterId = (AdapterChoice.SelectedItem as AdapterCounters)?.Id;
                    updatingAdapters = true;
                    try
                    {
                        AdapterChoice.ItemsSource = snapshot.Counters;
                        // Default to the busiest adapter rather than whichever Windows lists first.
                        AdapterChoice.SelectedItem = snapshot.Counters.FirstOrDefault(x => x.Id == adapterId) ?? snapshot.Counters.MaxBy(x => x.Received + x.Sent);
                    }
                    finally { updatingAdapters = false; }
                }
                UpdateLists(freshChanged);
                DrawTraffic();
                if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) ((Microsoft.UI.Xaml.Media.Animation.Storyboard)Resources["LivePulse"]).Begin();
            }
            MonitorStatus.Text = $"Updated {now.ToLocalTime():T} · {current.Count} endpoints · {archive.Events.Count} history events";
            CountryDataStatus.Text = enrichment?.CountryDataStatus ?? "";
            if (SaveHistory.IsOn && now - lastSaved > TimeSpan.FromSeconds(15)) await PersistAsync();
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException or System.Net.NetworkInformation.NetworkInformationException)
        { MonitorStatus.Text = $"Refresh failed; displayed data may be stale. {ex.Message}"; }
        finally { busy = false; if (closed) enrichment?.Dispose(); }
    }

    private void UpdateLists(bool force = true)
    {
        if (ConnectionList is null || HistoryList is null || GroupChoice is null || HistoryWindow is null) return;
        string query = MonitorSearch.Text ?? "";
        if (force || !ReferenceEquals(displayedConnections, current)) UpdateStats(query);
        if (history)
        {
            var selectedEvent = HistoryList.SelectedItem as ConnectionEvent;
            var since = HistorySince();
            var events = archive.Events.Where(x => x.Time >= since && MatchesFilters(x.Connection, query)).Reverse().ToList();
            HistoryList.ItemsSource = events;
            HistoryList.SelectedItem = events.Contains(selectedEvent!) ? selectedEvent : null;
            ShowEmpty(events.Count == 0, "No activity in this range", query.Length > 0 ? "Try another search, filter or range." : "New, changed and closed connections appear here as they happen.");
            return;
        }
        if (!force && ReferenceEquals(displayedConnections, current)) return;
        displayedConnections = current;
        string? selected = (ConnectionList.SelectedItem as ActivityRow)?.Key;
        var visible = current.Where(x => MatchesFilters(x, query)).ToList();
        activityTree.Update(visible, GroupChoice.SelectedIndex == 1, UsageLabels, fresh);
        if (!ReferenceEquals(ConnectionList.ItemsSource, activityTree.Rows)) ConnectionList.ItemsSource = activityTree.Rows;
        ConnectionList.SelectedItem = activityTree.Rows.FirstOrDefault(x => x.Key == selected);
        ShowEmpty(visible.Count == 0, CaptureCount == 0 ? "Reading Windows connection tables…" : "No matching connections",
            CaptureCount == 0 ? "" : query.Length > 0 ? "Try another search or filter." : "Change the filter to include local and listening endpoints.");
    }
    // Usage labels stay empty until usage is collected; the Options flyout explains how.
    private (string Rate, string Total) UsageLabels(string key)
    {
        bool active = usageClient?.Connected == true;
        var (rate, total) = usage.Labels(key, active, DateOnly.FromDateTime(DateTime.Now));
        return (active && rate != "—" ? rate : "", total == "No usage recorded" ? "" : total);
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

    private async void AllUsageClicked(object sender, RoutedEventArgs args)
    {
        var list = new ListView { MaxHeight = 440, MinWidth = 500, SelectionMode = ListViewSelectionMode.None };
        list.ItemsSource = usage.Days.OrderByDescending(x => x.Day).ThenBy(x => x.Path).Select(x => $"{x.Day:yyyy-MM-dd} · {Path.GetFileName(x.Path)}\n↓ {UsageLedger.Bytes(x.Received)}   ↑ {UsageLedger.Bytes(x.Sent)}\n{x.Path}").ToList();
        await new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = "Observed usage · last 30 days", Content = list.Items.Count == 0 ? "No recorded usage. Enable usage to begin collecting. Totals cover collection time only." : list, CloseButtonText = "Close" }.ShowAsync();
    }
    private async void UsageClicked(object sender, RoutedEventArgs args)
    {
        if (usageBusy) return;
        if (usageClient is not null)
        {
            usageTimer.Stop(); usageClient.Dispose(); usageClient = null; SaveUsage();
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
            if (result.Usage is { } sample) { usage.Observe(sample); usageDirty = true; }
            UsageButton.Content = "Pause usage"; usageTimer.Start();
            UsageStatus.Text = "Observing TCP and UDP packet sizes. Counts start now and may be partial; VPNs/proxies affect attribution.";
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException or OperationCanceledException)
        { client.Dispose(); UsageStatus.Text = $"Usage unavailable: {ex.Message}"; }
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
            usage.Observe(sample); usageDirty = true;
            UsageStatus.Text = $"Observed network traffic · {sample.Events:N0} events · {sample.BufferKiB / 1024d:0.0} MB trace buffers · {sample.LostEvents} lost events · {UsageLedger.Bytes(sample.UnattributedBytes)} unattributed · {sample.RejectedEvents} unsupported events. Totals cover collection time only.";
            if (sample.Events == 0) UsageStatus.Text += " Waiting for supported network events; coverage is unverified.";
            if (sample.LostEvents > 0 || sample.UnattributedBytes > 0 || sample.RejectedEvents > 0) UsageStatus.Text += " Accounting is incomplete.";
            if (!WindowMinimized && Visibility == Visibility.Visible) UpdateLists();
            if (DateTimeOffset.UtcNow - lastUsageSaved > TimeSpan.FromSeconds(30)) SaveUsage();
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException or OperationCanceledException or System.Text.Json.JsonException)
        { usageTimer.Stop(); usageClient?.Dispose(); usageClient = null; UsageButton.Content = "Enable usage"; UsageStatus.Text = $"Usage stopped: {ex.Message}"; }
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
    private void OptionsOpened(object sender, object args) => DrawTraffic();
    public void TogglePause() => PauseClicked(this, new RoutedEventArgs());
    private void SearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => UpdateLists();
    private bool MatchesFilters(ConnectionEntry entry, string query) => MonitorFilter.Matches(entry, query, ScopeChoice?.SelectedIndex ?? 1, DirectionChoice?.SelectedIndex ?? 0);
    private void RecentAlertsChanged(object sender, RoutedEventArgs args) { recent.ResetBaseline(); if (RecentBanner is not null) RecentBanner.IsOpen = false; }
    private void DesktopAlertsChanged(object sender, RoutedEventArgs args)
    {
        if (DesktopAlerts is null || MonitorStatus is null) return;
        recent.ResetBaseline();
        try { if (DesktopAlerts.IsOn) desktopNotifications.Enable(); else desktopNotifications.Dispose(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        { DesktopAlerts.IsOn = false; RecentBanner.Severity = InfoBarSeverity.Warning; RecentBanner.Message = $"Windows notifications could not be enabled: {ex.Message}"; RecentBanner.IsOpen = true; }
    }
    private DateTimeOffset HistorySince() => DateTimeOffset.UtcNow - (HistoryWindow.SelectedIndex switch { 1 => TimeSpan.FromDays(1), 2 => TimeSpan.FromDays(7), _ => TimeSpan.FromHours(1) });
    private void MonitorOptionsChanged(object sender, SelectionChangedEventArgs args) { if (ConnectionList is null || HistoryList is null) return; UpdateLists(); DrawTraffic(); }
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
        if (!started) return;
        if (SaveHistory.IsOn) await PersistAsync();
        else MonitorStatus.Text = "Saving is off. Existing saved history remains until you clear it.";
    }
    private async void ClearHistoryClicked(object sender, RoutedEventArgs args)
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = "Clear local activity?", Content = "This removes the displayed activity and the saved ZikWall activity file on this PC.", PrimaryButtonText = "Clear", CloseButtonText = "Cancel" };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try { store.Clear(); archive = new([], []); connections.Reset(); recent.ResetBaseline(); traffic.Reset(); baselineTaken = false; fresh = []; UpdateLists(); DrawTraffic(); MonitorStatus.Text = "Local activity cleared."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MonitorStatus.Text = ex.Message; }
    }
    private async void DetailsClicked(object sender, RoutedEventArgs args) => await RunRowActionAsync(() => ShowDetailsAsync(SelectedActionRow()));
    private async Task ShowDetailsAsync(ActivityRow? row)
    {
        if (row is { Expandable: true } group)
        {
            var days = usage.Days.Where(x => x.AppKey == group.Entry.AppKey).OrderByDescending(x => x.Day).ToList();
            string totals = days.Count == 0 ? "No usage recorded. Enable usage to start observing traffic." : string.Join("\n", days.Select(x => $"{x.Day:yyyy-MM-dd}   ↓ {UsageLedger.Bytes(x.Received)}   ↑ {UsageLedger.Bytes(x.Sent)}"));
            await new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = group.Title, Content = new TextBlock { Text = group.Entry.ProcessPath + "\n\nObserved traffic · retained daily totals\n" + totals + "\n\nTotals cover collection time only, include local traffic and can be incomplete. Expand a process and select a connection for IP details.", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 560 }, CloseButtonText = "Close" }.ShowAsync();
            return;
        }
        var item = row?.Entry;
        if (item is null) { ActionNotice("Select a connection or history event first.", true); return; }
        var details = new TextBlock { Text = $"{item.ProcessLabel}\n{item.ProcessPath}\n\n{item.Protocol} · {item.State} · {item.Direction}\nLocal: {item.LocalEndpoint}\nRemote: {item.RemoteEndpoint}\nCountry: {item.CountryLabel}\nScope: {item.NetworkScope}\n\nA connection snapshot does not establish the firewall decision or the hostname requested by the app.", IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
        var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = "Connection details", Content = details, CloseButtonText = "Close", PrimaryButtonText = item.RemotePort is > 0 ? "Reverse DNS lookup" : "", SecondaryButtonText = item.ProcessPath.Length > 0 ? "Inspect executable" : "" };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Secondary)
        {
            try
            {
                var identity = await ExecutableIdentity.InspectAsync(item.ProcessPath);
                await new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = "Executable identity", Content = new TextBlock { Text = $"{identity.Path}\n\n{identity.TrustDescription}\n\nSHA-256: {identity.Sha256}\n\nThis describes the file at inspection time. Current WFP rules match paths, not signatures or hashes.", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 520 }, CloseButtonText = "Close" }.ShowAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { MonitorStatus.Text = $"Could not inspect executable: {ex.Message}"; }
            return;
        }
        if (result != ContentDialogResult.Primary) return;
        try
        {
            var lookup = await Dns.GetHostEntryAsync(IPAddress.Parse(item.RemoteAddress)).WaitAsync(TimeSpan.FromSeconds(3));
            await new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = "Reverse DNS hint", Content = $"{item.RemoteAddress}\n{lookup.HostName}\n\nThis name comes from your DNS resolver. It is not proof of the domain the app requested and is not used for firewall decisions.", CloseButtonText = "Close" }.ShowAsync();
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or TimeoutException or ArgumentException)
        { MonitorStatus.Text = $"No reverse-DNS result: {ex.Message}"; }
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
        var csv = new StringBuilder("Time,Event,Application,PID,Protocol,Local,Remote,State,Path,Direction,CountryCode,Country,Scope\r\n");
        foreach (var (time, action, row) in rows) csv.AppendLine(string.Join(',', new[] { time, action, row.Application, row.ProcessId.ToString(), row.Protocol, row.LocalEndpoint, row.RemoteEndpoint, row.State, row.ProcessPath, row.Direction, row.CountryCode, row.CountryName, row.NetworkScope }.Select(Cell)));
        try { await Windows.Storage.FileIO.WriteTextAsync(file, csv.ToString()); MonitorStatus.Text = $"Exported {file.Name}."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MonitorStatus.Text = $"Export failed: {ex.Message}"; }
    }

    private void AdapterChanged(object sender, SelectionChangedEventArgs args) { if (!updatingAdapters) DrawTraffic(); }
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
        if (MonitorOptions?.IsOpen != true) return;
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
