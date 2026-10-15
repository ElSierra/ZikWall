using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI;

public sealed partial class LogAnalysisView
{
    private async Task ExerciseLogActionsAsync(LogEntry first, LogEntry second)
    {
        static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
        {
            if (root is T item) yield return item;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                foreach (var child in Children<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
        }
        static void Invoke(FrameworkElement element)
        {
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(element);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        }
        var manager = Presentation.ToolWindows.For(Owner!);
        var unrelated = manager.Text("log-test-unrelated", "Unrelated tool", "Keep this tool open.");
        var original = monitor!.BlockRequested;
        MonitorBlockPlan? captured = null;
        monitor.BlockRequested = plan => { captured = plan; return Task.FromResult<string?>("Diagnostic only; no firewall changes."); };
        MenuFlyout? menu = null;
        try
        {
            OpenLog(first);
            await Task.Delay(150);
            var initial = logDetailWindow!;
            OpenLog(second);
            await Task.Delay(150);
            if (manager.Open.Contains(initial) || !manager.Open.Contains(unrelated) || manager.Count != 2)
                throw new InvalidOperationException("Replacing a log must close only its previous detail window.");
            var buttons = Children<Button>(logDetailWindow!.Content).ToArray();
            var blockApp = buttons.Single(x => x.Content as string == "Block application…");
            var blockConnection = buttons.Single(x => x.Content as string == "Block this connection…");
            var more = buttons.Single(x => x.Content as string == "More actions");
            if (more.Flyout is not MenuFlyout) throw new InvalidOperationException("Log details need the shared action menu.");
            Invoke(blockApp); await Task.Delay(100);
            if (captured?.Rules.Single().Application != second.Connection.ProcessPath || captured.Rules[0].RemoteNetwork != "" || captured.Rules[0].Direction != 3)
                throw new InvalidOperationException("Detail app block must match the recorded executable in both directions.");
            captured = null;
            Invoke(blockConnection); await Task.Delay(100);
            if (captured?.Rules.Single().RemoteNetwork != second.Connection.RemoteAddress || captured.Rules[0].RemotePort != second.Connection.RemotePort)
                throw new InvalidOperationException("Detail connection block must match the recorded endpoint.");
            if (!LogActionBanner.IsOpen || !Children<InfoBar>(logDetailWindow.Content).Any(x => x.IsOpen))
                throw new InvalidOperationException("Log action results must be visible in the timeline and detail window.");
            await SavePreviewAsync(logDetailWindow.Content, "log-detail-actions-preview.png");

            var currentRow = timeline.SelectMany(x => x).First(x => x.Source == first.Source);
            EventList.SelectedItem = currentRow;
            EventList.UpdateLayout();
            var container = (ListViewItem)EventList.ContainerFromItem(currentRow);
            if (!ReferenceEquals(MonitoringView.ContextItem(EventList, VisualTreeHelper.GetChild(container, 0), true), currentRow) ||
                MonitoringView.ContextItem(EventList, EventList, true) is not null ||
                !ReferenceEquals(MonitoringView.ContextItem(EventList, EventList, false), currentRow))
                throw new InvalidOperationException("Log context routing must honor pointer rows, blank space and keyboard selection.");
            int unread = timeline.SelectMany(x => x).Count(x => x.IsUnread);
            menu = CreateLogMenu(currentRow);
            if (timeline.SelectMany(x => x).Count(x => x.IsUnread) != unread)
                throw new InvalidOperationException("Opening a log menu must not mark an event read.");
            foreach (string name in new[] { "Block application…", "Block this connection…", "Manage firewall rules", "Log details", "Inspect executable…", "Open file location" })
                if (!menu.Items.OfType<MenuFlyoutItem>().Any(x => x.Text == name && x.IsEnabled))
                    throw new InvalidOperationException("Missing enabled log menu action: " + name);
            if (menu.Items.OfType<MenuFlyoutSubItem>().Single(x => x.Text == "Copy").Items.Count != 5)
                throw new InvalidOperationException("Log copy menu must include endpoints, path and PID.");
            // Open a different event after capturing the menu target.
            OpenLog(second);
            async Task MenuAction(string name)
            {
                menu.ShowAt(EventList); await Task.Delay(100);
                Invoke(menu.Items.OfType<MenuFlyoutItem>().Single(x => x.Text == name));
                await Task.Delay(100);
            }
            captured = null;
            await MenuAction("Block this connection…");
            if (captured?.Rules.Single().Application != first.Connection.ProcessPath || captured.Rules[0].RemoteNetwork != first.Connection.RemoteAddress)
                throw new InvalidOperationException("A log menu must not retarget after selection changes.");
            await MenuAction("Log details");
            if (manager.Count != 2 || !Children<TextBlock>(logDetailWindow!.Content).Any(x => x.Text == first.Connection.Application))
                throw new InvalidOperationException("Context details must replace the current log with the captured event.");
            monitor.BlockRequested = _ => throw new IOException("Diagnostic action failure");
            await MenuAction("Block application…");
            if (LogActionBanner.Severity != InfoBarSeverity.Warning || !LogActionBanner.Message.Contains("Diagnostic action failure"))
                throw new InvalidOperationException("Action failure must be visible on the log page.");
            var unknown = monitor.CreateLogMenu(first.Connection with { Direction = "Unknown" }, () => { }, (_, _) => { });
            if (unknown.Items.OfType<MenuFlyoutItem>().Single(x => x.Text == "Block this connection…").IsEnabled)
                throw new InvalidOperationException("Unknown historical direction must disable connection blocking.");
            var missing = monitor.CreateLogBlockButton(first.Connection with { ProcessPath = "" }, false, (_, _) => { });
            if (missing.IsEnabled) throw new InvalidOperationException("A log without an executable path must not offer app blocking.");
        }
        finally
        {
            menu?.Hide(); monitor.BlockRequested = original;
            logDetailWindow?.Close(); unrelated.Close(); LogActionBanner.IsOpen = false;
        }
    }
}
