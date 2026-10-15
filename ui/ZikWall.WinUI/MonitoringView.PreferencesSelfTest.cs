namespace ZikWall.WinUI;

public sealed partial class MonitoringView
{
    internal async Task ExercisePreferencesAsync()
    {
        await PauseForDiagnosticAsync();
        string directory = Path.Combine(AppContext.BaseDirectory, "preferences-diagnostic", Guid.NewGuid().ToString("N"));
        var first = new MonitoringView(directory);
        first.GroupChoice.SelectedIndex = 1;
        first.DirectionChoice.SelectedIndex = 2;
        first.ScopeChoice.SelectedIndex = 0;
        first.RefreshChoice.SelectedIndex = 2;
        first.HistoryWindow.SelectedIndex = 2;
        first.RecentAlerts.IsOn = false;
        first.NotificationTypeChoice.SelectedIndex = 1;
        first.SaveHistory.IsOn = true;
        first.UsageSortClicked(first, new Microsoft.UI.Xaml.RoutedEventArgs());
        // Construct a fresh view before Stop: settings must be durable as soon as changed.
        var reopened = new MonitoringView(directory);
        if (reopened.GroupChoice.SelectedIndex != 1 || reopened.DirectionChoice.SelectedIndex != 2 ||
            reopened.ScopeChoice.SelectedIndex != 0 || reopened.RefreshChoice.SelectedIndex != 2 ||
            reopened.HistoryWindow.SelectedIndex != 2 || reopened.RecentAlerts.IsOn || !reopened.SaveHistory.IsOn ||
            reopened.usageSort != Models.UsageSort.Descending || reopened.NotificationTypeChoice.SelectedIndex != 1)
            throw new InvalidOperationException("Changed monitor settings reset when a fresh view is opened.");
        first.SaveHistory.IsOn = reopened.SaveHistory.IsOn = false;
        first.Stop(); reopened.Stop();
        string historyDirectory = Path.Combine(directory, "history");
        var activityStore = new Services.ActivityStore(historyDirectory);
        var entry = new Models.ConnectionEntry("TCPv4", "127.0.0.1", 12345, "192.0.2.1", 443, 1, "Established");
        var savedEvent = new Models.ConnectionEvent(DateTimeOffset.UtcNow, "First seen", entry);
        activityStore.Save(new([savedEvent], []));
        var unopened = new MonitoringView(historyDirectory);
        if (!unopened.SaveHistory.IsOn) throw new InvalidOperationException("History must save by default.");
        unopened.Stop();
        if (activityStore.Load().Events.Single() != savedEvent)
            throw new InvalidOperationException("Closing an unopened monitor overwrote saved history.");
        var restored = new MonitoringView(historyDirectory);
        restored.Show(false);
        await restored.PauseForDiagnosticAsync();
        var lastEvent = savedEvent with { Time = savedEvent.Time.AddTicks(1), Event = "No longer observed" };
        restored.archive.Events.Add(lastEvent);
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(Owner!);
        using (var shutdownTray = new Presentation.TrayIcon(handle, () => { }, () => { }, () => { }, () => { }, restored.FlushLocalData))
        {
            if (SendHistorySessionMessage(handle, 0x11, 0, 1) != 1 || !new Services.ActivityStore(historyDirectory).Load().Events.Contains(lastEvent))
                throw new InvalidOperationException("Update/shutdown query must synchronously flush the latest history before acknowledging.");
        }
        restored.Stop();
        if (!new Services.ActivityStore(historyDirectory).Load().Events.Contains(savedEvent))
            throw new InvalidOperationException("Starting and stopping a replacement view lost saved activity.");
        File.WriteAllText(activityStore.FilePath, "{broken");
        var unreadable = new MonitoringView(historyDirectory);
        unreadable.Show(false);
        await unreadable.PauseForDiagnosticAsync();
        unreadable.Stop();
        if (File.ReadAllText(activityStore.FilePath) != "{broken")
            throw new InvalidOperationException("Unreadable history was overwritten instead of preserved for recovery.");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "preferences-selftest.txt"), "PASS: monitor settings save immediately and survive a fresh view; history defaults on, survives replacement views, and unopened/corrupt histories cannot be overwritten.");
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendHistorySessionMessage(nint window, uint message, nuint wparam, nint lparam);
}
