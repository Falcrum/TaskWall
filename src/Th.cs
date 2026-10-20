using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace TaskWall;

/// <summary>
/// Dark or light theme. The dark one is the XAML default (Theme.xaml); the light one replaces the shared brushes at
/// start-up, before any window is built (changing the theme restarts the app, like the language). Code that draws
/// translucent overlays uses <see cref="Ink"/> – white on the dark glass, graphite on the light one.
/// </summary>
static class Th
{
    public static bool Light { get; private set; }

    /// <summary>Overlay colour: white with alpha on dark, near-black with alpha on light.</summary>
    public static Color Ink(byte a) => Light ? Color.FromArgb((byte)(a * 0.9), 0x10, 0x14, 0x1C) : Color.FromArgb(a, 255, 255, 255);

    /// <summary>Solid panel colour (toasts, cards, the backlog drawer).</summary>
    public static Color Panel(byte a) => Light ? Color.FromArgb(a, 0xF7, 0xF8, 0xFA) : Color.FromArgb(a, 0x16, 0x19, 0x21);

    /// <summary>Tint over the blurred wallpaper (the glass).</summary>
    public static Color Glass(byte a) => Light ? Color.FromArgb(a, 0xF4, 0xF5, 0xF8) : Color.FromArgb(a, 0x08, 0x09, 0x0D);

    /// <summary>"dark", "light" or "auto" (light wallpaper → light theme).</summary>
    public static void Apply(ResourceDictionary res, string setting)
    {
        Light = setting == "light" || (setting == "auto" && WallpaperIsLight());
        if (!Light) return;
        // a fresh colour dictionary in place of Colors.xaml (the first merged one): the styles of Theme.xaml
        // and all windows take their brushes from it, since nothing has been built yet
        var colors = new ResourceDictionary();
        void Set(string key, string color) => colors[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        Set("Fg", "#1D222C");
        Set("FgDim", "#4A5162");
        Set("FgFaint", "#79808F");
        Set("Line", "#2E10141C"); // day lines a bit stronger: on a light wallpaper the faint ones vanish
        Set("Hover", "#1410141C");
        Set("Field", "#0F10141C");
        Set("FieldBorder", "#2E10141C");
        Set("MenuBg", "#F5F7F8FA");
        Set("DialogBg", "#FFF3F4F7");
        Set("CardBg", "#FFFFFFFF");
        Set("MenuBorder", "#2410141C");
        Set("InkSoft", "#1C10141C");
        Set("InkMid", "#3310141C");
        Set("InkLine", "#5C10141C");
        Set("InkHi", "#9910141C");
        res.MergedDictionaries[0] = colors;
    }

    static bool WallpaperIsLight()
    {
        try
        {
            var mon = Monitors.Pick(App.Settings.Monitor);
            return Wallpaper.Luminance(Wallpaper.Resolve(mon), mon.Work) is > 0.6;
        }
        catch { return false; }
    }
}
