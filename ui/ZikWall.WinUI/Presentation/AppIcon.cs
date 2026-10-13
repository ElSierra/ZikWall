using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Graphics.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;

namespace ZikWall.WinUI.Presentation;

// Only realized rows request icons; one cached lookup per path and pixel size, bounded to 256.
// Windows supplies the application's thumbnail/icon without launching the file.
// Icons decode at the display's physical size so they stay sharp at 125-200% scaling.
public sealed class AppIcon : Grid
{
    private static readonly Dictionary<string, Task<byte[]?>> cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> order = new();
    private static readonly SemaphoreSlim loaders = new(2);
    private readonly Image image = new() { Stretch = Stretch.Uniform };
    private readonly FontIcon fallback = new() { Glyph = "\uE7C3", FontSize = 18 };
    public static readonly DependencyProperty ExecutableProperty = DependencyProperty.Register(nameof(Executable), typeof(string), typeof(AppIcon), new PropertyMetadata("", Changed));
    public string Executable { get => (string)GetValue(ExecutableProperty); set => SetValue(ExecutableProperty, value); }
    public AppIcon()
    {
        Width = Height = 24;
        Children.Add(fallback);
        Children.Add(image);
        // Bindings can arrive before the row joins a window; decode once the display scale is known.
        Loaded += (_, _) => { if (image.Source is null && Executable.Length > 0) UpdateIconAsync(Executable); };
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
        // No executable means Windows-owned or an exited owner; a page icon reads as a file.
        target.fallback.Glyph = path.Length == 0 ? "" : "";
        if (path.Length == 0 || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") || target.XamlRoot is null)
        {
            target.image.Source = null;
            target.fallback.Visibility = Visibility.Visible;
            return;
        }
        int size = PixelSize(double.IsNaN(target.Width) ? 24 : target.Width, target.XamlRoot.RasterizationScale);
        string key = $"{size}|{path}";
        // Cached icons apply in the same frame; clearing first made re-bound rows blink on every refresh.
        if (cache.TryGetValue(key, out var done) && done.IsCompletedSuccessfully)
        {
            target.image.Source = CreateBitmap(done.Result, size);
            target.fallback.Visibility = done.Result is null ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        // While a new icon decodes, show nothing rather than the placeholder glyph.
        target.image.Source = null;
        target.fallback.Visibility = Visibility.Collapsed;
        if (!cache.TryGetValue(key, out var task))
        {
            // Prevent an unbounded request queue during rapid scrolling.
            if (cache.Count(x => !x.Value.IsCompleted) >= 32) { target.fallback.Visibility = Visibility.Visible; return; }
            while (cache.Count >= 256 && order.TryDequeue(out string? old)) cache.Remove(old);
            cache[key] = task = LoadPixelsAsync(path, size); order.Enqueue(key);
        }
        var pixels = await task;
        if (target.Executable == path) { target.image.Source = CreateBitmap(pixels, size); target.fallback.Visibility = pixels is null ? Visibility.Visible : Visibility.Collapsed; }
    }
    // Round up to a few common sizes so rows at the same scale share one decode.
    internal static int PixelSize(double logical, double scale)
    {
        int needed = (int)Math.Ceiling(Math.Max(16, logical) * Math.Max(1, scale));
        foreach (int bucket in new[] { 16, 20, 24, 32, 40, 48, 64, 96, 128 }) if (bucket >= needed) return bucket;
        return 128;
    }
    internal static async Task<WriteableBitmap?> LoadAsync(string path, int size = 32) => CreateBitmap(await LoadPixelsAsync(path, size), size);
    private static WriteableBitmap? CreateBitmap(byte[]? pixels, int size)
    {
        if (pixels is null) return null;
        // Recycled native Image controls crashed when sharing a cached BitmapImage.
        // Cache pixels per path and size; each realized row owns its image source.
        var bitmap = new WriteableBitmap(size, size);
        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(pixels);
        bitmap.Invalidate();
        return bitmap;
    }
    // Toast logos are 48 DIP; 192 physical pixels also covers 400% display scaling.
    // Keep the PNG on disk because Windows can render it later in Notification Center.
    internal static async Task<string?> SaveNotificationIconAsync(string path, string directory)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\")) return null;
        try
        {
            if (!File.Exists(path)) return null;
            string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
            string output = Path.Combine(directory, key + ".png");
            if (File.Exists(output) && File.GetLastWriteTimeUtc(output) >= File.GetLastWriteTimeUtc(path)) return output;
            const int size = 192;
            var pixels = await LoadPixelsAsync(path, size, requireFullSize: true);
            if (pixels is null) return null;
            Directory.CreateDirectory(directory);
            var folder = await StorageFolder.GetFolderFromPathAsync(directory);
            var file = await folder.CreateFileAsync(key + ".png", CreationCollisionOption.ReplaceExisting);
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, size, size, 96, 96, pixels);
            await encoder.FlushAsync();
            return output;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException) { return null; }
    }
    private static async Task<byte[]?> LoadPixelsAsync(string path, int size, bool requireFullSize = false)
    {
        await loaders.WaitAsync();
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            // Ask the shell for a larger icon and downscale; upscaling a 32 px icon is what blurred rows.
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, (uint)Math.Min(256, size * 2), ThumbnailOptions.None);
            if (thumbnail is null) return null;
            var decoder = await BitmapDecoder.CreateAsync(thumbnail);
            // Don't manufacture a high-resolution toast by enlarging a tiny source icon.
            if (requireFullSize && (decoder.PixelWidth < size || decoder.PixelHeight < size)) return null;
            var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform { ScaledWidth = (uint)size, ScaledHeight = (uint)size, InterpolationMode = BitmapInterpolationMode.Fant }, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            return pixels.DetachPixelData();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException) { return null; }
        finally { loaders.Release(); }
    }
}
