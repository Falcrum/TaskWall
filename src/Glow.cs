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

    /// <summary>The same light in the top bar, rising from its bottom edge: the board's glow goes on up into it.</summary>
    public static Brush Bottom()
    {
        var c = Accent;
        var b = new RadialGradientBrush
        {
            Center = new Point(0.5, 1), GradientOrigin = new Point(0.5, 1), RadiusX = 0.6, RadiusY = 7,
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

    /// <summary>Settings → Wygląd → Poświata ikon: the state icon and the priority / project bars glow in their own colour.</summary>
    public static bool IconsEnabled => App.Settings.GlowIcons;

    /// <summary>
    /// A mark's light (a thin priority / project / state bar): a soft oval in its colour, spilling past the mark on
    /// every side. A gradient, not a blur effect: free to draw on every row of the board. Null when it is off.
    /// </summary>
    public static FrameworkElement? Spill(Color c)
    {
        if (!IconsEnabled) return null;
        return new FreeBorder
        {
            Margin = new Thickness(-8, -6, -8, -6), IsHitTestVisible = false,
            Background = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0xA0, c.R, c.G, c.B), 0.25),
                    new GradientStop(Color.FromArgb(0x48, c.R, c.G, c.B), 0.6),
                    new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1),
                },
            },
        };
    }
}
