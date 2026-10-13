using ZikWall.WinUI.Models;

namespace ZikWall.WinUI;

public sealed partial class MonitoringView
{
    private readonly ActivityUsageWindow overviewUsage = new();
    public event Action? ActivityOverviewChanged;
    public void UpdateOverview(ActivityOverviewView view)
    {
        var entries = current.Where(IsVisibleApp).ToList();
        var events = archive.Events.Where(x => IsVisibleApp(x.Connection))
            .Select(x => enrichment is null ? x : x with { Connection = enrichment.WithDomainHint(x.Connection) }).ToList();
        view.Update(entries, events, overviewUsage.Samples, usage.Days, overviewUsage.ObservedSince, usageEnabled, UsageStatus.Text);
    }
    public Task EnableOverviewUsageAsync() => usageEnabled ? Task.CompletedTask : ToggleUsageAsync();
}
