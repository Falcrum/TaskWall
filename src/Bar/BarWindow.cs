using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace TaskWall;

/// <summary>
/// Three sectors on one bar (children: left, middle, right): the side ones at the edges, the middle one centred on the
/// bar – where they go is <see cref="SectorLayout.Place"/>.
/// </summary>
sealed class SectorPanel : Panel
{
    public double Gap { get; set; } = 16;

    protected override Size MeasureOverride(Size available)
    {
        double h = 0;
        foreach (UIElement c in InternalChildren)
        {
            c.Measure(new Size(double.PositiveInfinity, available.Height));
            h = Math.Max(h, c.DesiredSize.Height);
        }
        return new Size(double.IsInfinity(available.Width) ? 0 : available.Width, double.IsInfinity(available.Height) ? h : available.Height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (InternalChildren.Count < 3) return final;
        UIElement l = InternalChildren[0], m = InternalChildren[1], r = InternalChildren[2];
        var (pl, pm, pr) = SectorLayout.Place(final.Width, l.DesiredSize.Width, m.DesiredSize.Width, r.DesiredSize.Width, Gap);
        l.Arrange(new Rect(pl.X, 0, pl.W, final.Height));
        m.Arrange(new Rect(pm.X, 0, pm.W, final.Height));
        r.Arrange(new Rect(pr.X, 0, pr.W, final.Height));
        return final;
    }
}

/// <summary>
/// The bar across the whole screen (Settings → Ogólne → Pasek na całą szerokość, off by default): one continuous glass at
/// the top or the bottom of the screen, registered with Windows as an app bar (<see cref="AppBar"/>) so its strip is
/// reserved. Left: Start and the weather now; middle: room for the dock; right: the computer's load, the tray's hidden
/// icons and the clock with the month calendar. A full-screen program on its monitor hides it and stops its timers.
/// </summary>
public sealed class BarWindow : GlassWindow
{
    /// <summary>The bar's height (DIP before the UI scale): with the dock it is taller, for the 44 px icons.</summary>
    public static double HeightFor(bool dock) => dock ? 56 : 48;

    /// <summary>The room past the bar's edge that grown dock icons reach into (window, not reserved): none without magnification.</summary>
    public static double HeadroomFor(bool dock, string magnify) =>
        !dock || magnify == "off" ? 0 : Math.Ceiling(Magnify.IconRest * (Magnify.Peak(magnify == "subtle" ? Strength.Subtle : Strength.Full) - 1)) + 4;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern bool RegisterShellHookWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool DeregisterShellHookWindow(IntPtr h);
    static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
    static readonly uint ShellHook = RegisterWindowMessage("SHELLHOOK");

    readonly WeatherBadge _weather = new();
    readonly ClockFace _clock = new();
    readonly LoadMeters _meters = new();
    readonly CalendarWindow _calendar = new();
    readonly Border _glow = new();
    readonly Border _middle = new();
    readonly SectorPanel _sectors = new() { Margin = new Thickness(10, 0, 12, 0) };
    readonly Button _tray;
    DockView? _dock;
    AppBar? _appBar;
    bool _top = true, _narrow, _shellHooked;
    double _barHeight = HeightFor(false);

    public CalendarWindow Calendar => _calendar;
    /// <summary>Hidden while a program runs full screen (or the screen is locked, the computer asleep).</summary>
    public bool Paused { get; private set; }
    public bool Top => _top;
    /// <summary>The reserved strip (physical pixels): the glass; the window may be taller by <see cref="Headroom"/>.</summary>
    public PxRect StripRect { get; private set; }
    /// <summary>The transparent room past the glass that grown dock icons reach into (DIP before the UI scale).</summary>
    public double Headroom { get; private set; }

    /// <summary>Windows moved the other bars (the taskbar resized, a monitor changed): lay the windows out again.</summary>
    public event Action? Moved;

