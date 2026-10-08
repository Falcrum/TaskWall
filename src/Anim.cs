using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DeskWall;

/// <summary>Small animation helpers; all of them become instant when animations are off.</summary>
static class Anim
{
    public static bool On => App.Settings.Animations;
    static readonly IEasingFunction Out = new CubicEase { EasingMode = EasingMode.EaseOut };

    static TranslateTransform Translate(UIElement el)
    {
        if (el.RenderTransform is TranslateTransform t && !t.IsFrozen) return t;
        var nt = new TranslateTransform();
        el.RenderTransform = nt;
        return nt;
    }

    static DoubleAnimation A(double from, double to, int ms, IEasingFunction? ease = null) =>
        new(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease ?? Out };

    /// <summary>Fade in while sliding from (dx, dy) to the final position.</summary>
    public static void Enter(UIElement el, double dx = 0, double dy = 8, int ms = 260, int delayMs = 0)
    {
        if (!On) return;
        var t = Translate(el);
        var fade = A(0, 1, ms);
        fade.BeginTime = TimeSpan.FromMilliseconds(delayMs);
        el.Opacity = 0;
        el.BeginAnimation(UIElement.OpacityProperty, fade);
        if (dx != 0) t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out, BeginTime = fade.BeginTime });
        if (dy != 0) t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out, BeginTime = fade.BeginTime });
    }

    /// <summary>Small "pop" (used when a task gets checked).</summary>
    public static void Pop(FrameworkElement el)
    {
        if (!On) return;
        var s = new ScaleTransform(1, 1);
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = s;
        var a = new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut } };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    /// <summary>Fade + collapse height, then run <paramref name="done"/>.</summary>
    public static void Leave(FrameworkElement el, Action done)
    {
        if (!On) { done(); return; }
        el.IsHitTestVisible = false;
        var h = el.ActualHeight;
        var fade = A(el.Opacity, 0, 150);
        var shrink = A(h, 0, 200);
        shrink.BeginTime = TimeSpan.FromMilliseconds(60);
        shrink.Completed += (_, _) => done();
        el.BeginAnimation(UIElement.OpacityProperty, fade);
        el.BeginAnimation(FrameworkElement.HeightProperty, shrink);
    }

    public static void Slide(UIElement el, double fromX, double toX, int ms = 280, Action? done = null)
    {
        var t = Translate(el);
        if (!On) { t.BeginAnimation(TranslateTransform.XProperty, null); t.X = toX; done?.Invoke(); return; }
        var a = A(fromX, toX, ms);
        if (done != null) a.Completed += (_, _) => done();
        t.BeginAnimation(TranslateTransform.XProperty, a);
    }

    public static void Height(FrameworkElement el, double to, int ms = 300, Action? done = null)
    {
        double from = double.IsNaN(el.Height) ? el.ActualHeight : el.Height;
        if (!On) { el.BeginAnimation(FrameworkElement.HeightProperty, null); el.Height = to; done?.Invoke(); return; }
        var a = A(from, to, ms);
        a.Completed += (_, _) =>
        {
            el.BeginAnimation(FrameworkElement.HeightProperty, null);
            el.Height = to;
            done?.Invoke();
        };
        el.BeginAnimation(FrameworkElement.HeightProperty, a);
    }

    /// <summary>Smooth hover background on a Border (uses its own brush instance).</summary>
    public static void HoverBackground(System.Windows.Controls.Border b, Color normal, Color hover)
    {
        var brush = new SolidColorBrush(normal);
        b.Background = brush;
        b.MouseEnter += (_, _) => brush.BeginAnimation(SolidColorBrush.ColorProperty, On ? new ColorAnimation(hover, TimeSpan.FromMilliseconds(120)) : null);
        b.MouseLeave += (_, _) => brush.BeginAnimation(SolidColorBrush.ColorProperty, On ? new ColorAnimation(normal, TimeSpan.FromMilliseconds(200)) : null);
        if (!On)
        {
            b.MouseEnter += (_, _) => brush.Color = hover;
            b.MouseLeave += (_, _) => brush.Color = normal;
        }
    }
}
