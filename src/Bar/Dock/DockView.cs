using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TaskWall;

/// <summary>Bringing windows up, down and closed – for the dock and its previews.</summary>
static class WindowActions
{
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern void SwitchToThisWindow(IntPtr h, bool altTab);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    const int SW_MINIMIZE = 6, SW_RESTORE = 9;

    /// <summary>Up and in front (restored when minimised).</summary>
    public static void Activate(IntPtr h)
    {
        if (TaskList.IsIconic(h)) Native.ShowWindow(h, SW_RESTORE);
        if (!SetForegroundWindow(h) || Native.GetForegroundWindow() != h) SwitchToThisWindow(h, true); // an elevated window, a focus lock
    }

    public static void Minimize(IntPtr h) => Native.ShowWindow(h, SW_MINIMIZE);

    /// <summary>Asks the window to close (it may ask to save first).</summary>
    public static void Close(IntPtr h) => PostMessage(h, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
}

/// <summary>
/// The dock in the full-width bar's middle (Settings → Pasek → Dock): the pinned programs, a line, the other running ones; a dot
/// under a running one, a longer line under the one in front, a pulse when one asks for attention. The icons grow under the pointer
/// like the Mac's dock – the bell, the push and the spring are <see cref="Magnify"/> / <see cref="Presence"/>, ported 1:1 from
/// Rustyfiles – and frames are drawn only while something moves (CompositionTarget.Rendering is hooked for that time only).
/// A click brings a program's window up (again: down; several windows: their live previews), the middle button starts a new one,
/// the right button pins, starts, closes; a pinned icon is dragged to another place. With a maximised window on the screen the icons
/// are thin (30 px, the centres stay) and grow back when the pointer comes to the bar.
/// </summary>
sealed class DockView : Canvas
{
    /// <summary>Room for the row to widen into on each side while icons grow (Rustyfiles' MIDDLE_RESERVE).</summary>
    public const double Reserve = 40;

    sealed class Entry
    {
        public string Key = "";
        public DockPin? Pin;
        public List<TaskList.Task> Windows = new();
        public readonly Border Visual = new() { Background = Brushes.Transparent, Cursor = Cursors.Hand };
        public readonly Image Img = new() { Stretch = Stretch.Uniform };
        public readonly Border Mark = new() { IsHitTestVisible = false, CornerRadius = new CornerRadius(1.5) };
        public double Shown = double.NaN;   // the centre drawn last (rest coordinates), for the slide after a change of the row
        public double SlideFrom;            // offset from the new rest centre when the slide began
        public long SlideStart;
        public bool Attention;
        public string Name = "";
        /// <summary>The red counter at the icon's corner: the notifications waiting (a dot when the program only asks for attention).</summary>
        public readonly Border Badge = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed, RenderTransformOrigin = new Point(0.5, 0.5) };
        public readonly TextBlock BadgeText = new() { Foreground = Brushes.White, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
        public AppBadges.Count Count;
    }

    readonly BarWindow _bar;
    readonly List<Entry> _entries = new();
    readonly List<Rectangle> _lines = new();
    readonly Rectangle _catcher = new() { IsHitTestVisible = true };
    List<Slot> _slots = new();
    List<(double X, double W)> _rest = new();
    double _restWidth;
    readonly List<string> _runningOrder = new();
    readonly Presence _bell = new(Magnify.FadeMs), _grow = new(Magnify.FadeMs);
    double _pointer = -1e6;
    bool _hooked, _thin, _near, _paused;
    IntPtr _front;
    Entry? _pressed, _dragging;
    Point _pressAt;
    readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly DispatcherTimer _thinCheck = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly DispatcherTimer _hoverPreview = new() { Interval = TimeSpan.FromMilliseconds(700) }; // the name shows at once, the previews when the pointer stays
    readonly DispatcherTimer _hidePreview = new() { Interval = TimeSpan.FromMilliseconds(320) };
    Entry? _hovered;
    readonly WindowPreview _preview = new();
    static readonly Spring Slide = Spring.Smooth(240);

