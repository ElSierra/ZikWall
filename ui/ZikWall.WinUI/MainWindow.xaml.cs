using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZikWall.WinUI.Models;
using Windows.Storage.Pickers;

namespace ZikWall.WinUI;

public sealed partial class MainWindow : Window
{
    internal Task ExerciseMonitorAsync() => MonitorPanel.ExerciseRowsAsync();
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
    private List<RuleEntry> entries = PreviewData.Create();
    private string view = "all";
    private bool ready;
    private bool preview = true;
    private readonly DispatcherTimer timer;
    private DateTimeOffset lastTimerCheck = DateTimeOffset.UtcNow;
    private bool confirmedClose, closeDialogOpen;
    private readonly bool diagnostic;
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

    public MainWindow(bool diagnostic = false)
    {
        this.diagnostic = diagnostic;
        InitializeComponent();
        MonitorPanel.Owner = this;
        GeoPanel.Owner = this;
        MonitorPanel.SnapshotChanged += GeoPanel.UpdateConnections;
        MonitorPanel.BlockRequested = plan => PolicyPanel.BlockFromMonitorAsync(plan);
        MonitorPanel.ManageRulesRequested += () => Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(x => x.Tag?.ToString() == "policy");
        PolicyPanel.Owner = this;
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
        ready = true;
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
        var visible = RuleQuery.Filter(entries, view, Search.Text).ToList();
        RuleList.ItemsSource = visible;
        RuleList.SelectedItem = selection is not null && visible.Contains(selection) ? selection : visible.FirstOrDefault();
        EntryCount.Text = $"{visible.Count} {(visible.Count == 1 ? "entry" : "entries")}";
        EmptyState.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NewButton.IsEnabled = preview;
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

    private void SearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Refresh();
    private void RuleSelectionChanged(object sender, SelectionChangedEventArgs args) => ShowSelection();

    private void ShowSelection()
    {
        if (!ready) return;
        var rule = RuleList.SelectedItem as RuleEntry;
        DetailName.Text = rule?.Name ?? "Select an entry";
        DetailCategory.Text = rule?.Category ?? "";
        DetailSummary.Text = rule is null ? "Choose an entry to inspect its details." : $"{rule.State} · {rule.Duration}\nThis entry does not establish the effective firewall decision.";
        DetailDestination.Text = rule is null ? "—" : $"Remote: {rule.Destination}\nLocal: {rule.LocalEndpoint}";
        DetailDirection.Text = rule?.Direction ?? "—";
        DetailProtocol.Text = rule?.Protocol ?? "—";
        DetailPath.Text = rule is null ? "—" : !string.IsNullOrEmpty(rule.Path) ? rule.Path : !string.IsNullOrEmpty(rule.Applications) ? rule.Applications : "Not restricted to a listed application";
        DetailNotes.Text = string.IsNullOrEmpty(rule?.Notes) ? "No notes for this entry." : rule.Notes;
        EditButton.Content = preview ? "Edit preview rule" : "Read-only profile";
        EditButton.IsEnabled = preview && rule is not null;
        DetailsButton.IsEnabled = rule is not null;
    }

    private async void DetailsClicked(object sender, RoutedEventArgs args)
    {
        if (RuleList.SelectedItem is not RuleEntry rule) return;
        var text = new TextBlock { Text = $"{rule.State} · {rule.Duration}\n\nRemote: {rule.Destination}\nLocal: {rule.LocalEndpoint}\n\n{rule.Direction} · {rule.Protocol}\n\n{rule.Path}\n{rule.Applications}\n\n{rule.Notes}", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 460 };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = rule.Name, Content = text, CloseButtonText = "Close", PrimaryButtonText = preview ? "Edit preview" : "", IsPrimaryButtonEnabled = preview };
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
            preview = false;
            ModeBanner.Title = "Read-only profile";
            ModeBanner.Message = $"{file.Name} · Stored entries only. Internal overrides and effective WFP decisions are not evaluated.";
            Refresh();
        }
        catch (Exception error) when (error is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            await new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "Couldn't open this profile", Content = error.Message, CloseButtonText = "OK" }.ShowAsync();
        }
    }

    private void ResetClicked(object sender, RoutedEventArgs args)
    {
        entries = PreviewData.Create();
        preview = true;
        ModeBanner.Title = "Preview mode";
        ModeBanner.Message = "Sample entries. Changes stay in memory and do not affect your firewall.";
        Search.Text = "";
        Refresh();
    }

    private async void NewRuleClicked(object sender, RoutedEventArgs args) => await EditRule(null);
    private async void EditRuleClicked(object sender, RoutedEventArgs args) => await EditRule(RuleList.SelectedItem as RuleEntry);

    private async Task EditRule(RuleEntry? original)
    {
        if (!preview) return;
        var name = new TextBox { Header = "Rule name", Text = original?.Name ?? "", MaxLength = 64 };
        var destination = new TextBox { Header = "Remote address or port", Text = original?.Destination ?? "Any destination", MaxLength = 1024 };
        var action = new ComboBox { Header = "Action", HorizontalAlignment = HorizontalAlignment.Stretch };
        action.Items.Add("Allow"); action.Items.Add("Block"); action.SelectedIndex = original?.Action == "Block" ? 1 : 0;
        var direction = new ComboBox { Header = "Direction", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var value in new[] { "Outbound", "Inbound", "Both" }) direction.Items.Add(value);
        direction.SelectedItem = original?.Direction ?? "Outbound";
        var duration = new ComboBox { Header = "Duration", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var value in new[] { "Until changed", "5 minutes", "30 minutes", "1 hour" }) duration.Items.Add(value);
        duration.SelectedIndex = 0;
        var enabled = new CheckBox { Content = "Enable this preview entry", IsChecked = original?.Enabled ?? true };
        var form = new StackPanel { Spacing = 14, MinWidth = 340 };
        form.Children.Add(new TextBlock { Text = "Preview only. No rule is sent to the firewall.", TextWrapping = TextWrapping.Wrap });
        foreach (var control in new UIElement[] { name, destination, action, direction, duration, enabled }) form.Children.Add(control);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = original is null ? "New preview rule" : "Edit preview rule", Content = form, PrimaryButtonText = "Save preview", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text) };
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        DateTimeOffset? expires = duration.SelectedIndex switch { 1 => DateTimeOffset.UtcNow.AddMinutes(5), 2 => DateTimeOffset.UtcNow.AddMinutes(30), 3 => DateTimeOffset.UtcNow.AddHours(1), _ => null };
        var updated = new RuleEntry(name.Text.Trim(), original?.Path ?? "", original?.Category ?? "Custom rules", action.SelectedItem.ToString()!, destination.Text.Trim(), original?.Protocol ?? "Any", direction.SelectedItem.ToString()!, enabled.IsChecked == true, expires, original?.Notes ?? "Preview entry");
        if (original is not null) entries.Remove(original);
        entries.Add(updated);
        Refresh();
        if (((IEnumerable<RuleEntry>)RuleList.ItemsSource).Contains(updated)) RuleList.SelectedItem = updated;
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
