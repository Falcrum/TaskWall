using System;
using System.Collections.Generic;
using System.Linq;

namespace TaskWall;

// The dock's decisions as plain arithmetic, ported 1:1 from Rustyfiles (github.com/gelenmedya/rustyfiles, MIT, commit 243f22c:
// crates/rf-win/src/dock/spring.rs, magnify.rs, layout.rs, tasklist.rs) – the numbers and the formulas, not the code. The pointer,
// the rest layout and the clock are arguments, so every moment of an animation can be worked out and tested without a window.

/// <summary>
/// A weight on a damped spring pulled towards a target: <c>x'' = -2 z w x' - w² (x - target)</c>. It starts gently, comes to rest
/// softly and – unlike a fixed curve – a motion turned around in mid flight keeps its speed. <c>duration</c> is the time the motion is
/// felt to take (<c>w = 2π / duration</c>), <c>bounce</c> how far it swings past (<c>z = 1 - bounce</c>; 0: never), the way SwiftUI's
/// <c>Spring(duration:bounce:)</c> describes a spring. Closed form, so the same on a 60 Hz and a 240 Hz display and after a stall.
/// </summary>
public readonly record struct Spring(double Omega, double Zeta)
{
    public const double MaxBounce = 0.6;

    public static Spring Create(double durationMs, double bounce)
    {
        double d = double.IsFinite(durationMs) ? Math.Max(durationMs, 1) : 1;
        double b = double.IsFinite(bounce) ? Math.Clamp(bounce, 0, MaxBounce) : 0;
        return new Spring(2 * Math.PI / d, 1 - b);
    }

    public static Spring Smooth(double durationMs) => Create(durationMs, 0);

    /// <summary>Where the spring is <paramref name="t"/> ms after it was <paramref name="e0"/> from its target moving at <paramref name="v0"/> per ms: the distance left and the speed.</summary>
    public (double E, double V) At(double e0, double v0, double t)
    {
        t = double.IsFinite(t) ? Math.Max(t, 0) : 0;
        double w = Omega, z = Zeta;
        if (z >= 1)
        {
            double c = v0 + w * e0, decay = Math.Exp(-w * t);
            return ((e0 + c * t) * decay, (v0 - w * c * t) * decay);
        }
        double wd = w * Math.Sqrt(1 - z * z), dec = Math.Exp(-z * w * t), s = Math.Sin(wd * t), co = Math.Cos(wd * t);
        double b = (v0 + z * w * e0) / wd;
        return (dec * (e0 * co + b * s), dec * (v0 * co - (w * w * e0 + z * w * v0) / wd * s));
    }

    /// <summary>A time (ms) after which the spring stays within <paramref name="eps"/> of its target: never too early, at most a little late.</summary>
    public double SettleMs(double e0, double v0, double eps)
    {
        eps = Math.Max(Math.Abs(eps), 1e-6);
        double w = Omega, z = Zeta;
        if (z >= 1)
        {
            double c = Math.Abs(v0 + w * e0), a = Math.Abs(e0);
            if (a < eps && c * (1 / w) * Math.Exp(-1) < eps) return 0;
            double step = 0.2 / w, t = 0;
            for (int i = 0; i < 400; i++)
            {
                t += step;
                if (t * w >= 1 && (a + c * t) * Math.Exp(-w * t) < eps) break;
            }
            return t;
        }
        double wd = w * Math.Sqrt(1 - z * z);
        double amp = Math.Sqrt(e0 * e0 + Math.Pow((v0 + z * w * e0) / wd, 2));
        return amp <= eps ? 0 : Math.Log(amp / eps) / (z * w);
    }

    /// <summary>How much of the spring's way <see cref="Curve"/> holds (a spring without bounce is 99.66 % there).</summary>
    public const double CurveReach = 7.854;

    /// <summary>The spring's way from 0 to 1 over a fixed time (x 0..1 → place, exactly 1 at 1): panels growing out of the bar.</summary>
    public static double Curve(double x, double bounce = 0)
    {
        if (!double.IsFinite(x) || x <= 0) return 0;
        if (x >= 1) return 1;
        var s = Create(1, bounce);
        double reach = CurveReach / s.Omega;
        double At(double u) => 1 + s.At(-1, 0, reach * u).E;
        return At(x) / At(1);
    }
}

/// <summary>
/// A height that goes to 1 (<see cref="Enter"/>) or 0 (<see cref="Leave"/>) as a spring without bounce, from where it is and with the
/// speed it has; a pure function of the time (ms). The magnification's bell, the thin dock's growth.
/// </summary>
public sealed class Presence
{
    /// <summary>The spring is one and a half times the given duration (160 ms → a spring of 240 ms).</summary>
    public const double SpringFactor = 1.5;
    public const double Eps = 0.001;

    double _from, _vel, _to;
    long _started;
    double _duration;
    double _settle;

    /// <summary>Away. <paramref name="durationMs"/> 0: the animations are off, it jumps.</summary>
    public Presence(double durationMs) { _duration = durationMs; }