    static long Now => Environment.TickCount64;
    static AppSettings S => App.Settings;
    Strength Strength => S.DockMagnify switch { "off" => Strength.Off, "subtle" => Strength.Subtle, _ => Strength.Full };

    /// <summary>The bar's height (DIP): the icons stand on its middle line and grow away from the screen's edge.</summary>
    public double BarHeight { get; set; } = 56;

    /// <summary>The icons at rest (DIP): the bar's size; the layout is worked out for 44 px and drawn at this.</summary>
    public double IconSize { get; set; } = Magnify.IconRest;

    public DockView(BarWindow bar)
    {
        _bar = bar;
        Background = Brushes.Transparent; // the whole sector follows the pointer
        ClipToBounds = false;
        Children.Add(_catcher);
        _refresh.Tick += (_, _) => { _refresh.Stop(); Refresh(); };
        _thinCheck.Tick += (_, _) => { _thinCheck.Stop(); CheckThin(); };
        _hoverPreview.Tick += (_, _) => { _hoverPreview.Stop(); if (_hovered is { Windows.Count: > 0 } h && _dragging == null) ShowPreview(h); };
        _hidePreview.Tick += (_, _) => { _hidePreview.Stop(); if (!IsMouseOver && !_preview.IsMouseOver) _preview.Dismiss(); };
        _preview.Picked += h => { _preview.Dismiss(); WindowActions.Activate(h); };
        _preview.CloseRequested += h => { WindowActions.Close(h); ScheduleRefresh(); _preview.Dismiss(); };
        _preview.MouseLeave += (_, _) => { _hidePreview.Stop(); _hidePreview.Start(); };
        MouseMove += OnMove;
        // the bell dies away where the pointer was last (the pointer stays where it left), not all at once
        MouseLeave += (_, _) => { _bell.Leave(Now); _hovered = null; _hoverPreview.Stop(); _hidePreview.Stop(); _hidePreview.Start(); Animate(); };
        StartHooks();
        AppBadges.Changed += UpdateBadges;
        AppBadges.Start();
        _badgesOn = true;
        Unloaded += (_, _) => Detach();
        Refresh();
    }

    // ---------- the windows: what runs, what is in front, what asks for attention ----------

    bool _refreshAfterDrop, _badgesOn;

    /// <summary>The dock taken off the bar (switched off, the bar closed): no hooks, no frames, no timers, the previews closed.</summary>
    public void Detach()
    {
        StopHooks();
        Unhook();
        if (_badgesOn) { _badgesOn = false; AppBadges.Changed -= UpdateBadges; AppBadges.Stop(); }
        foreach (var t in new[] { _refresh, _thinCheck, _hoverPreview, _hidePreview }) t.Stop();
        _preview.Dismiss();
        _preview.Close();
    }

    Native.WinEventProc? _proc;
    readonly List<IntPtr> _hooks = new();
    IntPtr _moveHook;
    uint _moveHookPid;

    void StartHooks()
    {
        if (_hooks.Count > 0) return;
        _proc = OnWinEvent;
        // the window in front; minimised / restored; moved or resized by hand – a few events a minute, not a stream
        foreach (var (a, b) in new[] { (0x0003u, 0x0003u), (0x0016u, 0x0017u), (0x000Bu, 0x000Bu) })
            _hooks.Add(Native.SetWinEventHook(a, b, IntPtr.Zero, _proc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | 0x2 /* SKIPOWNPROCESS */));
    }

    void StopHooks()
    {
        foreach (var h in _hooks) Native.UnhookWinEvent(h);
        _hooks.Clear();
        WatchMoves(IntPtr.Zero);
    }

