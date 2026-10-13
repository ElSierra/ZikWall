using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Graphics.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;

namespace ZikWall.WinUI.Presentation;

// Only realized rows request icons; one cached lookup per path, bounded to 256.
// Windows supplies the application's thumbnail/icon without launching the file.
public sealed class AppIcon : Grid
{
    private static readonly Dictionary<string, Task<byte[]?>> cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> order = new();
    private static readonly SemaphoreSlim loaders = new(2);
    private readonly Image image = new() { Width = 28, Height = 28 };
    private readonly FontIcon fallback = new() { Glyph = "\uE7C3", FontSize = 22 };
    public static readonly DependencyProperty ExecutableProperty = DependencyProperty.Register(nameof(Executable), typeof(string), typeof(AppIcon), new PropertyMetadata("", Changed));
    public string Executable { get => (string)GetValue(ExecutableProperty); set => SetValue(ExecutableProperty, value); }
    public AppIcon()
    {
        Width = Height = 32;
        Children.Add(fallback);
        Children.Add(image);
    }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var target = (AppIcon)sender;
        string path = args.NewValue as string ?? "";
        // Bindings can change while WinUI is measuring/recycling the item template.
        // Defer visual mutations until that native layout operation completes.
        target.DispatcherQueue.TryEnqueue(() => target.UpdateIconAsync(path));
    }
    private async void UpdateIconAsync(string path)
    {
        var target = this;
        if (target.Executable != path) return;
        target.image.Source = null;
        target.fallback.Visibility = Visibility.Visible;
        // No executable means Windows-owned or an exited owner; a page icon reads as a file.
        target.fallback.Glyph = path.Length == 0 ? "" : "";
        if (path.Length == 0 || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\")) return;
        if (!cache.TryGetValue(path, out var task))
        {
            // Prevent an unbounded request queue during rapid scrolling.
            if (cache.Count(x => !x.Value.IsCompleted) >= 32) return;
            while (cache.Count >= 256 && order.TryDequeue(out string? old)) cache.Remove(old);
            cache[path] = task = LoadPixelsAsync(path); order.Enqueue(path);
        }
        var pixels = await task;
        if (target.Executable == path) { target.image.Source = CreateBitmap(pixels); target.fallback.Visibility = pixels is null ? Visibility.Visible : Visibility.Collapsed; }
    }
    internal static async Task<WriteableBitmap?> LoadAsync(string path) => CreateBitmap(await LoadPixelsAsync(path));
    private static WriteableBitmap? CreateBitmap(byte[]? pixels)
    {
        if (pixels is null) return null;
        // Recycled native Image controls crashed when sharing a cached BitmapImage.
        // Cache 4 KiB of pixels per path; each realized row owns its image source.
        var bitmap = new WriteableBitmap(32, 32);
        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(pixels);
        bitmap.Invalidate();
        return bitmap;
    }
    private static async Task<byte[]?> LoadPixelsAsync(string path)
    {
        await loaders.WaitAsync();
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 32);
            if (thumbnail is null) return null;
            var decoder = await BitmapDecoder.CreateAsync(thumbnail);
            var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform { ScaledWidth = 32, ScaledHeight = 32 }, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            return pixels.DetachPixelData();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException) { return null; }
        finally { loaders.Release(); }
    }
}
