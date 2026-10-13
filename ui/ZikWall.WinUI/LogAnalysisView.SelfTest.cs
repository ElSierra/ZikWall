using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI;

public sealed partial class MonitoringView
{
    internal void SetLogFixture()
    {
        var local = DateTimeOffset.Now;
        var now = new DateTimeOffset(local.Date.AddHours(12), local.Offset);
        var browser = new ConnectionEntry("TCPv4", "192.168.1.2", 50000, "8.8.8.8", 443, Environment.ProcessId,
            "Established", "Example browser", Environment.ProcessPath!, "Outgoing", "US", "United States", "Internet", DomainHint: "dns.google");
        archive = new([
            new(now.AddMinutes(-1), "First seen", browser),
            new(now.AddMinutes(-2), "First seen", browser with { ProcessName = "Editor", ProcessPath = @"C:\Fixture\editor.exe", DomainHint = "updates.example" }),
            new(now.AddMinutes(-3), "SynSent → Established", browser),
            new(now.AddMinutes(-4), "No longer observed", browser),
            new(new DateTimeOffset(now.Date.AddDays(-1).AddHours(12), now.Offset), "First seen", browser with { ProcessName = "Yesterday app", ProcessPath = @"C:\Fixture\yesterday.exe" })
        ], []);
    }
}

public sealed partial class LogAnalysisView
{
    internal async Task ExerciseAsync()
    {
        readThrough = DateTimeOffset.MinValue;
        CategoryChoice.SelectedIndex = (int)LogFilter.All;
        Refresh();
        await Task.Delay(200);
        var groups = (IReadOnlyList<LogDayGroup>)GroupedEvents.Source;
        if (groups.Count != 2 || groups[0].Title != "Today" || groups[1].Title != "Yesterday" || groups.Sum(x => x.Count) != 5)
            throw new InvalidOperationException("Timeline must render both date groups and all fixture events.");
        if (EventList.ContainerFromItem(groups[0][0]) is not ListViewItem)
            throw new InvalidOperationException("Grouped timeline failed to realize native event rows.");
        LogSearch.Text = "Editor";
        Refresh();
        if (((IReadOnlyList<LogDayGroup>)GroupedEvents.Source).Sum(x => x.Count) != 1)
            throw new InvalidOperationException("Timeline search did not filter the native collection.");
        LogSearch.Text = "";
        RangeChoice.SelectedIndex = (int)LogRange.Today;
        Refresh();
        if (((IReadOnlyList<LogDayGroup>)GroupedEvents.Source).Sum(x => x.Count) != 4)
            throw new InvalidOperationException("Today filter included yesterday's event.");
        readThrough = monitor!.LogEvents.Max(x => x.Time);
        UnreadOnly.IsChecked = true;
        Refresh();
        if (EmptyState.Visibility != Visibility.Visible || MarkReadButton.IsEnabled)
            throw new InvalidOperationException("Read watermark failed to clear unread results.");
        UnreadOnly.IsChecked = false;
        RangeChoice.SelectedIndex = (int)LogRange.All;
        readThrough = DateTimeOffset.MinValue;
        Refresh();
        await Task.Delay(200);
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(this);
        var file = await (await StorageFolder.GetFolderFromPathAsync(AppContext.BaseDirectory)).CreateFileAsync("log-analysis-preview.png", CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, (await bitmap.GetPixelsAsync()).ToArray());
        await encoder.FlushAsync();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "log-analysis-selftest.txt"), "PASS: native date grouping, realized rows, app search, today range, unread filtering and timeline render.");
    }
}

public sealed partial class MainWindow
{
    internal async Task ExerciseLogAnalysisAsync()
    {
        await MonitorPanel.PauseForDiagnosticAsync();
        Root.RequestedTheme = ElementTheme.Light;
        MonitorPanel.SetLogFixture();
        Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(x => x.Tag?.ToString() == "logs");
        await (LogPanel ?? throw new InvalidOperationException("Log analysis page did not load on navigation.")).ExerciseAsync();
    }
}
