using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace ZikWall.WinUI.Presentation;

// Explicit diagnostics: no firewall, DNS, public lookup or saved activity changes.
internal static class MonitorSelfTest
{
    public static async Task RunAsync(Window window, bool map = false)
    {
        var results = new Dictionary<string, object>();
        try
        {
            await Task.Delay(map ? 400 : 6000);
            var icon = await AppIcon.LoadAsync(Environment.ProcessPath!);
            results["ExecutableIconDecoded"] = icon is { PixelWidth: > 0, PixelHeight: > 0 };
            var flags = new List<string>();
            foreach (string path in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Assets", "Flags"), "*.svg"))
            {
                using var stream = await (await StorageFile.GetFileFromPathAsync(path)).OpenReadAsync();
                var source = new SvgImageSource();
                if (await source.SetSourceAsync(stream) != SvgImageSourceLoadStatus.Success) flags.Add(Path.GetFileName(path));
            }
            results["FailedFlagDecodes"] = flags;
            if (!map) using (var notifications = new DesktopNotifications())
            {
                notifications.Enable();
                results["WindowsNotificationsRegistered"] = true;
                // Silent, explicitly labeled test of the requested Windows notifications.
                var sample = new Models.ConnectionEntry("TCPv6", "::1", 50000, "2001:db8::1", 443, 1,
                    "Established", "Example browser", Direction: "Outgoing", CountryCode: "US", CountryName: "United States", NetworkScope: "Internet");
                var message = Models.ConnectionNotification.From(new([sample], 1, Models.ConnectionNotificationType.NewApps));
                results["NotificationXml"] = message.ToToastXml(Path.Combine(AppContext.BaseDirectory, "Assets"));
                notifications.Show(message with { Title = "Notification test: " + message.Title });
                results["WindowsNotificationSubmitted"] = true;
            }
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync((UIElement)window.Content);
            byte[] pixels = (await bitmap.GetPixelsAsync()).ToArray();
            var target = await (await StorageFolder.GetFolderFromPathAsync(AppContext.BaseDirectory)).CreateFileAsync(map ? "map-preview.png" : "monitor-preview.png", CreationCollisionOption.ReplaceExisting);
            using var output = await target.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync();
            results["RenderedWidth"] = bitmap.PixelWidth;
            results["RenderedHeight"] = bitmap.PixelHeight;
        }
        catch (Exception ex) { results["Error"] = ex.ToString(); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, map ? "map-selftest.json" : "monitor-selftest.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
}
