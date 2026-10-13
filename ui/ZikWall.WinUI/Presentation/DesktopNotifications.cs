using Microsoft.Windows.AppNotifications;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Presentation;

public sealed class DesktopNotifications : IDisposable
{
    private bool registered;
    public Action? Invoked { get; set; }
    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) => Invoked?.Invoke();
    public void Enable()
    {
        if (registered) return;
        AppNotificationManager.Default.NotificationInvoked += OnInvoked;
        try { AppNotificationManager.Default.Register(); registered = true; }
        catch { AppNotificationManager.Default.NotificationInvoked -= OnInvoked; throw; }
    }
    public void Show(ConnectionNotification message)
    {
        if (!registered) return;
        var toast = new AppNotification(message.ToToastXml(Path.Combine(AppContext.BaseDirectory, "Assets"))) { Tag = "recent-connections", Group = "monitor" };
        AppNotificationManager.Default.Show(toast);
    }
    public async Task ClearRecentAsync()
    {
        if (registered) await AppNotificationManager.Default.RemoveByTagAndGroupAsync("recent-connections", "monitor");
    }
    public void Dispose() { if (registered) { AppNotificationManager.Default.Unregister(); AppNotificationManager.Default.NotificationInvoked -= OnInvoked; registered = false; } }
}
