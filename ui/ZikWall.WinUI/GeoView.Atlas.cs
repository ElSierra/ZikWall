using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace ZikWall.WinUI;

public sealed partial class GeoView
{
    private Windows.Foundation.Point? dragStart;
    private double dragX, dragY;
    private void MapPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse || !e.GetCurrentPoint(MapViewport).Properties.IsLeftButtonPressed) return;
        for (DependencyObject? node = e.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is Button) return;
        dragStart = e.GetCurrentPoint(MapViewport).Position;
        dragX = MapViewport.HorizontalOffset; dragY = MapViewport.VerticalOffset;
        MapViewport.CapturePointer(e.Pointer); e.Handled = true;
    }
    private void MapMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (dragStart is not { } start) return;
        var point = e.GetCurrentPoint(MapViewport).Position;
        MapViewport.ChangeView(Math.Max(0, dragX + start.X - point.X), Math.Max(0, dragY + start.Y - point.Y), null, true);
        e.Handled = true;
    }
    private void MapReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) { dragStart = null; MapViewport.ReleasePointerCaptures(); }
    private void ViewportChanged(object sender, SizeChangedEventArgs args)
    {
        SizeMap();
    }
    private void SizeMap()
    {
        double width = MapViewport.ActualWidth, height = MapViewport.ActualHeight;
        if (width <= 0 || height <= 0) return;
        // Fill the viewport with geography; overflow is pannable, never stretched.
        double scale = fitWorld ? Math.Min(width / 720, height / 360) : Math.Max(width / 720, height / 360);
        MapBox.Width = 720 * scale; MapBox.Height = 360 * scale;
        MapViewport.UpdateLayout();
        MapViewport.ChangeView(Math.Max(0, (MapBox.Width - width) / 2), Math.Max(0, (MapBox.Height - height) / 2), 1, true);
    }

    private void ZoomClicked(object sender, RoutedEventArgs args)
    {
        string? command = (sender as Button)?.Tag as string;
        if (command == "fit") { fitWorld = true; SizeMap(); return; }
        float zoom = Math.Clamp(MapViewport.ZoomFactor + (command == "in" ? .5f : -.5f), 1, 4);
        double ratio = zoom / MapViewport.ZoomFactor;
        double x = command == "fit" ? 0 : (MapViewport.HorizontalOffset + MapViewport.ViewportWidth / 2) * ratio - MapViewport.ViewportWidth / 2;
        double y = command == "fit" ? 0 : (MapViewport.VerticalOffset + MapViewport.ViewportHeight / 2) * ratio - MapViewport.ViewportHeight / 2;
        MapViewport.ChangeView(Math.Max(0, x), Math.Max(0, y), zoom, !uiSettings.AnimationsEnabled || MotionToggle.IsChecked != true);
    }

    // Turning motion on replays the entrance, so the toggle doubles as "play".
    private void MotionClicked(object sender, RoutedEventArgs args)
    {
        StopAnimations();
        if (MotionToggle.IsChecked == true) PlayIntro();
    }

    private void Pulse(Ellipse halo, bool selection = false, double delay = 0)
    {
        if (!Active || !MotionAllowed) return;
        var visual = ElementCompositionPreview.GetElementVisual(halo);
        visual.CenterPoint = new Vector3((float)halo.Width / 2, (float)halo.Height / 2, 0);
        var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, new Vector3(.65f, .65f, 1));
        scale.InsertKeyFrame(1, new Vector3(2.2f, 2.2f, 1));
        // Live updates are frequent; keep their compositor duty cycle short.
        // A deliberate selection gets a longer response without taxing idle monitoring.
        scale.Duration = TimeSpan.FromMilliseconds(selection ? 850 : 420);
        var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, .8f); opacity.InsertKeyFrame(.25f, .55f); opacity.InsertKeyFrame(1, 0);
        opacity.Duration = scale.Duration;
        // The default delay behavior keeps the halo hidden until it fires.
        scale.DelayTime = opacity.DelayTime = TimeSpan.FromMilliseconds(delay);
        visual.StartAnimation("Scale", scale); visual.StartAnimation("Opacity", opacity);
        animations.Add(visual);
        scale.Dispose(); opacity.Dispose();
    }

    private void BuildGraticule()
    {
        var gridBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(18, 160, 201, 225));
        for (int x = 60; x < 720; x += 60)
            Graticule.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = 360, Stroke = gridBrush, StrokeThickness = .5 });
        for (int y = 60; y < 360; y += 60)
            Graticule.Children.Add(new Line { X1 = 0, X2 = 720, Y1 = y, Y2 = y, Stroke = gridBrush, StrokeThickness = y == 180 ? 1 : .5 });
        foreach (var (x, y, text) in new[] { (105, 95, "NORTH AMERICA"), (190, 230, "SOUTH AMERICA"), (350, 175, "AFRICA"), (470, 92, "ASIA"), (610, 242, "AUSTRALIA"), (275, 125, "ATLANTIC"), (52, 200, "PACIFIC OCEAN"), (467, 246, "INDIAN OCEAN") })
        {
            var label = new TextBlock { Text = text, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 177, 209, 218)), Opacity = .65, FontSize = 7, CharacterSpacing = 140 };
            Canvas.SetLeft(label, x); Canvas.SetTop(label, y); Graticule.Children.Add(label);
        }
    }

    private void PositionMarkers()
    {
        // Deterministic packing keeps dense Europe markers individually selectable.
        // A leader always preserves the true (approximate) country centroid.
        var placed = new List<MapMarker>();
        foreach (var (code, marker) in markers.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var location = countries[code];
            double originX = 2 * (location.Longitude + 180), originY = 2 * (90 - location.Latitude);
            double x = originX, y = originY;
            for (int attempt = 0; attempt < 400; attempt++)
            {
                double radius = 3 * Math.Sqrt(attempt), angle = attempt * 2.399963;
                x = Math.Clamp(originX + radius * Math.Cos(angle), marker.Size / 2 + 2, 718 - marker.Size / 2);
                y = Math.Clamp(originY + radius * Math.Sin(angle), marker.Size / 2 + 2, 358 - marker.Size / 2);
                if (placed.All(other => Math.Pow(other.X - x, 2) + Math.Pow(other.Y - y, 2) >= Math.Pow((other.Size + marker.Size) / 2 + 3, 2))) break;
            }
            marker.X = x; marker.Y = y;
            Canvas.SetLeft(marker.Button, x - marker.Size / 2); Canvas.SetTop(marker.Button, y - marker.Size / 2);
            Canvas.SetLeft(marker.Halo, x - (marker.Size + 10) / 2); Canvas.SetTop(marker.Halo, y - (marker.Size + 10) / 2);
            Canvas.SetLeft(marker.Glow, x - marker.Size * 1.2); Canvas.SetTop(marker.Glow, y - marker.Size * 1.2);
            marker.Leader.X1 = originX; marker.Leader.Y1 = originY; marker.Leader.X2 = x; marker.Leader.Y2 = y;
            placed.Add(marker);
        }
    }

    private void UpdateFocus()
    {
        FocusLayer.Children.Clear();
        if (!markers.TryGetValue(selected, out var marker))
        {
            MapHint.Text = markers.Count == 0 ? countryDataStatus.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
                ? "Location data unavailable · repair the installation"
                : groups.Count > 0 ? "These IPs have no known country in the offline database" : "No Internet connections for this selection"
                : "Drag to pan · select a destination to explore";
            return;
        }
        var location = countries[selected];
        double x = 2 * (location.Longitude + 180), y = 2 * (90 - location.Latitude);
        FocusLayer.Children.Add(new Line { X1 = 0, X2 = 720, Y1 = y, Y2 = y, Stroke = Amber, StrokeThickness = .6, Opacity = .35, StrokeDashArray = [3, 5] });
        FocusLayer.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = 360, Stroke = Amber, StrokeThickness = .6, Opacity = .35, StrokeDashArray = [3, 5] });
        var ring = new Ellipse { Width = marker.Size + 14, Height = marker.Size + 14, Stroke = Amber, StrokeThickness = 1, Opacity = .6 };
        Canvas.SetLeft(ring, marker.X - ring.Width / 2); Canvas.SetTop(ring, marker.Y - ring.Height / 2); FocusLayer.Children.Add(ring);
        var label = new Border { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(240, 16, 41, 62)),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 4, 7, 4),
            Child = new TextBlock { Text = location.Label, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 235, 246, 255)), FontSize = 10 } };
        Canvas.SetLeft(label, Math.Clamp(marker.X + marker.Size / 2 + 5, 4, 580)); Canvas.SetTop(label, Math.Max(4, marker.Y - 12)); FocusLayer.Children.Add(label);
        var entries = groups[selected];
        int outgoing = entries.Count(e => e.Direction == "Outgoing"), incoming = entries.Count(e => e.Direction == "Incoming");
        MapHint.Text = $"{location.Label}  ·  {outgoing} outgoing / {incoming} incoming connections";
    }
}
