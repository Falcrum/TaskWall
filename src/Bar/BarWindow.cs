using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// How squeezed the side sectors are (0: all of it; each step shows less – the weather shorter, then only the temperature, then
    /// the load without numbers). The bar applies a level; <see cref="MaxLevel"/> is the last one.
    /// </summary>
    public Action<int>? ApplyLevel { get; set; }
    public int MaxLevel { get; set; }
    int _level;
    readonly Dictionary<int, double> _sides = new(); // the side sectors' width at each level, as last measured

    double MeasureAll(Size available)
    {
        double h = 0;
        foreach (UIElement c in InternalChildren)
        {
            c.Measure(new Size(double.PositiveInfinity, available.Height));
            h = Math.Max(h, c.DesiredSize.Height);
        }
        return h;
    }

    protected override Size MeasureOverride(Size available)
    {
        double h = MeasureAll(available);
        if (InternalChildren.Count >= 3 && !double.IsInfinity(available.Width) && ApplyLevel != null)
        {
            // the dock in the middle keeps its room; the sides give way step by step – and take it back when there is room again
            for (int guard = 0; guard < 2 * MaxLevel + 2; guard++)
            {
                double sides = InternalChildren[0].DesiredSize.Width + InternalChildren[2].DesiredSize.Width;
                _sides[_level] = sides;
                double need = sides + InternalChildren[1].DesiredSize.Width + 2 * Gap;
                if (need > available.Width && _level < MaxLevel) _level++;
                else if (_level > 0 && _sides.TryGetValue(_level - 1, out var wider) && wider + InternalChildren[1].DesiredSize.Width + 2 * Gap <= available.Width) _level--;
                else break;
                ApplyLevel(_level);
                h = MeasureAll(available);
            }
        }
        return new Size(double.IsInfinity(available.Width) ? 0 : available.Width, double.IsInfinity(available.Height) ? h : available.Height);
    }

    /// <summary>The sectors' content changed size by itself (the weather, the meters chosen): measured afresh at every level.</summary>
    public void Forget() { _sides.Clear(); InvalidateMeasure(); }

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
    static AppSettings S => App.Settings;

    /// <summary>The bar's height now (DIP before the UI scale): Settings → Pasek → Wysokość paska, taller with the dock when large.</summary>
    public static double HeightNow => BarMetrics.Height(S.BarSize, S.Dock);

    static Strength MagnifyNow => S.DockMagnify switch { "off" => Strength.Off, "subtle" => Strength.Subtle, _ => Strength.Full };

    /// <summary>The room past the bar that grown dock icons reach into (window, not reserved): none without the dock or its magnification.</summary>
    public static double HeadroomNow => BarMetrics.Headroom(S.BarSize, S.Dock, Magnify.Peak(MagnifyNow));

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern bool RegisterShellHookWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool DeregisterShellHookWindow(IntPtr h);
    static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
    static readonly uint ShellHook = RegisterWindowMessage("SHELLHOOK");

    readonly WeatherBadge _weather = new();
    readonly ClockFace _clock = new(dateFirst: true); // "ŚRODA 18 LISTOPAD 22:07"
    readonly LoadMeters _meters = new();
    readonly CalendarWindow _calendar = new();
    readonly Border _glow = new();
    /// <summary>The middle: the search's magnifier, then the dock.</summary>
    readonly StackPanel _middle = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _dockHost = new();
    readonly Button _searchButton;
    readonly AppSearch _appSearch = new();
    readonly SectorPanel _sectors = new() { Margin = new Thickness(10, 0, 12, 0) };
    readonly Button _tray;
    readonly NetworkButton _network;
    readonly VolumeButton _volumeButton;
    DockView? _dock;
    AppBar? _appBar;
    bool _top = true, _shellHooked;
    double _barHeight = HeightNow;

    public CalendarWindow Calendar => _calendar;
    /// <summary>Hidden while a program runs full screen (or the screen is locked, the computer asleep).</summary>
    public bool Paused { get; private set; }
    public bool AtTop => _top;
    /// <summary>The reserved strip (physical pixels): the glass and its gap from the screen's edge; the window may be taller by <see cref="Headroom"/>.</summary>
    public PxRect StripRect { get; private set; }
    /// <summary>The glass itself (physical pixels): the strip less the gap at the screen's edge and its sides, like the board's.</summary>
    public PxRect FrameRect { get; private set; }
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
        var start = IconButton(Icons.Start(), L.T("Start"), (_, _) => ToggleStart());
        WatchStart();
        left.Children.Add(start);
        left.Children.Add(Separator());
        left.Children.Add(_weather);

        // ----- middle: the search's magnifier (its field opens right over it), the dock (Settings → Pasek → Dock) -----
        _searchButton = IconButton(Icons.Search(18), L.T("Szukaj"), (_, _) => OpenSearch());
        _searchButton.Margin = new Thickness(0, 0, 6, 0);
        _middle.Children.Add(_searchButton);
        _middle.Children.Add(_dockHost);
        AppSearch.Warm();

        // ----- right: the load, the tray's hidden icons, the clock -----
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(_meters);
        // a line between the load and the connection / speakers, as between the other groups
        right.Children.Add(Separator());
        // the connection and the speakers
        _network = new NetworkButton(this);
        right.Children.Add(_network);
        _volumeButton = new VolumeButton(this);
        right.Children.Add(_volumeButton);
        _tray = IconButton(Icons.Caret(true), L.T("Ukryte ikony zasobnika"), (_, _) => ToggleTray());
        _tray.Margin = new Thickness(2, 0, 2, 0);
        right.Children.Add(_tray);
        right.Children.Add(Separator());
        // a click on the clock while the calendar is open: pressing the mouse already closed it (it lost the focus) – don't open it again
        _clock.Clicked += () => { if (!_calendar.JustClosed) _calendar.Toggle(); };
        _clock.TenMinutes += _weather.Update;
        right.Children.Add(_clock);

        _sectors.Children.Add(left);
        _sectors.Children.Add(_middle);
        _sectors.Children.Add(right);
        // when the dock fills the bar the sides give way: the weather shorter, then only the temperature (the rest in its tool
        // tip), then the load without its numbers – and back as soon as there is room
        _sectors.MaxLevel = 3;
        _sectors.ApplyLevel = level =>
        {
            _weather.Compact = level >= 2;
            _weather.Compact = level >= 2;
            _weather.Update();
            bool narrowMeters = level >= 3;
            if (_meters.Narrow != narrowMeters) { _meters.Narrow = narrowMeters; _meters.Build(true, IsVisible && !Paused); }
        };
        WeatherService.Changed += _sectors.Forget;
        // the sectors lie over the glass, not in it: the dock's grown icons reach past the glass into the window's transparent room
        _sectors.Background = Brushes.Transparent; // the whole strip takes the right click
        // the right button: a small menu, as on the Windows taskbar (not the settings at once)
        _sectors.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var m = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
            void Item(string text, Action a) { var i = new MenuItem { Header = text }; i.Click += (_, _) => a(); m.Items.Add(i); }
            // the board out of the way and back (like the tray icon's menu; the hotkey brings it back too)
            Item(S.BoardHidden ? L.T("Pokaż tablicę") : L.T("Ukryj tablicę"), () => App.Instance.SetBoardHidden(!S.BoardHidden));
            m.Items.Add(new Separator());
            Item(L.T("Ustawienia paska…"), () => App.Instance.ShowSettings("bar"));
            Item(L.T("Menedżer zadań"), () => GlassWindow.OpenUrl("taskmgr.exe"));
            m.Items.Add(new Separator());
            Item(L.T("Ustawienia TaskWall…"), () => App.Instance.ShowSettings());
            m.IsOpen = true;
        };
        _sectors.MouseEnter += (_, _) => _dock?.PointerNear(true);
        _sectors.MouseLeave += (_, _) => _dock?.PointerNear(false);
        Overlay.Children.Add(_sectors);
        Body = _glow; // the glass: the glow only

        IsVisibleChanged += (_, _) => _meters.Build(false, IsVisible && !Paused);
        Closed += (_, _) =>
        {
            _appBar?.Remove();
            if (_shellHooked) DeregisterShellHookWindow(Hwnd);
            // off every static event: a closed bar must not stay alive through them (and go on working for nothing)
            WeatherService.Changed -= _sectors.Forget;
            _weather.Detach();
            _clock.Detach();
            _meters.Detach();
            _network.Detach();
            _volumeButton.Detach();
            _dock?.Detach();
            CloseTray();
            _calendar.Close();
        };
        SetEdge(true);
        ApplyGlow();
        _weather.Update();
        ApplyDock();
    }

    static Button IconButton(FrameworkElement icon, string tip, RoutedEventHandler click)
    {
        // at least 32 × 32 to aim at, and a name for screen readers (not only the tool tip)
        var b = new Button { Style = (Style)Application.Current.Resources["BarIcon"], Content = icon, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center, MinWidth = 32, MinHeight = 32 };
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        b.Click += click;
        return b;
    }

    /// <summary>Dev runs (--open stack / search): the first stack's island, the search with some words typed.</summary>
    public void DevStack() => _dock?.DevStack();
    public void DevSearch(string words) { _appSearch.Toggle(_searchButton, this); _appSearch.DevType(words); }

    /// <summary>The magnifier: TaskWall's own field over it, or the search Settings → Pasek → Wyszukiwarka names, by its shortcut.</summary>
    void OpenSearch()
    {
        switch (App.Settings.SearchKeys)
        {
            case "TaskWall": _appSearch.Toggle(_searchButton, this); break;
            case "Alt+Space": Keys.Press(Keys.Alt, Keys.Space); break;
            case "Win+S": Keys.Press(Keys.LWin, Keys.S); break;
            default: Keys.Press(Keys.LWin, Keys.Alt, Keys.Space); break;
        }
    }

    static FrameworkElement Separator() => new Border { Width = 1, Height = 20, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, Background = Ui.Brush(Th.Ink(0x22)) };

    /// <summary>The thin line and the glow on the side facing the board; the chevrons point where the panels open.</summary>
    void SetEdge(bool top)
    {
        _top = top;
        BorderSides = null; // a frame all round, like the board's (the glass stands apart from the screen's edge)
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
        bool want = S.Dock;
        _barHeight = HeightNow;
        _sectors.Height = _barHeight;
        // the text the size of the bar: the date and time as big as the temperature
        double font = BarMetrics.Font(S.BarSize);
        _weather.TextSize = font;
        _clock.TextSize = font;
        _meters.TextSize = font;
        _meters.Build(false, IsVisible && !Paused);
        _weather.Update();
        _sectors.Forget();
        if (want && _dock == null)
        {
            _dock = new DockView(this);
            _dockHost.Child = _dock;
        }
        else if (!want && _dock != null)
        {
            _dock.Detach();
            _dockHost.Child = null;
            _dock = null;
        }
        if (_dock != null) { _dock.BarHeight = _barHeight; _dock.IconSize = BarMetrics.Icon(S.BarSize); _dock.Refresh(); }
        // the magnifier next to the first icon: over the dock's room for growing icons (above it, so it still takes the click)
        _searchButton.Margin = new Thickness(0, 0, _dock != null ? 4 - _dock.IconSize / Magnify.IconRest * DockView.Reserve : 0, 0);
        Panel.SetZIndex(_searchButton, 1);
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
    /// <summary>Dev check: clicks the network and the volume buttons and logs their panels (no screenshot).</summary>
    public void DevPanels()
    {
        foreach (var b in new Button[] { _network, _volumeButton })
        {
            b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var open = Application.Current.Windows.OfType<BarPanel>().FirstOrDefault(p => p.IsVisible);
            Log.Error($"dev panel {b.GetType().Name}: open {open != null}, {open?.PhysicalRect.X},{open?.PhysicalRect.Y} {open?.PhysicalRect.Width}x{open?.PhysicalRect.Height}, bar frame {FrameRect.X},{FrameRect.Y} {FrameRect.Width}x{FrameRect.Height}");
            open?.Hide();
        }
    }

    /// <param name="gap">the gap round the glass (DIP, Settings → Ekran → Odstęp, as round the board)</param>
    /// <param name="ui">the UI scale (Settings → Ekran → Skala)</param>
    /// <param name="scale">the monitor's DPI scale</param>
    /// <param name="span">where the glass stands across (physical x and width: the board's, Settings → Pasek → Szerokość); null: the screen's width less the gap</param>
    public PxRect Dock(PxRect monitor, bool top, double gap, double ui, double scale, (int X, int W)? span = null)
    {
        int P(double dip) => (int)Math.Round(dip * scale);
        int g = P(gap), h = P(_barHeight * ui), head = P(HeadroomNow * ui);
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
            _appBar.FullscreenChanged += full => Dispatcher.BeginInvoke(() => Pause("fullscreen", full));
        }
        if (top != _top) SetEdge(top);
        // the strip: the glass and its gap from the screen's edge (between the glass and the board the board keeps its own gap)
        var r = _appBar.Dock(monitor, top, g + h);
        StripRect = r;
        var (fx, fw) = span is { } sp && sp.W > 0 ? (Math.Max(r.X, sp.X), Math.Min(sp.W, r.Right - Math.Max(r.X, sp.X))) : (r.X + g, r.Width - 2 * g);
        FrameRect = new PxRect(fx, top ? r.Y + g : r.Bottom - g - h, fw, h);
        double sideL = (fx - r.X) / scale, sideR = (r.Right - fx - fw) / scale; // DIP from the window's sides to the glass
        Headroom = HeadroomNow;
        // the glass and the sectors stand in from the window's edges like the board; rounded like it (at most a pill)
        Radius = BarMetrics.Radius(_barHeight * ui);
        Frame.Height = h / scale;
        Frame.Margin = top ? new Thickness(sideL, gap, sideR, 0) : new Thickness(sideL, 0, sideR, gap);
        _sectors.Margin = new Thickness(sideL / ui + 10, top ? gap / ui : 0, sideR / ui + 12, top ? 0 : gap / ui);
        PlacePhysical(r.X, top ? r.Y : r.Y - head, r.Width, r.Height + head, scale);
        Guard.Watch(Hwnd);
        return r;
    }

    /// <summary>Gives the strip back (the bar switched off).</summary>
    public void Undock()
    {
        _appBar?.Remove();
        Guard.Watch(IntPtr.Zero);
    }

    readonly HashSet<string> _pauses = new();

    /// <summary>
    /// A full-screen program (a game, a film, a presentation), the locked screen, sleep: the bar hides and stops its clock and
    /// the load reading; back afterwards with fresh readings. One reason on or off at a time – "fullscreen", "locked", "asleep":
    /// the bar stays hidden while any of them holds (unlocking the screen during a full-screen game must not bring the bar over
    /// the game).
    /// </summary>
    public void Pause(string reason, bool on)
    {
        if (on) _pauses.Add(reason); else _pauses.Remove(reason);
        bool paused = _pauses.Count > 0;
        if (paused == Paused) return;
        Paused = paused;
        _clock.Pause(paused);
        _dock?.Pause(paused);
        _network.Pause(paused);
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

    // ---------- Start ----------

    Native.WinEventProc? _startProc;
    IntPtr _startHook;
    bool _startFront;
    DateTime _startClosedAt;
    readonly Dictionary<uint, bool> _startPids = new();

    /// <summary>The Start menu's own windows (StartMenuExperienceHost; Search when Start opens with it).</summary>
    bool IsStart(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (_startPids.TryGetValue(pid, out var known)) return known;
        bool start = false;
        try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); start = p.ProcessName is "StartMenuExperienceHost" or "SearchHost"; } catch { }
        if (_startPids.Count > 200) _startPids.Clear();
        return _startPids[pid] = start;
    }

    /// <summary>Notes when Start leaves the front: a click on the Start button that closed it (by taking the focus) must not open it again.</summary>
    void WatchStart()
    {
        if (_startHook != IntPtr.Zero) return;
        _startProc = (hook, evt, hwnd, idObject, idChild, thread, time) =>
        {
            if (hwnd == IntPtr.Zero) return;
            bool start = IsStart(hwnd);
            if (_startFront && !start) _startClosedAt = DateTime.Now;
            _startFront = start;
        };
        _startHook = Native.SetWinEventHook(0x0003, 0x0003, IntPtr.Zero, _startProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        Closed += (_, _) => { if (_startHook != IntPtr.Zero) Native.UnhookWinEvent(_startHook); _startHook = IntPtr.Zero; };
    }

    /// <summary>The Start button: opens Start, or – when this very click just closed it – leaves it shut (like the taskbar's button).</summary>
    void ToggleStart()
    {
        if (_startFront || (DateTime.Now - _startClosedAt).TotalMilliseconds < 500) { if (_startFront) Keys.Press(Keys.Escape); return; }
        Keys.Press(Keys.LWin);
    }

    // ---------- the tray's hidden icons ----------

    bool _trayOpen, _trayCut;
    IntPtr _trayWindow;
    DateTime _trayClosedAt;
    DispatcherTimer? _trayTimer;
    Native.WinEventProc? _trayFrontProc;
    IntPtr _trayFrontHook;

    static IntPtr FindOverflow()
    {
        var w = Native.FindWindow("TopLevelWindowForOverflowXamlIsland", null);
        return w != IntPtr.Zero ? w : Native.FindWindow("NotifyIconOverflowWindow", null);
    }

    /// <summary>The chevron's click: opens the hidden icons, or – when they are open, or just closed by this very click – leaves them shut.</summary>
    void ToggleTray()
    {
        if (_trayOpen || (DateTime.Now - _trayClosedAt).TotalMilliseconds < 400) { CloseTray(); return; }
        OpenTrayOverflow();
    }

    /// <summary>The chevron turns over (up ↔ down) when the panel opens and closes, as on the Windows 11 taskbar.</summary>
    void TurnCaret(bool open)
    {
        if (_tray.Content is not FrameworkElement icon) return;
        icon.RenderTransformOrigin = new Point(0.5, 0.5);
        if (icon.RenderTransform is not RotateTransform turn) icon.RenderTransform = turn = new RotateTransform();
        double to = open ? 180 : 0;
        if (Anim.On) turn.BeginAnimation(RotateTransform.AngleProperty, new System.Windows.Media.Animation.DoubleAnimation(to, TimeSpan.FromMilliseconds(200)) { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } });
        else { turn.BeginAnimation(RotateTransform.AngleProperty, null); turn.Angle = to; }
    }

    // the window cut to nothing (an empty region): open or closing, it is never seen anywhere but at the chevron
    void CutTray() { if (_trayCut || _trayWindow == IntPtr.Zero) return; _trayCut = true; Native.SetWindowRgn(_trayWindow, Native.CreateRectRgn(0, 0, 0, 0), false); }
    void UncutTray() { if (!_trayCut || _trayWindow == IntPtr.Zero) return; _trayCut = false; Native.SetWindowRgn(_trayWindow, IntPtr.Zero, true); }

    /// <summary>Where the window stands: right-aligned to the chevron, just past the bar's glass.</summary>
    (int X, int Y) TrayTarget(Native.RECT r)
    {
        var anchor = _tray.PointToScreen(new Point(_tray.ActualWidth, 0)); // physical pixels: the chevron's right edge
        int gap = (int)Math.Round(6 * VisualTreeHelper.GetDpi(this).DpiScaleY);
        int width = r.Right - r.Left, height = r.Bottom - r.Top;
        return (Math.Max(FrameRect.X, (int)Math.Round(anchor.X) - width), _top ? FrameRect.Bottom + gap : FrameRect.Y - height - gap);
    }

    /// <summary>
    /// The hidden icons of the Windows tray: Win+B moves the focus to the tray's chevron, Enter opens it. Windows puts that
    /// window over its own taskbar, so it is cut to nothing until it stands at this bar's chevron, and cut again the moment it
    /// starts to close (another window in front, or it moves away for its closing animation) – no flash at the taskbar's place
    /// either way (Windows 11: TopLevelWindowForOverflowXamlIsland, Windows 10: NotifyIconOverflowWindow).
    /// </summary>
    public void OpenTrayOverflow()
    {
        const uint Move = Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE;
        _trayTimer?.Stop();
        _trayWindow = FindOverflow();
        if (_trayWindow != IntPtr.Zero)
        {
            CutTray();
            if (Native.GetWindowRect(_trayWindow, out var before) && before.Right > before.Left)
            {
                var (x0, y0) = TrayTarget(before);
                Native.SetWindowPos(_trayWindow, IntPtr.Zero, x0, y0, 0, 0, Move);
            }
        }
        Keys.Press(Keys.LWin, Keys.B);
        _trayOpen = true;
        TurnCaret(true);
        int ticks = 0, still = 0;
        bool shown = false;
        _trayTimer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(15) };
        _trayTimer.Tick += (_, _) =>
        {
            ticks++;
            if (ticks == 4) Keys.Press(Keys.Return);
            if (ticks < 4) return;
            if (_trayWindow == IntPtr.Zero) _trayWindow = FindOverflow();
            bool visible = _trayWindow != IntPtr.Zero && Native.IsWindowVisible(_trayWindow) && Native.GetWindowRect(_trayWindow, out var r) && r.Right > r.Left;
            if (!visible)
            {
                // closed (or never opened: no chevron when every icon is on the taskbar)
                if (shown || ticks > 120) { CloseTray(); }
                return;
            }
            Native.GetWindowRect(_trayWindow, out var now);
            var (x, y) = TrayTarget(now);
            bool there = now.Left == x && now.Top == y;
            if (!shown)
            {
                CutTray();
                if (!there) { Native.SetWindowPos(_trayWindow, IntPtr.Zero, x, y, 0, 0, Move); still = 0; }
                // shown only once Windows has stopped placing it (two quiet ticks at the chevron)
                else if (++still >= 2) { UncutTray(); shown = true; WatchTrayFront(true); }
            }
            else if (!there) CutTray(); // moving away: its closing animation starts at the taskbar's place – not seen
        };
        _trayTimer.Start();
    }

    /// <summary>While the panel is open: another window in front means it is closing – cut at once, before its animation shows.</summary>
    void WatchTrayFront(bool on)
    {
        if (on && _trayFrontHook == IntPtr.Zero)
        {
            // (an event that arrives late – the taskbar focused by Win+B before the panel opened – is not a close: the panel is still in front)
            _trayFrontProc = (hook, evt, hwnd, idObject, idChild, thread, time) => { if (_trayOpen && hwnd != _trayWindow && Native.GetForegroundWindow() != _trayWindow) CutTray(); };
            _trayFrontHook = Native.SetWinEventHook(0x0003, 0x0003, IntPtr.Zero, _trayFrontProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        }
        else if (!on && _trayFrontHook != IntPtr.Zero)
        {
            Native.UnhookWinEvent(_trayFrontHook);
            _trayFrontHook = IntPtr.Zero;
        }
    }

    /// <summary>The panel shut (or shutting): no more watching, the chevron back; the window's cut lifted once it is hidden.</summary>
    void CloseTray()
    {
        bool wasOpen = _trayOpen;
        _trayOpen = false;
        WatchTrayFront(false);
        if (wasOpen) { _trayClosedAt = DateTime.Now; TurnCaret(false); }
        if (_trayWindow != IntPtr.Zero && Native.IsWindowVisible(_trayWindow))
        {
            // still on screen: closed by the chevron – cut, then Escape; the cut is lifted when it has hidden
            CutTray();
            Keys.Press(Keys.Escape);
            var wait = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(30) };
            int n = 0;
            wait.Tick += (_, _) => { if (!Native.IsWindowVisible(_trayWindow) || ++n > 40) { wait.Stop(); UncutTray(); } };
            wait.Start();
        }
        else UncutTray();
        _trayTimer?.Stop();
    }
}

/// <summary>Key presses sent to Windows (the Start menu, the tray's chevron).</summary>
static class Keys
{
    public const byte LWin = 0x5B, B = 0x42, S = 0x53, Return = 0x0D, Escape = 0x1B, Alt = 0x12, Space = 0x20;
    const uint KEYEVENTF_KEYUP = 2;

    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    /// <summary>The keys down in order, then up in reverse (Win+B).</summary>
    public static void Press(params byte[] keys)
    {
        foreach (var k in keys) keybd_event(k, 0, 0, UIntPtr.Zero);
        for (int i = keys.Length - 1; i >= 0; i--) keybd_event(keys[i], 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}
