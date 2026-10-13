using ZikWall.WinUI.Models;

namespace ZikWall.WinUI;

public sealed partial class MonitoringView
{
    private string? preferredAdapterId;
    private bool rememberUsage, rememberDomains;
    private void LoadPreferences()
    {
        var saved = preferencesStore.Load();
        HideSystemApps.IsOn = saved.HideWindowsSystemApps;
        GroupChoice.SelectedIndex = saved.Group;
        DirectionChoice.SelectedIndex = saved.Direction;
        ScopeChoice.SelectedIndex = saved.Scope;
        RefreshChoice.SelectedIndex = saved.Refresh;
        HistoryWindow.SelectedIndex = saved.HistoryRange;
        RecentAlerts.IsOn = saved.RecentAlerts;
        NotificationTypeChoice.SelectedIndex = saved.NotificationType;
        DesktopAlerts.IsOn = saved.DesktopAlerts;
        SaveHistory.IsOn = saved.SaveHistory;
        TcpDetails.IsOn = saved.TcpDetails;
        ReverseDnsLookup.IsOn = saved.ReverseDns;
        preferredAdapterId = saved.AdapterId;
        usageSort = (UsageSort)saved.UsageSort;
        rememberUsage = saved.UsageCollection;
        rememberDomains = saved.DomainAccess;
    }
    private void SavePreferences()
    {
        if (!preferencesReady || diagnostic) return;
        try
        {
            preferencesStore.Save(new(HideSystemApps.IsOn, GroupChoice.SelectedIndex, DirectionChoice.SelectedIndex,
                ScopeChoice.SelectedIndex, RefreshChoice.SelectedIndex, HistoryWindow.SelectedIndex,
                RecentAlerts.IsOn, DesktopAlerts.IsOn, SaveHistory.IsOn, preferredAdapterId, (int)usageSort,
                rememberUsage, rememberDomains, TcpDetails.IsOn, NotificationTypeChoice.SelectedIndex, ReverseDnsLookup.IsOn));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MonitorStatus.Text = $"Settings apply for now but could not be saved: {ex.Message}"; }
    }
    private async Task RestoreSessionsAsync()
    {
        if (diagnostic) return;
        // Await one prompt at a time. Failed/cancelled elevation never marks a helper active.
        if (rememberDomains) await ToggleDomainAsync();
        if (!closed && rememberUsage && usageClient is null && !usageBusy) await ToggleUsageAsync();
    }
}
