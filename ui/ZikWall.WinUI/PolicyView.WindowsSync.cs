using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

namespace ZikWall.WinUI;

public sealed partial class PolicyView
{
    private DateTimeOffset nextWindowsSync;
    private bool syncingWindows;
    private bool windowsSyncKeepalive;
    private string windowsSyncNotice = "";
    private ContentDialog? windowsSyncDialog;
    private readonly bool windowsSyncDiagnostic = Environment.GetCommandLineArgs().Skip(1).Any(x => x.StartsWith("--test-", StringComparison.Ordinal) || x == "--benchmark");
    private static Task<List<WindowsFirewallEntry>> ReadWindowsAsync() => Task.Run(() =>
    {
        using var windows = new WindowsFirewallStore();
        return windows.Read();
    });
    private async Task KeepFilteringAliveDuringWindowsSyncAsync()
    {
        // A preview can remain open longer than the WFP broker's idle timeout.
        // Native sync uses its own client, so keep the existing session alive.
        if (!syncingWindows || windowsSyncKeepalive || !client.Connected) return;
        windowsSyncKeepalive = true;
        try
        {
            var response = await client.SendAsync(new("status"));
            if (!response.Success) throw new IOException(response.Message);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        { windowsSyncNotice = "The separate ZikWall filtering session lost its connection: " + ex.Message; }
        finally { windowsSyncKeepalive = false; }
    }
    private void RefreshWindowsSyncStatus()
    {
        var state = policy.WindowsSync;
        WindowsSyncStatus.Text = state is null ? "Windows Firewall sync is off. Use Sync Windows Firewall to review and link rules." :
            $"Windows Firewall linked to ‘{policy.Profiles.Single(x => x.Id == state.ProfileId).Name}’ · {state.Links.Count} rules · " +
            (state.Pending is not null ? "Interrupted sync: use Sync Windows Firewall to resume." : state.Automatic ? "Sync every 30 seconds while ZikWall is open." : "Manual sync.") +
            " Windows rules persist after closing ZikWall and apply on all Windows network profiles. Disabling session filtering does not remove them.";
        if (windowsSyncNotice.Length > 0) WindowsSyncStatus.Text += "\n" + windowsSyncNotice;
    }
    private async void WindowsSyncClicked(object sender, RoutedEventArgs args) => await ShowWindowsSyncAsync();
    private async Task ShowWindowsSyncAsync()
    {
        if (busy || dialogOpen || syncingWindows) return;
        Show(); busy = dialogOpen = syncingWindows = true;
        try
        {
            var before = policy;
            var state = policy.WindowsSync;
            if (state?.Pending is { } interrupted)
            {
                var resume = new ContentDialog { XamlRoot = Owner?.Content.XamlRoot ?? XamlRoot, RequestedTheme = ActualTheme,
                    Title = "Resume interrupted Windows Firewall sync", PrimaryButtonText = "Resume sync", SecondaryButtonText = "Disconnect sync", CloseButtonText = "Cancel",
                    Content = new ScrollViewer { MaxHeight = 440, Content = new TextBlock { TextWrapping = TextWrapping.Wrap,
                        Text = "Some Windows changes may already have completed. Resume checks each rule before continuing. If a rule changed again, restore its previewed version in Windows Firewall before resuming, or disconnect to abandon this journal. Disconnect leaves existing Windows rules in place.\n\n" +
                            string.Join("\n", interrupted.Changes.Select(x => $"{(x.After is null ? "Delete" : x.Before is null ? "Create" : "Update")}: {x.After?.Name ?? x.Before!.Name}")) } } };
                var result = await resume.ShowAsync();
                if (result == ContentDialogResult.Secondary) await DisconnectWindowsSyncAsync();
                else if (result == ContentDialogResult.Primary) await CommitWindowsSyncAsync(before, interrupted, state.Automatic, true);
                return;
            }
            if (state is not null && state.ProfileId != Selected.Id)
                throw new InvalidDataException("Select the linked profile before syncing or disconnecting Windows Firewall.");
            var native = await ReadWindowsAsync();
            var linked = (state?.Links ?? []).SelectMany(x => x.Windows).Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidates = native.Where(x => !linked.Contains(x.Name) && x.Problem.Length == 0 && !x.Description.StartsWith(WindowsFirewallSync.Marker, StringComparison.Ordinal)).OrderBy(x => x.Name).ToList();
            var imports = new ListView { ItemsSource = candidates, DisplayMemberPath = "Label", SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 180 };
            var choice = new ComboBox { Header = "When both apps changed the same rule", ItemsSource = new[] { "Pause and review conflicts", "Keep ZikWall version", "Keep Windows Firewall version" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            var automatic = new CheckBox { Content = "Sync this profile every 30 seconds while ZikWall is open", IsChecked = state?.Automatic == true };
            var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            var unsupported = native.Where(x => x.Problem.Length != 0).Select(x => x.Label).ToList();
            var content = new StackPanel { Spacing = 12, MaxWidth = 720 };
            content.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"Link profile ‘{Selected.Name}’ with Windows Firewall. All saved rules in this profile are included. Select existing Windows rules below to import and link them too. Edits, disabling, and deletions then sync both ways. Unselected Windows rules are untouched.\n\nWindows rules persist across app exit and reboot, and apply on Public, Private, and Domain networks regardless of ZikWall profile selection. Windows Firewall must be enabled for enforcement; existing Windows blocks may override allows. Sync does not change firewall defaults." });
            content.Children.Add(new TextBlock { Text = $"Import existing Windows rules ({candidates.Count} compatible)", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            content.Children.Add(imports); content.Children.Add(choice); content.Children.Add(automatic);
            if (unsupported.Count > 0) content.Children.Add(new Expander { Header = $"{unsupported.Count} Windows rules cannot be linked", Content = new ListView { ItemsSource = unsupported, SelectionMode = ListViewSelectionMode.None, MaxHeight = 180 } });
            content.Children.Add(preview);
            var dialog = new ContentDialog { XamlRoot = Owner?.Content.XamlRoot ?? XamlRoot, RequestedTheme = ActualTheme, Title = "Two-way Windows Firewall sync",
                Content = new ScrollViewer { Content = content, MaxHeight = 620 }, PrimaryButtonText = "Sync now", SecondaryButtonText = state is null ? "" : "Disconnect sync", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
            WindowsFirewallPlan? plan = null;
            void UpdatePreview()
            {
                plan = WindowsFirewallSync.Plan(Selected, state, native, imports.SelectedItems.Cast<WindowsFirewallEntry>(), (WindowsFirewallConflictChoice)choice.SelectedIndex);
                preview.Text = (plan.Problems.Count == 0 ? "Ready to sync." : "Resolve these issues before syncing:\n" + string.Join("\n", plan.Problems)) +
                    "\n\n" + (plan.Summary.Count == 0 ? "No rule changes." : string.Join("\n", plan.Summary));
                dialog.IsPrimaryButtonEnabled = plan.Ready;
            }
            imports.SelectionChanged += (_, _) => UpdatePreview(); choice.SelectionChanged += (_, _) => UpdatePreview(); UpdatePreview();
            windowsSyncDialog = dialog;
            var action = await dialog.ShowAsync();
            if (action == ContentDialogResult.Secondary) { await DisconnectWindowsSyncAsync(); return; }
            if (action != ContentDialogResult.Primary || plan is null || !plan.Ready) return;
            await CommitWindowsSyncAsync(before, new(plan.Rules, plan.Links, plan.Changes), automatic.IsChecked == true, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or Win32Exception or OperationCanceledException)
        {
            Error("Windows Firewall sync did not finish. " + ex.Message + (policy.WindowsSync?.Pending is null ? "" : " The recovery journal is saved; use Sync Windows Firewall to resume."));
        }
        finally { busy = dialogOpen = syncingWindows = false; windowsSyncDialog = null; nextWindowsSync = DateTimeOffset.UtcNow.AddSeconds(30); RefreshWindowsSyncStatus(); RulesChanged?.Invoke(); }
    }
    internal async Task ExerciseWindowsSyncCancelAsync()
    {
        Show();
        AddDraft(new(Guid.NewGuid(), "Disabled sync preview fixture", Environment.ProcessPath!, "127.0.0.1", 6, 59997, 3, true, false, null));
        var before = policy;
        var saved = File.ReadAllBytes(store.FilePath);
        var operation = ShowWindowsSyncAsync();
        for (int i = 0; i < 200 && windowsSyncDialog is null && !operation.IsCompleted; i++) await Task.Delay(50);
        if (windowsSyncDialog is not { } dialog) { await operation; throw new InvalidOperationException("Sync preview did not open: " + StatusMessage); }
        try
        {
            await Task.Delay(250);
            if (!dialog.IsPrimaryButtonEnabled || dialog.PrimaryButtonText != "Sync now") throw new InvalidOperationException("The compatible disabled fixture did not produce an actionable sync preview.");
        }
        finally { dialog.Hide(); }
        await operation;
        if (policy != before || policy.WindowsSync is not null || client.Connected || !File.ReadAllBytes(store.FilePath).SequenceEqual(saved))
            throw new InvalidOperationException("Cancelling Windows sync changed the policy or opened a filtering session.");
    }
    private async Task DisconnectWindowsSyncAsync()
    {
        var confirm = new ContentDialog { XamlRoot = Owner?.Content.XamlRoot ?? XamlRoot, RequestedTheme = ActualTheme, Title = "Disconnect Windows Firewall sync?",
            Content = "This forgets rule links and any pending recovery journal. Existing Windows Firewall rules remain active, including partially completed changes. Manage or remove those rules in Windows Firewall. ZikWall rules remain saved locally.",
            PrimaryButtonText = "Disconnect", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        var next = policy with { WindowsSync = null }; store.Save(next); policy = lastSaved = next;
        windowsSyncNotice = "";
        PolicyStatus.Title = "Windows Firewall sync disconnected"; PolicyStatus.Message = "Windows rules were retained. They can still allow or block traffic."; PolicyStatus.Severity = InfoBarSeverity.Informational;
    }
    private async Task CommitWindowsSyncAsync(PolicyDocument before, WindowsFirewallPending work, bool automatic, bool resume)
    {
        if (!ReferenceEquals(policy, before)) throw new InvalidDataException("Saved policy changed while reviewing sync. Open the preview again.");
        Guid profileId = before.WindowsSync?.ProfileId ?? before.SelectedProfile;
        var batch = new WindowsFirewallBatch(work.Changes, resume);
        WindowsFirewallSync.ValidateBatch(batch); BrokerWire.ValidateSize(new BrokerRequest("windows-sync", WindowsBatch: batch));
        var journal = before with { WindowsSync = new(profileId, before.WindowsSync?.Links ?? [], work, automatic) };
        // The entire intended local result and native before/after images are durable
        // before starting any persistent writes, including when UAC is cancelled.
        store.Save(journal); policy = lastSaved = journal;
        if (work.Changes.Count > 0)
        {
            using var syncClient = new FirewallClient();
            await syncClient.StartAsync();
            var response = await syncClient.SendAsync(new("windows-sync", WindowsBatch: batch));
            if (!response.Success) throw new IOException(response.Message);
        }
        var native = await ReadWindowsAsync();
        var nativeIndex = new WindowsFirewallIndex(native);
        var links = new List<WindowsFirewallLink>();
        foreach (var link in work.Links)
        {
            var actual = new List<WindowsFirewallEntry>();
            foreach (var expected in link.Windows)
            {
                var found = nativeIndex.Find(expected);
                if (found.Count != 1 || !WindowsFirewallSync.Same(expected, found[0])) throw new IOException($"{expected.Name}: changed during sync. The journal was retained.");
                actual.Add(found[0]);
            }
            links.Add(link with { Windows = actual });
        }
        var next = journal with { Profiles = journal.Profiles.Select(x => x.Id == profileId ? x with { Rules = work.Rules } : x).ToList(), WindowsSync = new(profileId, links, Automatic: automatic) };
        store.Save(next); policy = lastSaved = next;
        windowsSyncNotice = "";
        if (client.Connected && appliedProfile == profileId) pending = true;
        Refresh();
        PolicyStatus.Title = "Windows Firewall sync complete";
        PolicyStatus.Message = $"{links.Count} linked rules. Windows rules persist after ZikWall closes. " + (pending ? "Apply saved policy to update the separate ZikWall session filters." : "Other Windows rules and firewall defaults were retained.");
        PolicyStatus.Severity = InfoBarSeverity.Success;
    }
    private async Task AutoSyncWindowsAsync()
    {
        if (windowsSyncDiagnostic || dialogOpen || syncingWindows || DateTimeOffset.UtcNow < nextWindowsSync || policy.WindowsSync is not { Automatic: true, Pending: null } state) return;
        nextWindowsSync = DateTimeOffset.UtcNow.AddSeconds(30);
        syncingWindows = true;
        try
        {
            var before = policy;
            var profile = policy.Profiles.Single(x => x.Id == state.ProfileId);
            var plan = await Task.Run(() =>
            {
                using var windows = new WindowsFirewallStore();
                return WindowsFirewallPolling.Plan(windows, profile, state);
            });
            if (!plan.Ready) { windowsSyncNotice = "Automatic Windows Firewall sync needs review: " + string.Join(" ", plan.Problems.Take(3)); return; }
            windowsSyncNotice = "";
            if (plan.Summary.Count > 0) await CommitWindowsSyncAsync(before, new(plan.Rules, plan.Links, plan.Changes), true, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or Win32Exception or OperationCanceledException)
        { windowsSyncNotice = "Windows Firewall sync needs review: " + ex.Message; }
        finally { syncingWindows = false; }
    }
}
