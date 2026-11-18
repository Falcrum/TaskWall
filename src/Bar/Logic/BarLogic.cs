using System;
using System.Collections.Generic;
using System.Linq;

namespace TaskWall;

// Decisions of the bar and the desktop windows as plain functions (no WPF, no Win32): the windows only carry them out.
// The test project (tests/TaskWall.Tests) links this file.

/// <summary>A rectangle in physical pixels.</summary>
public readonly record struct PxRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Covers(PxRect other) => X <= other.X && Y <= other.Y && Right >= other.Right && Bottom >= other.Bottom;
}

/// <summary>Where a desktop window goes in the z-order.</summary>
public enum ZPlace { Bottom, Keep, Top, After }

public static class ZOrder
{
    /// <summary>
    /// The board lives right above the desktop. Rustyfiles' experimental desktop puts its own full-screen windows there
    /// (marked "rustyfiles.DesktopCover"): the board then stays right above the highest of them instead of under them
    /// (where it would vanish). <paramref name="topToBottom"/> is the z-order, the top window first.
    /// Returns <see cref="ZPlace.After"/> with the window to insert after (Win32: the board goes right under it).
    /// </summary>
    public static (ZPlace Place, long After) Desktop(IReadOnlyList<long> topToBottom, Func<long, bool> isCover, long self)
    {
        int cover = -1;
        for (int i = 0; i < topToBottom.Count; i++)
            if (topToBottom[i] != self && isCover(topToBottom[i])) { cover = i; break; }
        if (cover < 0) return (ZPlace.Bottom, 0);
        // the window right above the highest cover, not counting the board itself
        int above = cover - 1;
        if (above >= 0 && topToBottom[above] == self) return (ZPlace.Keep, 0);
        while (above >= 0 && topToBottom[above] == self) above--;
        return above < 0 ? (ZPlace.Top, 0) : (ZPlace.After, topToBottom[above]);
    }
}

public static class BarGeometry
{
    /// <summary>The bar's strip on a monitor: the full width, <paramref name="height"/> px at the top or the bottom.</summary>
    public static PxRect Strip(PxRect monitor, bool top, int height)
    {
        height = Math.Clamp(height, 1, monitor.Height);
        return new PxRect(monitor.X, top ? monitor.Y : monitor.Bottom - height, monitor.Width, height);
    }

    /// <summary>
    /// After Windows moved the proposed strip out of other bars' way (ABM_QUERYPOS: the taskbar at the same edge),
    /// the strip keeps its height from the edge Windows left it.
    /// </summary>
    public static PxRect AfterQuery(PxRect granted, bool top, int height) =>
        top ? new PxRect(granted.X, granted.Y, granted.Width, height)
            : new PxRect(granted.X, granted.Bottom - height, granted.Width, height);
}

public static class SectorLayout
{
    /// <summary>
    /// The three sectors on one bar: the left and the right at the sides, the middle centred on the bar – moved aside
    /// when a side sector reaches into it, narrowed when there is no room at all. Widths in, (x, width) of each out.
    /// </summary>
    public static ((double X, double W) Left, (double X, double W) Middle, (double X, double W) Right) Place(double width, double left, double middle, double right, double gap)
    {
        width = Math.Max(0, width);
        left = Math.Min(Math.Max(0, left), width);
        right = Math.Min(Math.Max(0, right), Math.Max(0, width - left));
        double from = left + (left > 0 ? gap : 0), to = width - right - (right > 0 ? gap : 0);
        double room = Math.Max(0, to - from);
        double w = Math.Min(Math.Max(0, middle), room);
        double x = Math.Clamp((width - w) / 2, from, Math.Max(from, to - w));
        return ((0, left), (x, w), (width - right, right));
    }
}

public static class Fullscreen
{
    static readonly HashSet<string> Shell = new(StringComparer.Ordinal) { "WorkerW", "Progman", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    /// <summary>A window covering its whole monitor that isn't the desktop or the taskbar (and not ours): a game, a video, a presentation.</summary>
    public static bool Covers(PxRect window, PxRect monitor, string windowClass, bool ours) =>
        !ours && !Shell.Contains(windowClass) && window.Covers(monitor);

    /// <summary>SHQueryUserNotificationState: busy (2), a Direct3D game (3), a presentation (4).</summary>
    public static bool FromNotificationState(int state) => state is 2 or 3 or 4;
}

public static class CrashLoop
{
    /// <summary>The crashes still counted (the last <paramref name="window"/>), the newest last.</summary>
    public static List<DateTime> Recent(IEnumerable<DateTime> crashes, DateTime now, TimeSpan window) =>
        crashes.Where(c => c <= now && now - c <= window).OrderBy(c => c).ToList();

    /// <summary>Three crashes in ten minutes: the bar isn't started this time.</summary>
    public static bool TooMany(IEnumerable<DateTime> crashes, DateTime now, int max = 3, TimeSpan? window = null) =>
        Recent(crashes, now, window ?? TimeSpan.FromMinutes(10)).Count >= max;
}

/// <summary>The full-width bar's sizes (DIP before the UI scale) for Settings → Pasek → Wysokość paska.</summary>
public static class BarMetrics
{
    /// <summary>The bar's height: large (taller with the dock, for 44 px icons), medium, small (a little more than a load ring).</summary>
    public static double Height(string size, bool dock) => size switch { "large" => dock ? 56 : 48, "small" => 30, _ => 40 };

    /// <summary>The dock's icons at rest: the bar's height less a margin above and below.</summary>
    public static double Icon(string size) => size switch { "large" => 44, "small" => 22, _ => 32 };

    /// <summary>The text of the clock, the date and the temperature.</summary>
    public static double Font(string size) => size switch { "large" => 17, "small" => 13, _ => 15 };

    /// <summary>The room past the bar that grown dock icons reach into: what the icon grows by, and a little.</summary>
    public static double Headroom(string size, bool dock, double peak) => !dock || peak <= 1 ? 0 : Math.Ceiling(Icon(size) * (peak - 1)) + 4;

    /// <summary>The corners: the board's radius, at most half the bar's height (a pill).</summary>
    public static double Radius(double height, double boardRadius = 22) => Math.Min(boardRadius, height / 2);
}