    /// <summary>
    /// Maximising (the button, a double click on the title) raises no event of its own but a location change: listened to only
    /// for the program in front (a system-wide hook would wake TaskWall at every move of the mouse and every blink of a caret).
    /// </summary>
    void WatchMoves(IntPtr front)
    {
        uint pid = 0;
        if (front != IntPtr.Zero && S.DockThin) Native.GetWindowThreadProcessId(front, out pid);
        if (pid == _moveHookPid && (_moveHook != IntPtr.Zero || pid == 0)) return;
        if (_moveHook != IntPtr.Zero) { Native.UnhookWinEvent(_moveHook); _moveHook = IntPtr.Zero; }
        _moveHookPid = pid;
        if (pid != 0 && pid != (uint)Environment.ProcessId)
            _moveHook = Native.SetWinEventHook(0x800B, 0x800B, IntPtr.Zero, _proc!, pid, 0, Native.WINEVENT_OUTOFCONTEXT);
    }

    void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || hwnd == IntPtr.Zero) return; // the window itself, not its caret or scroll bars
        if (evt == 0x0003)
        {
            Activated(hwnd);
            WatchMoves(hwnd);
        }
        if (S.DockThin) { _thinCheck.Stop(); _thinCheck.Start(); }
    }

    /// <summary>A window came in front: the marks follow; the list is read again only for a window the dock doesn't know (a new one, another virtual desktop).</summary>
    void Activated(IntPtr hwnd)
    {
        _front = hwnd;
        bool known = false;
        foreach (var e in _entries)
            if (e.Windows.Any(w => w.Hwnd == hwnd)) { e.Attention = false; known = true; }
        UpdateMarks();
        if (!known) ScheduleRefresh();
    }

    /// <summary>The bar's shell hook (HSHELL_*): windows created, gone, activated, renamed, flashing.</summary>
    public void OnShell(int code, IntPtr hwnd)
    {
        switch (code)
        {
            case 0x8006: // HSHELL_FLASH: asks for attention
                foreach (var e in _entries) if (e.Windows.Any(w => w.Hwnd == hwnd)) e.Attention = true;
                UpdateMarks();
                break;
            case 4 or 0x8004: // activated
                Activated(hwnd);
                break;
            case 1 or 2 or 6: // created, gone, renamed
                ScheduleRefresh();
                break;
        }
    }

    void ScheduleRefresh()
    {
        if (_paused) return;
        _refresh.Stop();
        _refresh.Start();
    }

    /// <summary>The bar hid (full screen, locked): no frames, no reading; back with a fresh look.</summary>
    public void Pause(bool paused)
    {
        _paused = paused;
        if (paused) { _preview.Dismiss(); Unhook(); }
        else { Refresh(); CheckThin(); }
    }

    /// <summary>
    /// A window maximised on the bar's screen (not minimised, not on another virtual desktop): the thin dock. Only the windows
    /// the dock already knows are looked at – no new reading of all windows.
    /// </summary>
    void CheckThin()
    {
        bool thin = false;
        if (S.DockThin && !_paused)
        {
            var strip = _bar.StripRect;
            foreach (var t in _entries.SelectMany(e => e.Windows))
            {
                if (!Native.IsWindowVisible(t.Hwnd) || TaskList.IsIconic(t.Hwnd) || !TaskList.IsZoomed(t.Hwnd) || TaskList.Cloaked(t.Hwnd)) continue;
                Native.GetWindowRect(t.Hwnd, out var r);
                int cx = (r.Left + r.Right) / 2;
                if (cx >= strip.X && cx < strip.Right) { thin = true; break; }
            }
        }
        if (thin == _thin) return;
        _thin = thin;
        if (thin && !_near) _grow.Leave(Now); else _grow.Enter(Now);
        Animate();
    }
    /// <summary>The pointer came to the bar (anywhere on it) or left it: the thin dock grows back, or thin again.</summary>
    public void PointerNear(bool near)
    {
        _near = near;
        if (near || !_thin) _grow.Enter(Now); else if (!_preview.IsVisible) _grow.Leave(Now);
        Animate();
    }

    // ---------- the row ----------

    /// <summary>The pinned programs and the running windows read again; the row rebuilt where it changed (icons slide to their new places).</summary>
    public void Refresh()
    {
        if (_paused) return;
        // mid-drag the row keeps the order being dragged: read again once the icon is dropped
        if (_dragging != null) { _refreshAfterDrop = true; return; }
        List<TaskList.Task> tasks;
        try { tasks = TaskList.Tasks(); }
        catch (Exception ex) { Log.Error("dock tasks", ex); return; }
        var groups = new Dictionary<string, List<TaskList.Task>>(StringComparer.OrdinalIgnoreCase);
        var pinByExe = S.DockPins.Where(p => !string.IsNullOrEmpty(p.Exe)).GroupBy(p => p.Exe!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);
        foreach (var t in tasks)
        {
            // a program pinned by its file groups its windows under the pin even when they carry an app id of their own
            var key = t.Exe != null && pinByExe.TryGetValue(t.Exe, out var pk) ? pk : t.Group;
            if (!groups.TryGetValue(key, out var l)) groups[key] = l = new();
            l.Add(t);
        }
        // the running ones keep the order they came in (a new one at the end), like the taskbar
        _runningOrder.RemoveAll(k => !groups.ContainsKey(k));
        foreach (var k in groups.Keys) if (!_runningOrder.Contains(k, StringComparer.OrdinalIgnoreCase)) _runningOrder.Add(k);
        var pins = S.DockPins.Select(p => p.Key).ToList();
        var (items, slots) = DockRow.Build(pins, _runningOrder.Select(k => (k, groups[k].Count)).ToList());

        var old = _entries.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);
        _entries.Clear();
        foreach (var item in items)
        {
            if (!old.Remove(item.Key, out var e)) e = NewEntry(item.Key);
            e.Pin = item.Pinned ? S.DockPins.FirstOrDefault(p => string.Equals(p.Key, item.Key, StringComparison.OrdinalIgnoreCase)) : null;
            e.Windows = groups.GetValueOrDefault(item.Key) ?? new();
            var first = e.Windows.FirstOrDefault();
            string? exe = first?.Exe ?? e.Pin?.Exe, appId = first?.AppId ?? e.Pin?.AppId;
            e.Name = e.Pin?.Name is { Length: > 0 } pn ? pn : ShellIcons.Name(e.Key, exe, appId, first?.Title ?? e.Key);
            e.Img.Source ??= ShellIcons.Icon(e.Key, exe, appId, first?.Hwnd ?? IntPtr.Zero);
            // the name at once (like the Mac's dock); the live previews of a running one follow when the pointer stays
            e.Visual.ToolTip = new ToolTip { Content = e.Name + "\n" + (e.Windows.Count == 0 ? L.T("Kliknij, aby uruchomić") : L.T("Środkowy przycisk: nowe okno")), Placement = _bar.AtTop ? System.Windows.Controls.Primitives.PlacementMode.Bottom : System.Windows.Controls.Primitives.PlacementMode.Top };
            ToolTipService.SetInitialShowDelay(e.Visual, 0);
            _entries.Add(e);
        }
        foreach (var gone in old.Values) { Children.Remove(gone.Visual); Children.Remove(gone.Mark); Children.Remove(gone.Badge); }
        foreach (var line in _lines) Children.Remove(line);
        _lines.Clear();
        _slots = slots;
        foreach (var s in slots.Where(s => s == Slot.Separator))
        {
            var line = new Rectangle { Width = Magnify.SeparatorWidth, Fill = Ui.Brush(Th.Ink(0x30)), IsHitTestVisible = false };
            _lines.Add(line);
            Children.Add(line);
        }
        Relayout();
        UpdateMarks();
        if (_preview.IsVisible && _hovered != null) ShowPreview(_hovered);
    }

    Entry NewEntry(string key)
    {
        var e = new Entry { Key = key };
        e.Visual.Child = e.Img;
        RenderOptions.SetBitmapScalingMode(e.Img, BitmapScalingMode.HighQuality);
        e.Visual.MouseEnter += (_, _) => { _hovered = e; _hidePreview.Stop(); if (_preview.IsVisible && e.Windows.Count > 0) ShowPreview(e); else { _hoverPreview.Stop(); _hoverPreview.Start(); } };
        e.Visual.MouseLeftButtonDown += (_, ev) => { _pressed = e; _pressAt = ev.GetPosition(this); e.Visual.CaptureMouse(); ev.Handled = true; };
        e.Visual.MouseLeftButtonUp += (_, ev) => { ev.Handled = true; e.Visual.ReleaseMouseCapture(); if (_dragging != null) Drop(); else if (_pressed == e) Click(e); _pressed = null; };
        e.Visual.MouseUp += (_, ev) => { if (ev.ChangedButton == MouseButton.Middle) { ev.Handled = true; Launch(e); } };
        e.Visual.MouseRightButtonUp += (_, ev) => { ev.Handled = true; Menu(e); };
        e.Badge.Child = e.BadgeText;
        e.Badge.Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));
        e.Badge.BorderBrush = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0));
        e.Badge.RenderTransform = new ScaleTransform(1, 1);
        Children.Add(e.Visual);
        Children.Add(e.Mark);
        Children.Add(e.Badge);
        return e;
    }

    /// <summary>The rest row for the current items; every icon whose place changed slides there.</summary>
    void Relayout()
    {
        var sizes = Enumerable.Repeat(Magnify.IconRest, _entries.Count).ToList();
        _restWidth = _slots.Count == 0 ? 0 : Magnify.NaturalWidth(_slots, sizes);
        _rest = Magnify.Fit(_slots, sizes, _restWidth);
        int icon = 0;
        long now = Now;
        for (int i = 0; i < _slots.Count; i++)
        {
            if (_slots[i] != Slot.Icon) continue;
            var e = _entries[icon++];
            double centre = _rest[i].X + _rest[i].W / 2;
            if (!double.IsNaN(e.Shown) && Math.Abs(e.Shown - centre) > 0.5 && Anim.On) { e.SlideFrom = e.Shown - centre; e.SlideStart = now; }
            else if (double.IsNaN(e.Shown)) e.SlideFrom = 0;
        }
        InvalidateMeasure();
        Animate(force: true);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        base.MeasureOverride(constraint);
        return new Size(_entries.Count == 0 ? 0 : IconSize / Magnify.IconRest * (_restWidth + 2 * Reserve), BarHeight);
    }

    // ---------- drawing ----------

    /// <summary>Frames while something moves; unhooked when all is still.</summary>
    void Animate(bool force = false)
    {
        if (_paused) return;
        if (!_hooked) { _hooked = true; CompositionTarget.Rendering += OnFrame; }
        if (force) Draw(Now);
    }

    void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        long now = Now;
        if (Draw(now)) Unhook();
    }

    /// <summary>Puts everything where it is at <paramref name="now"/>; true when nothing moves any more.</summary>
    bool Draw(long now)
    {
        double ms = Anim.On ? Magnify.FadeMs : 0;
        _bell.SetDuration(ms);
        _grow.SetDuration(ms);
        double grow = S.DockThin && _thin ? _grow.Value(now) : 1;
        double thinScale = Magnify.ThinIcon / Magnify.IconRest + (1 - Magnify.ThinIcon / Magnify.IconRest) * grow;
        double presence = _bell.Value(now) * grow;
        var f = _slots.Count == 0 ? new Magnify.Frame(new(), 0, 0) : Magnify.Layout(_slots, _rest, _restWidth, _pointer, presence, Strength, _restWidth + 2 * Reserve);
        bool still = _bell.Settled(now) && (!(S.DockThin && _thin) || _grow.Settled(now)) || !Anim.On;
        bool top = _bar.AtTop;
        double mid = BarHeight / 2;
        double k = IconSize / Magnify.IconRest; // the arithmetic is in 44 px icons (as in Rustyfiles); drawn at the bar's size
        double X(double v) => k * (Reserve + v);
        int icon = 0, line = 0;
        for (int i = 0; i < _slots.Count && i < f.Spans.Count; i++)
        {
            var (x, w) = f.Spans[i];
            if (_slots[i] == Slot.Separator)
            {
                var l = _lines[line++];
                l.Height = 24 * k * thinScale;
                SetLeft(l, X(x));
                SetTop(l, mid - l.Height / 2);
                continue;
            }
            var e = _entries[icon++];
            double restCentre = _rest[i].X + _rest[i].W / 2;
            // a slide after the row changed: the offset dies away on a spring (the dragged icon follows the pointer instead)
            double offset = 0;
            if (e == _dragging) offset = _pointer - (x + w / 2);
            else if (e.SlideFrom != 0)
            {
                long t = now - e.SlideStart;
                offset = Slide.At(e.SlideFrom, 0, t).E;
                if (Math.Abs(offset) < 0.3 && t > 50) { offset = 0; e.SlideFrom = 0; } else still = false;
            }
            double centre = x + w / 2 + offset;
            e.Shown = restCentre + offset;
            double baseSize = IconSize * thinScale, size = k * w * thinScale;
            // the icons stand on a line half an icon from the bar's middle and grow away from the screen's edge (into the board's side)
            double edge = top ? mid - baseSize / 2 : mid + baseSize / 2;
            e.Visual.Width = e.Visual.Height = size;
            SetLeft(e.Visual, X(centre) - size / 2);
            SetTop(e.Visual, top ? edge : edge - size);
            bool front = e.Windows.Any(t => t.Hwnd == _front);
            // the mark: in the middle of the room between the icon and the glass's edge (the screen's side) – a dot, a line for the one in front
            double room = Math.Max(4, (BarHeight - IconSize) / 2);
            e.Mark.Width = e.Windows.Count == 0 ? 0 : front ? 14 : 4;
            e.Mark.Height = e.Windows.Count == 0 ? 0 : front ? 3 : 4;
            e.Mark.CornerRadius = new CornerRadius(2);
            SetLeft(e.Mark, X(centre) - e.Mark.Width / 2);
            SetTop(e.Mark, top ? room / 2 - e.Mark.Height / 2 : BarHeight - room / 2 - e.Mark.Height / 2);
            // the counter: on the icon's upper corner (away from the screen's edge), growing with the icon
            if (e.Count.Any)
            {
                double h = Math.Max(13, Math.Round(size * (e.Count.N > 0 ? 0.46 : 0.28)));
                string text = e.Count.N > 99 ? "99+" : e.Count.N > 0 ? e.Count.N.ToString() : "";
                double bw = text.Length <= 1 ? h : h * (text.Length == 2 ? 1.4 : 1.8);
                e.Badge.Width = bw;
                e.Badge.Height = h;
                e.Badge.CornerRadius = new CornerRadius(h / 2);
                e.Badge.BorderThickness = new Thickness(Math.Max(1, h / 14));
                e.BadgeText.FontSize = h * 0.66;
                e.BadgeText.Margin = new Thickness(0, -h * 0.06, 0, 0);
                double iconTop = top ? edge + size : edge - size; // the far side from the screen's edge
                double inset = size * 0.08;
                SetLeft(e.Badge, X(centre) + size / 2 - inset - bw / 2);
                SetTop(e.Badge, top ? iconTop - inset - h / 2 : iconTop + inset - h / 2);
            }
        }
        // while the icons are grown, the room above them catches the pointer (otherwise it passes through to the window under it)
        double head = _bar.Headroom;
        _catcher.Fill = presence > 0.01 ? new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) : null;
        _catcher.Width = Math.Max(0, k * f.Width);
        _catcher.Height = head;
        SetLeft(_catcher, X(f.Left));
        SetTop(_catcher, top ? BarHeight : -head);
        return still;
    }

    void UpdateMarks()
    {
        foreach (var e in _entries)
        {
            bool front = e.Windows.Any(t => t.Hwnd == _front);
            e.Mark.Background = e.Attention ? Ui.Res("AccentBrush") : front ? Ui.Res("AccentBrush") : Ui.Brush(Th.Ink(0x90));
            if (e.Attention && Anim.On)
                e.Mark.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(600)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            else e.Mark.BeginAnimation(OpacityProperty, null);
        }
        UpdateBadges();
    }

    /// <summary>The counters read again (the notification database or a title changed, a program asked for attention).</summary>
    void UpdateBadges()
    {
        foreach (var e in _entries)
        {
            var c = !S.DockBadges ? default
                : AppBadges.For(e.Windows.Select(w => w.AppId).Concat(e.Windows.Select(w => AppBadges.PackageAppId(w.Pid))).Append(e.Pin?.AppId),
                                e.Windows.Select(w => w.Exe).Append(e.Pin?.Exe), e.Windows.Select(w => w.Title));
            // a program that flashes for attention without a number: a dot
            if (S.DockBadges && !c.Any && e.Attention) c = new AppBadges.Count(0, true);
            bool grew = c.N > e.Count.N || c.Dot && !e.Count.Any;
            e.Count = c;
            e.Badge.Visibility = c.Any ? Visibility.Visible : Visibility.Collapsed;
            e.BadgeText.Text = c.N > 99 ? "99+" : c.N > 0 ? c.N.ToString() : "";
            System.Windows.Automation.AutomationProperties.SetName(e.Visual, c.N > 0 ? L.F("{0} – powiadomienia: {1}", e.Name, c.N) : e.Name);
            // a new one: the counter pops (a short spring of its size)
            if (grew && Anim.On && e.Badge.RenderTransform is ScaleTransform st)
            {
                var pop = new DoubleAnimation(1.35, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 4, EasingMode = EasingMode.EaseOut } };
                st.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            }
        }
        Animate(force: true);
    }

    // ---------- the pointer ----------

    void OnMove(object sender, MouseEventArgs ev)
    {
        var p = ev.GetPosition(this);
        _pointer = p.X / (IconSize / Magnify.IconRest) - Reserve;
        if (_pressed != null && _dragging == null && _pressed.Pin != null && Math.Abs(p.X - _pressAt.X) > 6)
        {
            _dragging = _pressed;
            _preview.Dismiss();
            _hoverPreview.Stop();
        }
        if (_dragging != null) DragTo(_pointer);
        if (Strength != Strength.Off) _bell.Enter(Now);
        Animate();
    }

    void DragTo(double x)
    {
        // the dragged icon's new place among the pinned: past the centres of the others
        var pinned = _entries.Where(e => e.Pin != null).ToList();
        var others = pinned.Where(e => e != _dragging).ToList();
        int at = DockRow.DropIndex(others.Select(e => e.Shown).ToList(), x);
        int now = pinned.IndexOf(_dragging!);
        if (at == now) return;
        _entries.Remove(_dragging!);
        _entries.Insert(at, _dragging!);
        Relayout();
    }

    void Drop()
    {
        var order = _entries.Where(e => e.Pin != null).Select(e => e.Pin!).ToList();
        _dragging = null;
        if (!order.SequenceEqual(S.DockPins))
        {
            S.DockPins = order;
            SettingsStore.Save(S);
        }
        Relayout();
        if (_refreshAfterDrop) { _refreshAfterDrop = false; Refresh(); }
    }

    // ---------- clicks ----------

    void Click(Entry e)
    {
        switch (e.Windows.Count)
        {
            case 0: Launch(e); break;
            case 1:
                var w = e.Windows[0].Hwnd;
                // the one in front again: down (the taskbar's toggle); otherwise up
                if (w == _front && !TaskList.IsIconic(w)) WindowActions.Minimize(w); else WindowActions.Activate(w);
                _preview.Dismiss();
                break;
            default: ShowPreview(e); break;
        }
    }

    void Launch(Entry e)
    {
        var first = e.Windows.FirstOrDefault();
        ShellIcons.Launch(e.Pin?.Exe ?? first?.Exe, e.Pin?.Args, e.Pin?.AppId ?? first?.AppId);
    }

    void ShowPreview(Entry e)
    {
        if (e.Windows.Count == 0 || !e.Visual.IsVisible) return;
        _hovered = e;
        if (e.Visual.ToolTip is ToolTip tip) tip.IsOpen = false; // the previews take the name's place
        var c = e.Visual.PointToScreen(new Point(e.Visual.ActualWidth / 2, 0));
        _preview.Present(e.Windows, c.X, _bar.FrameRect, _bar.AtTop, VisualTreeHelper.GetDpi(_bar).DpiScaleX);
    }

    void Menu(Entry e)
    {
        _preview.Dismiss();
        var m = new ContextMenu { PlacementTarget = e.Visual, Placement = _bar.AtTop ? System.Windows.Controls.Primitives.PlacementMode.Bottom : System.Windows.Controls.Primitives.PlacementMode.Top };
        MenuItem Item(string text, Action a) { var i = new MenuItem { Header = text }; i.Click += (_, _) => a(); m.Items.Add(i); return i; }
        var header = new MenuItem { Header = e.Name, IsEnabled = false, FontWeight = FontWeights.SemiBold };
        m.Items.Add(header);
        m.Items.Add(new Separator());
        Item(L.T("Nowe okno"), () => Launch(e));
        if (e.Pin != null) Item(L.T("Odepnij od docka"), () => Unpin(e));
        else Item(L.T("Przypnij do docka"), () => Pin(e));
        if (e.Windows.Count > 0)
        {
            m.Items.Add(new Separator());
            Item(e.Windows.Count == 1 ? L.T("Zamknij okno") : L.F("Zamknij wszystkie okna ({0})", e.Windows.Count), () => { foreach (var w in e.Windows) WindowActions.Close(w.Hwnd); ScheduleRefresh(); });
        }
        m.IsOpen = true;
    }

    void Pin(Entry e)
    {
        var first = e.Windows.FirstOrDefault();
        if (first == null) return;
        S.DockPins.Add(new DockPin { Key = e.Key, Exe = first.Exe, AppId = first.AppId, Name = e.Name });
        SettingsStore.Save(S);
        Refresh();
    }

    /// <summary>Dev runs (--open dock-hover): the pointer as if over the third icon, for a screenshot of the magnification.</summary>
    public void DevHover()
    {
        int i = Math.Min(2, _rest.Count - 1);
        if (i < 0) return;
        _pointer = _rest[i].X + _rest[i].W / 2;
        _bell.Enter(Now);
        Animate();
    }

    /// <summary>Dev check (no screenshot – it would show the windows' content): the preview of the first running icon, and how many live pictures it got.</summary>
    public void DevPreview()
    {
        var e = _entries.FirstOrDefault(x => x.Windows.Count > 0);
        if (e == null) { Log.Error("dev dock preview: nothing runs"); return; }
        ShowPreview(e);
        Log.Error($"dev dock preview: windows {e.Windows.Count}, live pictures {_preview.ThumbCount}, shown {_preview.IsVisible}, at {_preview.PhysicalRect.X},{_preview.PhysicalRect.Y} {_preview.PhysicalRect.Width}x{_preview.PhysicalRect.Height}");
        _preview.Dismiss();
    }

    void Unpin(Entry e)
    {
        S.DockPins.RemoveAll(p => string.Equals(p.Key, e.Key, StringComparison.OrdinalIgnoreCase));
        SettingsStore.Save(S);
        Refresh();
    }
}
