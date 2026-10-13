namespace ZikWall.WinUI;

public sealed partial class MainWindow
{
    internal async Task ExercisePreferencesAsync()
    {
        await MonitorPanel.ExercisePreferencesAsync();
        using (var icon = new Presentation.TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(this), () => { }, () => { }, () => { }, () => { })) { }
        string directory = Path.Combine(AppContext.BaseDirectory, "preferences-diagnostic", Guid.NewGuid().ToString("N"));
        var first = new MainWindow(true, directory);
        first.ThemeChoice.SelectedIndex = 2;
        var reopened = new MainWindow(true, directory);
        try
        {
            if (reopened.ThemeChoice.SelectedIndex != 2 || reopened.Root.RequestedTheme != Microsoft.UI.Xaml.ElementTheme.Dark)
                throw new InvalidOperationException("Theme reset after reopening the window.");
            await PrivacyPanel.ExercisePreferencesAsync(directory);
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "preferences-selftest.txt"), " Theme and DNS provider choices also survive native view recreation. Separate tray icon loads and registers with Windows.");
        }
        finally { first.Close(); reopened.Close(); }
    }
}
