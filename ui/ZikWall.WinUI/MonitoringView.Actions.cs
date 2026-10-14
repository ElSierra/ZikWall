using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

namespace ZikWall.WinUI;

public sealed partial class MonitoringView
{
    private bool rowActionBusy;
    private MenuFlyout? activeRowMenu;
    private static ActivityRow HistoryRow(ConnectionEntry entry) => new("connection:" + entry.Key, entry.ProcessLabel,
        entry.RemoteEndpoint, "", 0, entry, entry.Direction, entry.RemoteEndpoint, entry.NetworkLabel, entry.CountryFlag, entry.State);
    private ActivityRow? SelectedActionRow() => history
        ? HistoryList.SelectedItem is ConnectionEvent item ? HistoryRow(item.Connection) : null
        : ConnectionList.SelectedItem as ActivityRow;

    private void ConnectionsContextRequested(UIElement sender, ContextRequestedEventArgs args) => ShowRowMenu(ConnectionList, args);
    private void HistoryContextRequested(UIElement sender, ContextRequestedEventArgs args) => ShowRowMenu(HistoryList, args);
    internal static object? ContextItem(ListView list, DependencyObject? source, bool pointer)
    {
        while (source is not null && source != list && source is not ListViewItem) source = VisualTreeHelper.GetParent(source);
        return source is ListViewItem container ? list.ItemFromContainer(container) : !pointer ? list.SelectedItem : null;
    }
    private void ShowRowMenu(ListView list, ContextRequestedEventArgs args)
    {
        if (rowActionBusy) { args.Handled = true; return; }
        bool pointer = args.TryGetPosition(list, out var point);
        // A pointer on empty space must not act on a previously selected row.
        object? item = ContextItem(list, args.OriginalSource as DependencyObject, pointer);
        var row = item as ActivityRow ?? (item is ConnectionEvent entry ? HistoryRow(entry.Connection) : null);
        if (row is null) return;
        list.SelectedItem = item;
        var target = MonitorActionTarget.Capture(row, current);
        activeRowMenu?.Hide();
        var menu = CreateRowMenu(target);
        activeRowMenu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(activeRowMenu, menu)) activeRowMenu = null; };
        args.Handled = true;
        if (pointer) menu.ShowAt(list, new FlyoutShowOptions { Position = point });
        else menu.ShowAt(list.ContainerFromItem(item) as FrameworkElement ?? list);
    }

    internal MenuFlyout CreateRowMenu(MonitorActionTarget target)
    {
        var menu = new MenuFlyout();
        void Add(string text, Func<Task> action, bool enabled = true, string? hint = null)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
            if (hint is not null) ToolTipService.SetToolTip(item, hint);
            item.Click += async (_, _) => { menu.Hide(); await RunRowActionAsync(action); };
            menu.Items.Add(item);
        }
        void Block(bool connection)
        {
            string label = connection ? "Block this connection…" : target.IsApp ? "Block this app…" : "Block this executable…";
            try
            {
                var plan = target.Block(connection);
                Add(label, () => SubmitBlockAsync(plan), BlockRequested is not null);
            }
            catch (InvalidDataException ex) { Add(label, () => Task.CompletedTask, false, ex.Message); }
        }
        var row = target.Row;
        if (row.Expandable)
        {
            Add(row.Chevron == "›" ? "Expand" : "Collapse", () =>
            {
                var present = activityTree.Rows.FirstOrDefault(x => x.Key == row.Key);
                if (present is not null) { activityTree.Toggle(present); UpdateLists(); }
                return Task.CompletedTask;
            });
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        if (target.IsConnection) Block(true);
        Block(false);
        Add("Manage firewall rules", () => { ManageRulesRequested?.Invoke(); return Task.CompletedTask; });
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(row.Expandable ? "Details and app usage" : "Connection details", () => ShowDetailsAsync(row));
        Add("Inspect executable…", () => InspectTargetAsync(row.Entry.ProcessPath), row.Entry.ProcessPath.Length > 0);
        Add("Open file location", async () =>
        {
            string path = Path.GetDirectoryName(row.Entry.ProcessPath) ?? "";
            if (!Directory.Exists(path) || !await Windows.System.Launcher.LaunchFolderPathAsync(path))
                throw new IOException("The executable's folder is unavailable.");
        }, row.Entry.ProcessPath.Length > 0);
        var copy = new MenuFlyoutSubItem { Text = "Copy" };
        void Copy(string label, string value, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = label, IsEnabled = enabled && value.Length > 0 };
            item.Click += async (_, _) => { menu.Hide(); await RunRowActionAsync(() =>
            {
                var data = new DataPackage(); data.SetText(value); Clipboard.SetContent(data);
                ActionNotice($"Copied {label.ToLowerInvariant()}."); return Task.CompletedTask;
            }); };
            copy.Items.Add(item);
        }
        if (target.IsConnection)
        {
            Copy("Remote IP address", row.Entry.RemoteAddress, row.Entry.RemotePort is > 0);
            Copy("Remote endpoint", row.Entry.RemoteEndpoint, row.Entry.RemotePort is > 0);
            Copy("Local endpoint", row.Entry.LocalEndpoint);
        }
        Copy("Executable path", row.Entry.ProcessPath);
        if (!target.IsApp) Copy("Process ID", row.Entry.ProcessId.ToString());
        menu.Items.Add(copy);
        return menu;
    }

    private async Task BlockTargetAsync(MonitorActionTarget target, bool connection) => await SubmitBlockAsync(target.Block(connection));
    private async Task SubmitBlockAsync(MonitorBlockPlan plan)
    {
        if (BlockRequested is null) throw new InvalidOperationException("The firewall action is unavailable.");
        if (await BlockRequested(plan) is { } message) ActionNotice(message);
    }
    private async Task InspectTargetAsync(string path)
    {
        var identity = await ExecutableIdentity.InspectAsync(path);
        if (!closed && Owner is not null) Presentation.ToolWindows.For(Owner).Text("identity:" + path, "Executable identity",
            $"{identity.Path}\n\n{identity.TrustDescription}\n\nSHA-256: {identity.Sha256}\n\nThis describes the file at inspection time. Firewall rules match paths, not signatures or hashes.");
    }
    private async Task RunRowActionAsync(Func<Task> action)
    {
        if (rowActionBusy) return;
        rowActionBusy = true;
        try { await action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or
            System.Runtime.InteropServices.COMException or System.Security.Cryptography.CryptographicException)
        { ActionNotice(ex.Message, true); }
        finally { rowActionBusy = false; }
    }
    private void ActionNotice(string message, bool failed = false)
    {
        ActionBanner.Title = failed ? "Action unavailable" : "Connection action";
        ActionBanner.Message = message;
        ActionBanner.Severity = failed ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        ActionBanner.IsOpen = true;
    }

    internal async Task ExerciseContextMenuAsync()
    {
        await PauseForDiagnosticAsync();
        int checks = 0;
        void Check(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); checks++; }
        var fixture = new ConnectionEntry("TCPv4", "127.0.0.1", 50123, "8.8.8.8", 443, Environment.ProcessId,
            "Established", "Context fixture", Environment.ProcessPath!, "Outgoing", NetworkScope: "Internet", ProcessStarted: 123);
        current = [fixture]; UpdateLists(); ConnectionList.UpdateLayout();
        var app = activityTree.Rows.Single();
        ConnectionList.SelectedItem = app;
        var container = (ListViewItem)ConnectionList.ContainerFromItem(app);
        Check(ContextItem(ConnectionList, VisualTreeHelper.GetChild(container, 0), true) as ActivityRow == app, "Pointer must target its realized row.");
        Check(ContextItem(ConnectionList, ConnectionList, true) is null, "Blank-space right-click must not reuse selection.");
        Check(ContextItem(ConnectionList, ConnectionList, false) as ActivityRow == app, "Keyboard context action must use selection.");
        var target = MonitorActionTarget.Capture(app, current);
        var menu = CreateRowMenu(target);
        async Task Invoke(string label)
        {
            menu.ShowAt(ConnectionList); await Task.Delay(150);
            var item = menu.Items.OfType<MenuFlyoutItem>().Single(x => x.Text == label);
            var peer = new Microsoft.UI.Xaml.Automation.Peers.MenuFlyoutItemAutomationPeer(item);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
            await Task.Delay(150);
        }
        await Invoke("Expand");
        Check(activityTree.Rows.Count == 2, "Native Expand invocation must reveal the subprocess.");
        var process = activityTree.Rows[1];
        menu = CreateRowMenu(MonitorActionTarget.Capture(process, current));
        Check(menu.Items.OfType<MenuFlyoutItem>().Any(x => x.Text == "Block this executable…" && x.IsEnabled), "Process must offer an explicitly executable-scoped block.");
        await Invoke("Expand");
        var leaf = activityTree.Rows.Single(x => !x.Expandable);
        menu = CreateRowMenu(MonitorActionTarget.Capture(leaf, current));
        var previous = BlockRequested;
        MonitorBlockPlan? captured = null;
        // Exercise the native command route without saving policy, UAC or filtering.
        BlockRequested = plan => { captured = plan; return Task.FromResult<string?>("Diagnostic only: no firewall changes."); };
        try
        {
            current = [fixture with { RemoteAddress = "1.1.1.1", ProcessPath = @"C:\Other\other.exe" }]; UpdateLists();
            await Invoke("Block this connection…");
            Check(captured?.Rules.Single().RemoteNetwork == "8.8.8.8" && captured.Rules[0].Application == fixture.ProcessPath,
                "Menu invocation after refresh must retain original app and endpoint.");
            menu = CreateRowMenu(MonitorActionTarget.Capture(HistoryRow(fixture with { Direction = "Unknown" }), []));
            Check(!menu.Items.OfType<MenuFlyoutItem>().Single(x => x.Text == "Block this connection…").IsEnabled,
                "Unknown direction must disable connection blocking.");
            Check(menu.Items.OfType<MenuFlyoutSubItem>().Single().Items.Count == 5, "Connection copy menu must expose endpoints, executable and PID.");
        }
        finally { menu.Hide(); BlockRequested = previous; }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "context-menu-selftest.txt"), $"PASS: {checks} native context-menu routing, invocation and stale-target checks.");
    }
}
