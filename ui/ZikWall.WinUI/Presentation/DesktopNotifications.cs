using Microsoft.Windows.AppNotifications;
using System.Security;

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
    public void Show(string message)
    {
        if (!registered) return;
        var toast = new AppNotification($"<toast duration=\"short\"><visual><binding template=\"ToastGeneric\"><text>ZikWall · Recent connections</text><text>{SecurityElement.Escape(message)}</text></binding></visual><audio silent=\"true\"/></toast>") { Tag = "recent-connections", Group = "monitor" };
        AppNotificationManager.Default.Show(toast);
    }
    public void Dispose() { if (registered) { AppNotificationManager.Default.Unregister(); AppNotificationManager.Default.NotificationInvoked -= OnInvoked; registered = false; } }
}
