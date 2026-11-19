using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace TaskWall;

/// <summary>
/// The themes. Glass ("dark", the XAML default of Theme.xaml): dark tint over the blurred wallpaper. Light ("light", or "auto"
/// on a light wallpaper): the same glass, light. Dark ("studio", in the style of Shader Studio): flat graphite panels with fine
/// lines, no wallpaper behind them, small corners. Light and dark replace the shared brushes at start-up, before any window is
/// built (changing the theme restarts the app, like the language). Code that draws translucent overlays uses <see cref="Ink"/>
/// – white on the dark themes, graphite on the light one.
/// </summary>
static class Th
{
    public static bool Light { get; private set; }

    /// <summary>The flat dark theme ("studio"): opaque surfaces, no blurred wallpaper, no glow, corners of at most 8 px.</summary>
    public static bool Studio { get; private set; }

    /// <summary>Overlay colour: white with alpha on dark, near-black with alpha on light.</summary>
    public static Color Ink(byte a) => Light ? Color.FromArgb((byte)(a * 0.9), 0x10, 0x14, 0x1C) : Color.FromArgb(a, 255, 255, 255);

    /// <summary>Solid panel colour (toasts, cards, the backlog drawer).</summary>
    public static Color Panel(byte a) => Light ? Color.FromArgb(a, 0xF7, 0xF8, 0xFA) : Studio ? Color.FromArgb(a, 0x1A, 0x1A, 0x1B) : Color.FromArgb(a, 0x16, 0x19, 0x21);

    /// <summary>Tint over the blurred wallpaper (the glass); in the dark theme the surface itself.</summary>
    public static Color Glass(byte a) => Light ? Color.FromArgb(a, 0xF4, 0xF5, 0xF8) : Studio ? Color.FromArgb(a, 0x13, 0x13, 0x14) : Color.FromArgb(a, 0x08, 0x09, 0x0D);

    /// <summary>The corners of the glass windows: the dark theme's are small, like the panels of Shader Studio.</summary>
    public static double Corner(double radius) => Studio ? System.Math.Min(radius, 8) : radius;

    /// <summary>"dark" (Glass), "studio" (Dark), "light" or "auto" (light wallpaper → light theme).</summary>
    public static void Apply(ResourceDictionary res, string setting)
    {
        Light = setting == "light" || (setting == "auto" && WallpaperIsLight());
        Studio = setting == "studio";
        if (!Light && !Studio) return;
        // a fresh colour dictionary in place of Colors.xaml (the first merged one): the styles of Theme.xaml
        // and all windows take their brushes from it, since nothing has been built yet
        var colors = new ResourceDictionary();
        void Set(string key, string color) => colors[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        if (Studio)
        {
            // neutral greys without a blue cast, lines finer and clearer than on the glass, opaque menus and dialogs
            Set("Fg", "#E4E4E4");
            Set("FgDim", "#A0A0A0");
            Set("FgFaint", "#6E6E6E");
            Set("Line", "#24FFFFFF");
            Set("Hover", "#12FFFFFF");
            Set("Field", "#FF1C1C1D");
            Set("FieldBorder", "#FF2E2E30");
            Set("MenuBg", "#FF1B1B1C");
            Set("DialogBg", "#FF161617");
            Set("CardBg", "#FF1E1E1F");
            Set("MenuBorder", "#FF2E2E30");
            Set("InkSoft", "#1CFFFFFF");
            Set("InkMid", "#36FFFFFF");
            Set("InkLine", "#5CFFFFFF");
            Set("InkHi", "#A0FFFFFF");
            res.MergedDictionaries[0] = colors;
            return;
        }
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
