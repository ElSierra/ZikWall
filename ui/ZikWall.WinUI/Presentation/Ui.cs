using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ZikWall.WinUI.Presentation;

// x:Bind helpers for row templates. Brushes are shared and created once on the UI thread;
// colors are mid-tone so they read on both light and dark Mica.
public static class Ui
{
    private static SolidColorBrush? outgoing, incoming, active, listening, idle;
    private static SolidColorBrush Brush(ref SolidColorBrush? cache, byte r, byte g, byte b) => cache ??= new(Color.FromArgb(255, r, g, b));
    public static SolidColorBrush Outgoing => Brush(ref outgoing, 0x1F, 0x86, 0xE0);
    public static SolidColorBrush Incoming => Brush(ref incoming, 0x9B, 0x6B, 0xE8);
    public static SolidColorBrush Active => Brush(ref active, 0x2F, 0xA8, 0x4F);
    public static SolidColorBrush Listening => Brush(ref listening, 0xD8, 0x9B, 0x1A);
    public static SolidColorBrush Idle => Brush(ref idle, 0x8A, 0x8A, 0x8A);

    public static Visibility Show(string value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility ShowIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static GridLength UsageColumnWidth(bool enabled) => new(enabled ? 160 : 0);
    public static double Opacity(bool value) => value ? 1 : 0;

    // Direction strings arrive as "Outgoing", "↑ Outgoing" or a group's "Outgoing / Incoming".
    public static string DirectionGlyph(string direction)
    {
        bool up = direction.Contains("Outgoing"), down = direction.Contains("Incoming");
        return up && down ? "" : up ? "" : down ? "" : direction.Contains("Listening") ? "" : "";
    }
    public static SolidColorBrush DirectionBrush(string direction)
    {
        bool up = direction.Contains("Outgoing"), down = direction.Contains("Incoming");
        return up && !down ? Outgoing : down && !up ? Incoming : direction.Contains("Listening") ? Listening : Idle;
    }
    public static string DirectionText(string direction)
    {
        string text = direction.Replace("↑ ", "").Replace("↓ ", "");
        if (text == "Unknown") return ""; // UDP has no direction; a blank reads better than a warning.
        return text.Contains("Outgoing") && text.Contains("Incoming") ? "Both" : text;
    }
    public static SolidColorBrush StateBrush(string state) => state switch
    {
        "Established" => Active,
        "Listen" or "Local endpoint" => Listening,
        _ => Idle,
    };
}
