using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WKey = System.Windows.Input.Key; // "Key" is the day key here

namespace TaskWall;

/// <summary>What the day view needs from the board (its menus, state changes and the add row).</summary>
public sealed record DayViewHost(
    Func<BoardStore> Store,
    Func<DateTime, List<TaskItem>> TasksOf,
    Func<TaskItem, FrameworkElement, ContextMenu> Menu,
    Action<TaskItem> CycleState,
    Action<IReadOnlyList<(TaskItem T, TimeSpan From, TimeSpan To, bool Length)>> SetTimes, // all at once (one Ctrl+Z); Length: it becomes the estimate
    Action<string, DateTime, TimeSpan> DropTask,
    Func<string, DateTime, FrameworkElement> AddRow,
    Action<Alarm, DateTime> OpenAlarm,
    Action<TaskItem> ClearTime); // a block dropped back on the "Bez godziny" strip

/// <summary>
/// The day view: an agenda on a vertical line of hours – meetings and alarms at their times, the working hours
/// (drag their edges to change them for this day), the day's tasks as blocks (without hours they line up from the
/// start of work; drag a block to its time, its lower edge to how long it took – that becomes its estimate) – and the
/// day's notebook next to it.
/// </summary>
public sealed class DayView : Grid
{
    readonly DayViewHost _host;
    readonly Canvas _canvas = new() { Background = Brushes.Transparent, AllowDrop = true, ClipToBounds = true };
    readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly StackPanel _addHost = new() { Margin = new Thickness(56, 0, 8, 6) };
    readonly WrapPanel _loose = new() { Margin = new Thickness(0, 2, 0, 0) };
    readonly TextBlock _looseHead = new() { FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("FgFaint") };
    readonly Border _looseBox = new() { Margin = new Thickness(56, 0, 8, 8), Padding = new Thickness(8, 5, 4, 3), CornerRadius = new CornerRadius(6) };
    readonly NotesEditor _notes = new();
    readonly TextBlock _notesTitle = new() { FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) };
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMinutes(1) };
    DateTime _date = DateTime.Today;
    bool _menuOpen; // a task's menu is open: no redraw under it
    bool _enter; // the next render shows a new day
    BoardStore? _noteStore; // where the note shown was loaded from
    string? _noteKey;
    int _enterIndex;
    bool _scrolled;
    readonly DockPanel _left = new(), _right = new() { Margin = new Thickness(0) };
    readonly Border _notesCard = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 10, 8), Margin = new Thickness(2, 0, 2, 4) };
    /// <summary>One hour: the whole day fits the height there is (no scrolling), but never so small it can't be grabbed.</summary>
    double HourH => _scroll.ActualHeight > 0
        ? Math.Clamp(Math.Floor((_scroll.ActualHeight - 18) / 24 * 4) / 4, Math.Round(App.Settings.FontSize * 2.2), Math.Round(App.Settings.FontSize * 6))
        : Math.Round(App.Settings.FontSize * 3.8);
    const double Gutter = 56;

    static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    static SolidColorBrush B(Color c) => Ui.Brush(c); // shared, frozen
    static Color A(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
    static SolidColorBrush Tx(Color c) => new(Th.Light ? Color.FromArgb(c.A, (byte)(c.R * 0.6), (byte)(c.G * 0.6), (byte)(c.B * 0.6)) : c);
    static string Key(DateTime d) => Days.Key(d);
    static TimeSpan? Hm(string? s) => TimeSpan.TryParseExact(s ?? "", @"h\:mm", CultureInfo.InvariantCulture, out var t) ? t : null;
    static readonly Color Violet = Color.FromRgb(0x8C, 0x9B, 0xFF), Amber = Color.FromRgb(0xF2, 0xB1, 0x4C);

    public DayView(DayViewHost host)
    {
        _host = host;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 260 });

        var left = _left;
        DockPanel.SetDock(_addHost, Dock.Top);
        left.Children.Add(_addHost);
        var looseScroll = new ScrollViewer { Content = _loose, MaxHeight = 96, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var looseStack = new DockPanel();
        var looseHead = _looseHead;
        DockPanel.SetDock(looseHead, Dock.Top);
        looseStack.Children.Add(looseHead);
        looseStack.Children.Add(looseScroll);
        _looseBox.Child = looseStack;
        DockPanel.SetDock(_looseBox, Dock.Top);
        left.Children.Add(_looseBox);
        _scroll.Content = _canvas;
        left.Children.Add(_scroll);
        Children.Add(At2(left, 0));

        Children.Add(At2(new Border { Width = 1, Background = Res("Line"), HorizontalAlignment = HorizontalAlignment.Center }, 1));

        var right = _right;
        DockPanel.SetDock(_notesTitle, Dock.Top);
        right.Children.Add(_notesTitle);
        right.Children.Add(_notes);
        // the notebook on a card a shade apart from the board, so it reads as a page to write on
        _notesCard.Child = right;
        Children.Add(At2(_notesCard, 2));
        // a note is saved where it was loaded from (that account, that day) – never into the account switched to since
        _notes.Changed += md => (_noteStore ?? _host.Store()).SetNote(_noteKey ?? Key(_date), md);
        _notes.ExportClicked += ExportMenu;
        _scroll.SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 0.5) Render(); };

        _canvas.SizeChanged += (_, _) => Render();
        _canvas.Drop += OnDrop;
        _canvas.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent("TaskWallTask") ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        _canvas.PreviewMouseMove += OnMove;
        _canvas.PreviewMouseLeftButtonUp += OnUp;
        _canvas.LostMouseCapture += (_, _) => CancelDrag();
        // every minute only the "now" line moves (the rest is drawn again when something changes)
        _clock.Tick += (_, _) => { if (IsVisible && _drag == null && !_menuOpen) MoveNow(); };
        _clock.Start();
    }

    /// <summary>The board closed: no more ticks.</summary>
    public void StopTimers() => _clock.Stop();

    static UIElement At2(UIElement el, int col) { SetColumn(el, col); return el; }
    static T At<T>(T el, double x, double y) where T : UIElement { Canvas.SetLeft(el, x); Canvas.SetTop(el, y); return el; }

    /// <summary>Shows a day (the board calls it on every rebuild).</summary>
    public void Show(DateTime date)
    {
        bool newDay = date.Date != _date;
        bool newStore = !ReferenceEquals(_host.Store(), _noteStore);
        if (newDay || newStore) _notes.Flush(); // the last words typed belong to the day (and account) they were typed on
        // the notebook on the left or the right (Settings → Tablica)
        bool notesLeft = App.Settings.DayNotesLeft;
        SetColumn(_left, notesLeft ? 2 : 0);
        SetColumn(_notesCard, notesLeft ? 0 : 2);
        _notesCard.Background = B(Th.Ink(Th.Light ? (byte)0x0D : (byte)0x09));
        _notesCard.BorderBrush = B(Th.Ink(Th.Light ? (byte)0x14 : (byte)0x0F));
        _notesCard.BorderThickness = new Thickness(1);
        ColumnDefinitions[0].Width = new GridLength(notesLeft ? 2 : 3, GridUnitType.Star);
        ColumnDefinitions[2].Width = new GridLength(notesLeft ? 3 : 2, GridUnitType.Star);
        ColumnDefinitions[0].MinWidth = notesLeft ? 260 : 0;
        ColumnDefinitions[2].MinWidth = notesLeft ? 0 : 260;
        _date = date.Date;
        _notesTitle.Text = L.Up(L.T("Notatki")) + "  ·  " + _date.ToString("dddd d MMMM", BoardWindow.Pl);
        _notesTitle.Foreground = Res("FgDim");
        _noteStore = _host.Store();
        _noteKey = Key(_date);
        _notes.Load(_noteStore.Note(_noteKey), force: newDay || newStore);
        _addHost.Children.Clear();
        _addHost.Children.Add(_host.AddRow(Key(_date), _date));
        if (newDay) { _scrolled = false; _enter = true; _enterIndex = 0; _selKey = null; }
        Render();
    }

    // ---------- export: this day or every day's notes, as Markdown (.md) or plain text (.txt) ----------

    void ExportMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor };
        void Item(string header, bool all)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => Export(all);
            menu.Items.Add(mi);
        }
        Item(L.F("Ten dzień ({0})…", _date.ToString("d MMM", BoardWindow.Pl)), false);
        Item(L.T("Wszystkie notatki…"), true);
        menu.IsOpen = true;
    }

    void Export(bool all)
    {
        var store = _host.Store();
        var today = Key(_date);
        var notes = all
            ? (store.Data.Notes ?? new()).Where(kv => (kv.Value?.Text ?? "").Trim().Length > 0 && DateTime.TryParseExact(kv.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value.Text)).ToList()
            : new List<(string Key, string Text)> { (today, _notes.Markdown) };
        if (notes.Count == 0 || notes.All(n => n.Text.Trim().Length == 0)) { MessageBox.Show(L.T("Nie ma jeszcze notatek do wyeksportowania."), "TaskWall"); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = L.T("Markdown (*.md)|*.md|Tekst (*.txt)|*.txt"),
            FileName = all ? "TaskWall-notatki" : "TaskWall-notatki-" + today,
            DefaultExt = ".md",
        };
        if (dlg.ShowDialog() != true) return;
        bool md = !dlg.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
        var sb = new System.Text.StringBuilder();
        foreach (var (key, text) in notes)
        {
            var day = DateTime.ParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("dddd d MMMM yyyy", BoardWindow.Pl);
            if (md)
            {
                // the day as the top heading, the note's own headings one level lower (outside code blocks)
                sb.Append("# ").Append(day).Append("\n\n");
                bool code = false;
                foreach (var line in text.Replace("\r", "").Split('\n'))
                {
                    if (line.TrimStart().StartsWith("```")) code = !code;
                    sb.Append(!code && line.StartsWith('#') && !line.StartsWith("######") ? "#" + line : line).Append('\n');
                }
            }
            else sb.Append(day.ToUpper(BoardWindow.Pl)).Append('\n').Append(new string('=', day.Length)).Append("\n\n").Append(NotesEditor.ToPlain(text)).Append('\n');
            sb.Append('\n');
        }
        try
        {
            System.IO.File.WriteAllText(dlg.FileName, sb.ToString().TrimEnd() + "\n", new System.Text.UTF8Encoding(false));
            MessageBox.Show(L.F("Zapisano notatki:\n{0}", dlg.FileName), "TaskWall");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "TaskWall"); }
    }

    // ---------- the agenda ----------

    sealed record Block(TimeSpan From, TimeSpan To, string Kind, object Source, bool Proposed, int Lane = 0, int Lanes = 1);

    /// <summary>A working day has its regular hours; a weekend, holiday, company day off or leave only what was added by hand.</summary>
    bool HasRegularHours() => _host.Store().HasOwnHours(Key(_date)) || !(_date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || DaysOff.Is(_date));

    (TimeSpan Start, TimeSpan End) WorkHours()
    {
        var (s, e) = _host.Store().WorkHours(Key(_date), App.Settings.WorkStart, App.Settings.WorkEnd);
        var ws = Hm(s) ?? TimeSpan.FromHours(9);
        var we = Hm(e) ?? TimeSpan.FromHours(17);
        if (e == "23:59") we = TimeSpan.FromHours(24); // midnight is kept as 23:59
        return we > ws ? (ws, we) : (ws, ws + TimeSpan.FromHours(8));
    }

    /// <summary>A working day's length from the settings' default hours (8 hours when they make no sense).</summary>
    static TimeSpan DayLength()
    {
        var len = (Hm(App.Settings.WorkEnd) ?? TimeSpan.FromHours(17)) - (Hm(App.Settings.WorkStart) ?? TimeSpan.FromHours(9));
        return len > TimeSpan.FromMinutes(30) ? len : TimeSpan.FromHours(8);
    }

    List<Block> Blocks()
    {
        var store = _host.Store();
        var list = new List<Block>();
        foreach (var a in store.Alarms.Where(a => a.Occurs(_date)))
        {
            var at = a.At(_date).TimeOfDay;
            var to = a.IsMeeting ? (a.EndAt(_date).Date > _date ? TimeSpan.FromHours(24) : a.EndAt(_date).TimeOfDay) : at + TimeSpan.FromMinutes(20);
            list.Add(new Block(at, to > at ? to : at + TimeSpan.FromMinutes(15), a.IsMeeting ? "meeting" : "alarm", a, false));
        }
        // yesterday's meeting running past midnight: its tail from 00:00
        foreach (var a in store.Alarms.Where(a => a.ContinuesInto(_date)))
        {
            var to = a.EndAt(_date.AddDays(-1)).TimeOfDay;
            if (to > TimeSpan.Zero) list.Add(new Block(TimeSpan.Zero, to, "meeting", a, false));
        }
        // an event over midnight or several days: only its part on this day
        foreach (var ev in CalendarService.On(_date).Where(e => !e.IsHoliday && !e.AllDay))
        {
            var from = ev.Start.Date < _date ? TimeSpan.Zero : ev.Start.TimeOfDay;
            var to = ev.End.Date > _date ? TimeSpan.FromHours(24) : ev.End.TimeOfDay;
            if (to > from) list.Add(new Block(from, to, "event", ev, false));
        }

        // tasks with hours where they are (the others wait in the strip above)
        foreach (var t in _host.TasksOf(_date))
        {
            if (Hm(t.Time) is not { } from) continue;
            var dur = TimeSpan.FromHours(Math.Clamp(t.Estimate ?? 0.5, 0.25, 8));
            var to = t.End == "23:59" ? TimeSpan.FromHours(24) : Hm(t.End) is { } e && e > from ? e : from + dur;
            if (to > TimeSpan.FromHours(24)) to = TimeSpan.FromHours(24);
            list.Add(new Block(from, to, "task", t, false));
        }

        // the breaks (AFK, lunch, private): like blocks, side by side with the tasks they interrupted
        var breaks = store.Breaks(Key(_date));
        for (int i = 0; i < breaks.Count; i++) list.Add(new Block(breaks[i].Start, breaks[i].End, "break", new BreakRef(i, breaks[i]), false));

        // side by side where they overlap
        var sorted = list.OrderBy(b => b.From).ThenByDescending(b => b.To).ToList();
        var result = new List<Block>();
        var cluster = new List<(Block B, int Lane)>();
        var ends = new List<TimeSpan>();
        TimeSpan clusterEnd = TimeSpan.Zero;
        void Close()
        {
            int lanes = Math.Max(1, ends.Count);
            result.AddRange(cluster.Select(x => x.B with { Lane = x.Lane, Lanes = lanes }));
            cluster.Clear();
            ends.Clear();
        }
        foreach (var b in sorted)
        {
            if (cluster.Count > 0 && b.From >= clusterEnd) Close();
            int lane = ends.FindIndex(e => e <= b.From);
            if (lane < 0) { lane = ends.Count; ends.Add(b.To); } else ends[lane] = b.To;
            cluster.Add((b, lane));
            if (b.To > clusterEnd || cluster.Count == 1) clusterEnd = cluster.Max(x => x.B.To);
        }
        Close();
        return result;
    }

    double Y(TimeSpan t) => t.TotalHours * HourH + 8;
    TimeSpan T(double y) => TimeSpan.FromMinutes(Math.Clamp(Math.Round((y - 8) / HourH * 60 / 15) * 15, 0, 24 * 60));

    void Render()
    {
        using var watch = Watchdog.Step("drawing the day view");
        RenderLoose();
        double w = _canvas.ActualWidth;
        if (w <= 0) { _canvas.Height = 24 * HourH + 16; return; }
        _canvas.Children.Clear();
        _canvas.Height = 24 * HourH + 16;
        var (ws, we) = WorkHours();
        bool regular = HasRegularHours(); // weekends, holidays, leave: none – only overtime added by hand
        double area = w - Gutter - 8;

        // outside the working hours: hatched like the days off elsewhere; the hours themselves clean
        var hatch = DayHatch();
        // extra working periods (overtime, e.g. 20:00–22:00): clean like the regular hours, with their own slot for tasks
        var store = _host.Store();
        var overtime = store.Overtime(Key(_date));
        var accentC = ((SolidColorBrush)Res("AccentBrush")).Color;
        var cursor = TimeSpan.Zero;
        foreach (var (a, b) in (regular ? overtime.Append((ws, we)) : overtime).OrderBy(p => p.Item1))
        {
            if (a > cursor) HatchRect(cursor, a);
            if (b > cursor) cursor = b;
        }
        if (cursor < TimeSpan.FromHours(24)) HatchRect(cursor, TimeSpan.FromHours(24));
        if (regular)
        {
            // the working hours stand apart from the hatched rest: a lighter field, an accent strip down its left edge and
            // a line at its start and end; right-click: an AFK break from there
            var work = new Rectangle { Width = area + 4, Height = Y(we) - Y(ws), Fill = B(Th.Ink(0x0C)), ToolTip = L.T("Godziny pracy · prawy przycisk: dodaj przerwę AFK") };
            work.MouseRightButtonUp += (_, e) => { e.Handled = true; BreakMenu(work, T(e.GetPosition(_canvas).Y)); };
            _canvas.Children.Add(At(work, Gutter - 2, Y(ws)));
            _canvas.Children.Add(At(new Rectangle { Width = 3, Height = Y(we) - Y(ws), Fill = B(A(accentC, 0x90)), IsHitTestVisible = false }, Gutter - 5, Y(ws)));
            foreach (var edge in new[] { ws, we })
                _canvas.Children.Add(At(new Rectangle { Width = area + 4, Height = 1, Fill = B(A(accentC, 0x60)), IsHitTestVisible = false }, Gutter - 2, Y(edge)));
        }
        for (int i = 0; i < overtime.Count; i++)
        {
            var (a, b) = overtime[i];
            int index = i;
            var slot = new Border
            {
                Width = area + 4, Height = Math.Max(4, Y(b) - Y(a)), Background = B(A(accentC, 0x10)), BorderBrush = B(A(accentC, 0x50)),
                BorderThickness = new Thickness(0, 1, 0, 1), ToolTip = L.F("Nadgodziny {0}–{1}", a.ToString(@"hh\:mm"), Hhmm(b)) + "\n" + L.T("Przeciągnij krawędzie, żeby zmienić · prawy przycisk: usuń"),
            };
            slot.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                var menu = new ContextMenu();
                var del = new MenuItem { Header = L.T("Usuń te nadgodziny") };
                del.Click += (_, _) => { store.Checkpoint(); store.SetOvertime(Key(_date), store.Overtime(Key(_date)).Where((_, k) => k != index)); Render(); };
                menu.Items.Add(del);
                menu.PlacementTarget = slot;
                menu.IsOpen = true;
            };
            _canvas.Children.Add(At(slot, Gutter - 2, Y(a)));
            _canvas.Children.Add(At(new TextBlock
            {
                Text = L.Up(L.T("Nadgodziny")) + "  " + BoardWindow.Hours((b - a).TotalHours), FontSize = 9, FontWeight = FontWeights.Bold,
                Foreground = Tx(accentC), IsHitTestVisible = false,
            }, Gutter + 6, Y(a) + 3));
        }

        void HatchRect(TimeSpan from, TimeSpan to)
        {
            // outside work: darker under the hatch, so it doesn't run into the working hours
            _canvas.Children.Add(At(new Rectangle { Width = area + 4, Height = Math.Max(0, Y(to) - Y(from)), Fill = B(Th.Light ? Color.FromArgb(0x10, 0, 0, 0) : Color.FromArgb(0x40, 0, 0, 0)), IsHitTestVisible = false }, Gutter - 2, Y(from)));
            var r = new Rectangle { Width = area + 4, Height = Math.Max(0, Y(to) - Y(from)), Fill = hatch, ToolTip = L.T("Poza godzinami pracy · prawy przycisk: dodaj tu nadgodziny") };
            r.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                // two hours from the full hour clicked, within this free stretch
                var at = TimeSpan.FromHours(Math.Floor(T(e.GetPosition(_canvas).Y).TotalHours));
                if (at < from) at = from;
                var end = at + TimeSpan.FromHours(2) > to ? to : at + TimeSpan.FromHours(2);
                if (end - at < TimeSpan.FromMinutes(15)) return;
                var menu = new ContextMenu();
                var add = new MenuItem { Header = L.F("Dodaj nadgodziny {0}–{1}", at.ToString(@"hh\:mm"), Hhmm(end)) };
                add.Click += (_, _) => { store.Checkpoint(); store.SetOvertime(Key(_date), store.Overtime(Key(_date)).Append((at, end))); Render(); };
                menu.Items.Add(add);
                menu.PlacementTarget = r;
                menu.IsOpen = true;
            };
            _canvas.Children.Add(At(r, Gutter - 2, Y(from)));
        }

        for (int h = 0; h <= 24; h++)
        {
            double y = Y(TimeSpan.FromHours(h));
            _canvas.Children.Add(At(new Rectangle { Width = area + 4, Height = 1, Fill = B(Th.Ink(h % 6 == 0 ? (byte)0x22 : (byte)0x14)) }, Gutter - 2, y));
            if (h < 24) _canvas.Children.Add(At(new Rectangle { Width = area + 4, Height = 1, Fill = B(Th.Ink(0x08)) }, Gutter - 2, y + HourH / 2));
            if (h < 24) _canvas.Children.Add(At(new TextBlock { Text = $"{h:00}:00", FontSize = 10.5, Foreground = Res("FgFaint"), Width = Gutter - 12, TextAlignment = TextAlignment.Right }, 0, y - 7));
        }

        // the working hours' edges: drag them (this day only); double-click goes back to the default
        if (regular)
        {
            WorkHandle(ws, true, area);
            WorkHandle(we, false, area);
        }
        for (int i = 0; i < overtime.Count; i++)
        {
            WorkHandle(overtime[i].Start, true, area, i);
            WorkHandle(overtime[i].End, false, area, i);
        }

        foreach (var b in Blocks()) DrawBlock(b, area);

        // now
        if (_date == DateTime.Today)
        {
            var now = DateTime.Now.TimeOfDay;
            double y = Y(now);
            var accent = ((SolidColorBrush)Res("AccentBrush")).Color;
            _nowLine = At(new Rectangle { Width = area + 10, Height = 1.6, Fill = B(accent), IsHitTestVisible = false }, Gutter - 6, y - 0.8);
            _canvas.Children.Add(_nowLine);
            _nowText = new TextBlock { Text = DateTime.Now.ToString("HH:mm"), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
            _nowLabel = At(new Border { CornerRadius = new CornerRadius(4), Background = B(accent), Padding = new Thickness(4, 0, 4, 1), IsHitTestVisible = false, Child = _nowText }, 4, y - 8);
            _canvas.Children.Add(_nowLabel);
        }
        else { _nowLine = null; _nowLabel = null; }

        if (!_scrolled && _scroll.ActualHeight > 0)
        {
            _scrolled = true;
            var start = regular ? ws : overtime.Count > 0 ? overtime[0].Start : TimeSpan.FromHours(9);
            var first = _date == DateTime.Today ? TimeSpan.FromTicks(Math.Min(DateTime.Now.TimeOfDay.Ticks, start.Ticks)) : start;
            _scroll.ScrollToVerticalOffset(Math.Max(0, Y(first) - HourH * 0.75));
        }
        foreach (var el in _canvas.Children.OfType<FrameworkElement>()) if (el.ToolTip != null) ToolTipService.SetInitialShowDelay(el, 250);
        _enter = false;
    }

    static string Hhmm(TimeSpan t) => t >= TimeSpan.FromHours(24) ? "24:00" : t.ToString(@"hh\:mm");

    /// <summary>A working period's edge: the regular hours (<paramref name="overtime"/> null) or an overtime period's.</summary>
    /// <summary>The hatch outside the working hours: one frozen brush (per theme), not one per render.</summary>
    static Brush DayHatch()
    {
        if (_hatch != null && _hatchLight == Th.Light) return _hatch;
        var b = new DrawingBrush(new GeometryDrawing(null, new Pen(Ui.Brush(Th.Ink(Th.Light ? (byte)0x22 : (byte)0x12)), 1.1), Geometry.Parse("M0,10 L10,0 M-3,3 L3,-3 M7,13 L13,7")))
            { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 10, 10), ViewportUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 10, 10), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        b.Freeze();
        _hatchLight = Th.Light;
        return _hatch = b;
    }

    Rectangle? _nowLine;
    Border? _nowLabel;
    TextBlock? _nowText;

    void MoveNow()
    {
        if (_date != DateTime.Today || _nowLine == null || _nowLabel == null || _nowText == null) { Render(); return; }
        double y = Y(DateTime.Now.TimeOfDay);
        Canvas.SetTop(_nowLine, y - 0.8);
        Canvas.SetTop(_nowLabel, y - 8);
        _nowText.Text = DateTime.Now.ToString("HH:mm");
    }

    static Brush? _hatch;
    static bool _hatchLight;

    /// <summary>"  ·  7h 30m (przerwy 30 min)": the day's working time without its breaks (on the end-of-work handle).</summary>
    string WorkSummary()
    {
        var (ws, we) = WorkHours();
        // only the part of each break inside the working hours takes time off them
        var afk = _host.Store().Breaks(Key(_date)).Sum(p => Math.Max(0, ((p.End < we ? p.End : we) - (p.Start > ws ? p.Start : ws)).TotalHours));
        return "  ·  " + BoardWindow.Duration(((we - ws).TotalHours - afk) * 60) + (afk > 0 ? "  (" + L.F("przerwy {0}", BoardWindow.Duration(afk * 60)) + ")" : "");
    }

    /// <summary>A break's kind as the day view names it.</summary>
    static string BreakName(string kind) => kind switch { "lunch" => L.T("Lunch"), "private" => L.T("Prywatne"), _ => "AFK" };

    /// <summary>
    /// Right-click on the working hours: a break from the quarter clicked – AFK (15 min / 30 min / 1 h), lunch, something
    /// private – or AFK from now; a day whose breaks were changed can go back to the default lunch.
    /// </summary>
    void BreakMenu(FrameworkElement target, TimeSpan at)
    {
        var store = _host.Store();
        var menu = new ContextMenu { PlacementTarget = target };
        void Item(TimeSpan from, TimeSpan len, string kind, string header)
        {
            var to = from + len > TimeSpan.FromHours(24) ? TimeSpan.FromHours(24) : from + len;
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => { store.Checkpoint(); store.SetBreaks(Key(_date), store.Breaks(Key(_date)).Append(new Break(from, to, kind))); Render(); };
            menu.Items.Add(mi);
        }
        foreach (var min in new[] { 15, 30, 60 })
            Item(at, TimeSpan.FromMinutes(min), "afk", L.F("AFK od {0} · {1}", Hhmm(at), BoardWindow.Hours(min / 60.0)));
        Item(at, TimeSpan.FromMinutes(30), "lunch", L.F("Lunch od {0} · {1}", Hhmm(at), BoardWindow.Hours(0.5)));
        Item(at, TimeSpan.FromHours(1), "private", L.F("Prywatne od {0} · {1}", Hhmm(at), BoardWindow.Hours(1)));
        if (_date == DateTime.Today)
        {
            // from now, to the quarter: "I'm off for a while"
            var now = TimeSpan.FromMinutes(Math.Floor(DateTime.Now.TimeOfDay.TotalMinutes / 15) * 15);
            menu.Items.Add(new Separator());
            Item(now, TimeSpan.FromMinutes(30), "afk", L.F("AFK teraz ({0}) · {1}", Hhmm(now), BoardWindow.Hours(0.5)));
        }
        ResetBreaksItem(menu);
        _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;
        menu.IsOpen = true;
    }

    /// <summary>"Przywróć domyślne przerwy": on a day whose breaks were changed, while a default lunch is set.</summary>
    void ResetBreaksItem(ContextMenu menu)
    {
        var store = _host.Store();
        if (!store.OwnBreaks(Key(_date)) || App.Settings.Lunch.Length == 0) return;
        menu.Items.Add(new Separator());
        var reset = new MenuItem { Header = L.F("Przywróć domyślne przerwy (lunch {0})", App.Settings.Lunch) };
        reset.Click += (_, _) => { store.Checkpoint(); store.ResetBreaks(Key(_date)); Render(); };
        menu.Items.Add(reset);
    }

    void WorkHandle(TimeSpan at, bool start, double area, int? overtime = null)
    {
        var pill = new Border
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 1), Background = B(Th.Ink(0x26)), Cursor = Cursors.SizeNS,
            ToolTip = overtime != null
                ? (start ? L.T("Początek nadgodzin") : L.T("Koniec nadgodzin")) + "\n" + L.T("Przeciągnij, żeby zmienić · dwuklik: usuń te nadgodziny")
                : start ? L.T("Początek pracy") + "\n" + (_host.Store().EndSet(Key(_date))
                    ? L.T("Przeciągnij, żeby zmienić dla tego dnia (koniec ustawiony ręcznie zostaje) · dwuklik: domyślne godziny")
                    : L.F("Przeciągnij, żeby zmienić dla tego dnia – koniec przesunie się z nim ({0} pracy), dopóki nie ustawisz go sam · dwuklik: domyślne godziny", BoardWindow.Hours(DayLength().TotalHours)))
                : L.T("Koniec pracy") + "\n" + L.T("Przeciągnij, żeby ustawić go dla tego dnia (początek go już nie przesunie) · dwuklik: domyślne godziny"),
            Child = new TextBlock { Text = (start ? "▸ " : "◂ ") + Hhmm(at) + (start || overtime != null ? "" : WorkSummary()), FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = Res("FgDim") },
        };
        pill.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            var store = _host.Store();
            if (e.ClickCount == 2)
            {
                store.Checkpoint(); // Ctrl+Z takes it back
                if (overtime is { } oi) store.SetOvertime(Key(_date), store.Overtime(Key(_date)).Where((_, k) => k != oi));
                else store.SetWorkHours(Key(_date), null, null);
                Render();
                return;
            }
            var kind = overtime is { } i ? $"ot:{i}:{(start ? "s" : "e")}" : start ? "workStart" : "workEnd";
            _drag = new Drag(kind, null, pill, e.GetPosition(_canvas).Y, at, at);
            _canvas.CaptureMouse();
        };
        Panel.SetZIndex(pill, 10); // above the task blocks: always grabbable
        pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); // right-aligned: the end shows the hours worked too
        _canvas.Children.Add(At(pill, Gutter + area - Math.Max(66, pill.DesiredSize.Width + 4), Y(at) - (start ? 0 : 16)));
    }

    void DrawBlock(Block b, double area)
    {
        double laneW = area / b.Lanes;
        double x = Gutter + b.Lane * laneW, y = Y(b.From), h = Math.Max(18, Y(b.To) - Y(b.From) - 2);
        var store = _host.Store();
        var card = new Border { Width = Math.Max(20, laneW - 4), Height = h, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), ClipToBounds = true };
        var stack = new StackPanel { Margin = new Thickness(8, 3, 6, 2) };
        var title = new TextBlock { FontSize = App.Settings.FontSize - 0.5, Foreground = Res("Fg"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = h > 40 ? TextWrapping.Wrap : TextWrapping.NoWrap };
        var sub = new TextBlock { FontSize = App.Settings.FontSize - 2.5, Foreground = Res("FgFaint"), TextTrimming = TextTrimming.CharacterEllipsis };
        string range = $"{b.From:hh\\:mm}–{(b.To >= TimeSpan.FromHours(24) ? "24:00" : b.To.ToString(@"hh\:mm"))}";
        var grid = new Grid();
        grid.Children.Add(stack);
        card.Child = grid;

        switch (b.Source)
        {
            case Alarm a:
                var col = a.IsMeeting ? Violet : Amber;
                card.Background = B(A(col, 0x2A));
                card.BorderBrush = B(A(col, 0x70));
                title.Text = (a.IsMeeting ? "" : "⏰ ") + (a.Text.Length > 0 ? a.Text : a.IsMeeting ? L.T("Spotkanie") : L.T("Alarm"));
                sub.Text = a.IsMeeting ? range + "  ·  " + BoardWindow.Hours((b.To - b.From).TotalHours) : a.Time;
                card.Cursor = Cursors.Hand;
                card.ToolTip = $"{title.Text}\n{sub.Text}" + (a.Once ? "" : "\n" + a.RepeatLabel) + "\n" + L.T("Kliknij, żeby zmienić");
                card.MouseLeftButtonUp += (_, e) => { e.Handled = true; _host.OpenAlarm(a, _date); };
                break;
            case CalEvent ev:
                card.Background = B(A(Violet, 0x22));
                card.BorderBrush = B(A(Violet, 0x60));
                title.Text = ev.Title;
                sub.Text = range + (ev.Location != null ? "  ·  " + ev.Location : "");
                card.ToolTip = $"{ev.Title}\n{ev.TimeText}" + (ev.Location != null ? $"\n{ev.Location}" : "") + $"\n{ev.Feed}";
                break;
            case TaskItem t:
                var accent = ((SolidColorBrush)Res("AccentBrush")).Color;
                card.Background = B(t.IsStarted ? A(accent, 0x2C) : Th.Ink(0x1C));
                card.BorderBrush = B(Th.Ink(0x1A));
                card.Opacity = t.Done ? 0.55 : 1;
                // the same marks as everywhere: the state bar on the left (click: next state), the project's on the right
                var mark = TaskMarks.Status(t, TaskMarks.BarWidth, () => _host.CycleState(t), gap: 0);
                mark.Margin = new Thickness(3, 3, 0, 3);
                grid.Children.Add(mark);
                if (TaskMarks.Right(t) is { } proj) { proj.Margin = new Thickness(0, 3, 3, 3); grid.Children.Add(proj); }
                stack.Margin = new Thickness(8 + TaskMarks.MarkWidth(), 3, 8, 2); // after the mark, a bar or a wider circle
                var tags = TaskItem.Tags(t.Text, out var rest);
                // like a task row: the hours in the accent before the name, the categories as chips on the second line
                title.Inlines.Add(new Run(range + "  ") { FontWeight = FontWeights.SemiBold, Foreground = Res("FgDim") }); // a plan, not an appointment: quieter than a meeting's hours
                var name = new Run(rest.Length > 0 ? rest : t.Text);
                if (t.Done) name.TextDecorations = TextDecorations.Strikethrough;
                title.Inlines.Add(name);
                stack.Children.Add(title);
                double mfs = Math.Max(8, App.Settings.FontSize - 3.5);
                var meta = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
                foreach (var tag in tags) meta.Children.Add(BoardWindow.TagChip(tag, mfs));
                meta.Children.Add(new TextBlock { Text = BoardWindow.Hours((b.To - b.From).TotalHours), FontSize = Math.Max(9, App.Settings.FontSize - 2.5), Foreground = Res("FgFaint"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
                if (t.IsTracking || t.TrackedMinutes > 0)
                    meta.Children.Add(new TextBlock { Text = (t.IsTracking ? "⏵ " : "⏱ ") + BoardWindow.Duration(t.TrackedNow), FontSize = mfs + 0.5, VerticalAlignment = VerticalAlignment.Center,
                        Foreground = t.IsTracking ? Res("AccentBrush") : Res("FgFaint"), FontWeight = t.IsTracking ? FontWeights.Bold : FontWeights.Normal });
                if (h >= 34) stack.Children.Add(meta);
                card.ToolTip = (t.NotionSource != null ? t.NotionSource + " · " : "") + t.Text + "\n" + range + "  ·  " + TaskMarks.StateName(t)
                    + "\n" + L.T("Przeciągnij: zmień godzinę · górna / dolna krawędź: początek / czas trwania (= estymacja) · prawy przycisk: menu");
                card.Cursor = Cursors.SizeAll;
                // both edges: the upper one moves the start, the lower one the end (the length becomes the estimate)
                var (top, bottom) = Grips(card, grid);
                card.MouseLeftButtonDown += (_, e) =>
                {
                    if (e.ClickCount > 1) return;
                    e.Handled = true;
                    _drag = new Drag(EdgeOf(e, top, bottom), t, card, e.GetPosition(_canvas).Y, b.From, b.To);
                    _canvas.CaptureMouse();
                };
                card.MouseRightButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    var menu = _host.Menu(t, card);
                    menu.PlacementTarget = card;
                    _menuOpen = true;
                    menu.Closed += (_, _) => _menuOpen = false;
                    menu.IsOpen = true;
                };
                break;
            case BreakRef br:
                // a break in the work rather than a task: AFK grey and striped, lunch green, private sand
                var kindColor = BreakColor(br.B.Kind);
                card.Background = br.B.Kind == "afk" ? AfkFill() : B(A(kindColor, 0x26));
                card.BorderBrush = br.B.Kind == "afk" ? B(Th.Ink(0x40)) : B(A(kindColor, 0x70));
                card.BorderThickness = new Thickness(1);
                // a short break: one line in the middle ("AFK  11:00–11:30"); a longer one: its length under it
                title.Inlines.Add(new Run(BreakName(br.B.Kind)) { FontWeight = FontWeights.Bold });
                if (h < 34) { title.Inlines.Add(new Run("   " + range) { FontSize = App.Settings.FontSize - 2.5 }); stack.Margin = new Thickness(8, 0, 6, 0); stack.VerticalAlignment = VerticalAlignment.Center; }
                title.Foreground = br.B.Kind == "afk" ? Res("FgDim") : Tx(kindColor);
                sub.Text = range + "  ·  " + BoardWindow.Hours((b.To - b.From).TotalHours);
                card.ToolTip = (br.B.Kind == "afk" ? L.F("AFK – przerwa od komputera {0}", range) : $"{BreakName(br.B.Kind)} {range}")
                    + "\n" + L.T("Przeciągnij: przesuń · krawędzie: początek / koniec · prawy przycisk: rodzaj, usuń") + "\n" + L.T("Nie liczy się do czasu pracy");
                card.Cursor = Cursors.SizeAll;
                var (breakTop, breakBottom) = Grips(card, grid);
                card.MouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    if (e.ClickCount > 1) return;
                    _drag = new Drag(EdgeOf(e, breakTop, breakBottom), null, card, e.GetPosition(_canvas).Y, b.From, b.To, br.Index);
                    _canvas.CaptureMouse();
                };
                card.MouseRightButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    var menu = new ContextMenu { PlacementTarget = card };
                    void Change(Func<List<Break>, IEnumerable<Break>> how)
                    {
                        store.Checkpoint();
                        store.SetBreaks(Key(_date), how(store.Breaks(Key(_date))));
                        Render();
                    }
                    // another kind: AFK ↔ lunch ↔ private
                    foreach (var kind in Break.Kinds.Where(k => k != br.B.Kind))
                    {
                        var to = new MenuItem { Header = L.F("Zmień na: {0}", BreakName(kind)) };
                        to.Click += (_, _) => Change(list => list.Select((x, k) => k == br.Index ? x with { Kind = kind } : x));
                        menu.Items.Add(to);
                    }
                    menu.Items.Add(new Separator());
                    var del = new MenuItem { Header = L.T("Usuń tę przerwę") };
                    del.Click += (_, _) => Change(list => list.Where((_, k) => k != br.Index));
                    menu.Items.Add(del);
                    ResetBreaksItem(menu);
                    _menuOpen = true;
                    menu.Closed += (_, _) => _menuOpen = false;
                    menu.IsOpen = true;
                };
                break;
        }
        // the block picked (a click, or ↑ ↓): a frame in the accent – Alt+↑ ↓ moves it, Alt+Shift+↑ ↓ changes its length
        if (SelKey(b.Source) is { } sk && sk == _selKey)
        {
            card.BorderBrush = Res("AccentBrush");
            card.BorderThickness = new Thickness(1.6);
        }
        if (b.Source is not TaskItem)
        {
            stack.Children.Add(title);
            if (h >= 34) stack.Children.Add(sub);
        }
        else if (h < 34) sub.Visibility = Visibility.Collapsed;
        _canvas.Children.Add(At(card, x + 2, y + 1));
        if (_enter) Anim.Enter(card, 0, 6, 260, Math.Min(400, _enterIndex++ * 30)); // a new day: its blocks come in one after another
    }

    /// <summary>
    /// A block's edges to drag (a strip at the top and the bottom). Over the block a short bar shows on each edge; over
    /// an edge itself its bar and a line along it light up in the accent, so it's clear which edge will move.
    /// </summary>
    (Border Top, Border Bottom) Grips(Border card, Grid grid)
    {
        var accent = ((SolidColorBrush)Res("AccentBrush")).Color;
        var bars = new List<Border>();
        Border Edge(VerticalAlignment at)
        {
            var bar = new Border
            {
                Width = 26, Height = 3, CornerRadius = new CornerRadius(1.5), Background = B(Th.Ink(0x90)), Opacity = 0, IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = at, Margin = new Thickness(0, 2, 0, 2),
            };
            var line = new Border { Height = 2, VerticalAlignment = at, Background = B(accent), Opacity = 0, IsHitTestVisible = false };
            var edge = new Border
            {
                Height = Math.Clamp(card.Height / 3, 4, 8), VerticalAlignment = at, Background = Brushes.Transparent, Cursor = Cursors.SizeNS,
                Child = new Grid { Children = { line, bar } },
            };
            edge.MouseEnter += (_, _) => { bar.Opacity = 1; bar.Background = B(accent); line.Opacity = 1; };
            edge.MouseLeave += (_, _) => { bar.Opacity = card.IsMouseOver ? 0.7 : 0; bar.Background = B(Th.Ink(0x90)); line.Opacity = 0; };
            bars.Add(bar);
            grid.Children.Add(edge);
            return edge;
        }
        var top = Edge(VerticalAlignment.Top);
        var bottom = Edge(VerticalAlignment.Bottom);
        card.MouseEnter += (_, _) => { foreach (var b in bars) if (b.Opacity < 1) b.Opacity = 0.7; };
        card.MouseLeave += (_, _) => { if (_drag == null) foreach (var b in bars) b.Opacity = 0; };
        return (top, bottom);
    }

    /// <summary>Which part of a block the mouse went down on: its upper edge, its lower edge, or the block (move).</summary>
    static string EdgeOf(MouseButtonEventArgs e, Border top, Border bottom)
    {
        var src = e.OriginalSource as DependencyObject;
        return IsInside(src, top) ? "resizeTop" : IsInside(src, bottom) ? "resize" : "move";
    }

    /// <summary>An AFK break's fill: grey stripes on a dim card (one frozen brush per theme).</summary>
    static Brush AfkFill()
    {
        if (_afk != null && _afkLight == Th.Light) return _afk;
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Ui.Brush(Th.Ink(0x10)), null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        group.Children.Add(new GeometryDrawing(null, new Pen(Ui.Brush(Th.Ink(0x24)), 1.4), Geometry.Parse("M0,8 L8,0 M-2,2 L2,-2 M6,10 L10,6")));
        var b = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 8, 8), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        b.Freeze();
        _afkLight = Th.Light;
        return _afk = b;
    }

    static Brush? _afk;
    static bool _afkLight;

    /// <summary>The day's tasks without an hour, as small chips: drag one onto the line to give it its time.</summary>
    void RenderLoose()
    {
        _loose.Children.Clear();
        var loose = _host.TasksOf(_date).Where(t => Hm(t.Time) == null).ToList();
        // also with no chip in it while blocks have hours: a block dropped on it loses its hours
        bool timed = _host.TasksOf(_date).Any(t => Hm(t.Time) != null);
        _looseBox.Visibility = loose.Count > 0 || timed ? Visibility.Visible : Visibility.Collapsed;
        _looseHead.Text = L.Up(L.T("Bez godziny")) + "  ·  " + (loose.Count > 0 ? L.T("przeciągnij na oś, żeby zaplanować") : L.T("upuść tu blok z osi, żeby zdjąć mu godziny"));
        _looseBox.Background = B(Th.Ink(0x0A));
        foreach (var t in loose)
        {
            TaskItem.Tags(t.Text, out var rest);
            var circle = TaskMarks.Status(t, TaskMarks.BarWidth, () => _host.CycleState(t), gap: 6);
            var name = new TextBlock { Text = rest.Length > 0 ? rest : t.Text, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Res("Fg"), FontSize = App.Settings.FontSize - 1 };
            if (t.Done) name.TextDecorations = TextDecorations.Strikethrough;
            var est = new TextBlock { Text = "  " + BoardWindow.Hours(Math.Clamp(t.Estimate ?? 0.5, 0.25, 8)), FontSize = App.Settings.FontSize - 2.5, Foreground = Res("FgFaint"), VerticalAlignment = VerticalAlignment.Center };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(circle);
            row.Children.Add(name);
            row.Children.Add(est);
            if (TaskMarks.Right(t) is { } proj) { proj.Margin = new Thickness(7, 1, 0, 1); proj.HorizontalAlignment = HorizontalAlignment.Left; row.Children.Add(proj); }
            var chip = new Border
            {
                Child = row, Margin = new Thickness(0, 0, 6, 5), Padding = new Thickness(5, 2, 5, 2), CornerRadius = new CornerRadius(4), Cursor = Cursors.SizeAll,
                Background = B(Th.Ink(0x16)), Opacity = t.Done ? 0.55 : 1,
                ToolTip = t.Text + "\n" + L.T("Przeciągnij na oś dnia, żeby ustawić godzinę · prawy przycisk: menu"),
            };
            ToolTipService.SetInitialShowDelay(chip, 250);
            Point? down = null;
            chip.MouseLeftButtonDown += (_, e) => down = e.GetPosition(chip);
            chip.MouseLeftButtonUp += (_, _) => down = null;
            chip.MouseMove += (_, e) =>
            {
                if (down is not { } p || e.LeftButton != MouseButtonState.Pressed) return;
                var at = e.GetPosition(chip);
                if (Math.Abs(at.X - p.X) < 4 && Math.Abs(at.Y - p.Y) < 4) return;
                down = null;
                DragDrop.DoDragDrop(chip, new DataObject("TaskWallTask", t.Id), DragDropEffects.Move);
            };
            chip.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                var menu = _host.Menu(t, chip);
                menu.PlacementTarget = chip;
                _menuOpen = true;
                menu.Closed += (_, _) => _menuOpen = false;
                menu.IsOpen = true;
            };
            _loose.Children.Add(chip);
        }
    }

    static bool IsInside(DependencyObject? el, DependencyObject parent)
    {
        for (; el != null; el = VisualTreeHelper.GetParent(el)) if (ReferenceEquals(el, parent)) return true;
        return false;
    }

    // ---------- dragging: a block (time / length) or a working-hours edge ----------

    /// <summary>What is being dragged; <paramref name="Afk"/>: the index of a break (AFK, lunch, private) (-1: a task or an edge).</summary>
    sealed record Drag(string Kind, TaskItem? Task, FrameworkElement El, double StartY, TimeSpan From, TimeSpan To, int Afk = -1);

    /// <summary>A break on the day's line: its place in the day's list and what it is.</summary>
    sealed record BreakRef(int Index, Break B);

    bool OverStrip(MouseEventArgs e)
    {
        if (!_looseBox.IsVisible) return false;
        var p = e.GetPosition(_looseBox);
        return p.X >= 0 && p.Y >= 0 && p.X <= _looseBox.ActualWidth && p.Y <= _looseBox.ActualHeight;
    }

    bool _stripHot;
    void StripHot(bool on)
    {
        if (on == _stripHot) return;
        _stripHot = on;
        _looseBox.Background = B(on ? Th.Ink(0x22) : Th.Ink(0x0A));
        _looseBox.BorderBrush = on ? Res("AccentBrush") : null;
        _looseBox.BorderThickness = new Thickness(on ? 1 : 0);
    }
    Drag? _drag;

    void OnMove(object sender, MouseEventArgs e)
    {
        if (_drag is not { } d || e.LeftButton != MouseButtonState.Pressed) return;
        double dy = e.GetPosition(_canvas).Y - d.StartY;
        switch (d.Kind)
        {
            case "move" or "resize" or "resizeTop":
                // a block (moved, or one of its edges): where it would land – a quarter hour or the edge of a block next to it
                var (from, to) = Target(d, dy);
                double shift = Y(from) - Y(d.From);
                d.El.RenderTransform = shift != 0 ? new TranslateTransform(0, shift) : null;
                d.El.Height = Math.Max(HourH / 4 - 2, Y(to) - Y(from) - 2);
                if (d.Kind == "move" && d.Task != null) StripHot(OverStrip(e)); // back onto the "Bez godziny" strip: lit while it would land there
                break;
            default: d.El.RenderTransform = new TranslateTransform(0, Math.Round(dy / HourH * 4) / 4 * HourH); break; // the working hours' edges: quarter hours
        }
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag is not { } d) return;
        _drag = null;
        _canvas.ReleaseMouseCapture();
        StripHot(false);
        if (d.Kind == "move" && d.Task != null && OverStrip(e)) { _edges = null; _host.ClearTime(d.Task); return; } // no hours any more: a chip again
        double dy = e.GetPosition(_canvas).Y - d.StartY;
        var store = _host.Store();
        if (d.Kind is "move" or "resize" or "resizeTop")
        {
            var (from, to) = Math.Abs(dy) < 4 ? (d.From, d.To) : Target(d, dy);
            _edges = null;
            // a click (or no change): it picks the block (the keys work on it)
            _selKey = d.Task != null ? SelKey(d.Task) : d.Afk >= 0 ? "b:" + d.Afk : _selKey;
            if (from == d.From && to == d.To) { d.El.RenderTransform = null; Render(); return; }
            // Alt held: the tasks after it stay where they are
            if (d.Task != null) Retime(d.Task, d.From, d.To, from, to, d.Kind != "move", push: !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
            else if (d.Afk >= 0) MoveBreak(d.Afk, from, to);
            return;
        }
        var delta = TimeSpan.FromMinutes(Math.Round(dy / HourH * 4) * 15);
        if (Math.Abs(dy) < 4 || delta == TimeSpan.Zero) { d.El.RenderTransform = null; Render(); return; } // a click (or no change), not a drag
        switch (d.Kind)
        {
            case "workStart":
            case "workEnd":
                var (ws, we) = WorkHours();
                var at = Clamp(d.From + delta, TimeSpan.Zero, TimeSpan.FromHours(24));
                bool? endSet = null;
                if (d.Kind == "workStart" && !store.EndSet(Key(_date)))
                {
                    // the start moved and the end wasn't set by hand: a whole working day from the new start (8 hours by default)
                    ws = Clamp(at, TimeSpan.Zero, TimeSpan.FromHours(23.5));
                    we = ws + DayLength() > TimeSpan.FromHours(24) ? TimeSpan.FromHours(24) : ws + DayLength();
                    endSet = false;
                }
                else if (d.Kind == "workStart") ws = Clamp(at < we ? at : we - TimeSpan.FromMinutes(30), TimeSpan.Zero, TimeSpan.FromHours(23.5));
                else { we = Clamp(at > ws ? at : ws + TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30), TimeSpan.FromHours(24)); endSet = true; } // set by hand: it stays
                // midnight is kept as 23:59 ("24:00" isn't a time of day)
                store.Checkpoint();
                store.SetWorkHours(Key(_date), ws.ToString(@"hh\:mm"), we >= TimeSpan.FromHours(24) ? "23:59" : we.ToString(@"hh\:mm"), endSet);
                Render();
                break;
            case { } k when k.StartsWith("ot:"):
                // an overtime period's edge: at least a quarter of an hour long, within the day
                var parts = k.Split(':');
                var periods = store.Overtime(Key(_date));
                if (int.TryParse(parts[1], out var oi) && oi < periods.Count)
                {
                    var (pa, pb) = periods[oi];
                    var to2 = Clamp(d.From + delta, TimeSpan.Zero, TimeSpan.FromHours(24));
                    if (parts[2] == "s") pa = to2 < pb - TimeSpan.FromMinutes(15) ? to2 : pb - TimeSpan.FromMinutes(15);
                    else pb = to2 > pa + TimeSpan.FromMinutes(15) ? to2 : pa + TimeSpan.FromMinutes(15);
                    periods[oi] = (pa, pb);
                    store.Checkpoint();
                    store.SetOvertime(Key(_date), periods);
                }
                Render();
                break;
        }
    }

    // ---------- where blocks land: quarter hours, the edges next to them, the tasks after them ----------

    /// <summary>
    /// Where a dragged block lands: to the quarter hour, or onto the edge of another block or of the working hours when
    /// within 7 minutes of it (after a meeting ending 10:50 the task starts at 10:50).
    /// </summary>
    (TimeSpan From, TimeSpan To) Target(Drag d, double dy)
    {
        var raw = TimeSpan.FromMinutes(dy / HourH * 60);
        var len = d.To - d.From;
        var day = TimeSpan.FromHours(24);
        var min = TimeSpan.FromMinutes(15);
        switch (d.Kind)
        {
            case "move":
                var (f, onEdge) = Place(d.From + raw, d);
                if (!onEdge && Place(d.To + raw, d) is (var end, true)) f = end - len; // or its end onto an edge
                f = Clamp(f, TimeSpan.Zero, day - len);
                return (f, f + len);
            case "resize": return (d.From, Clamp(Place(d.To + raw, d).At, d.From + min, day));
            case "resizeTop": return (Clamp(Place(d.From + raw, d).At, TimeSpan.Zero, d.To - min), d.To);
            default: return (d.From, d.To);
        }
    }

    (TimeSpan At, bool OnEdge) Place(TimeSpan t, Drag d)
    {
        var near = Edges(d).Select(x => (At: x, Off: Math.Abs((x - t).TotalMinutes))).Where(x => x.Off <= 7).OrderBy(x => x.Off).ToList();
        return near.Count > 0 ? (near[0].At, true) : (TimeSpan.FromMinutes(Math.Round(t.TotalMinutes / 15) * 15), false);
    }

    /// <summary>What a dragged block snaps to: the other blocks' starts and ends and the working hours' (once per drag).</summary>
    List<TimeSpan> Edges(Drag d)
    {
        if (_edges != null) return _edges;
        var list = new List<TimeSpan>();
        foreach (var b in Blocks())
        {
            if (d.Task != null && b.Source is TaskItem t && t.Id == d.Task.Id) continue;
            if (d.Afk >= 0 && b.Source is BreakRef r && r.Index == d.Afk) continue;
            list.Add(b.From);
            list.Add(b.To);
        }
        if (HasRegularHours()) { var (ws, we) = WorkHours(); list.Add(ws); list.Add(we); }
        return _edges = list;
    }

    List<TimeSpan>? _edges;

    /// <summary>
    /// A task's new hours. With <paramref name="push"/> the tasks after it that it now runs into move on, one after
    /// another, as far as needed (meetings and breaks stay where they are) – all of it one Ctrl+Z.
    /// </summary>
    void Retime(TaskItem task, TimeSpan oldFrom, TimeSpan oldTo, TimeSpan from, TimeSpan to, bool length, bool push = true)
    {
        var changes = new List<(TaskItem, TimeSpan, TimeSpan, bool)> { (task, from, to, length) };
        if (push)
        {
            // after it: what started at or after its end (stretched), or at or after its new start (moved)
            var after = length ? oldTo : from;
            var cursor = to;
            foreach (var b in Blocks().Where(b => b.Source is TaskItem t && t.Id != task.Id && b.From >= after).OrderBy(b => b.From))
            {
                if (b.From >= cursor) break; // a gap: the rest is free
                var nt = cursor + (b.To - b.From);
                if (nt > TimeSpan.FromHours(24)) break; // no room left in the day
                changes.Add(((TaskItem)b.Source, cursor, nt, false));
                cursor = nt;
            }
        }
        _selKey = SelKey(task);
        _host.SetTimes(changes);
    }

    /// <summary>A break moved or stretched (it stays picked).</summary>
    void MoveBreak(int index, TimeSpan from, TimeSpan to)
    {
        var store = _host.Store();
        var breaks = store.Breaks(Key(_date));
        if (index >= breaks.Count) { Render(); return; }
        var moved = breaks[index] with { Start = from, End = to };
        breaks[index] = moved;
        store.Checkpoint();
        store.SetBreaks(Key(_date), breaks);
        int now = store.Breaks(Key(_date)).FindIndex(b => b.Kind == moved.Kind && b.Start <= moved.Start && b.End >= moved.End);
        _selKey = now >= 0 ? "b:" + now : null;
        Render();
    }

    // ---------- the keys: up / down pick a block, Alt moves it, Alt+Shift changes its length ----------

    string? _selKey;

    static string? SelKey(object source) => source switch { TaskItem t => "t:" + t.Id, BreakRef r => "b:" + r.Index, _ => null };

    /// <summary>
    /// The day view's keys (the board passes them on): up / down pick the previous / next block (a task or a break),
    /// Alt+up / down move it by a quarter of an hour (the tasks after it make room), Alt+Shift+up / down make it a
    /// quarter shorter / longer, Esc lets it go. False: not a key for the day view.
    /// </summary>
    public bool HandleKey(KeyEventArgs e)
    {
        var key = e.Key == WKey.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (key == WKey.Escape && _selKey != null) { _selKey = null; Render(); return true; }
        if (key is not (WKey.Up or WKey.Down) || mods.HasFlag(ModifierKeys.Control)) return false;
        var blocks = Blocks().Where(b => b.Source is TaskItem or BreakRef).OrderBy(b => b.From).ThenBy(b => b.Lane).ToList();
        if (blocks.Count == 0) return false;
        int i = blocks.FindIndex(b => SelKey(b.Source) == _selKey);
        int dir = key == WKey.Up ? -1 : 1;
        if (mods == ModifierKeys.None || i < 0)
        {
            var next = blocks[i < 0 ? (dir > 0 ? 0 : blocks.Count - 1) : Math.Clamp(i + dir, 0, blocks.Count - 1)];
            _selKey = SelKey(next.Source);
            Render();
            double y = Y(next.From);
            if (y < _scroll.VerticalOffset || y > _scroll.VerticalOffset + _scroll.ViewportHeight - HourH) _scroll.ScrollToVerticalOffset(Math.Max(0, y - HourH));
            return true;
        }
        var b = blocks[i];
        var q = TimeSpan.FromMinutes(15 * dir);
        var day = TimeSpan.FromHours(24);
        TimeSpan from, to;
        bool length;
        if (mods == ModifierKeys.Alt) { from = Clamp(b.From + q, TimeSpan.Zero, day - (b.To - b.From)); to = from + (b.To - b.From); length = false; }
        else if (mods == (ModifierKeys.Alt | ModifierKeys.Shift)) { from = b.From; to = Clamp(b.To + q, b.From + TimeSpan.FromMinutes(15), day); length = true; }
        else return false;
        if (from == b.From && to == b.To) return true;
        if (b.Source is TaskItem t) Retime(t, b.From, b.To, from, to, length);
        else if (b.Source is BreakRef r) MoveBreak(r.Index, from, to);
        return true;
    }

    /// <summary>A break's colour: AFK grey, lunch green, private sand.</summary>
    static Color BreakColor(string kind) => kind switch
    {
        "lunch" => Color.FromRgb(0x5F, 0xB8, 0x7A), "private" => Color.FromRgb(0xC9, 0xA2, 0x6B), _ => Color.FromRgb(0x9A, 0xA1, 0xB2),
    };

    void CancelDrag()
    {
        if (_drag is { } d) { d.El.RenderTransform = null; _drag = null; _edges = null; Render(); }
    }

    static TimeSpan Clamp(TimeSpan t, TimeSpan min, TimeSpan max) => t < min ? min : t > max ? max : t;

    void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("TaskWallTask") is not string id) return;
        e.Handled = true;
        var at = T(e.GetPosition(_canvas).Y);
        // already on this day (a chip from the strip above): just give it the hour
        if (_host.TasksOf(_date).FirstOrDefault(t => t.Id == id) is { } own)
        {
            var dur = TimeSpan.FromHours(Math.Clamp(own.Estimate ?? 0.5, 0.25, 8));
            if (at + dur > TimeSpan.FromHours(24)) at = TimeSpan.FromHours(24) - dur;
            _host.SetTimes(new[] { (own, at, at + dur, false) });
            return;
        }
        // from another day or the backlog: never past the end of the day (24:00 would be read as 00:00)
        if (_host.Store().Find(id) is { } other)
        {
            var len = TimeSpan.FromHours(Math.Clamp(other.Estimate ?? 0.5, 0.25, 8));
            at = Clamp(at, TimeSpan.Zero, TimeSpan.FromTicks(Math.Min((TimeSpan.FromHours(24) - len).Ticks, TimeSpan.FromHours(23.75).Ticks)));
        }
        _host.DropTask(id, _date, at);
    }
}
