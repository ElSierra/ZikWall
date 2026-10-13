using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZikWall.WinUI.Models;
using Windows.Storage.Pickers;

namespace ZikWall.WinUI;

public sealed partial class MainWindow : Window
{
    internal Task ExerciseMonitorAsync() => MonitorPanel.ExerciseRowsAsync();
    internal Task ExerciseMonitorOptionsAsync() => MonitorPanel.ExerciseOptionsAsync();
    internal Task ExerciseUsageViewAsync(bool benchmark) => MonitorPanel.ExerciseUsageViewAsync(benchmark);
    internal Task ExerciseSystemAppsAsync() => MonitorPanel.ExerciseSystemAppsAsync();
    internal async Task ExerciseContextMenuAsync() { await MonitorPanel.ExerciseContextMenuAsync(); await PolicyPanel.ExerciseMonitorCancelAsync(); }
    internal async Task ExerciseLifecycleAsync()
    {
        await Task.Delay(6000);
        if (tray is null) throw new InvalidOperationException("Tray icon was not created.");
        int before = MonitorPanel.CaptureCount;
        HideToTray();
        if (AppWindow.IsVisible) throw new InvalidOperationException("Window did not hide.");
        await Task.Delay(17000);
        if (MonitorPanel.CaptureCount <= before) throw new InvalidOperationException("Background snapshots stopped.");
        ShowFromTray();
        if (!AppWindow.IsVisible) throw new InvalidOperationException("Window did not restore.");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "lifecycle-selftest.txt"), "PASS: native tray icon created; window hides/restores; background sampling continues.");
    }
    internal async Task ShowMapFixtureAsync()
    {
        Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(x => x.Tag?.ToString() == "map");
        await MonitorPanel.PauseForDiagnosticAsync();
        GeoPanel.UpdateConnections([new("TCPv4", "192.168.1.2", 12345, "8.8.8.8", 443, Environment.ProcessId, "Established", "ZikWall map fixture", Environment.ProcessPath!, "Outgoing", "US", "United States", "Internet"), new("TCPv4", "192.168.1.2", 12346, "1.1.1.1", 443, Environment.ProcessId, "Established", "ZikWall map fixture", Environment.ProcessPath!, "Incoming", "AU", "Australia", "Internet")]);
        await GeoPanel.ExerciseSelectionAsync();
    }
    private List<RuleEntry> entries = [];
    private string view = "all";
    private bool ready;
    private bool imported;
    private readonly DispatcherTimer timer;
    private DateTimeOffset lastTimerCheck = DateTimeOffset.UtcNow;
    private bool confirmedClose, closeDialogOpen;
    private readonly bool diagnostic;
    private readonly Services.AppearancePreferencesStore appearanceStore = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZikWall"));
    private readonly bool isolatedPreferences;
    private Presentation.TrayIcon? tray;
    public void ShowFromTray() { AppWindow.Show(); Activate(); MonitorPanel.SetBackground(false); GeoPanel.SetBackground(false); }
    public void HideToTray() { if (tray is null) return; AppWindow.Hide(); MonitorPanel.SetBackground(true); GeoPanel.SetBackground(true); }
    public async void ExitFromTray()
    {
        if (closeDialogOpen) return;
        if (PrivacyPanel.DnsRunning)
        {
            closeDialogOpen = true; ShowFromTray();
            try
            {
                var confirm = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "The DNS proxy is running", Content = "If your adapter uses 127.0.0.1 for DNS, restore its normal DNS settings before exiting. Exiting also removes ZikWall's session firewall rules.", PrimaryButtonText = "Exit ZikWall", CloseButtonText = "Keep running" };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            }
            catch (System.Runtime.InteropServices.COMException) { return; }
            finally { closeDialogOpen = false; }
        }
        confirmedClose = true;
        if (tray is not null) tray.AllowClose = true;
        Close();
    }

    public MainWindow(bool diagnostic = false, string? preferencesDirectory = null)
    {
        this.diagnostic = diagnostic;
        isolatedPreferences = preferencesDirectory is not null;
        if (preferencesDirectory is not null) appearanceStore = new(preferencesDirectory);
        InitializeComponent();
        MonitorPanel.Owner = this;
        GeoPanel.Owner = this;
        MonitorPanel.SnapshotChanged += GeoPanel.UpdateConnections;
        MonitorPanel.BlockRequested = plan => PolicyPanel.BlockFromMonitorAsync(plan);
        MonitorPanel.ManageRulesRequested += () => Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(x => x.Tag?.ToString() == "policy");
        PolicyPanel.Owner = this;
        if (diagnostic && Environment.GetCommandLineArgs().Contains("--test-rules"))
            PolicyPanel.UseDiagnosticStore(Path.Combine(AppContext.BaseDirectory, "rules-diagnostic", Guid.NewGuid().ToString("N")));
        PolicyPanel.RulesChanged += Refresh;
        PrivacyPanel.CopyNetworks += networks => { PolicyPanel.Show(); PolicyPanel.ImportNetworks(networks); Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(x => x.Tag?.ToString() == "policy"); };
        PolicyPanel.FilteringChanged += connected => { EngineState.Text = connected ? "WFP broker connected" : "Filtering off · monitoring on"; EngineHint.Text = connected ? "Session rules apply" : "Enable rules in Firewall policy"; };
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "ZikWall.ico"));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        Root.ActualThemeChanged += (_, _) => UpdateCaptionTheme();
        UpdateCaptionTheme();
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Min(1800, area.Width - 80), Math.Min(1150, area.Height - 80)));
        if (!diagnostic)
        {
            try { tray = new(WinRT.Interop.WindowNative.GetWindowHandle(this), ShowFromTray, HideToTray, MonitorPanel.TogglePause, ExitFromTray); }
            catch (System.ComponentModel.Win32Exception ex) { EngineHint.Text = ex.Message; }
        }
        StartupToggle.IsOn = Services.StartupSettings.Enabled;
        if (!diagnostic || isolatedPreferences)
            try { ThemeChoice.SelectedIndex = appearanceStore.Load().Theme; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { StartupStatus.Text = $"Could not load preferences: {ex.Message}"; }
        ready = true;
        PolicyPanel.Show();
        Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(x => x.Tag?.ToString() == "connections");
        Refresh();
        timer = new() { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += (_, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            if (entries.Any(entry => entry.Expires > lastTimerCheck && entry.Expires <= now)) Refresh();
            lastTimerCheck = now;
        };
        timer.Start();
        AppWindow.Changed += (_, _) =>
        {
            bool hidden = !AppWindow.IsVisible || AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized };
            MonitorPanel.SetBackground(hidden); GeoPanel.SetBackground(hidden);
        };
        AppWindow.Closing += (_, args) =>
        {
            if (diagnostic || confirmedClose || tray is null) return;
            args.Cancel = true; HideToTray();
        };
        Closed += (_, _) => { tray?.Dispose(); timer.Stop(); MonitorPanel.Stop(); PolicyPanel.Stop(); PrivacyPanel.Stop(); };
    }

    private void Refresh()
    {
        if (!ready) return;
        var selection = RuleList.SelectedItem as RuleEntry;
        if (!imported)
        {
            entries = PolicyPanel.SavedRules.Select(PolicyRuleEntries.FromRule).ToList();
            ModeBanner.Title = $"{PolicyPanel.ProfileName} · {PolicyPanel.StatusTitle}";
            ModeBanner.Message = PolicyPanel.StatusMessage;
            ModeBanner.Severity = PolicyPanel.StatusSeverity;
        }
        FilteringRuleButton.Label = PolicyPanel.IsFiltering ? "Disable filtering" : "Enable filtering…";
        FilteringRuleButton.Visibility = ApplyRuleButton.Visibility = imported ? Visibility.Collapsed : Visibility.Visible;
        ApplyRuleButton.IsEnabled = PolicyPanel.IsFiltering;
        SavedRulesButton.Visibility = imported ? Visibility.Visible : Visibility.Collapsed;
        var visible = RuleQuery.Filter(entries, view, Search.Text).ToList();
        RuleList.ItemsSource = visible;
        RuleList.SelectedItem = visible.FirstOrDefault(x => selection?.PolicyRuleId is { } id ? x.PolicyRuleId == id : x == selection) ?? visible.FirstOrDefault();
        EntryCount.Text = $"{visible.Count} {(visible.Count == 1 ? "entry" : "entries")}";
        EmptyState.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NewButton.IsEnabled = !imported;
        ShowSelection();
    }

    private void NavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!ready) return;
        view = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag?.ToString() ?? "all";
        PageTitle.Text = args.IsSettingsSelected ? "Settings" : (args.SelectedItem as NavigationViewItem)?.Content?.ToString() ?? "All entries";
        bool monitor = view is "connections" or "activity";
        PageSubtitle.Visibility = monitor || view == "map" ? Visibility.Collapsed : Visibility.Visible;
        bool settings = view == "settings";
        bool policy = view == "policy";
        bool privacy = view == "privacy";
        bool map = view == "map";
        MonitorPanel.SetPageActive(monitor || map);
        RuleWorkspace.Visibility = monitor || settings || policy || privacy || map ? Visibility.Collapsed : Visibility.Visible;
        RuleToolbar.Visibility = monitor || settings || policy || privacy || map ? Visibility.Collapsed : Visibility.Visible;
        Search.Visibility = monitor || settings || policy || privacy || map ? Visibility.Collapsed : Visibility.Visible;
        MonitorPanel.Visibility = monitor ? Visibility.Visible : Visibility.Collapsed;
        if (monitor) MonitorPanel.Show(view == "activity");
        ModeBanner.Visibility = monitor || policy || privacy || map || settings ? Visibility.Collapsed : Visibility.Visible;
        PolicyPanel.Visibility = policy ? Visibility.Visible : Visibility.Collapsed;
        if (policy) PolicyPanel.Show();
        PrivacyPanel.Visibility = privacy ? Visibility.Visible : Visibility.Collapsed;
        if (privacy) PrivacyPanel.Show();
        GeoPanel.Visibility = map ? Visibility.Visible : Visibility.Collapsed;
        if (map) { GeoPanel.Show(); MonitorPanel.Show(false); }
        SettingsPanel.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        PageSubtitle.Text = settings ? "Make ZikWall feel at home." : map ? "Explore approximate remote IP locations using your local data." : privacy ? "Manage encrypted DNS and trusted list sources." : policy ? "Save profiles and apply rules through Windows Filtering Platform." : monitor ? "Understand your network activity." : "Inspect application permissions and network rules.";
        Refresh();
    }

    private void SearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // Read the current text after AutoSuggestBox finishes its property update.
        DispatcherQueue.TryEnqueue(Refresh);
    }
    private void RuleSelectionChanged(object sender, SelectionChangedEventArgs args) => ShowSelection();

    private void ShowSelection()
    {
        if (!ready) return;
        var rule = RuleList.SelectedItem as RuleEntry;
        DetailName.Text = rule?.Name ?? "Select an entry";
        DetailCategory.Text = rule?.Category ?? "";
        DetailSummary.Text = rule is null ? "Choose an entry to inspect its details." : $"{rule.State} · {rule.Duration}\n{(rule.PolicyRuleId is { } id ? PolicyPanel.RuleStatus(id) : "Imported entry only. Not applied to the firewall.")}";
        DetailDestination.Text = rule is null ? "—" : $"Remote: {rule.Destination}\nLocal: {rule.LocalEndpoint}";
        DetailDirection.Text = rule?.Direction ?? "—";
        DetailProtocol.Text = rule?.Protocol ?? "—";
        DetailPath.Text = rule is null ? "—" : !string.IsNullOrEmpty(rule.Path) ? rule.Path : !string.IsNullOrEmpty(rule.Applications) ? rule.Applications : "Not restricted to a listed application";
        DetailNotes.Text = string.IsNullOrEmpty(rule?.Notes) ? "No notes for this entry." : rule.Notes;
        EditButton.Content = imported ? "Read-only profile" : "Edit rule";
        EditButton.IsEnabled = EditRuleButton.IsEnabled = RemoveRuleButton.IsEnabled = !imported && rule is not null;
        DetailsButton.IsEnabled = rule is not null;
    }

    private async void DetailsClicked(object sender, RoutedEventArgs args)
    {
        if (RuleList.SelectedItem is not RuleEntry rule) return;
        var text = new TextBlock { Text = $"{rule.State} · {rule.Duration}\n\nRemote: {rule.Destination}\nLocal: {rule.LocalEndpoint}\n\n{rule.Direction} · {rule.Protocol}\n\n{rule.Path}\n{rule.Applications}\n\n{rule.Notes}", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 460 };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = rule.Name, Content = text, CloseButtonText = "Close", PrimaryButtonText = imported ? "" : "Edit rule", IsPrimaryButtonEnabled = !imported };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await EditRule(rule);
    }

    private async void OpenProfileClicked(object sender, RoutedEventArgs args)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".xml");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            using var stream = await file.OpenStreamForReadAsync();
            var parsed = ProfileReader.Read(stream);
            entries = parsed.ToList();
            imported = true;
            ModeBanner.Severity = InfoBarSeverity.Informational;
            ModeBanner.Title = "Read-only profile";
            ModeBanner.Message = $"{file.Name} · Stored entries only. Internal overrides and effective WFP decisions are not evaluated.";
            Refresh();
        }
        catch (Exception error) when (error is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            await new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "Couldn't open this profile", Content = error.Message, CloseButtonText = "OK" }.ShowAsync();
        }
    }

    private void SavedRulesClicked(object sender, RoutedEventArgs args)
    {
        imported = false;
        Search.Text = "";
        Refresh();
    }

    private void FilteringRuleClicked(object sender, RoutedEventArgs args) => PolicyPanel.ToggleFiltering();
    private void ApplyRuleClicked(object sender, RoutedEventArgs args) => PolicyPanel.ApplySavedPolicy();
    private void RemoveRuleClicked(object sender, RoutedEventArgs args)
    {
        if (!imported && RuleList.SelectedItem is RuleEntry { PolicyRuleId: { } id }) PolicyPanel.RemoveRule(id);
    }
    private async void NewRuleClicked(object sender, RoutedEventArgs args) => await EditRule(null);
    private async void EditRuleClicked(object sender, RoutedEventArgs args) => await EditRule(RuleList.SelectedItem as RuleEntry);
    private async Task EditRule(RuleEntry? original)
    {
        if (!imported) await PolicyPanel.EditRuleAsync(original?.PolicyRuleId);
    }

    private void StartupChanged(object sender, RoutedEventArgs args)
    {
        if (!ready) return;
        try { Services.StartupSettings.Set(StartupToggle.IsOn); StartupStatus.Text = "X hides ZikWall. Use Exit in the tray to stop it and remove session rules."; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { StartupStatus.Text = ex.Message; }
    }
    private void ThemeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (Root is null) return;
        Root.RequestedTheme = ((ComboBox)sender).SelectedIndex switch { 1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default };
        if (!ready || (diagnostic && !isolatedPreferences)) return;
        try { appearanceStore.Update(value => value with { Theme = ThemeChoice.SelectedIndex }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { StartupStatus.Text = $"Theme applies for now but could not be saved: {ex.Message}"; }
    }

    private void UpdateCaptionTheme()
    {
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Root.ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = Microsoft.UI.Colors.Gray;
    }

    private void ContentSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (Inspector is null) return;
        bool wide = args.NewSize.Width >= 930;
        Inspector.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        RuleWorkspace.ColumnDefinitions[1].Width = wide ? new GridLength(304) : new GridLength(0);
        RuleWorkspace.ColumnSpacing = wide ? 20 : 0;
    }
}
