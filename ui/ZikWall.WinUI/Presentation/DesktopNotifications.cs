using Microsoft.Windows.AppNotifications;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Presentation;

public sealed class DesktopNotifications : IDisposable
{
    private bool registered;
    private int generation;
    public Action? Invoked { get; set; }
    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) => Invoked?.Invoke();
    public void Enable()
    {
        if (registered) return;
        AppNotificationManager.Default.NotificationInvoked += OnInvoked;
        try { AppNotificationManager.Default.Register(); registered = true; }
        catch { AppNotificationManager.Default.NotificationInvoked -= OnInvoked; throw; }
    }
    internal static async Task<string> CreateToastXmlAsync(ConnectionNotification message, string? iconDirectory = null)
    {
        iconDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZikWall", "NotificationIcons");
        string? icon = await AppIcon.SaveNotificationIconAsync(message.ProcessPath, iconDirectory);
        return message.ToToastXml(Path.Combine(AppContext.BaseDirectory, "Assets"), icon);
    }
    public async Task ShowAsync(ConnectionNotification message)
    {
        if (!registered) return;
        int request = ++generation;
        string xml = await CreateToastXmlAsync(message);
        if (!registered || request != generation) return;
        var toast = new AppNotification(xml) { Tag = "recent-connections", Group = "monitor" };
        AppNotificationManager.Default.Show(toast);
    }
    public async Task ClearRecentAsync()
    {
        generation++;
        if (registered) await AppNotificationManager.Default.RemoveByTagAndGroupAsync("recent-connections", "monitor");
    }
    public void Dispose() { generation++; if (registered) { AppNotificationManager.Default.Unregister(); AppNotificationManager.Default.NotificationInvoked -= OnInvoked; registered = false; } }
}
