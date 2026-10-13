using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;
using Windows.Storage.Pickers;

namespace ZikWall.WinUI;

public sealed partial class PolicyView : UserControl
{
    private PolicyStore store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZikWall"));
    private readonly FirewallClient client = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private PolicyDocument policy = PolicyDocument.Empty();
    private PolicyDocument? lastSaved;
    private bool loaded, refreshing, busy, pending;
    private Guid? appliedProfile;
    private bool dialogOpen;
    private ContentDialog? monitorDialog;
    private ContentDialog? ruleDialog;
    private void ProfileControlsSizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool narrow = args.NewSize.Width < 900;
        ProfileControls.RowSpacing = narrow ? 12 : 0;
        ProfileControls.ColumnDefinitions[0].Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(250);
        ProfileControls.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : GridLength.Auto;
        ProfileControls.ColumnDefinitions[2].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ProfileControls.ColumnDefinitions[3].Width = narrow ? new GridLength(0) : GridLength.Auto;
        FrameworkElement[] groups = [ProfileSelector, ProfileActions, NetworkSwitchGroup, FilteringButton];
        for (int i = 0; i < groups.Length; i++)
        {
            Grid.SetColumn(groups[i], narrow ? 0 : i);
            Grid.SetRow(groups[i], narrow ? i : 0);
        }
    }
    internal void UseDiagnosticStore(string directory)
    {
        if (loaded) throw new InvalidOperationException("Configure the diagnostic store before loading rules.");
        store = new(directory);
    }
    internal async Task ExerciseRuleEditorCancelAsync(Guid id)
    {
        var before = policy;
        var operation = EditRuleAsync(id);
        await Task.Delay(200);
        if (ruleDialog is null) throw new InvalidOperationException("The saved rule editor did not open.");
        ruleDialog.Hide();
        await operation;
        if (policy != before || client.Connected) throw new InvalidOperationException("Cancel changed the policy or enabled filtering.");
    }
    internal async Task ExerciseRuleEditorSaveAsync(Guid id)
    {
        var operation = EditRuleAsync(id);
        await Task.Delay(250);
        var dialog = ruleDialog ?? throw new InvalidOperationException("The editor did not open.");
        var form = (StackPanel)((ScrollViewer)dialog.Content).Content;
        form.Children.OfType<ComboBox>().Single(x => (string)x.Header == "Rule group").SelectedItem = "System rules";
        form.Children.OfType<ComboBox>().Single(x => (string)x.Header == "Protocol").SelectedIndex = 2;
        form.Children.OfType<CheckBox>().Single().IsChecked = false;
        Button? FindPrimary(DependencyObject parent)
        {
            if (parent is Button { Name: "PrimaryButton" } button) return button;
            for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
                if (FindPrimary(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
            return null;
        }
        var primary = FindPrimary(dialog) ?? throw new InvalidOperationException("The editor Save button was not found.");
        var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(primary);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
        var saved = store.Load().Profiles.Single(x => x.Id == Selected.Id).Rules.Single(x => x.Id == id);
        if (saved.Enabled || saved.Protocol != 17 || saved.Group != "System rules" || client.Connected)
            throw new InvalidOperationException("The editor did not persist its fields correctly.");
    }
    public event Action<bool>? FilteringChanged;
    public event Action? RulesChanged;
    public IReadOnlyList<FirewallRule> SavedRules => Selected.Rules;
    public string ProfileName => Selected.Name;
    public bool IsFiltering => client.Connected;
    public string StatusTitle => PolicyStatus.Title;
    public string StatusMessage => PolicyStatus.Message;
    public InfoBarSeverity StatusSeverity => PolicyStatus.Severity;
    public string RuleStatus(Guid id)
    {
        var rule = Selected.Rules.FirstOrDefault(x => x.Id == id);
        if (rule is null) return "Rule no longer exists in this profile.";
        if (!client.Connected) return "Saved locally. Filtering is off.";
        if (pending || appliedProfile != Selected.Id) return "Saved changes are not applied. Apply saved policy to update filtering.";
        return !rule.Enabled ? "Disabled. This rule is not applied." : !rule.IsActive(DateTimeOffset.UtcNow) ? "Expired. This rule is not applied." : "Applied to the current WFP session. Rules are evaluated in their saved order.";
    }
    public Task EditRuleAsync(Guid? id)
    {
        Show();
        var rule = id is null ? null : Selected.Rules.FirstOrDefault(x => x.Id == id);
        return id is not null && rule is null ? Task.CompletedTask : EditAsync(rule);
    }
    public void RemoveRule(Guid id)
    {
        if (busy || dialogOpen) return;
        try { ReplaceRules(Selected.Rules.Where(x => x.Id != id).ToList()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error(ex.Message); }
    }
    public void ToggleFiltering() => FilteringClicked(this, new RoutedEventArgs());
    public void ApplySavedPolicy() => ApplyClicked(this, new RoutedEventArgs());
    public Window? Owner { get; set; }
    private NetworkProfile Selected => policy.Profiles.Single(x => x.Id == policy.SelectedProfile);
    public PolicyView() { InitializeComponent(); timer.Tick += async (_, _) => await TickAsync(); }
    public void Show()
    {
        if (loaded) return;
        loaded = true;
        try { policy = store.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { Error($"Saved policy could not be loaded: {ex.Message}"); }
        lastSaved = policy; Refresh(); timer.Start();
    }
    public void Stop() { timer.Stop(); client.Dispose(); }
    public void ImportNetworks(IReadOnlyList<string> networks)
    {
        try
        {
            var rules = Selected.Rules.ToList();
            var existing = rules.Where(x => x.Block && x.Application.Length == 0 && x.Direction == 3 && x.Protocol == 0).Select(x => x.RemoteNetwork).ToHashSet();
            foreach (string network in networks.Where(x => !existing.Contains(x))) rules.Add(new(Guid.NewGuid(), $"Blocklist {network}", "", network, 0, 0, 3, true, true, null) { Group = "Blocklists" });
            ReplaceRules(rules);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error(ex.Message); }
    }
    public void AddDraft(FirewallRule rule)
    {
        try { rule.Validate(); ReplaceRules([rule, .. Selected.Rules]); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error(ex.Message); }
    }
    public async Task<string?> BlockFromMonitorAsync(MonitorBlockPlan plan)
    {
        Show();
        if (busy || dialogOpen) throw new InvalidOperationException("The firewall is busy. Try again in a moment.");
        busy = dialogOpen = true;
        bool saved = false, wasConnected = client.Connected;
        try
        {
            var lifetime = new ComboBox { Header = "Block duration", ItemsSource = new[] { "Until removed (while ZikWall is running)", "5 minutes", "30 minutes", "1 hour" }, SelectedIndex = 0 };
            var content = new StackPanel { Spacing = 12, MaxWidth = 560 };
            content.Children.Add(new TextBlock { Text = plan.Explanation, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            content.Children.Add(lifetime);
            content.Children.Add(new TextBlock { Text = $"Adds {plan.Rules.Count} block rule(s) first in profile ‘{Selected.Name}’ and applies that profile's {Selected.Rules.Count} existing saved rules too, including pending edits.\n\n" +
                (client.Connected ? "Filtering is enabled." : "Windows will request administrator approval to enable filtering.") +
                " Session filters end when ZikWall closes. A displayed connection may remain in the snapshot; that is not evidence of a successful block.", TextWrapping = TextWrapping.Wrap });
            var dialog = new ContentDialog { XamlRoot = Owner?.Content.XamlRoot ?? XamlRoot, RequestedTheme = ActualTheme, Title = plan.Title,
                Content = new ScrollViewer { Content = content, MaxHeight = 540 }, PrimaryButtonText = "Block now", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
            monitorDialog = dialog;
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
            DateTimeOffset? expires = lifetime.SelectedIndex switch { 1 => DateTimeOffset.UtcNow.AddMinutes(5), 2 => DateTimeOffset.UtcNow.AddMinutes(30), 3 => DateTimeOffset.UtcNow.AddHours(1), _ => null };
            var rules = plan.Rules.Select(x => x with { Expires = expires }).ToList();
            foreach (var rule in rules) rule.Validate();
            ReplaceRules([.. rules, .. Selected.Rules]); saved = true;
            if (!client.Connected) await client.StartAsync();
            await ApplyAsync();
            return $"Windows accepted {rules.Count} new block rule(s) in ‘{Selected.Name}’. Manage or remove them in Firewall policy, then apply the saved policy.";
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or OperationCanceledException or UnauthorizedAccessException)
        {
            if (!wasConnected) { client.Dispose(); appliedProfile = null; }
            string message = (saved ? "Rule saved, but blocking was not confirmed. Review Firewall policy. " : "No new block was applied. ") + ex.Message;
            Error(message);
            throw new IOException(message, ex);
        }
        finally
        {
            busy = dialogOpen = false;
            monitorDialog = null;
            FilteringButton.Content = client.Connected ? "Disable filtering" : "Enable filtering…";
            FilteringChanged?.Invoke(client.Connected); RulesChanged?.Invoke();
        }
    }
    internal async Task ExerciseMonitorCancelAsync()
    {
        Show();
        var before = policy;
        var rule = new FirewallRule(Guid.NewGuid(), "Diagnostic only", Environment.ProcessPath!, "127.0.0.1", 6, 59999, 1, true, true, null);
        var operation = BlockFromMonitorAsync(new("Diagnostic block review", "Cancellation test: no rule will be applied.", new[] { rule }));
        await Task.Delay(250);
        if (monitorDialog is null) throw new InvalidOperationException("The monitor block review dialog did not open.");
        monitorDialog.Hide();
        var result = await operation;
        if (result is not null || policy != before || client.Connected) throw new InvalidOperationException("Cancel changed the policy or started a broker.");
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "context-menu-selftest.txt"), "\nPASS: native block-review cancellation leaves policy and filtering unchanged.");
    }
    private void Refresh()
    {
        refreshing = true;
        ProfileChoice.ItemsSource = policy.Profiles.ToList(); ProfileChoice.SelectedItem = Selected;
        AutoSwitch.IsOn = policy.AutoSwitch;
        var selection = (PolicyRules.SelectedItem as FirewallRule)?.Id;
        PolicyRules.ItemsSource = Selected.Rules.ToList(); PolicyRules.SelectedItem = Selected.Rules.FirstOrDefault(x => x.Id == selection);
        PolicyEmpty.Visibility = Selected.Rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NetworkLabel.Text = Selected.NetworkId.Length == 0 ? "No network associated with this profile." : $"Associated Windows network ID: {Selected.NetworkId}";
        refreshing = false;
        RulesChanged?.Invoke();
    }
    private void Saved()
    {
        try { store.Save(policy); lastSaved = policy; }
        catch { policy = lastSaved ?? PolicyDocument.Empty(); Refresh(); throw; }
        pending = client.Connected; Refresh();
        PolicyStatus.Severity = InfoBarSeverity.Informational;
        PolicyStatus.Title = client.Connected ? "Saved changes are not applied" : "Saved locally · filtering is off";
        PolicyStatus.Message = client.Connected ? "Click Apply saved policy to replace the broker's active rules." : "Enable filtering to apply this profile. Windows will request administrator approval.";
        RulesChanged?.Invoke();
    }
    private void Error(string message) { PolicyStatus.Title = "Action failed"; PolicyStatus.Message = message; PolicyStatus.Severity = InfoBarSeverity.Error; RulesChanged?.Invoke(); }
    private async Task ApplyAsync()
    {
        if (!client.Connected) throw new IOException("Enable filtering before applying a policy.");
        policy.Validate();
        var selected = Selected;
        var response = await client.SendAsync(new("apply", selected.Rules));
        if (!response.Success) throw new IOException(response.Message);
        appliedProfile = selected.Id; pending = Selected != selected;
        PolicyStatus.Title = $"Filtering active · {selected.Name}"; PolicyStatus.Message = pending ? "The selected profile has pending changes. Click Apply saved policy." : $"Windows accepted {response.ActiveRules} active rules. Expired rules are removed by the broker."; PolicyStatus.Severity = InfoBarSeverity.Success;
    }
    private async void FilteringClicked(object sender, RoutedEventArgs args)
    {
        if (busy || dialogOpen) return;
        if (!client.Connected)
        {
            var confirm = new ContentDialog { XamlRoot = Owner?.Content.XamlRoot ?? XamlRoot, RequestedTheme = ActualTheme, Title = "Enable ZikWall filtering?", Content = "This starts an elevated WFP broker and applies the selected profile. Rules can interrupt matching connections. These session filters are removed when ZikWall or its broker closes. Windows Firewall defaults are unchanged.", PrimaryButtonText = "Enable filtering", CloseButtonText = "Cancel" };
            dialogOpen = true;
            ContentDialogResult confirmation;
            try { confirmation = await confirm.ShowAsync(); } finally { dialogOpen = false; }
            if (confirmation != ContentDialogResult.Primary) return;
        }
        busy = true; FilteringButton.IsEnabled = false;
        try
        {
            if (client.Connected)
            {
                var response = await client.SendAsync(new("stop"));
                if (!response.Success) throw new IOException(response.Message);
                client.Dispose(); appliedProfile = null; PolicyStatus.Title = "Filtering is off"; PolicyStatus.Message = "The broker removed ZikWall's session filters."; PolicyStatus.Severity = InfoBarSeverity.Informational;
            }
            else { await client.StartAsync(); await ApplyAsync(); }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or OperationCanceledException or UnauthorizedAccessException) { client.Dispose(); appliedProfile = null; Error(ex.Message); }
        finally { busy = false; FilteringButton.IsEnabled = true; FilteringButton.Content = client.Connected ? "Disable filtering" : "Enable filtering…"; FilteringChanged?.Invoke(client.Connected); RulesChanged?.Invoke(); }
    }
    private async void ApplyClicked(object sender, RoutedEventArgs args)
    {
        if (busy || dialogOpen) return; busy = true;
        try { await ApplyAsync(); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { Error(ex.Message); }
        finally { busy = false; FilteringChanged?.Invoke(client.Connected); RulesChanged?.Invoke(); }
    }
    private async Task TickAsync()
    {
        if (busy) return; busy = true;
        try
        {
            if (policy.AutoSwitch && !pending && !dialogOpen)
            {
                var ids = NetworkIdentity.Read().Select(x => x.Id).ToHashSet();
                var matches = policy.Profiles.Where(x => ids.Contains(x.NetworkId)).ToList();
                // Ambiguous multi-network matches must not choose an arbitrary policy.
                if (matches.Count == 1 && matches[0].Id != Selected.Id)
                {
                    var next = policy with { SelectedProfile = matches[0].Id }; store.Save(next); policy = next; lastSaved = next; Refresh();
                    if (client.Connected) { pending = true; await ApplyAsync(); }
                }
                else if (matches.Count > 1) { PolicyStatus.Title = "Multiple associated networks"; PolicyStatus.Message = "Automatic switching is paused. Select the intended profile manually."; PolicyStatus.Severity = InfoBarSeverity.Warning; }
            }
            if (client.Connected)
            {
                var response = await client.SendAsync(new("status"));
                if (!response.Success) throw new IOException(response.Message);
                if (!pending && appliedProfile == Selected.Id) PolicyStatus.Message = $"Windows accepted {response.ActiveRules} active rules. Expired rules are removed by the broker.";
            }
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or COMException or UnauthorizedAccessException) { Error(ex.Message); FilteringButton.Content = client.Connected ? "Disable filtering" : "Enable filtering…"; }
        finally { busy = false; FilteringChanged?.Invoke(client.Connected); RulesChanged?.Invoke(); }
    }
    private void ProfileChanged(object sender, SelectionChangedEventArgs args)
    {
        if (refreshing || !loaded || ProfileChoice.SelectedItem is not NetworkProfile profile) return;
        try { policy = policy with { SelectedProfile = profile.Id }; Saved(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error(ex.Message); }
    }
    private async void NewProfileClicked(object sender, RoutedEventArgs args)
    {
        var name = new TextBox { Header = "Profile name", MaxLength = 80 };
        var dialog = new ContentDialog { XamlRoot = Owner?.Content.XamlRoot ?? XamlRoot, RequestedTheme = ActualTheme, Title = "New network profile", Content = name, PrimaryButtonText = "Create", CloseButtonText = "Cancel" };
        dialogOpen = true;
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); } finally { dialogOpen = false; }
        if (result != ContentDialogResult.Primary) return;
        try { var profile = new NetworkProfile(Guid.NewGuid(), name.Text.Trim(), []); policy = policy with { SelectedProfile = profile.Id, Profiles = [.. policy.Profiles, profile] }; Saved(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error(ex.Message); }
    }
    private async void AssociateClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            var networks = NetworkIdentity.Read();
            var choice = new ComboBox { Header = "Connected Windows network", ItemsSource = networks, DisplayMemberPath = "Name", SelectedIndex = networks.Count > 0 ? 0 : -1, MinWidth = 300 };
            var dialog = new ContentDialog { XamlRoot = Owner?.Content.XamlRoot ?? XamlRoot, RequestedTheme = ActualTheme, Title = "Associate this profile", Content = choice, PrimaryButtonText = "Associate", SecondaryButtonText = "Remove association", CloseButtonText = "Cancel", IsPrimaryButtonEnabled = networks.Count > 0 };
            dialogOpen = true;
            ContentDialogResult result;
            try { result = await dialog.ShowAsync(); } finally { dialogOpen = false; }
            if (result == ContentDialogResult.None) return;
            string network = result == ContentDialogResult.Secondary ? "" : ((ConnectedNetwork)choice.SelectedItem).Id;
            policy = policy with { Profiles = policy.Profiles.Select(x => x.Id == Selected.Id ? x with { NetworkId = network } : x).ToList() }; Saved();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException) { Error(ex.Message); }
    }
    private void AutoSwitchChanged(object sender, RoutedEventArgs args)
    {
        if (refreshing || !loaded) return;
        try { var next = policy with { AutoSwitch = AutoSwitch.IsOn }; store.Save(next); policy = next; lastSaved = next; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error(ex.Message); }
    }
    private async void NewRuleClicked(object sender, RoutedEventArgs args) => await EditAsync(null);
    private async void CheckRulesClicked(object sender, RoutedEventArgs args)
    {
        if (dialogOpen) return;
        dialogOpen = true;
        try
        {
            var selected = Selected;
            var snapshot = selected.Rules.ToArray();
            var now = DateTimeOffset.UtcNow;
            var analysis = await Task.Run(() => PolicyAnalysis.Analyze(snapshot, now, File.Exists));
            var content = new StackPanel { Spacing = 12, MinWidth = 400 };
            content.Children.Add(new TextBlock { Text = $"Saved snapshot: {selected.Name} · {now.ToLocalTime():g}. These checks describe ordering within ZikWall, not observed connection decisions or rules from other firewalls. No rules are changed.", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = !analysis.Complete ? "Partial results: the analysis reached its work limit. Review the remaining rules in smaller profiles." : analysis.Findings.Count == 0 ? "No fully covered, inactive or unavailable-executable rules found. Partial overlaps and combinations of several earlier rules are not analyzed." : $"{analysis.Findings.Count} findings. Partial overlaps and combinations of several earlier rules are not analyzed.", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new ListView { ItemsSource = analysis.Findings, ItemTemplate = (DataTemplate)Resources["PolicyFindingTemplate"], SelectionMode = ListViewSelectionMode.None, HorizontalContentAlignment = HorizontalAlignment.Stretch, MaxHeight = 380 });
            await new ContentDialog { XamlRoot = Owner?.Content.XamlRoot ?? XamlRoot, RequestedTheme = ActualTheme, Title = "Check saved rules", Content = content, CloseButtonText = "Done" }.ShowAsync();
        }
        finally { dialogOpen = false; }
    }
    private async void EditClicked(object sender, RoutedEventArgs args) { if (PolicyRules.SelectedItem is FirewallRule rule) await EditAsync(rule); }
    private void RemoveClicked(object sender, RoutedEventArgs args)
    {
        if (PolicyRules.SelectedItem is not FirewallRule rule) return;
        try { ReplaceRules(Selected.Rules.Where(x => x.Id != rule.Id).ToList()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error(ex.Message); }
    }
    private void UpClicked(object sender, RoutedEventArgs args)
    {
        if (PolicyRules.SelectedItem is not FirewallRule rule) return;
        var list = Selected.Rules.ToList(); int index = list.IndexOf(rule); if (index <= 0) return;
        (list[index - 1], list[index]) = (list[index], list[index - 1]);
        try { ReplaceRules(list); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error(ex.Message); }
    }
    private void ReplaceRules(List<FirewallRule> rules) { policy = policy with { Profiles = policy.Profiles.Select(x => x.Id == Selected.Id ? x with { Rules = rules } : x).ToList() }; Saved(); }
    private async Task EditAsync(FirewallRule? original)
    {
        if (busy || dialogOpen) return;
        var profileId = Selected.Id;
        var group = new ComboBox { Header = "Rule group", ItemsSource = new[] { "Applications", "Custom rules", "System rules", "Blocklists" }, SelectedItem = original is null ? "Custom rules" : PolicyRuleEntries.FromRule(original).Category };
        var name = new TextBox { Header = "Name", Text = original?.Name ?? "", MaxLength = 128 };
        var app = new TextBox { Header = "Local application .exe path (optional if IP is set)", Text = original?.Application ?? "" };
        var browse = new Button { Content = "Choose application…" };
        browse.Click += async (_, _) => { if (Owner is null) return; var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".exe"); WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(Owner)); var file = await picker.PickSingleFileAsync(); if (file is not null) app.Text = file.Path; };
        var network = new TextBox { Header = "Remote IPv4/IPv6 address or CIDR (blank = any)", Text = original?.RemoteNetwork ?? "" };
        var port = new NumberBox { Header = "Remote port (0 = any)", Minimum = 0, Maximum = 65535, Value = original?.RemotePort ?? 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var protocol = new ComboBox { Header = "Protocol", ItemsSource = new[] { "Any", "TCP", "UDP" }, SelectedIndex = original?.Protocol == 6 ? 1 : original?.Protocol == 17 ? 2 : 0 };
        var direction = new ComboBox { Header = "Direction", ItemsSource = new[] { "Outbound", "Inbound", "Both" }, SelectedIndex = (original?.Direction ?? 1) - 1 };
        var action = new ComboBox { Header = "Action", ItemsSource = new[] { "Block", "Allow" }, SelectedIndex = original?.Block == false ? 1 : 0 };
        var duration = new ComboBox { Header = "Expiry", ItemsSource = new[] { "Keep existing / no expiry", "5 minutes", "30 minutes", "1 hour", "No expiry" }, SelectedIndex = 0 };
        var enabled = new CheckBox { Content = "Enabled", IsChecked = original?.Enabled ?? true };
        var form = new StackPanel { Spacing = 10, MinWidth = 410 };
        foreach (var element in new UIElement[] { name, group, app, browse, network, protocol, port, direction, action, duration, enabled }) form.Children.Add(element);
        var dialog = new ContentDialog { XamlRoot = Owner?.Content.XamlRoot ?? XamlRoot, RequestedTheme = ActualTheme, Title = "Saved firewall rule", Content = new ScrollViewer { Content = form, MaxHeight = 600 }, PrimaryButtonText = "Save", CloseButtonText = "Cancel" };
        ruleDialog = dialog;
        dialogOpen = true;
        ContentDialogResult result;
        try { result = await dialog.ShowAsync(); } finally { dialogOpen = false; ruleDialog = null; }
        if (result != ContentDialogResult.Primary) return;
        try
        {
            if (double.IsNaN(port.Value) || port.Value != Math.Truncate(port.Value)) throw new InvalidDataException("Use a whole-number port.");
            var expires = duration.SelectedIndex switch { 1 => DateTimeOffset.UtcNow.AddMinutes(5), 2 => DateTimeOffset.UtcNow.AddMinutes(30), 3 => DateTimeOffset.UtcNow.AddHours(1), 4 => (DateTimeOffset?)null, _ => original?.Expires };
            var rule = new FirewallRule(original?.Id ?? Guid.NewGuid(), name.Text.Trim(), app.Text.Trim(), network.Text.Trim(), protocol.SelectedIndex == 1 ? 6 : protocol.SelectedIndex == 2 ? 17 : 0, (int)port.Value, direction.SelectedIndex + 1, action.SelectedIndex == 0, enabled.IsChecked == true, expires) { Group = (string)group.SelectedItem };
            rule.Validate();
            if (Selected.Id != profileId) throw new InvalidDataException("The selected profile changed. Reopen the editor.");
            var rules = Selected.Rules.ToList(); if (original is null) rules.Add(rule); else rules[rules.IndexOf(original)] = rule;
            ReplaceRules(rules);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error(ex.Message); }
    }
}
