using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace ZikWall.WinUI;

public sealed partial class GeoView
{
    // Schematic hub in the Atlantic; it does not claim a device location.
    private static readonly Point DeviceHub = new(300, 180);
    private const float TravelerRadius = 6;
    private const double SpotlightMs = 420, TravelMs = 620, SelectionTravelMs = 850;
    private sealed record CountryLink(Microsoft.UI.Xaml.Shapes.Path Path, ShapeVisual Traveler, CompositionSpriteShape Sprite,
        CompositionEllipseGeometry Dot, CompositionColorBrush TravelerBrush, CompositionSpriteShape GlowSprite,
        CompositionEllipseGeometry GlowDot, CompositionColorBrush GlowBrush, LinearGradientBrush Stroke, BezierSegment Curve)
    {
        public Point Destination;
        public bool StyleInitialized, Focused, Dimmed;
        public double MarkerSize;
    }
    private readonly Dictionary<string, CountryLink> links = new();
    private ContainerVisual? travelers;

    private void UpdateLinks()
    {
        if (travelers is null)
        {
            travelers = ElementCompositionPreview.GetElementVisual(TravelLayer).Compositor.CreateContainerVisual();
            ElementCompositionPreview.SetElementChildVisual(TravelLayer, travelers);
        }
        var removed = links.Keys.Where(x => !markers.ContainsKey(x)).ToArray();
        // An entrance may still reference a traveler that is about to be disposed.
        if (removed.Length > 0) StopAnimations();
        foreach (var code in removed)
        {
            var old = links[code];
            LinkLayer.Children.Remove(old.Path); travelers.Children.Remove(old.Traveler);
            old.Traveler.Dispose(); old.Sprite.Dispose(); old.Dot.Dispose(); old.TravelerBrush.Dispose();
            old.GlowSprite.Dispose(); old.GlowDot.Dispose(); old.GlowBrush.Dispose(); links.Remove(code);
        }
        foreach (var (code, marker) in markers)
        {
            if (!links.TryGetValue(code, out var link))
            {
                var curve = new BezierSegment();
                var figure = new PathFigure { StartPoint = DeviceHub, IsClosed = false, IsFilled = false };
                figure.Segments.Add(curve);
                var geometry = new PathGeometry(); geometry.Figures.Add(figure);
                // Fades in from the hub so the fan reads as leaving this device.
                var stroke = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = DeviceHub };
                stroke.GradientStops.Add(new GradientStop { Offset = 0 }); stroke.GradientStops.Add(new GradientStop { Offset = 1 });
                var path = new Microsoft.UI.Xaml.Shapes.Path { Data = geometry, Stroke = stroke, StrokeThickness = 1, Opacity = .3 };
                var compositor = travelers.Compositor;
                var glowDot = compositor.CreateEllipseGeometry(); glowDot.Center = new Vector2(TravelerRadius); glowDot.Radius = new Vector2(TravelerRadius);
                var glowShape = compositor.CreateSpriteShape(glowDot);
                var glowBrush = compositor.CreateColorBrush(); glowShape.FillBrush = glowBrush;
                var dot = compositor.CreateEllipseGeometry(); dot.Center = new Vector2(TravelerRadius); dot.Radius = new Vector2(2.4f);
                var shape = compositor.CreateSpriteShape(dot);
                var brush = compositor.CreateColorBrush(Teal.Color); shape.FillBrush = brush;
                var traveler = compositor.CreateShapeVisual(); traveler.Size = new Vector2(TravelerRadius * 2); traveler.Opacity = 0;
                traveler.Shapes.Add(glowShape); traveler.Shapes.Add(shape);
                LinkLayer.Children.Add(path); travelers.Children.InsertAtTop(traveler);
                link = new(path, traveler, shape, dot, brush, glowShape, glowDot, glowBrush, stroke, curve); links.Add(code, link);
            }
            var destination = new Point(marker.X, marker.Y);
            if (!link.Destination.Equals(destination))
            {
                link.Destination = destination;
                // A wide fan separates countries while keeping each curve anchored
                // to its actual marker. Curves are visual links, not routing data.
                double spread = destination.X - DeviceHub.X;
                link.Curve.Point1 = new(DeviceHub.X + spread * .65, DeviceHub.Y - 72);
                link.Curve.Point2 = new(destination.X - spread * .22, Math.Max(12, destination.Y - 75));
                link.Curve.Point3 = destination;
                link.Stroke.EndPoint = destination;
            }
            bool focus = code == selected;
            bool dimmed = selected.Length != 0 && !focus;
            if (!link.StyleInitialized || link.Focused != focus || link.Dimmed != dimmed || link.MarkerSize != marker.Size)
            {
                var color = focus ? Amber.Color : Teal.Color;
                link.Stroke.GradientStops[0].Color = Windows.UI.Color.FromArgb(50, color.R, color.G, color.B);
                link.Stroke.GradientStops[1].Color = color;
                link.TravelerBrush.Color = color;
                link.GlowBrush.Color = Windows.UI.Color.FromArgb(70, color.R, color.G, color.B);
                link.Path.StrokeThickness = focus ? 2 : .65 + marker.Size / 32 * .5;
                link.Path.Opacity = focus ? .95 : dimmed ? .16 : .5;
                Canvas.SetZIndex(link.Path, focus ? 1 : 0);
                link.StyleInitialized = true; link.Focused = focus; link.Dimmed = dimmed; link.MarkerSize = marker.Size;
            }
        }
    }

    private void AnimateLink(string code, bool selection = false, double delay = 0, double duration = SpotlightMs)
    {
        if (!Active || !MotionAllowed || !links.TryGetValue(code, out var link)) return;
        var visual = link.Traveler;
        var motion = visual.Compositor.CreateVector3KeyFrameAnimation();
        using var linear = visual.Compositor.CreateLinearEasingFunction();
        for (int step = 0; step <= 20; step++)
        {
            // Sampled ease-in-out: the dot launches gently and settles on the marker.
            double time = step / 20.0, t = time * time * (3 - 2 * time), u = 1 - t;
            double x = u * u * u * DeviceHub.X + 3 * u * u * t * link.Curve.Point1.X + 3 * u * t * t * link.Curve.Point2.X + t * t * t * link.Destination.X;
            double y = u * u * u * DeviceHub.Y + 3 * u * u * t * link.Curve.Point1.Y + 3 * u * t * t * link.Curve.Point2.Y + t * t * t * link.Destination.Y;
            motion.InsertKeyFrame((float)time, new Vector3((float)x - TravelerRadius, (float)y - TravelerRadius, 0), linear);
        }
        motion.Duration = TimeSpan.FromMilliseconds(selection ? SelectionTravelMs : duration);
        var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, 0); opacity.InsertKeyFrame(.12f, 1); opacity.InsertKeyFrame(.8f, 1); opacity.InsertKeyFrame(1, 0);
        opacity.Duration = motion.Duration;
        // Hold the start position during a delay so a staggered dot never jumps.
        motion.DelayTime = opacity.DelayTime = TimeSpan.FromMilliseconds(delay);
        motion.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation("Offset", motion); visual.StartAnimation("Opacity", opacity);
        animations.Add(visual);
        motion.Dispose(); opacity.Dispose();
    }
}
