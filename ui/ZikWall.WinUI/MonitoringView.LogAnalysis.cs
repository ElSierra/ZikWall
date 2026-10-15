using ZikWall.WinUI.Models;
using Microsoft.UI.Xaml.Controls;

namespace ZikWall.WinUI;

public sealed partial class MonitoringView
{
    internal MenuFlyout CreateLogMenu(ConnectionEntry entry, Action details, Action<string, bool> notice) =>
        CreateRowMenu(MonitorActionTarget.Capture(HistoryRow(entry), []),
            () => { details(); return Task.CompletedTask; }, notice, "Block application…");

    internal Button CreateLogBlockButton(ConnectionEntry entry, bool connection, Action<string, bool> notice)
    {
        var button = new Button { Content = connection ? "Block this connection…" : "Block application…" };
        try
        {
            var plan = MonitorActionTarget.Capture(HistoryRow(entry), []).Block(connection);
            button.IsEnabled = BlockRequested is not null;
            button.Click += async (_, _) => await RunRowActionAsync(() => SubmitBlockAsync(plan, notice), notice);
        }
        catch (InvalidDataException ex) { button.IsEnabled = false; ToolTipService.SetToolTip(button, ex.Message); }
        return button;
    }
    public event Action? LogChanged;
    public IReadOnlyList<ConnectionEvent> LogEvents => archive.Events;
    public bool IsLogEntryVisible(ConnectionEntry entry) => IsVisibleApp(entry);
    public ConnectionEntry EnrichLogEntry(ConnectionEntry entry) => enrichment?.WithDomainHint(entry) ?? entry;
    public bool IsHistorySaved => SaveHistory.IsOn;
    internal string? SavedLogDirectory => diagnostic ? null : Path.GetDirectoryName(store.FilePath);
    internal Func<ConnectionEntry, bool> CaptureLogVisibility()
    {
        bool hideSystemApps = HideSystemApps.IsOn;
        return entry => !hideSystemApps || !systemApps.IsSystem(entry);
    }
}
