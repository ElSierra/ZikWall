using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI;

public sealed partial class MonitoringView
{
    private Window? hostsWindow;
    private TextBlock? hostsTitle;
    private ListView? hostsList;

    private void HostsClicked(object sender, RoutedEventArgs args)
    {
        if (Owner is null || sender is not Button { Tag: HostSummary summary } || !summary.Available) return;
        // A single virtualized host list is shared by rows and released on close.
        hostsWindow = Presentation.ToolWindows.For(Owner).Show("hosts", "Connection hosts", () =>
        {
            hostsTitle = new() { Style = (Style)Resources["SectionTitle"], TextWrapping = TextWrapping.Wrap };
            hostsList = new() { SelectionMode = ListViewSelectionMode.None,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, ItemTemplate = (DataTemplate)Resources["HostEntryTemplate"] };
            var content = new Grid { RowSpacing = 10 };
            content.RowDefinitions.Add(new() { Height = GridLength.Auto });
            content.RowDefinitions.Add(new() { Height = GridLength.Auto });
            content.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
            content.Children.Add(hostsTitle);
            var hint = new TextBlock { Text = "Snapshot when selected. Names are DNS hints. Point to a host to see its source. IP addresses identify each host.",
                Style = (Style)Resources["StatLabel"], TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(hint, 1); content.Children.Add(hint);
            Grid.SetRow(hostsList, 2); content.Children.Add(hostsList);
            return content;
        }, scroll: false, closed: () => { hostsList!.ItemsSource = null; hostsList = null; hostsTitle = null; hostsWindow = null; });
        hostsTitle!.Text = summary.Title;
        hostsList!.ItemsSource = summary.Items;
    }
}
