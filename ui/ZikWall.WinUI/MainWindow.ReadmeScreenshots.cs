using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI;

public sealed partial class MonitoringView
{
    internal IReadOnlyList<ConnectionEntry> SetReadmeFixture()
    {
        // Only the explicit documentation diagnostic calls this. All endpoints and
        // amounts are illustrative; no real connection history goes into screenshots.
        var apps = new[] {
            ("Google Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", 12),
            ("Microsoft Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", 8),
            ("Firefox", @"C:\Program Files\Mozilla Firefox\firefox.exe", 6),
            ("Visual Studio Code", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Microsoft VS Code\Code.exe"), 5),
            ("Example Sync", @"C:\Example\sync.exe", 3)
        };
        var countries = new[] { ("US", "United States"), ("GB", "United Kingdom"), ("DE", "Germany"), ("CA", "Canada"), ("NL", "Netherlands") };
        var hosts = new[] { "news.example.net", "docs.example.net", "packages.example.net", "cdn.example.net", "sync.example.net" };
        current = apps.SelectMany((app, i) => Enumerable.Range(0, app.Item3).Select(j => new ConnectionEntry(
            "TCPv4", "192.0.2.10", 50000 + i * 100 + j, $"203.0.113.{i * 20 + j + 1}", j % 4 == 0 ? 80 : 443,
            4200 + i, "Established", app.Item1, app.Item2, "Outgoing", countries[(i + j) % countries.Length].Item1,
            countries[(i + j) % countries.Length].Item2, "Internet", DomainHint: j == 0 ? hosts[i] : $"service-{j}.{hosts[i]}", NetworkName: "Example network"))).ToList();
        CaptureCount = 1;
        fresh.Clear();
        ScopeChoice.SelectedIndex = 0;
        GroupChoice.SelectedIndex = 0;
        var now = DateTimeOffset.Now;
        archive = new(current.GroupBy(x => x.AppKey).Select((app, i) => new ConnectionEvent(now.AddMinutes(-i - 1), "First seen", app.First())).ToList(),
            Enumerable.Range(0, 30).Select(i => new TrafficSample(now.AddSeconds(i * 5 - 145), "example-adapter", "Wi-Fi",
                (0.3 + 0.4 * Math.Abs(Math.Sin(i * 0.7))) * 1024 * 1024, (20 + i % 7 * 4) * 1024)).ToList());
        AdapterChoice.ItemsSource = new[] { new AdapterCounters("example-adapter", "Wi-Fi", 0, 0) };
        AdapterChoice.SelectedIndex = 0;
        DrawTraffic();
        UpdateLists();
        if (activityTree.Rows.FirstOrDefault(x => x.Expandable && x.Title.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) is { } group)
        { activityTree.Toggle(group); UpdateLists(); }
        MonitorStatus.Text = "Illustrative connection data · Windows Firewall unchanged";
        return current;
    }
}

public sealed partial class LogAnalysisView
{
    internal void PrepareReadmeScreenshot()
    {
        readThrough = DateTimeOffset.MinValue;
        CategoryChoice.SelectedIndex = (int)LogFilter.Important;
        Refresh();
    }
}

public sealed partial class MainWindow
{
    internal async Task CaptureReadmeScreenshotsAsync()
    {
        await MonitorPanel.PauseForDiagnosticAsync();
        var rows = MonitorPanel.SetReadmeFixture();
        var now = DateTimeOffset.Now;
        var amounts = rows.GroupBy(x => x.AppKey).Select((app, i) => new ActivityUsageDelta(now.AddSeconds(-10), app.Key,
            app.First().ProcessPath, (12 - i * 2) * 1024 * 1024, (5 - i) * 384 * 1024)).ToArray();
        var output = await (await StorageFolder.GetFolderFromPathAsync(AppContext.BaseDirectory)).CreateFolderAsync("readme-screenshots", CreationCollisionOption.OpenIfExists);
        foreach (var theme in new[] { ElementTheme.Dark, ElementTheme.Light })
        {
            Root.RequestedTheme = theme;
            // Native offscreen rendering omits compositor Mica. Use its opaque
            // neutral fallback solely for these documentation captures.
            Root.Background = new SolidColorBrush(theme == ElementTheme.Dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243));
            foreach (string page in new[] { "connections", "activity", "logs" })
            {
                Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(x => x.Tag?.ToString() == page);
                if (page == "activity") ActivityPanel!.Update(rows, [], amounts, [], now.AddMinutes(-5), true);
                if (page == "logs") LogPanel!.PrepareReadmeScreenshot();
                Root.UpdateLayout(); await Task.Delay(650);
                var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(Root);
                var file = await output.CreateFileAsync($"{page}-{theme.ToString().ToLowerInvariant()}.png", CreationCollisionOption.ReplaceExisting);
                using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, (await bitmap.GetPixelsAsync()).ToArray());
                await encoder.FlushAsync();
            }
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "readme-screenshots.txt"), "PASS: six current native ZikWall views rendered in light and dark themes using illustrative data. No policy applied.");
    }
}