    public BarWindow()
    {
        Radius = 0;
        FontFamily = Ui.Font("UiFont");
        Foreground = Ui.Res("Fg");
        Title = "TaskWall bar";
        // above other windows like the taskbar (nothing overlaps its reserved strip anyway; a full-screen program hides it)
        StaysOnTop = true;
        _calendar.StaysOnTop = true;

        // ----- left: Start, the weather -----
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var start = IconButton(Icons.Start(), L.T("Start"), (_, _) => Keys.Press(Keys.LWin));
        left.Children.Add(start);
        left.Children.Add(Separator());
        left.Children.Add(_weather);

        // ----- middle: the dock (Settings → Pasek → Dock) -----

        // ----- right: the load, the tray's hidden icons, the clock -----
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(_meters);
        _tray = IconButton(Icons.Caret(true), L.T("Ukryte ikony zasobnika"), (_, _) => OpenTrayOverflow());
        _tray.Margin = new Thickness(10, 0, 2, 0);
        right.Children.Add(_tray);
        right.Children.Add(Separator());
        // a click on the clock while the calendar is open: pressing the mouse already closed it (it lost the focus) – don't open it again
        _clock.Clicked += () => { if (!_calendar.JustClosed) _calendar.Toggle(); };
        _clock.TenMinutes += _weather.Update;
        right.Children.Add(_clock);

        _sectors.Children.Add(left);
        _sectors.Children.Add(_middle);
        _sectors.Children.Add(right);
        _sectors.SizeChanged += (_, e) =>
        {
            bool narrow = e.NewSize.Width < 1100;
            if (narrow == _narrow) return;
            _narrow = _weather.Narrow = _meters.Narrow = narrow;
            _weather.Update();
            _meters.Build(true, IsVisible && !Paused);
        };
        // the sectors lie over the glass, not in it: the dock's grown icons reach past the glass into the window's transparent room
        _sectors.Background = Brushes.Transparent; // the whole strip takes the right click
        _sectors.MouseRightButtonUp += (_, e) => { e.Handled = true; App.Instance.ShowSettings(); };
        _sectors.MouseEnter += (_, _) => _dock?.PointerNear(true);
        _sectors.MouseLeave += (_, _) => _dock?.PointerNear(false);
        Overlay.Children.Add(_sectors);
        Body = _glow; // the glass: the glow only

        IsVisibleChanged += (_, _) => _meters.Build(false, IsVisible && !Paused);
        Closed += (_, _) => { _appBar?.Remove(); if (_shellHooked) DeregisterShellHookWindow(Hwnd); _calendar.Close(); };
        SetEdge(true);
        ApplyGlow();
        _weather.Update();
        ApplyDock();
    }

    static Button IconButton(FrameworkElement icon, string tip, RoutedEventHandler click)
    {
        var b = new Button { Style = (Style)Application.Current.Resources["BarIcon"], Content = icon, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center };
        b.Click += click;
        return b;
    }

    static FrameworkElement Separator() => new Border { Width = 1, Height = 20, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, Background = Ui.Brush(Th.Ink(0x22)) };

    /// <summary>The thin line and the glow on the side facing the board; the chevrons point where the panels open.</summary>
    void SetEdge(bool top)
    {
        _top = top;
        BorderSides = top ? new Thickness(0, 0, 0, 1) : new Thickness(0, 1, 0, 0);
        _clock.OpensUp = !top;
        _calendar.OpensUp = !top;
        _tray.Content = Icons.Caret(!top);
        // the glass and the sectors at the screen's edge of the window; the rest of it is the dock's room
        Frame.VerticalAlignment = _sectors.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        ApplyGlow();
    }

    /// <summary>The board's light: from the bar's edge that faces the board.</summary>
    public void ApplyGlow() => _glow.Background = Glow.Enabled ? (_top ? Glow.Bottom() : Glow.Top()) : null;

    public void ApplySettings()
    {
        ApplyGlow();
        _meters.Build(false, IsVisible && !Paused);
        _weather.Update();
        _clock.Refresh();
        ApplyDock();
    }

    /// <summary>The dock in the middle sector, or none (Settings → Pasek → Dock); the bar's height follows.</summary>
    void ApplyDock()
    {
        bool want = App.Settings.Dock;
        _barHeight = HeightFor(want);
        _sectors.Height = _barHeight;
        if (want && _dock == null)
        {
            _dock = new DockView(this);
            _middle.Child = _dock;
        }
        else if (!want && _dock != null)
        {
            _middle.Child = null;
            _dock = null;
        }
        if (_dock != null) { _dock.BarHeight = _barHeight; _dock.Refresh(); }
    }

    public void SetScale(double scale)
    {
        ApplyScale(scale);
        _calendar.SetScale(scale);
        _weather.Update();
        _meters.Build(true, IsVisible && !Paused);
    }

    /// <summary>
    /// Reserves the strip on <paramref name="monitor"/> (physical pixels) and moves the window into it – taller by
    /// <paramref name="headroom"/> px towards the board when the dock's icons grow past the glass; returns the strip (Windows may
    /// have moved it out of the taskbar's way).
    /// </summary>
    /// <summary>Dev runs: the dock as if the pointer were over it.</summary>
    public void DevHover() => _dock?.DevHover();
    public void DevPreview() => _dock?.DevPreview();