    Spring Spring => Spring.Smooth(_duration * SpringFactor);

    /// <summary>The animations setting changed: a motion under way ends where it was going.</summary>
    public void SetDuration(double durationMs)
    {
        if (_duration == durationMs) return;
        _duration = durationMs;
        _settle = 0;
    }

    (double X, double V) State(long now)
    {
        long elapsed = Math.Max(0, now - _started);
        if (_duration == 0 || elapsed >= _settle) return (_to, 0);
        var (e, v) = Spring.At(_from - _to, _vel, elapsed);
        return (_to + e, v);
    }

    public double Value(long now) => State(now).X;

    void Go(double to, long now)
    {
        if (_to == to) return;
        var (x, v) = State(now);
        (_from, _vel, _to, _started) = (x, v, to, now);
        _settle = _duration == 0 ? 0 : Math.Min(Math.Ceiling(Spring.SettleMs(x - to, v, Eps)), 10_000);
    }

    public void Enter(long now) => Go(1, now);
    public void Leave(long now) => Go(0, now);
    public bool IsIn => _to == 1;
    public bool Settled(long now) => _duration == 0 || now - _started >= _settle;
    public bool AtRest(long now) => _to == 0 && Settled(now);
}

/// <summary>An item of the dock's row: an icon or the line between the pinned and the other running programs.</summary>
public enum Slot { Icon, Separator }

/// <summary>How much the icon under the pointer grows (Settings: off / subtle / full).</summary>
public enum Strength { Off, Subtle, Full }

/// <summary>The magnification of the dock's icons, like the Mac's dock (Rustyfiles' magnify.rs and layout.rs).</summary>
public static class Magnify
{
    public const double IconRest = 44, DockPadding = 12, IconGap = 10, SeparatorWidth = 1;
    public const double ReachSlots = 2.5;
    public const double Reach = ReachSlots * (IconRest + IconGap);
    public const double FadeMs = 160;
    public const double MinGap = 2;
    /// <summary>The thin dock's icons (a maximised window on the screen); their centres stay.</summary>
    public const double ThinIcon = 30;

    public static double Peak(Strength s) => s switch { Strength.Subtle => 1.25, Strength.Full => 1.55, _ => 1.0 };
    public const double MaxPeak = 1.55;

    /// <summary>1 at the pointer, 0 at <see cref="Reach"/> and beyond, a raised cosine between (no kink at either end).</summary>
    public static double Bell(double distance)
    {
        double u = Math.Min(Math.Abs(distance) / Reach, 1);
        return 0.5 * (1 + Math.Cos(Math.PI * u));
    }

    /// <summary>The size factor of an icon whose rest centre is <paramref name="distance"/> px from the pointer; <paramref name="presence"/> is the bell's height 0..1.</summary>
    public static double Factor(double distance, Strength strength, double presence) =>
        1 + (Peak(strength) - 1) * Math.Clamp(presence, 0, 1) * Bell(distance);

    /// <summary>The width of a row for these icon sizes with the usual gaps.</summary>
    public static double NaturalWidth(IReadOnlyList<Slot> slots, IReadOnlyList<double> iconSizes)
    {
        if (slots.Count == 0) return 2 * DockPadding;
        int seps = slots.Count(s => s == Slot.Separator);
        return 2 * DockPadding + iconSizes.Sum() + seps * SeparatorWidth + (slots.Count - 1) * IconGap;
    }

