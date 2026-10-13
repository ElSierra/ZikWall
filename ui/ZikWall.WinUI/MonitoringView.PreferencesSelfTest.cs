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
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "preferences-selftest.txt"), "PASS: monitor settings save immediately and survive a fresh view without an exit-time save.");
    }
}
