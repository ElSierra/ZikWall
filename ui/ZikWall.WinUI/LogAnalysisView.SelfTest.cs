using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI;

public sealed partial class MonitoringView
{
    internal void LoadSavedLogFixture() => archive = store.Load();
    internal void SetLogFixture()
    {
        var local = DateTimeOffset.Now;
        var now = new DateTimeOffset(local.Date.AddHours(12), local.Offset);
        var browser = new ConnectionEntry("TCPv4", "192.168.1.2", 50000, "8.8.8.8", 443, Environment.ProcessId,
            "Established", "Example browser", Environment.ProcessPath!, "Outgoing", "US", "United States", "Internet", DomainHint: "dns.google");
        archive = new([
            new(now.AddMinutes(-1), "First seen", browser),
            new(now.AddMinutes(-2), "First seen", browser with { ProcessName = "Editor", ProcessPath = @"C:\Fixture\editor.exe", DomainHint = "updates.example" }),
            new(now.AddMinutes(-3), "SynSent → Established", browser),
            new(now.AddMinutes(-4), "No longer observed", browser),
            new(new DateTimeOffset(now.Date.AddDays(-1).AddHours(12), now.Offset), "First seen", browser with { ProcessName = "Yesterday app", ProcessPath = @"C:\Fixture\yesterday.exe" })
        ], []);
    }
}

