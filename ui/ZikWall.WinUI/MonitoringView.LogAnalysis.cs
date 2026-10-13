using ZikWall.WinUI.Models;

namespace ZikWall.WinUI;

public sealed partial class MonitoringView
{
    public event Action? LogChanged;
    public IReadOnlyList<ConnectionEvent> LogEvents => archive.Events;
    public bool IsLogEntryVisible(ConnectionEntry entry) => IsVisibleApp(entry);
    public ConnectionEntry EnrichLogEntry(ConnectionEntry entry) => enrichment?.WithDomainHint(entry) ?? entry;
    public bool IsHistorySaved => SaveHistory.IsOn;
}
