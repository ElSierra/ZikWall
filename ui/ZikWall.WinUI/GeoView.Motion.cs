using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace ZikWall.WinUI;

// All motion here is one-shot compositor keyframes: no timers or per-frame UI work.
// WinUI renders composition in-process, so nothing loops while idle.
public sealed partial class GeoView
{
    // Travelers beyond this still get a marker pop, just without a dot.
    private const int MaxIntroTravelers = 24;
    private readonly HashSet<Visual> entrances = [];
    private readonly RadialGradientBrush GlowFill = Glow(Teal.Color), SelectedGlowFill = Glow(Amber.Color);
    private bool introPending;
    private long introUntil;
    private bool MotionAllowed => MotionToggle.IsChecked == true && uiSettings.AnimationsEnabled;
    private bool IntroRunning => Environment.TickCount64 < introUntil;

    private static RadialGradientBrush Glow(Windows.UI.Color color)
    {
        var brush = new RadialGradientBrush();
        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(70, color.R, color.G, color.B), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, color.R, color.G, color.B), Offset = 1 });
        return brush;
    }

    // Arriving on the page replays the entrance even when the data is unchanged.
    private void Reveal()
    {
        introPending = true;
        Draw();
        if (introPending) PlayIntro();
    }

    private void PlayIntro()
    {
        // Without markers the entrance waits for the first located snapshot.
        if (!Active || markers.Count == 0) return;
        introPending = false;
        if (!MotionAllowed) return;
        StopAnimations();
        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        using var overshoot = compositor.CreateCubicBezierEasingFunction(new Vector2(.34f, 1.56f), new Vector2(.64f, 1));
        PulseHub(compositor);
        // Sweep west to east so the fan opens out of the hub in one pass.
        var order = markers.OrderBy(x => x.Value.X).ToArray();
        double stagger = Math.Min(80, 900.0 / order.Length);
        for (int i = 0; i < order.Length; i++)
        {
            var (code, marker) = order[i];
            double launch = 150 + i * stagger, arrival = launch;
            if (i < MaxIntroTravelers) { AnimateLink(code, delay: launch, duration: TravelMs); arrival += TravelMs * .85; }
            PopMarker(marker.Button, compositor, overshoot, arrival - 120);
            Pulse(marker.Halo, delay: arrival);
        }
        introUntil = Environment.TickCount64 + (long)(150 + order.Length * stagger + TravelMs + 450);
    }

    private void PopMarker(Microsoft.UI.Xaml.Controls.Button button, Compositor compositor, CompositionEasingFunction easing, double delay)
    {
        var visual = ElementCompositionPreview.GetElementVisual(button);
        visual.CenterPoint = new Vector3((float)button.Width / 2, (float)button.Height / 2, 0);
        using var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, new Vector3(.2f, .2f, 1));
        scale.InsertKeyFrame(1, Vector3.One, easing);
        scale.Duration = TimeSpan.FromMilliseconds(380);
        scale.DelayTime = TimeSpan.FromMilliseconds(Math.Max(0, delay));
        scale.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation("Scale", scale);
        entrances.Add(visual);
    }

    private void PulseHub(Compositor compositor)
    {
        var visual = ElementCompositionPreview.GetElementVisual(HubPulse);
        visual.CenterPoint = new Vector3((float)HubPulse.Width / 2, (float)HubPulse.Height / 2, 0);
        using var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, Vector3.One); scale.InsertKeyFrame(1, new Vector3(1.25f, 1.6f, 1));
        using var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, .8f); opacity.InsertKeyFrame(1, 0);
        scale.Duration = opacity.Duration = TimeSpan.FromMilliseconds(800);
        scale.IterationCount = opacity.IterationCount = 2;
        visual.StartAnimation("Scale", scale); visual.StartAnimation("Opacity", opacity);
        animations.Add(visual);
    }
}
