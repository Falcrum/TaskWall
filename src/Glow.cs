using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace TaskWall;

/// <summary>
/// The soft light round a few things that matter now (Settings → Wygląd → Poświata): today, the selected task, the
/// running work timer, the view that is on, a task in progress, and a glow in the accent at the top of the board.
/// A blur costs the graphics card a little for each element, so it goes only on these few, never on every row.
/// </summary>
static class Glow
{
    public static bool Enabled => App.Settings.Glow;

    static Color Accent => Application.Current.Resources["AccentBrush"] is SolidColorBrush b ? b.Color : Colors.White;

    static DropShadowEffect Light(Color c, double blur, double opacity) => new() { ShadowDepth = 0, BlurRadius = blur, Color = c, Opacity = opacity };

    /// <summary>The element's own shape glows (text, an icon, a pill): in the accent unless a colour is given.</summary>
    public static void Shape(UIElement e, Color? c = null, double blur = 10, double opacity = 0.8)
    {
        if (Enabled) e.Effect = Light(c ?? Accent, blur, opacity);
    }

    /// <summary>
    /// A glowing frame to lay over an element (no fill, nothing inside it glows): put it in the element's own grid,
    /// spanning it. <paramref name="margin"/> places it on the element's edge.
    /// </summary>
    public static Border? Halo(CornerRadius corner, Thickness margin, Color? c = null, double blur = 16, double opacity = 0.9)
    {
        if (!Enabled) return null;
        var color = c ?? Accent;
        return new Border
        {
            CornerRadius = corner, Margin = margin, BorderThickness = new Thickness(1.4), IsHitTestVisible = false, Tag = "glow",
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xC0, color.R, color.G, color.B)), Effect = Light(color, blur, opacity),
        };
    }

    /// <summary>The light from the top of the board: a soft ellipse in the accent, fading out downwards.</summary>
    public static Brush Top()
    {
        var c = Accent;
        var b = new RadialGradientBrush
        {
            Center = new Point(0.5, 0), GradientOrigin = new Point(0.5, 0), RadiusX = 0.6, RadiusY = 1,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x30, c.R, c.G, c.B), 0),
                new GradientStop(Color.FromArgb(0x10, c.R, c.G, c.B), 0.55),
                new GradientStop(Color.FromArgb(0x00, c.R, c.G, c.B), 1),
            },
        };
        b.Freeze();
        return b;
    }
}