    /// <summary>
    /// Where each item lies ((x, width) from the row's left edge, the padding included) for these icon sizes inside a row
    /// <paramref name="width"/> wide: the gaps give way down to <see cref="MinGap"/>, then all icons shrink together.
    /// </summary>
    public static List<(double X, double W)> Fit(IReadOnlyList<Slot> slots, IReadOnlyList<double> iconSizes, double width)
    {
        var out_ = new List<(double, double)>(slots.Count);
        if (slots.Count == 0) return out_;
        double inner = Math.Max(width - 2 * DockPadding, 0);
        int separators = slots.Count(s => s == Slot.Separator);
        double iconTotal = iconSizes.Sum();
        int gaps = slots.Count - 1;
        double fixedW = iconTotal + separators * SeparatorWidth;
        double gap, k;
        if (gaps == 0) { gap = 0; k = iconTotal > inner && iconTotal > 0 ? inner / iconTotal : 1; }
        else
        {
            gap = Math.Min((inner - fixedW) / gaps, IconGap);
            if (gap >= MinGap) k = 1;
            else
            {
                double room = inner - separators * SeparatorWidth - gaps * MinGap;
                gap = MinGap;
                k = iconTotal > 0 ? Math.Clamp(room / iconTotal, 0, 1) : 1;
            }
        }
        double x = DockPadding;
        int icon = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            if (i > 0) x += gap;
            double w = slots[i] == Slot.Icon ? (icon < iconSizes.Count ? iconSizes[icon] : IconRest) * k : SeparatorWidth;
            if (slots[i] == Slot.Icon) icon++;
            out_.Add((x, w));
            x += w;
        }
        return out_;
    }

    /// <summary>One moment of the dock: every item's (x, width) and the row's left edge and width (0 at the rest row's left edge).</summary>
    public sealed record Frame(List<(double X, double W)> Spans, double Left, double Width);

    /// <summary>
    /// The dock while the pointer is at <paramref name="pointerX"/> (from the rest row's left edge) with the bell at
    /// <paramref name="presence"/>: each icon its rest size times <see cref="Factor"/>, the row the rest width plus what the icons grew
    /// (at most <paramref name="maxWidth"/>), centred on the rest row's centre.
    /// </summary>
    public static Frame Layout(IReadOnlyList<Slot> slots, IReadOnlyList<(double X, double W)> rest, double restWidth, double pointerX, double presence, Strength strength, double maxWidth)
    {
        if (slots.Count == 0 || rest.Count != slots.Count) return new Frame(rest.ToList(), 0, restWidth);
        var sizes = slots.Zip(rest).Where(p => p.First == Slot.Icon).Select(p => p.Second.W * Factor(pointerX - (p.Second.X + p.Second.W / 2), strength, presence)).ToList();
        return WithSizes(slots, rest, restWidth, sizes, maxWidth);
    }

    public static Frame WithSizes(IReadOnlyList<Slot> slots, IReadOnlyList<(double X, double W)> rest, double restWidth, IReadOnlyList<double> sizes, double maxWidth)
    {
        maxWidth = Math.Max(maxWidth, restWidth);
        double grown = slots.Zip(rest).Where(p => p.First == Slot.Icon).Select(p => p.Second.W).Zip(sizes).Sum(p => p.Second - p.First);
        double width = Math.Clamp(restWidth + Math.Max(grown, 0), restWidth, maxWidth);
        double left = (restWidth - width) / 2;
        var spans = Fit(slots, sizes, width).Select(s => (s.X + left, s.W)).ToList();
        return new Frame(spans, left, width);
    }
}

/// <summary>Which windows are taskbar buttons and how they group – Explorer's rules (Rustyfiles' tasklist.rs).</summary>
public static class TaskRules
{
    const uint WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>A button: it asks for one (WS_EX_APPWINDOW), or has no owner and is neither a tool window nor one that never activates.</summary>
    public static bool StyleIsTask(bool hasOwner, uint exStyle) =>
        (exStyle & WS_EX_APPWINDOW) != 0 || (!hasOwner && (exStyle & WS_EX_TOOLWINDOW) == 0 && (exStyle & WS_EX_NOACTIVATE) == 0);

    /// <summary>An untitled window of no size is a helper – unless minimised (a captionless window reports no size then).</summary>
    public static bool IsHelper(string title, int width, int height, bool minimized) =>
        !minimized && title.Length == 0 && (width <= 0 || height <= 0);

    /// <summary>What Explorer groups by: the AppUserModelID, else the program's path (any case), else the process.</summary>
    public static string Group(string? appId, string? exe, uint pid) =>
        // "<id>;PrivateBrowsingAUMID" (Firefox's private windows): with the program's other windows, not an icon of their own
        !string.IsNullOrEmpty(appId) ? appId.Split(';')[0].ToLowerInvariant() : !string.IsNullOrEmpty(exe) ? exe.ToLowerInvariant() : "pid:" + pid;
}

/// <summary>The dock's row from the pinned programs and the running groups: the pinned first (in their order), a line, then the rest.</summary>
public static class DockRow
{
    public sealed record Item(string Key, bool Pinned, int Windows);

    /// <summary>
    /// <paramref name="pinned"/>: the pinned programs' group keys in order; <paramref name="running"/>: each running group's key and
    /// its window count, in the order they appeared. A running group that is pinned shows on its pinned icon.
    /// </summary>
    public static (List<Item> Items, List<Slot> Slots) Build(IReadOnlyList<string> pinned, IReadOnlyList<(string Key, int Windows)> running)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, n) in running) counts[k] = counts.GetValueOrDefault(k) + n;
        var items = new List<Item>();
        var slots = new List<Slot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in pinned)
            if (seen.Add(k)) { items.Add(new Item(k, true, counts.GetValueOrDefault(k))); slots.Add(Slot.Icon); }
        var rest = running.Select(r => r.Key).Where(k => !seen.Contains(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (items.Count > 0 && rest.Count > 0) slots.Add(Slot.Separator);
        foreach (var k in rest) { items.Add(new Item(k, false, counts[k])); slots.Add(Slot.Icon); }
        return (items, slots);
    }

    /// <summary>Where a dragged pinned icon goes: the index among the pinned whose rest centre the pointer passed.</summary>
    public static int DropIndex(IReadOnlyList<double> pinnedCentres, double pointerX)
    {
        int i = 0;
        while (i < pinnedCentres.Count && pointerX > pinnedCentres[i]) i++;
        return i;
    }
}
