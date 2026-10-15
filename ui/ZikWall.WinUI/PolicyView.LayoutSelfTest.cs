using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace ZikWall.WinUI;

public sealed partial class MainWindow
{
    internal async Task ExerciseWindowsSyncAsync()
    {
        Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(x => x.Tag?.ToString() == "policy");
        await PolicyPanel.ExerciseWindowsSyncCancelAsync();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "windows-sync-ui.txt"), "PASS: native Windows sync preview, compatible-rule plan, and cancellation without policy changes or filtering.\n");
    }
    internal async Task ExercisePolicyLayoutAsync()
    {
        Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(x => x.Tag?.ToString() == "policy");
        await PolicyPanel.ExerciseLayoutAsync();
    }
}

public sealed partial class PolicyView
{
    internal async Task ExerciseLayoutAsync()
    {
        IEnumerable<FrameworkElement> Descendants(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is FrameworkElement element) yield return element;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }
        double Bottom(FrameworkElement element) => element.TransformToVisual(this).TransformPoint(new Point(0, element.ActualHeight)).Y;
        var results = new List<string>();
        foreach (double width in new[] { 1800d, 1000d, 720d, 480d })
        {
            Width = width;
            UpdateLayout();
            await Task.Delay(200);
            var controls = Descendants(this).Where(x => x is Button button && button.Content is string label &&
                (label == "New profile" || label == "Associate network")).ToArray();
            if (width >= 1000 && controls.Concat(new FrameworkElement[] { AutoSwitch, FilteringButton }).Any(x => Math.Abs(Bottom(x) - Bottom(ProfileChoice)) > 2))
                throw new InvalidOperationException($"Profile actions are not aligned with the profile selector at {width} DIP: selector bottom {Bottom(ProfileChoice)}, action bottoms {string.Join(", ", controls.Select(Bottom))}.");
            foreach (var control in controls.Concat(new FrameworkElement[] { ProfileChoice, AutoSwitch, FilteringButton }))
            {
                var position = control.TransformToVisual(this).TransformPoint(new Point());
                if (position.X < -1 || position.X + control.ActualWidth > width + 1)
                    throw new InvalidOperationException($"Policy control {control.Name} overflows at {width} DIP.");
            }
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(this);
            var folder = await StorageFolder.GetFolderFromPathAsync(AppContext.BaseDirectory);
            var file = await folder.CreateFileAsync($"policy-{width}.png", CreationCollisionOption.ReplaceExisting);
            using var output = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, (await bitmap.GetPixelsAsync()).ToArray());
            await encoder.FlushAsync();
            results.Add($"PASS: profile control alignment and bounds at {width} DIP.");
        }
        Width = double.NaN;
        if (IsFiltering) throw new InvalidOperationException("Layout test enabled filtering.");
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "policy-layout-selftest.txt"), results);
    }
}
