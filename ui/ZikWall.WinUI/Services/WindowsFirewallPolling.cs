using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Services;

public static class WindowsFirewallPolling
{
    public static WindowsFirewallPlan Plan(IWindowsFirewallStore store, NetworkProfile profile, WindowsFirewallSyncState state) =>
        WindowsFirewallSync.Plan(profile, state, store.ReadForSync(state.Links.SelectMany(x => x.Windows).ToList()), []);
}