public sealed partial class LogAnalysisView
{
    private static async Task SavePreviewAsync(UIElement element, string name)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var file = await (await StorageFolder.GetFolderFromPathAsync(AppContext.BaseDirectory)).CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, (await bitmap.GetPixelsAsync()).ToArray());
        await encoder.FlushAsync();
    }

    internal async Task ExerciseAsync()
    {
        // RenderTargetBitmap cannot capture the window's Mica backdrop.
        ((Grid)Content).Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(ActualTheme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 250, 250, 250));
        readThrough = DateTimeOffset.MinValue;
        CategoryChoice.SelectedIndex = (int)LogFilter.All;
        Refresh();
        await Task.Delay(200);
        var groups = (IReadOnlyList<LogDayGroup>)GroupedEvents.Source;
        if (groups.Count != 2 || groups[0].Title != "Today" || groups[1].Title != "Yesterday" || groups.Sum(x => x.Count) != 5)
            throw new InvalidOperationException("Timeline must render both date groups and all fixture events.");
        if (EventList.ContainerFromItem(groups[0][0]) is not ListViewItem)
            throw new InvalidOperationException("Grouped timeline failed to realize native event rows.");
        var clicked = groups[0].First(x => x.IsUnread);
        int unreadBefore = groups.SelectMany(x => x).Count(x => x.IsUnread);
        var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(
            (ListViewItem)EventList.ContainerFromItem(clicked));
        var invoke = peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)
            as Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider
            ?? throw new InvalidOperationException("Timeline row must support native invocation.");
        invoke.Invoke();
        await Task.Delay(400);
        var detail = Presentation.ToolWindows.For(Owner!).Open.LastOrDefault(x => x.Title.StartsWith(clicked.CategoryLabel, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Clicking an event must open its detail window.");
        await SavePreviewAsync((UIElement)detail.Content, "log-detail-preview.png");
        var other = ((IReadOnlyList<LogDayGroup>)GroupedEvents.Source).SelectMany(x => x).First(x => x.Source != clicked.Source);
        var otherPeer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(
            (ListViewItem)EventList.ContainerFromItem(other));
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)otherPeer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        await Task.Delay(200);
        if (Presentation.ToolWindows.For(Owner!).Open.Contains(detail))
            throw new InvalidOperationException("Opening a second log must close the previous log detail window.");
        foreach (var opened in Presentation.ToolWindows.For(Owner!).Open.ToArray()) opened.Close();
        await ExerciseLogActionsAsync(clicked, other);
        readState = LogReadState.Empty(); readState.MarkRead(clicked.Source); SaveReadState(); Refresh();
        groups = (IReadOnlyList<LogDayGroup>)GroupedEvents.Source;
        if (groups.SelectMany(x => x).Single(x => x.Source == clicked.Source).IsUnread ||
            groups.SelectMany(x => x).Count(x => x.IsUnread) != unreadBefore - 1)
            throw new InvalidOperationException("Clicking an event must mark only that event read and refresh the timeline.");
        readState = readStore.LoadState();
        Refresh();
        groups = (IReadOnlyList<LogDayGroup>)GroupedEvents.Source;
        if (groups.SelectMany(x => x).Single(x => x.Source == clicked.Source).IsUnread)
            throw new InvalidOperationException("Clicked read state must survive reloading from disk.");
        UnreadOnly.IsChecked = true;
        Refresh();
        if (((IReadOnlyList<LogDayGroup>)GroupedEvents.Source).SelectMany(x => x).Any(x => x.Source == clicked.Source))
            throw new InvalidOperationException("Clicked entry must disappear from unread-only results.");
        UnreadOnly.IsChecked = false;
        LogSearch.Text = "Editor";
        Refresh();
        if (((IReadOnlyList<LogDayGroup>)GroupedEvents.Source).Sum(x => x.Count) != 1)
            throw new InvalidOperationException("Timeline search did not filter the native collection.");
        LogSearch.Text = "";
        RangeChoice.SelectedIndex = (int)LogRange.Today;
        Refresh();
        if (((IReadOnlyList<LogDayGroup>)GroupedEvents.Source).Sum(x => x.Count) != 4)
            throw new InvalidOperationException("Today filter included yesterday's event.");
        MarkReadClicked(MarkReadButton, new RoutedEventArgs());
        readState = readStore.LoadState();
        UnreadOnly.IsChecked = true;
        Refresh();
        if (EmptyState.Visibility != Visibility.Visible || MarkReadButton.IsEnabled)
            throw new InvalidOperationException("Read watermark failed to clear unread results.");
        UnreadOnly.IsChecked = false;
        RangeChoice.SelectedIndex = (int)LogRange.All;
        readThrough = DateTimeOffset.MinValue;
        Refresh();
        await Task.Delay(200);
        await SavePreviewAsync(this, "log-analysis-preview.png");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "log-analysis-selftest.txt"), "PASS: native log detail replacement, action buttons, pointer/keyboard context targeting, pinned block targets, unavailable action guards and visible feedback; no firewall changes. Native row invocation marks only the clicked event read, saved read state reloads, mark-all persists, date grouping, realized rows, app search, today range, unread filtering and timeline render.");
        await ExerciseSavedHistoryAsync();
    }

    private async Task ExerciseSavedHistoryAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "zikwall-saved-log-ui-" + Guid.NewGuid());
        var originalMonitor = monitor!;
        MonitoringView? savedMonitor = null;
        try
        {
            var now = DateTimeOffset.UtcNow.AddMinutes(-1);
            var app = new ConnectionEntry("TCPv4", "127.0.0.1", 1234, "192.0.2.1", 443, 10,
                "Established", "Saved browser", @"C:\Fixture\saved-browser.exe");
            var yesterday = new ConnectionEvent(now.AddDays(-1), "First seen", app);
            new Services.ActivityStore(directory).Save(new(new[] { yesterday }.Concat(
                Enumerable.Range(0, Services.ActivityStore.MaximumEvents + 5)
                    .Select(i => new ConnectionEvent(now, "First seen", app with { LocalPort = i + 2000 }))).ToList(), []));
            savedMonitor = new MonitoringView(directory);
            savedMonitor.LoadSavedLogFixture();
            if (savedMonitor.LogEvents.Contains(yesterday)) throw new InvalidOperationException("Saved log fixture must exceed the live cache.");
            Connect(savedMonitor);
            readState = LogReadState.Empty();
            CategoryChoice.SelectedIndex = (int)LogFilter.Important;
            RangeChoice.SelectedIndex = (int)LogRange.Week;
            Refresh(); await savedQuery;
            var rows = ((IReadOnlyList<LogDayGroup>)GroupedEvents.Source).SelectMany(x => x).ToArray();
            if (rows.Length != 1 || rows[0].Source != yesterday || !LogStatus.Text.Contains("Saved history"))
                throw new InvalidOperationException("Native log view did not restore yesterday's archived observation.");
            await Task.Delay(200);
            await SavePreviewAsync(this, "saved-log-history-preview.png");
            CategoryChoice.SelectedIndex = (int)LogFilter.All;
            Refresh(); await savedQuery;
            var firstPage = ((IReadOnlyList<LogDayGroup>)GroupedEvents.Source).SelectMany(x => x).Select(x => x.Source).ToHashSet();
            if (!OlderButton.IsEnabled || firstPage.Count != LogAnalysis.DisplayLimit)
                throw new InvalidOperationException("Native archive paging was not available.");
            OlderClicked(OlderButton, new RoutedEventArgs()); await savedQuery;
            if (!NewerButton.IsEnabled || ((IReadOnlyList<LogDayGroup>)GroupedEvents.Source).SelectMany(x => x).Any(x => firstPage.Contains(x.Source)))
                throw new InvalidOperationException("Native older-page navigation repeated events.");
            NewerClicked(NewerButton, new RoutedEventArgs()); await savedQuery;
            if (!firstPage.SetEquals(((IReadOnlyList<LogDayGroup>)GroupedEvents.Source).SelectMany(x => x).Select(x => x.Source)))
                throw new InvalidOperationException("Native newer-page navigation lost its position.");
            EventList.UpdateLayout();
            AttachScrollViewer();
            var scroll = timelineScroll ?? throw new InvalidOperationException("Saved timeline has no scroll viewer.");
            scroll.ChangeView(null, 1200, null, true);
            await Task.Delay(200);
            lastQueryStarted = DateTimeOffset.MinValue;
            var idleQuery = savedQuery;
            LogsChanged();
            if (scroll.VerticalOffset < 1000 || !ReferenceEquals(idleQuery, savedQuery))
                throw new InvalidOperationException("Automatic archive scans must pause while reading below the top.");
            scroll.ChangeView(null, 0, null, true);
            await Task.Delay(200);
            string statusBefore = LogStatus.Text;
            var displayedRow = timeline[0][0];
            Refresh(automatic: true);
            var pendingQuery = queryCancellation ?? throw new InvalidOperationException("Automatic query did not start.");
            scroll.ChangeView(null, 1200, null, true);
            await Task.Delay(50);
            bool canceled = pendingQuery.IsCancellationRequested;
            await savedQuery;
            if (!canceled || !OlderButton.IsEnabled || LogStatus.Text != statusBefore || !ReferenceEquals(displayedRow, timeline[0][0]))
                throw new InvalidOperationException("Scrolling must cancel a pending automatic scan without disabling paging or replacing rows.");
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "log-analysis-selftest.txt"),
                "\nPASS: the native saved-history query restores yesterday beyond the live cache and pages older/newer events without repeats; reading pauses automatic scans and scrolling cancels pending scans while retaining usable paging controls.");
        }
        finally
        {
            queryCancellation?.Cancel();
            await savedQuery;
            Connect(originalMonitor);
            savedMonitor?.Stop();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}

public sealed partial class MainWindow
{
    internal async Task ExerciseLogAnalysisAsync()
    {
        await MonitorPanel.PauseForDiagnosticAsync();
        Root.RequestedTheme = Environment.GetCommandLineArgs().Contains("--test-dark") ? ElementTheme.Dark : ElementTheme.Light;
        MonitorPanel.SetLogFixture();
        Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(x => x.Tag?.ToString() == "logs");
        await (LogPanel ?? throw new InvalidOperationException("Log analysis page did not load on navigation.")).ExerciseAsync();
    }
}
