using System.Windows;
using System.Windows.Media;

namespace TaskWall;

/// <summary>
/// The background light (Settings → Wygląd → Poświata): a soft glow in the accent at the top of the board that goes on
/// up into the top bar, so the two read as one.
/// </summary>
static class Glow
{
    public static bool Enabled => App.Settings.Glow;

    static Color Accent => Application.Current.Resources["AccentBrush"] is SolidColorBrush b ? b.Color : Colors.White;

    /// <summary>The light from the top of the board: a soft ellipse in the accent, fading out downwards.</summary>
    public static Brush Top() => Light(new Point(0.5, 0), 1);

    /// <summary>The same light in the top bar, rising from its bottom edge: the board's glow goes on up into it.</summary>
    public static Brush Bottom() => Light(new Point(0.5, 1), 7);

    static Brush Light(Point from, double radiusY)
    {
        var c = Accent;
        var b = new RadialGradientBrush
        {
            Center = from, GradientOrigin = from, RadiusX = 0.6, RadiusY = radiusY,
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