    public PxRect Dock(PxRect monitor, bool top, int height, int headroom, double scale, double headroomDip)
    {
        new WindowInteropHelper(this).EnsureHandle();
        if (_appBar == null)
        {
            HwndSource.FromHwnd(Hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                // Explorer restarted ("TaskbarCreated"): it forgot this bar, and a new taskbar is on screen – all again
                if (msg == (int)TaskbarCreated)
                    Dispatcher.BeginInvoke(() => { _appBar?.Forget(); WindowsTaskbar.Rehide(); _shellHooked = RegisterShellHookWindow(Hwnd); Moved?.Invoke(); }, DispatcherPriority.Background);
                // windows created, gone, activated, flashing: the dock follows (no polling)
                else if (msg == (int)ShellHook && _dock != null)
                    _dock.OnShell(w.ToInt32() & 0xFFFF, l);
                return IntPtr.Zero;
            });
            _shellHooked = RegisterShellHookWindow(Hwnd);
            _appBar = new AppBar(Hwnd);
            _appBar.PositionChanged += () => Dispatcher.BeginInvoke(() => Moved?.Invoke(), DispatcherPriority.Background);
            _appBar.FullscreenChanged += full => Dispatcher.BeginInvoke(() => Pause(full));
        }
        if (top != _top) SetEdge(top);
        var r = _appBar.Dock(monitor, top, height);
        StripRect = r;
        Headroom = headroomDip;
        Frame.Height = r.Height / scale;
        PlacePhysical(r.X, top ? r.Y : r.Y - headroom, r.Width, r.Height + headroom, scale);
        Guard.Watch(Hwnd);
        return r;
    }

    /// <summary>Gives the strip back (the bar switched off).</summary>
    public void Undock()
    {
        _appBar?.Remove();
        Guard.Watch(IntPtr.Zero);
    }

    /// <summary>
    /// A full-screen program (a game, a film, a presentation), the locked screen, sleep: the bar hides and stops its
    /// clock and the load reading; back afterwards with fresh readings.
    /// </summary>
    public void Pause(bool paused)
    {
        if (paused == Paused) return;
        Paused = paused;
        _clock.Pause(paused);
        _dock?.Pause(paused);
        if (paused)
        {
            if (_calendar.IsOpen) _calendar.Toggle(false);
            _meters.Build(false, false);
            Hide();
        }
        else
        {
            Show();
            _weather.Update();
            _meters.Build(false, true);
        }
    }

    /// <summary>
    /// The hidden icons of the Windows tray: Win+B moves the focus to the tray's chevron, Enter opens it – Windows puts
    /// that window over its own taskbar, so it is moved to this bar's chevron as soon as it shows (Windows 11:
    /// TopLevelWindowForOverflowXamlIsland, Windows 10: NotifyIconOverflowWindow).
    /// </summary>
    public void OpenTrayOverflow()
    {
        Keys.Press(Keys.LWin, Keys.B);
        var anchor = _tray.PointToScreen(new Point(_tray.ActualWidth, 0)); // physical pixels: the chevron's right edge
        var strip = StripRect;
        int tries = 0;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        t.Tick += (_, _) =>
        {
            if (++tries == 2) Keys.Press(Keys.Return);
            if (tries < 3) return;
            var w = Native.FindWindow("TopLevelWindowForOverflowXamlIsland", null);
            if (w == IntPtr.Zero) w = Native.FindWindow("NotifyIconOverflowWindow", null);
            if (w != IntPtr.Zero && Native.IsWindowVisible(w) && Native.GetWindowRect(w, out var r) && r.Right > r.Left)
            {
                int width = r.Right - r.Left, height = r.Bottom - r.Top, gap = (int)Math.Round(6 * VisualTreeHelper.GetDpi(this).DpiScaleY);
                int x = (int)Math.Round(anchor.X) - width;
                int y = _top ? strip.Y + strip.Height + gap : strip.Y - height - gap;
                Native.SetWindowPos(w, IntPtr.Zero, Math.Max(strip.X, x), y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                t.Stop();
            }
            else if (tries > 25) t.Stop(); // didn't open (all icons shown on the taskbar, no chevron): nothing to move
        };
        t.Start();
    }
}

/// <summary>Key presses sent to Windows (the Start menu, the tray's chevron).</summary>
static class Keys
{
    public const byte LWin = 0x5B, B = 0x42, Return = 0x0D;
    const uint KEYEVENTF_KEYUP = 2;

    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    /// <summary>The keys down in order, then up in reverse (Win+B).</summary>
    public static void Press(params byte[] keys)
    {
        foreach (var k in keys) keybd_event(k, 0, 0, UIntPtr.Zero);
        for (int i = keys.Length - 1; i >= 0; i--) keybd_event(keys[i], 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}
