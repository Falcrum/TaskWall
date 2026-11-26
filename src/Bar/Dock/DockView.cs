using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TaskWall;

/// <summary>Bringing windows up, down and closed – for the dock and its previews.</summary>
static class WindowActions
{
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern void SwitchToThisWindow(IntPtr h, bool altTab);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr parent);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    const int SW_MINIMIZE = 6, SW_RESTORE = 9;

    /// <summary>
    /// Up and in front (restored when minimised). Windows refuses the foreground to a program that didn't get the last input
    /// (the focus lock) – the click on the bar usually counts, but not always: then a released Alt (which lifts the lock, the
    /// trick Alt+Tab itself relies on), and at last the input of the window in front borrowed for a moment.
    /// </summary>
    public static void Activate(IntPtr h)
    {
        if (TaskList.IsIconic(h)) Native.ShowWindow(h, SW_RESTORE);
        if (SetForegroundWindow(h) && Native.GetForegroundWindow() == h) return;
        keybd_event(0x12 /* VK_MENU */, 0, 0x2 /* KEYUP */, UIntPtr.Zero);
        if (SetForegroundWindow(h) && Native.GetForegroundWindow() == h) return;
        var fg = Native.GetForegroundWindow();
        uint fgThread = Native.GetWindowThreadProcessId(fg, out _), me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        try { BringWindowToTop(h); SetForegroundWindow(h); }
        finally { if (attached) AttachThreadInput(me, fgThread, false); }
        if (Native.GetForegroundWindow() != h) SwitchToThisWindow(h, true); // an elevated window
    }

    /// <summary>Of these windows, the one highest in the Z order (the one used last).</summary>
    public static IntPtr Topmost(IReadOnlyCollection<IntPtr> windows)
    {
        var set = windows.ToHashSet();
        for (var w = GetTopWindow(IntPtr.Zero); w != IntPtr.Zero; w = GetWindow(w, 2 /* GW_HWNDNEXT */))
            if (set.Contains(w)) return w;
        return windows.First();
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
/// are thin (30 px, the centres stay) and grow back when the pointer comes to the bar. A pinned program that isn't running is grey
/// (a setting); started from the dock it hops and its colour breathes in until its first window comes. Under a running one a dot
/// for each window (up to three); a new running icon grows in, a closed one shrinks away; one that doesn't respond gets a red ring.
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
        /// <summary>The icon's greyscale copy under the colour one (pinned programs not running, with the setting on).</summary>
        public readonly Image Gray = new() { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
        /// <summary>The colour icon's opacity over the grey one: 0 grey, 1 colour; it breathes while the program starts.</summary>
        public double Tint = 1;
        /// <summary>The marks under a running icon: a dot for each window (up to three), one longer line for the one in front.</summary>
        public readonly StackPanel Mark = new() { IsHitTestVisible = false, Orientation = Orientation.Horizontal };
        public readonly Border[] Dots = { new(), new(), new() };
        /// <summary>Started from the dock: the hops began (0: not starting); the hop under way lands at LaunchStop once a window came.</summary>
        public long LaunchStart, LaunchStop;
        /// <summary>The windows when it was started (its own, and all the program's by file / app id): one more ends the hops.</summary>
        public int LaunchOwn, LaunchMatch;
        public string? LaunchExe, LaunchAppId;
        /// <summary>A new running icon growing in / a gone one shrinking away (0: neither).</summary>
        public long AppearStart, LeaveStart;
        /// <summary>Where the icon was drawn last (canvas): its centre, the line it stands on, its size – where it shrinks away.</summary>
        public double DrawX, DrawEdge, DrawSize;
        /// <summary>A window of it doesn't respond (IsHungAppWindow): a red ring pulses around the icon.</summary>
        public bool Hung;
        public readonly Border HungRing = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed, BorderBrush = HungBrush };
        public string Hint = "";
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
    /// <summary>Icons gone from the row, shrinking away before they leave the canvas.</summary>
    readonly List<Entry> _leaving = new();
    /// <summary>The first reading is done: icons that come later grow in (the first ones are simply there).</summary>
    bool _built;
    long _lastDraw;
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
    /// <summary>The window in front when the icon was pressed: the click's toggle compares with it (the dock's own idea of the front
    /// misses TaskWall's windows – a click on the board left Firefox "in front" and the next click minimised it instead).</summary>
    IntPtr _fgAtPress;
    /// <summary>The window in front before the bar itself took a click (a click on the bar activates it first).</summary>
    IntPtr _realFront = Native.GetForegroundWindow();
    /// <summary>The magnifier at the dock's start (the search's field opens over it); it grows with the icons.</summary>
    public const string SearchKey = "::search";
    /// <summary>The stack the dragged icon is over: dropped there, the program goes into it.</summary>
    Entry? _dropTarget;
    Entry? _pressed, _dragging;
    Point _pressAt;
    readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly DispatcherTimer _thinCheck = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly DispatcherTimer _hoverPreview = new() { Interval = TimeSpan.FromMilliseconds(700) }; // the name shows at once, the previews when the pointer stays
    readonly DispatcherTimer _hidePreview = new() { Interval = TimeSpan.FromMilliseconds(320) };
    /// <summary>Every 2 s: does any of the windows not respond (only while the dock shows; stopped when paused or detached).</summary>
    readonly DispatcherTimer _hungCheck = new() { Interval = TimeSpan.FromSeconds(2) };
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
        _hungCheck.Tick += (_, _) => CheckHung();
        _hungCheck.Start();
        _hoverPreview.Tick += (_, _) => { _hoverPreview.Stop(); if (_hovered is { Windows.Count: > 0 } h && _dragging == null) ShowPreview(h); };
        _hidePreview.Tick += (_, _) => { _hidePreview.Stop(); if (!IsMouseOver && !_preview.IsMouseOver) _preview.Dismiss(); };
        _preview.Picked += h => { _preview.Dismiss(); WindowActions.Activate(h); };
        _preview.PickedAll += () =>
        {
            var shown = _preview.Shown.Select(t => t.Hwnd).ToList();
            _preview.Dismiss();
            if (shown.Count == 0) return;
            var top = WindowActions.Topmost(shown);
            foreach (var w in shown.Where(w => w != top)) { if (TaskList.IsIconic(w)) Native.ShowWindow(w, 9 /* SW_RESTORE */); WindowActions.Activate(w); }
            WindowActions.Activate(top); // the one used last ends on top
        };
        _preview.CloseRequested += h => { WindowActions.Close(h); ScheduleRefresh(); _preview.Dismiss(); };
        _preview.MouseLeave += (_, _) => { _hidePreview.Stop(); _hidePreview.Start(); };
        MouseMove += OnMove;
        // the bell dies away where the pointer was last (the pointer stays where it left), not all at once
        MouseLeave += (_, _) => { _bell.Leave(Now); _hovered = null; _hoverPreview.Stop(); _hidePreview.Stop(); _hidePreview.Start(); Animate(); };
        StartHooks();
        SyncBadges();
        // no Detach on Unloaded: WPF unloads an element that only moves in the tree (and loads it again) – that closed the
        // previews and unhooked the windows for good; the bar detaches the dock itself (switched off, the bar closed)
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
        foreach (var t in new[] { _refresh, _thinCheck, _hoverPreview, _hidePreview, _hungCheck }) t.Stop();
        FlushLeaving();
        _preview.Dismiss();
        _preview.Close();
    }

    /// <summary>The counters' reading on or off with the setting (off: the notification database is not opened or watched at all).</summary>
    void SyncBadges()
    {
        if (S.DockBadges == _badgesOn) return;
        _badgesOn = S.DockBadges;
        if (_badgesOn) { AppBadges.Changed += UpdateBadges; AppBadges.Start(); }
        else { AppBadges.Changed -= UpdateBadges; AppBadges.Stop(); }
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
        // the window in front: TaskWall's own too (the board counts as "in front", the bar itself doesn't – see _realFront)
        _hooks.Add(Native.SetWinEventHook(0x0003, 0x0003, IntPtr.Zero, _proc, 0, 0, Native.WINEVENT_OUTOFCONTEXT));
        foreach (var (a, b) in new[] { (0x0016u, 0x0017u), (0x000Bu, 0x000Bu) })
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
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == (uint)Environment.ProcessId)
            {
                if (hwnd != _bar.Hwnd) _realFront = hwnd; // the board, a dialog: in front; the bar taking a click: not
                return;
            }
            _realFront = hwnd;
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
        bool seen = false;
        foreach (var e in _entries)
            if (e.Windows.Any(w => w.Hwnd == hwnd))
            {
                e.Attention = false;
                known = true;
                if (S.DockBadges) seen |= AppBadges.MarkSeen(AppIdsOf(e), ExesOf(e));
            }
        if (seen) SettingsStore.Save(S);
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
        if (paused) { _preview.Dismiss(); Unhook(); _hungCheck.Stop(); FlushLeaving(); }
        else { Refresh(); CheckThin(); _hungCheck.Start(); }
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
        SyncBadges();
        List<TaskList.Task> tasks;
        try { tasks = TaskList.Tasks(); }
        catch (Exception ex) { Log.Error("dock tasks", ex); return; }
        AppBadges.KeepPackages(tasks.Select(t => t.Pid));
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
        var pins = S.DockPins.Select(p => p.Key).Prepend(SearchKey).ToList();
        var (items, slots) = DockRow.Build(pins, _runningOrder.Select(k => (k, groups[k].Count)).ToList());

        var old = _entries.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);
        _entries.Clear();
        foreach (var item in items)
        {
            bool fresh = !old.Remove(item.Key, out var e);
            if (fresh)
            {
                e = NewEntry(item.Key);
                // a program started later (not pinned): its icon grows in while the others slide aside
                if (_built && !item.Pinned && Anim.On) e!.AppearStart = Now;
            }
            e!.Pin = item.Pinned ? S.DockPins.FirstOrDefault(p => string.Equals(p.Key, item.Key, StringComparison.OrdinalIgnoreCase)) : null;
            e.Windows = groups.GetValueOrDefault(item.Key) ?? new();
            if (fresh) e.Tint = e.Windows.Count > 0 ? 1 : 0; // grey or colour from the start, no fade
            var first = e.Windows.FirstOrDefault();
            string? exe = first?.Exe ?? e.Pin?.Exe, appId = first?.AppId ?? e.Pin?.AppId;
            string hint;
            if (e.Key == SearchKey)
            {
                e.Name = L.T("Szukaj");
                e.Img.Source ??= StackIsland.SearchIcon();
                hint = L.T("Programy, kalkulator, internet, pliki");
            }
            else
            {
                e.Name = e.Pin?.Name is { Length: > 0 } pn ? pn : ShellIcons.Name(e.Key, exe, appId, first?.Title ?? e.Key);
                if (e.Pin?.Items != null) e.Img.Source = StackIsland.Icon(e.Pin); // the stack's first programs in its tile (redrawn: they change)
                else e.Img.Source ??= ShellIcons.Icon(e.Key, exe, appId, first?.Hwnd ?? IntPtr.Zero);
                hint = e.Pin?.Items != null ? L.F("Stos: {0} – kliknij, aby otworzyć", e.Pin.Items.Count)
                    : e.Windows.Count == 0 ? L.T("Kliknij, aby uruchomić; przeciągnij na stos, aby go tam dodać")
                    : L.T("Kliknij: na wierzch / zminimalizuj · środkowy przycisk: nowe okno · przeciągnij na stos");
            }
            if (e.Key != SearchKey && e.Pin?.Items == null) hint += "\n" + L.T("Shift+klik: nowe okno · Ctrl+Shift+klik: jako administrator");
            e.Hint = hint;
            SetTip(e);
            UpdateGray(e);
            _entries.Add(e);
        }
        // started from the dock and a window came: the hop under way lands, the colour settles
        foreach (var e in _entries)
            if (e.LaunchStart != 0 && (e.Windows.Count > e.LaunchOwn || Matching(e.LaunchExe, e.LaunchAppId) > e.LaunchMatch)) EndLaunch(e, Now);
        foreach (var gone in old.Values)
        {
            Children.Remove(gone.Mark);
            Children.Remove(gone.Badge);
            if (gone.Visual.ToolTip is ToolTip gt) gt.IsOpen = false;
            if (_hovered == gone) _hovered = null;
            // the last window closed: the icon shrinks and fades away where it stood (the others slide together meanwhile)
            if (_built && Anim.On && gone.DrawSize > 0)
            {
                gone.LeaveStart = Now;
                gone.AppearStart = 0;
                gone.Visual.IsHitTestVisible = false;
                _leaving.Add(gone);
            }
            else Children.Remove(gone.Visual);
        }
        foreach (var line in _lines) Children.Remove(line);
        _lines.Clear();
        _slots = slots;
        foreach (var s in slots.Where(s => s == Slot.Separator))
        {
            var line = new Rectangle { Width = Magnify.SeparatorWidth, Fill = Ui.Brush(Th.Ink(0x30)), IsHitTestVisible = false };
            _lines.Add(line);
            Children.Add(line);
        }
        _built = true;
        Relayout();
        UpdateMarks();
        if (_preview.IsVisible && _hovered != null) ShowPreview(_hovered);
    }

    /// <summary>The name at once (like the Mac's dock), what a click does, and when it hangs, that it doesn't respond.</summary>
    void SetTip(Entry e)
    {
        // the live previews of a running one follow when the pointer stays. The same tip object stays: a new one at every
        // refresh (a browser's title changes all the time) closed the open tip at once
        var tipText = e.Name + "\n" + e.Hint + (e.Hung ? "\n" + L.T("Nie odpowiada – prawy przycisk: Zakończ zadanie") : "");
        if (e.Visual.ToolTip is not ToolTip tt)
        {
            tt = new ToolTip();
            e.Visual.ToolTip = tt;
            ToolTipService.SetInitialShowDelay(e.Visual, 0);
            ToolTipService.SetShowDuration(e.Visual, 60000);
        }
        if (!Equals(tt.Content, tipText)) tt.Content = tipText;
        tt.Placement = _bar.AtTop ? System.Windows.Controls.Primitives.PlacementMode.Bottom : System.Windows.Controls.Primitives.PlacementMode.Top;
    }

    // ---------- grey, starting, not responding ----------

    /// <summary>The greyscale copies, by the icon they were made from (the shell's icons are cached by key, so the same object comes again).</summary>
    static readonly Dictionary<string, (ImageSource From, ImageSource? Gray)> Grays = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A pinned program (not a stack, not the magnifier) gets its grey copy while the setting is on; it shows while the program isn't running.</summary>
    void UpdateGray(Entry e)
    {
        ImageSource? gray = null;
        if (S.DockGrayIdle && e.Pin != null && e.Pin.Items == null && e.Key != SearchKey && e.Img.Source is { } src)
        {
            if (Grays.TryGetValue(e.Key, out var known) && ReferenceEquals(known.From, src)) gray = known.Gray;
            else
            {
                try { gray = Grayscale(src); }
                catch (Exception ex) { Log.Error("dock gray " + e.Name, ex); }
                Grays[e.Key] = (src, gray);
            }
        }
        e.Gray.Source = gray;
        if (gray == null) { e.Tint = 1; e.Img.Opacity = 1; e.Gray.Visibility = Visibility.Collapsed; }
    }

    /// <summary>The icon's luminance in each pixel, its transparency kept (null for an icon that isn't a bitmap).</summary>
    static ImageSource? Grayscale(ImageSource src)
    {
        if (src is not BitmapSource b) return null;
        var c = new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
        int w = c.PixelWidth, h = c.PixelHeight, stride = w * 4;
        if (w == 0 || h == 0) return null;
        var px = new byte[stride * h];
        c.CopyPixels(px, stride, 0);
        for (int i = 0; i < px.Length; i += 4)
        {
            // Rec. 709 luminance (Bgra32 is not premultiplied: the alpha stays as it is)
            var y = (byte)Math.Round(0.0722 * px[i] + 0.7152 * px[i + 1] + 0.2126 * px[i + 2]);
            px[i] = px[i + 1] = px[i + 2] = y;
        }
        var g = BitmapSource.Create(w, h, b.DpiX > 0 ? b.DpiX : 96, b.DpiY > 0 ? b.DpiY : 96, PixelFormats.Bgra32, null, px, stride);
        g.Freeze();
        return g;
    }

    /// <summary>The windows of all icons that belong to this program (by its file or app id).</summary>
    int Matching(string? exe, string? appId) => _entries.SelectMany(x => x.Windows).Count(w =>
        !string.IsNullOrEmpty(exe) && string.Equals(w.Exe, exe, StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(appId) && string.Equals(w.AppId, appId, StringComparison.OrdinalIgnoreCase));

    const double HopMs = 620, LaunchMaxMs = 8000, AppearMs = 240, LeaveMs = 200;

    /// <summary>Started from the dock: the icon hops (away from the screen's edge) and its colour breathes in until one more of its windows comes (or 8 s).</summary>
    void StartLaunch(Entry e, string? exe, string? appId)
    {
        if (e.Key == SearchKey) return;
        e.LaunchExe = exe;
        e.LaunchAppId = appId;
        e.LaunchOwn = e.Windows.Count;
        e.LaunchMatch = Matching(exe, appId);
        if (!Anim.On) return; // no hops: the colour simply comes with the window
        if (e.LaunchStart == 0 || e.LaunchStop != 0) e.LaunchStart = Now;
        e.LaunchStop = 0;
        Animate();
    }

    /// <summary>The start is over: the hop under way lands first, then the icon rests.</summary>
    static void EndLaunch(Entry e, long now)
    {
        if (e.LaunchStart == 0 || e.LaunchStop != 0) return;
        double t = now - e.LaunchStart;
        e.LaunchStop = e.LaunchStart + (long)(Math.Max(1, Math.Ceiling(t / HopMs)) * HopMs);
    }

    /// <summary>The hop's height now (in 44 px icons): a ball's arc, touching the line between hops.</summary>
    static double Hop(Entry e, long now, ref bool still)
    {
        if (e.LaunchStart == 0) return 0;
        if (!Anim.On) { e.LaunchStart = e.LaunchStop = 0; return 0; } // switched off meanwhile: at rest at once
        double t = now - e.LaunchStart;
        if (t > LaunchMaxMs) EndLaunch(e, now);
        if (e.LaunchStop != 0 && now >= e.LaunchStop) { e.LaunchStart = e.LaunchStop = 0; return 0; }
        still = false;
        return Math.Sin(Math.PI * (t % HopMs / HopMs));
    }

    /// <summary>The colour copy's opacity: while starting it comes in and breathes (half to full), otherwise it fades to grey or colour.</summary>
    static void Shade(Entry e, long now, double dt, ref bool still)
    {
        if (e.Gray.Source == null) return;
        double target = e.Windows.Count > 0 ? 1 : 0, c;
        if (e.LaunchStart != 0 && e.LaunchStop == 0)
        {
            double t = now - e.LaunchStart;
            c = t < 400 ? Math.Max(e.Tint, t / 400) : 0.75 + 0.25 * Math.Cos(2 * Math.PI * (t - 400) / 900);
            still = false;
        }
        else if (!Anim.On || Math.Abs(target - e.Tint) <= dt / 450) c = target;
        else { c = e.Tint + Math.Sign(target - e.Tint) * dt / 450; still = false; }
        e.Tint = c;
        e.Img.Opacity = c;
        // fully in colour: the grey copy away (its half-transparent edges would show under the colour ones)
        e.Gray.Visibility = c >= 0.999 ? Visibility.Collapsed : Visibility.Visible;
    }

    [DllImport("user32.dll")] static extern bool IsHungAppWindow(IntPtr h);

    /// <summary>A program whose window doesn't respond: a red ring pulses around its icon, the tip says how to end it. Gone when it responds again.</summary>
    void CheckHung()
    {
        if (_paused) return;
        bool changed = false;
        foreach (var e in _entries)
        {
            bool hung = e.Windows.Any(w => IsHungAppWindow(w.Hwnd));
            if (hung == e.Hung) continue;
            changed = true;
            e.Hung = hung;
            e.HungRing.Visibility = hung ? Visibility.Visible : Visibility.Collapsed;
            if (hung && Anim.On)
                e.HungRing.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            else e.HungRing.BeginAnimation(OpacityProperty, null);
            SetTip(e);
        }
        if (changed) Animate(force: true);
    }

    /// <summary>The shrinking icons taken away at once (paused, detached).</summary>
    void FlushLeaving()
    {
        foreach (var e in _leaving) Children.Remove(e.Visual);
        _leaving.Clear();
    }

    static readonly SolidColorBrush HungBrush = Frozen(Color.FromRgb(0xE5, 0x48, 0x48));

    Entry NewEntry(string key)
    {
        var e = new Entry { Key = key };
        // the grey copy under the colour icon, the ring of a program that doesn't respond over both
        e.HungRing.Effect = new DropShadowEffect { Color = HungBrush.Color, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.9 };
        e.Visual.Child = new Grid { Children = { e.Gray, e.Img, e.HungRing } };
        RenderOptions.SetBitmapScalingMode(e.Img, BitmapScalingMode.HighQuality);
        RenderOptions.SetBitmapScalingMode(e.Gray, BitmapScalingMode.HighQuality);
        foreach (var d in e.Dots) { d.CornerRadius = new CornerRadius(2); e.Mark.Children.Add(d); }
        e.Visual.MouseEnter += (_, _) => { _hovered = e; _hidePreview.Stop(); if (_preview.IsVisible && e.Windows.Count > 0) ShowPreview(e); else { _hoverPreview.Stop(); _hoverPreview.Start(); } };
        // a stack opens its island when the pointer rests on it (Ustawienia → Dock), and closes when the pointer leaves it and the island
        e.Visual.MouseEnter += (_, _) => { if (e.Pin?.Items != null) StackIsland.HoverEnter(e.Pin, e.Visual, _bar, this); };
        e.Visual.MouseLeave += (_, _) => StackIsland.HoverLeave();
        e.Visual.MouseLeftButtonDown += (_, ev) => { var fg = Native.GetForegroundWindow(); _fgAtPress = fg == _bar.Hwnd || fg == IntPtr.Zero ? _realFront : fg; _pressed = e; _pressAt = ev.GetPosition(this); e.Visual.CaptureMouse(); ev.Handled = true; };
        e.Visual.MouseLeftButtonUp += (_, ev) =>
        {
            ev.Handled = true;
            var pressed = _pressed;
            _pressed = null;
            bool dragged = _dragging != null;
            if (dragged) Drop();
            e.Visual.ReleaseMouseCapture();
            if (dragged || pressed != e) return;
            // Shift: a new window; Ctrl+Shift: as administrator (programs only – not a stack or the search)
            var mods = Keyboard.Modifiers;
            if ((mods & ModifierKeys.Shift) != 0 && e.Pin?.Items == null && e.Key != SearchKey)
            {
                _preview.Dismiss();
                var w0 = e.Windows.FirstOrDefault();
                if ((mods & ModifierKeys.Control) != 0) ShellIcons.Launch(e.Pin?.Exe ?? w0?.Exe, e.Pin?.Args, e.Pin?.AppId ?? w0?.AppId, admin: true);
                else Launch(e);
                return;
            }
            Click(e);
        };
        // the capture taken away (a preview, a menu, another window): the press and any drag end there – no icon left sliding
        e.Visual.LostMouseCapture += (_, _) => { _pressed = null; if (_dragging != null) Drop(); };
        e.Visual.MouseUp += (_, ev) => { if (ev.ChangedButton == MouseButton.Middle && e.Pin?.Items == null && e.Key != SearchKey) { ev.Handled = true; Launch(e); } };
        e.Visual.MouseRightButtonUp += (_, ev) => { ev.Handled = true; Menu(e); };
        // files dragged in from Explorer (the dock's own reordering uses the mouse capture, not OLE drag): a program opens them
        bool CanTake(DragEventArgs ev) => e.Key != SearchKey && e.Pin?.Items == null && ev.Data.GetDataPresent(DataFormats.FileDrop);
        void Light(bool on)
        {
            e.Visual.CornerRadius = new CornerRadius(Th.Corner(10));
            e.Visual.Background = on ? Ui.Res("AccentSoft") : Brushes.Transparent;
        }
        e.Visual.AllowDrop = e.Key != SearchKey;
        e.Visual.DragEnter += (_, ev) => { if (CanTake(ev)) Light(true); };
        e.Visual.DragOver += (_, ev) =>
        {
            ev.Effects = CanTake(ev) ? DragDropEffects.Copy : DragDropEffects.None;
            if (CanTake(ev)) Light(true);
            ev.Handled = true;
        };
        e.Visual.DragLeave += (_, _) => Light(false);
        e.Visual.Drop += (_, ev) =>
        {
            Light(false);
            ev.Handled = true;
            if (!CanTake(ev) || ev.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
            var first = e.Windows.FirstOrDefault();
            ShellIcons.OpenFiles(e.Pin?.Exe ?? first?.Exe, e.Pin?.AppId ?? first?.AppId, files);
        };
        e.Badge.Child = e.BadgeText;
        e.Badge.SetResourceReference(Border.BackgroundProperty, "AccentBrush"); // TaskWall's accent (follows a change of it)
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
        double dt = Math.Clamp(now - _lastDraw, 0, 50); // the time since the last frame, for the colour's fade (a long pause counts as one frame)
        _lastDraw = now;
        // a hop rises half an icon at most, but stays in the room the window has away from the screen's edge (no headroom without magnification)
        double hopRoom = Math.Max(4, _bar.Headroom + (BarHeight - IconSize) / 2 - 2);
        double hopHeight = Math.Min(12 * k, hopRoom);
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
            // a new running icon grows in from the line it stands on and fades in
            double appear = 1;
            if (e.AppearStart != 0)
            {
                double at = (now - e.AppearStart) / AppearMs;
                if (at >= 1 || !Anim.On) e.AppearStart = 0;
                else { appear = 1 - (1 - at) * (1 - at) * (1 - at); still = false; }
            }
            // starting: hops away from the screen's edge
            double lift = Hop(e, now, ref still) * hopHeight;
            if (top) lift = -lift;
            double drawn = size * appear;
            e.Visual.Width = e.Visual.Height = drawn;
            e.Visual.Opacity = appear;
            SetLeft(e.Visual, X(centre) - drawn / 2);
            SetTop(e.Visual, (top ? edge : edge - drawn) - lift);
            e.DrawX = X(centre);
            e.DrawEdge = edge;
            e.DrawSize = size;
            Shade(e, now, dt, ref still);
            if (e.Hung)
            {
                e.HungRing.CornerRadius = new CornerRadius(drawn * 0.24);
                e.HungRing.BorderThickness = new Thickness(Math.Max(1.5, drawn * 0.05));
                e.HungRing.Margin = new Thickness(-drawn * 0.06);
            }
            bool front = e.Windows.Any(t => t.Hwnd == _front);
            // the marks: in the middle of the room between the icon and the glass's edge (the screen's side) – a dot for each window
            // (three for three and more), one longer line for the one in front
            double room = Math.Max(4, (BarHeight - IconSize) / 2);
            int dots = e.Windows.Count == 0 ? 0 : front ? 1 : Math.Min(3, e.Windows.Count);
            double dotW = front ? 14 : 4, dotH = front ? 3 : 4, gap = 2.5;
            for (int d = 0; d < e.Dots.Length; d++)
            {
                var dot = e.Dots[d];
                dot.Visibility = d < dots ? Visibility.Visible : Visibility.Collapsed;
                dot.Width = dotW;
                dot.Height = dotH;
                dot.Margin = new Thickness(d == 0 ? 0 : gap, 0, 0, 0);
            }
            double markW = dots == 0 ? 0 : dots * dotW + (dots - 1) * gap;
            SetLeft(e.Mark, X(centre) - markW / 2);
            SetTop(e.Mark, top ? room / 2 - dotH / 2 : BarHeight - room / 2 - dotH / 2);
            // the counter: on the icon's upper corner (away from the screen's edge), growing with the icon
            if (e.Count.Any)
            {
                double h = Math.Max(13, Math.Round(size * (e.Count.N > 0 ? 0.54 : 0.3)));
                string text = e.Count.N > 99 ? "99+" : e.Count.N > 0 ? e.Count.N.ToString() : "";
                double bw = text.Length <= 1 ? h : h * (text.Length == 2 ? 1.4 : 1.8);
                e.Badge.Width = bw;
                e.Badge.Height = h;
                e.Badge.CornerRadius = new CornerRadius(h / 2);
                e.Badge.BorderThickness = new Thickness(Math.Max(1, h / 14));
                e.BadgeText.FontSize = h * 0.68;
                e.BadgeText.Margin = new Thickness(0, -h * 0.06, 0, 0);
                double iconTop = (top ? edge + size : edge - size) - lift; // the far side from the screen's edge (hopping with the icon)
                double inset = size * 0.08;
                SetLeft(e.Badge, X(centre) + size / 2 - inset - bw / 2);
                SetTop(e.Badge, top ? iconTop - inset - h / 2 : iconTop + inset - h / 2);
            }
        }
        // the gone icons shrink to the line they stood on and fade, then leave the canvas
        for (int i = _leaving.Count - 1; i >= 0; i--)
        {
            var e = _leaving[i];
            double lt = (now - e.LeaveStart) / LeaveMs;
            if (lt >= 1 || !Anim.On) { Children.Remove(e.Visual); _leaving.RemoveAt(i); continue; }
            double a = 1 - lt * lt * (3 - 2 * lt), s = e.DrawSize * a;
            e.Visual.Width = e.Visual.Height = s;
            e.Visual.Opacity = a;
            SetLeft(e.Visual, e.DrawX - s / 2);
            SetTop(e.Visual, top ? e.DrawEdge : e.DrawEdge - s);
            still = false;
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
            var ink = e.Attention ? Ui.Res("AccentBrush") : front ? Ui.Res("AccentBrush") : Ui.Brush(Th.Ink(0x90));
            foreach (var d in e.Dots) d.Background = ink;
            if (e.Attention && Anim.On)
                e.Mark.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(600)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            else e.Mark.BeginAnimation(OpacityProperty, null);
        }
        UpdateBadges();
    }

    static readonly SolidColorBrush DarkInk = Frozen(Color.FromRgb(0x1A, 0x1A, 0x1C));
    static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>The app ids an icon stands for: its windows' own, their packages', the pinned program's.</summary>
    static IEnumerable<string?> AppIdsOf(Entry e) =>
        e.Windows.Select(w => w.AppId).Concat(e.Windows.Select(w => AppBadges.PackageAppId(w.Pid))).Append(e.Pin?.AppId);

    static IEnumerable<string?> ExesOf(Entry e) => e.Windows.Select(w => w.Exe).Append(e.Pin?.Exe);

    /// <summary>The counters read again (the notification database or a title changed, a program asked for attention).</summary>
    void UpdateBadges()
    {
        foreach (var e in _entries)
        {
            var c = !S.DockBadges ? default : AppBadges.For(AppIdsOf(e), ExesOf(e), e.Windows.Select(w => (w.Title, w.Exe)));
            // a program that flashes for attention without a number: a dot
            if (S.DockBadges && !c.Any && e.Attention) c = new AppBadges.Count(0, true);
            bool grew = c.N > e.Count.N || c.Dot && !e.Count.Any;
            e.Count = c;
            e.Badge.Visibility = c.Any ? Visibility.Visible : Visibility.Collapsed;
            e.BadgeText.Text = c.N > 99 ? "99+" : c.N > 0 ? c.N.ToString() : "";
            // the number white on a dark or vivid accent, dark on a light one (the grey-white accent)
            if (Ui.Res("AccentBrush") is SolidColorBrush acc)
                e.BadgeText.Foreground = 0.2126 * acc.Color.R + 0.7152 * acc.Color.G + 0.0722 * acc.Color.B > 170 ? DarkInk : Brushes.White;
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
        // the button let go where the dock didn't hear it: no press, no drag any more
        if (ev.LeftButton != MouseButtonState.Pressed && (_pressed != null || _dragging != null)) { _pressed = null; if (_dragging != null) Drop(); }
        if (_pressed != null && _dragging == null && _pressed.Key != SearchKey && (_pressed.Pin != null || _pressed.Windows.Count > 0) && Math.Abs(p.X - _pressAt.X) > 6)
        {
            _dragging = _pressed;
            _preview.Dismiss();
            _hoverPreview.Stop();
        }
        if (_dragging != null)
        {
            SetDropTarget(_dragging.Pin?.Items == null
                ? _entries.FirstOrDefault(x => x != _dragging && x.Pin?.Items != null && Math.Abs(x.Shown - _pointer) < Magnify.IconRest * 0.45)
                : null);
            if (_dropTarget == null && _dragging.Pin != null) DragTo(_pointer); // over a stack: no reordering under it
        }
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

    /// <summary>The stack under the dragged icon lights up (it would take the program).</summary>
    void SetDropTarget(Entry? target)
    {
        if (target == _dropTarget) return;
        if (_dropTarget != null) { _dropTarget.Visual.Background = Brushes.Transparent; }
        _dropTarget = target;
        if (target != null) { target.Visual.CornerRadius = new CornerRadius(Th.Corner(10)); target.Visual.Background = Ui.Res("AccentSoft"); }
    }

    void Drop()
    {
        var dragged = _dragging;
        var target = _dropTarget;
        SetDropTarget(null);
        if (dragged != null && target?.Pin?.Items is { } into)
        {
            // into the stack: a pinned program moves there from the dock, a running one is added
            _dragging = null;
            var w0 = dragged.Windows.FirstOrDefault();
            var pin = dragged.Pin ?? new DockPin { Key = dragged.Key, Exe = w0?.Exe, AppId = w0?.AppId, Name = dragged.Name };
            if (dragged.Pin != null) S.DockPins.Remove(dragged.Pin);
            if (!into.Any(x => string.Equals(x.Key, pin.Key, StringComparison.OrdinalIgnoreCase))) into.Add(pin);
            SettingsStore.Save(S);
            _refreshAfterDrop = false;
            Refresh();
            return;
        }
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

    /// <summary>
    /// A click is a toggle: the program up (its window used last), and when it already is in front, down (all its windows
    /// minimised). Not running: it starts. A stack: its island opens. The previews of several windows come on hover.
    /// </summary>
    void Click(Entry e)
    {
        _preview.Dismiss();
        if (e.Key == SearchKey) { _bar.OpenSearch(e.Visual); return; }
        if (e.Pin?.Items != null) { StackIsland.Toggle(e.Pin, e.Visual, _bar, this); return; }
        if (e.Windows.Count == 0) { Launch(e); return; }
        var fg = _fgAtPress;
        if (e.Windows.Any(w => w.Hwnd == fg) && !TaskList.IsIconic(fg))
        {
            foreach (var w in e.Windows) if (!TaskList.IsIconic(w.Hwnd)) WindowActions.Minimize(w.Hwnd);
            return;
        }
        // several windows: their live previews to pick one (or all) – no guessing which one comes up
        if (e.Windows.Count > 1) { ShowPreview(e); return; }
        WindowActions.Activate(e.Windows[0].Hwnd);
    }

    /// <summary>Win+n: the n-th program or stack of the dock (the magnifier not counted) – a click on it, or a new window with Shift.</summary>
    public void ActivateNth(int n, bool newWindow)
    {
        var e = _entries.Where(x => x.Key != SearchKey).Skip(n - 1).FirstOrDefault();
        if (e == null) return;
        var fg = Native.GetForegroundWindow(); // the toggle judges by the window in front now, not by the last mouse press
        _fgAtPress = fg == _bar.Hwnd || fg == IntPtr.Zero ? _realFront : fg;
        if (e.Pin?.Items != null) { _preview.Dismiss(); StackIsland.Toggle(e.Pin, e.Visual, _bar, this); return; }
        if (newWindow) { _preview.Dismiss(); Launch(e); return; }
        Click(e);
    }

    /// <summary>A program from a stack: its window up when it runs (by its file or app id), otherwise started.</summary>
    public void ActivateOrLaunch(DockPin pin)
    {
        var windows = _entries.SelectMany(x => x.Windows).Where(w =>
            !string.IsNullOrEmpty(pin.Exe) && string.Equals(w.Exe, pin.Exe, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(pin.AppId) && string.Equals(w.AppId, pin.AppId, StringComparison.OrdinalIgnoreCase)).Select(w => w.Hwnd).ToList();
        if (windows.Count > 0) { WindowActions.Activate(WindowActions.Topmost(windows)); return; }
        ShellIcons.Launch(pin.Exe, pin.Args, pin.AppId);
        // its own icon hops when the dock has one (pinned there too), otherwise the stack's that holds it
        var at = _entries.FirstOrDefault(x => x.Pin?.Items == null && string.Equals(x.Key, pin.Key, StringComparison.OrdinalIgnoreCase))
            ?? _entries.FirstOrDefault(x => x.Pin?.Items is { } items && Holds(items, pin));
        if (at != null) StartLaunch(at, pin.Exe, pin.AppId);
    }

    static bool Holds(List<DockPin> items, DockPin pin) => items.Any(x => x == pin || x.Items != null && Holds(x.Items, pin));

    /// <summary>The programs running now (for "Dodaj uruchomiony" in a stack): name, key, file, app id.</summary>
    public IEnumerable<DockPin> RunningPrograms() => _entries.Where(x => x.Windows.Count > 0 && x.Pin?.Items == null).Select(x => new DockPin
    {
        Key = x.Key, Exe = x.Windows[0].Exe, AppId = x.Windows[0].AppId, Name = x.Name,
    });

    void Launch(Entry e)
    {
        var first = e.Windows.FirstOrDefault();
        string? exe = e.Pin?.Exe ?? first?.Exe, appId = e.Pin?.AppId ?? first?.AppId;
        ShellIcons.Launch(exe, e.Pin?.Args, appId);
        StartLaunch(e, exe, appId);
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
        if (e.Key == SearchKey)
        {
            Item(L.T("Ustawienia wyszukiwarki…"), () => App.Instance.ShowSettings("bar"));
            m.IsOpen = true;
            return;
        }
        if (e.Pin?.Items is { } items)
        {
            Item(L.T("Otwórz stos"), () => StackIsland.Toggle(e.Pin, e.Visual, _bar, this));
            Item(L.T("Zmień nazwę stosu…"), () => { if (AskWindow.Ask(L.T("Stos"), L.T("Nazwa stosu:"), e.Pin.Name) is { Length: > 0 } n) { e.Pin.Name = n.Trim(); SettingsStore.Save(S); Refresh(); } });
            m.Items.Add(StackIsland.IconMenu(e.Pin, () => { SettingsStore.Save(S); Refresh(); }));
            Item(L.F("Usuń stos z docka ({0})", items.Count), () => Unpin(e));
            m.IsOpen = true;
            return;
        }
        Item(L.T("Nowe okno"), () => Launch(e));
        if (e.Pin != null) Item(L.T("Odepnij od docka"), () => Unpin(e));
        else Item(L.T("Przypnij do docka"), () => Pin(e));
        // into a stack (any, nested ones by their path)
        var stacks = StackIsland.AllStacks(S.DockPins).ToList();
        if (stacks.Count > 0 && (e.Windows.FirstOrDefault() is { } fw || e.Pin != null))
        {
            var into = new MenuItem { Header = L.T("Dodaj do stosu") };
            foreach (var (stack, path) in stacks)
            {
                var si = new MenuItem { Header = path };
                si.Click += (_, _) =>
                {
                    var w0 = e.Windows.FirstOrDefault();
                    stack.Items!.Add(new DockPin { Key = e.Key, Exe = e.Pin?.Exe ?? w0?.Exe, AppId = e.Pin?.AppId ?? w0?.AppId, Args = e.Pin?.Args, Name = e.Name });
                    SettingsStore.Save(S);
                    Refresh();
                };
                into.Items.Add(si);
            }
            m.Items.Add(into);
        }
        if (e.Windows.Count > 0)
        {
            m.Items.Add(new Separator());
            Item(e.Windows.Count == 1 ? L.T("Zamknij okno") : L.F("Zamknij wszystkie okna ({0})", e.Windows.Count), () => { foreach (var w in e.Windows) WindowActions.Close(w.Hwnd); ScheduleRefresh(); });
            // like the Task Manager's "End task": the program's processes ended at once (no asking to save)
            Item(L.T("Zakończ zadanie"), () =>
            {
                foreach (var pid in e.Windows.Select(w => w.Pid).Distinct())
                {
                    try { using var proc = System.Diagnostics.Process.GetProcessById((int)pid); proc.Kill(); }
                    catch (Exception ex) { Log.Error("end task " + e.Name, ex); MessageBox.Show(L.F("Nie udało się zakończyć „{0}”: {1}", e.Name, ex.Message), "TaskWall"); }
                }
                ScheduleRefresh();
            });
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

    /// <summary>Dev runs (--open stack): the first stack's island opened.</summary>
    public void DevStack()
    {
        if (_entries.FirstOrDefault(x => x.Pin?.Items != null) is { } st) StackIsland.Toggle(st.Pin!, st.Visual, _bar, this);
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
        var e = _entries.OrderByDescending(x => x.Windows.Count).FirstOrDefault(x => x.Windows.Count > 0);
        if (e == null) { Log.Error("dev dock preview: nothing runs"); return; }
        Log.Error($"dev dock preview: {e.Name} – " + string.Join(" | ", e.Windows.Select(w => $"{w.Hwnd} min={w.Minimized}")));
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
