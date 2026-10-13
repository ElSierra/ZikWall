using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI;

public sealed partial class MainWindow
{
    internal async Task ExerciseActivityOverviewAsync()
    {
        if (ActivityPanel is not null || LogPanel is not null) throw new InvalidOperationException("Unopened analysis pages were created at startup.");
        await MonitorPanel.PauseForDiagnosticAsync();
        Root.RequestedTheme = Environment.GetCommandLineArgs().Contains("--test-dark") ? ElementTheme.Dark : ElementTheme.Light;
        // Give the actual parent enough client width; a child-only Width change
        // can pass measure checks while still being clipped by the window.
        AppWindow.Resize(new Windows.Graphics.SizeInt32(2500, 1300));
        await Task.Delay(250);
        Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(x => x.Tag?.ToString() == "activity");
        await (ActivityPanel ?? throw new InvalidOperationException("Activity page did not load on navigation.")).ExerciseOverviewAsync();
    }
}

public sealed partial class ActivityOverviewView
{
    internal async Task ExerciseOverviewAsync()
    {
        var now = DateTimeOffset.Now;
        var fixture = Enumerable.Range(0, 32).Select(i => new ConnectionEntry("TCPv4", "192.168.1.2", 50000 + i,
            $"8.8.8.{i + 1}", i % 3 == 0 ? 53 : 443, 100 + i / 4, "Established", $"Example app {i / 4 + 1}",
            Environment.ProcessPath!, "Outgoing", i % 2 == 0 ? "US" : "GB", i % 2 == 0 ? "United States" : "United Kingdom", "Internet",
            $"Fixture_{i / 4}", DomainHint: $"service-{i + 1}.example.net")).ToList();
        var amounts = fixture.GroupBy(x => x.AppKey).Select((g, i) => new ActivityUsageDelta(now.AddSeconds(-5), g.Key,
            g.First().ProcessPath, (8 - i) * 1024 * 1024, (i + 1) * 256 * 1024)).ToList();
        var older = fixture[0] with { ProcessId = 900, ProcessName = "Earlier app", PackageFamily = "Earlier_fixture", RemoteAddress = "1.1.1.1" };
        Update(fixture, [new(now.AddMinutes(-20), "First seen", older)], amounts, [], now.AddMinutes(-5), true);
        if (AppsList.Items.Count != 8 || HostsList.Items.Count != 32 || CountriesList.Items.Count != 2)
            throw new InvalidOperationException("Activity rankings did not reflect fixture apps, hosts and countries.");
        ActivityRange.SelectedIndex = 1;
        if (AppsList.Items.Count != 9) throw new InvalidOperationException("One-hour range failed to include earlier activity.");
        ActivityRange.SelectedIndex = 0;
        ActivitySearch.Text = "Earlier app"; Refresh();
        if (OverviewEmpty.Visibility != Visibility.Visible) throw new InvalidOperationException("Search did not produce the expected empty range.");
        ActivitySearch.Text = ""; Refresh();
        var results = new List<string>();
        foreach (double width in new[] { 1280d, 800d })
        {
            Width = width; UpdateLayout(); await Task.Delay(250);
            if (Parent is FrameworkElement parent && parent.ActualWidth + 1 < width)
                throw new InvalidOperationException("Diagnostic window clips the requested activity width.");
            foreach (var column in new FrameworkElement[] { AppsColumn, HostsColumn, TypesColumn, CountriesColumn })
            {
                var position = column.TransformToVisual(this).TransformPoint(new Point());
                if (column.ActualWidth < 100 || position.X < -1 || position.X + column.ActualWidth > width + 1 || position.Y + column.ActualHeight > ActualHeight + 1)
                    throw new InvalidOperationException($"Activity column {column.Name} overflows at {width} DIP.");
            }
            if (Grid.GetRow(TypesColumn) != (width < 1050 ? 1 : 0)) throw new InvalidOperationException("Activity columns did not adapt to the available width.");
            var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(this);
            var file = await (await StorageFolder.GetFolderFromPathAsync(AppContext.BaseDirectory)).CreateFileAsync($"activity-{width}.png", CreationCollisionOption.ReplaceExisting);
            using var output = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, (await bitmap.GetPixelsAsync()).ToArray());
            await encoder.FlushAsync();
            results.Add($"PASS: activity columns fit {width} DIP; native rank lists, byte bars and country flags rendered.");
        }
        Width = double.NaN;
        results.Add("PASS: selected time range, search and empty state change the actual rankings.");
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "activity-overview-selftest.txt"), results);
    }
}
