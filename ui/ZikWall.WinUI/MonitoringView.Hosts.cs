using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI;

public sealed partial class MonitoringView
{
    private Flyout? hostsFlyout;
    private TextBlock? hostsTitle;
    private ListView? hostsList;

    private void HostsClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: HostSummary summary } button || !summary.Available) return;
        // One lazily created popup, shared by all rows. Closed rows must not
        // construct ListViews and bind their host collections on every refresh.
        if (hostsFlyout is null)
        {
            hostsTitle = new() { Style = (Style)Resources["SectionTitle"] };
            hostsList = new() { MaxHeight = 360, SelectionMode = ListViewSelectionMode.None,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, ItemTemplate = (DataTemplate)Resources["HostEntryTemplate"] };
            var content = new StackPanel { Spacing = 10, Width = 420 };
            content.Children.Add(hostsTitle);
            content.Children.Add(new TextBlock { Text = "Names are DNS hints. Point to a host to see its source. IP addresses identify each host.",
                Style = (Style)Resources["StatLabel"], TextWrapping = TextWrapping.Wrap });
            content.Children.Add(hostsList);
            hostsFlyout = new() { Placement = FlyoutPlacementMode.Bottom, Content = content };
            hostsFlyout.Closed += (_, _) => hostsList.ItemsSource = null;
        }
        hostsTitle!.Text = summary.Title;
        hostsList!.ItemsSource = summary.Items;
        hostsFlyout.ShowAt(button);
    }
}
