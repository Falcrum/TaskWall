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

namespace TaskWall;

/// <summary>What the day view needs from the board (its menus, state changes and the add row).</summary>
public sealed record DayViewHost(
    Func<BoardStore> Store,
    Func<DateTime, List<TaskItem>> TasksOf,
    Func<TaskItem, FrameworkElement, ContextMenu> Menu,
    Action<TaskItem> CycleState,
    Action<TaskItem, TimeSpan, TimeSpan, bool> SetTime, // the last: the length changed (it becomes the estimate)
    Action<string, DateTime, TimeSpan> DropTask,
    Func<string, DateTime, FrameworkElement> AddRow,
    Action<Alarm, DateTime> OpenAlarm);

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
        var looseHead = new TextBlock { Text = L.Up(L.T("Bez godziny")) + "  ·  " + L.T("przeciągnij na oś, żeby zaplanować"), FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = Res("FgFaint") };
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
        if (newDay) { _scrolled = false; _enter = true; _enterIndex = 0; }
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
        if (regular) _canvas.Children.Add(At(new Rectangle { Width = area + 4, Height = Y(we) - Y(ws), Fill = B(Th.Ink(0x06)) }, Gutter - 2, Y(ws)));
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
                del.Click += (_, _) => { store.SetOvertime(Key(_date), store.Overtime(Key(_date)).Where((_, k) => k != index)); Render(); };
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
                add.Click += (_, _) => { store.SetOvertime(Key(_date), store.Overtime(Key(_date)).Append((at, end))); Render(); };
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

    void WorkHandle(TimeSpan at, bool start, double area, int? overtime = null)
    {
        var pill = new Border
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 1), Background = B(Th.Ink(0x26)), Cursor = Cursors.SizeNS,
            ToolTip = overtime != null
                ? (start ? L.T("Początek nadgodzin") : L.T("Koniec nadgodzin")) + "\n" + L.T("Przeciągnij, żeby zmienić · dwuklik: usuń te nadgodziny")
                : (start ? L.T("Początek pracy") : L.T("Koniec pracy")) + "\n" + L.T("Przeciągnij, żeby zmienić dla tego dnia · dwuklik: domyślne godziny"),
            Child = new TextBlock { Text = (start ? "▸ " : "◂ ") + Hhmm(at), FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = Res("FgDim") },
        };
        pill.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            var store = _host.Store();
            if (e.ClickCount == 2)
            {
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
        _canvas.Children.Add(At(pill, Gutter + area - 70, Y(at) - (start ? 0 : 16)));
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
                stack.Margin = new Thickness(12, 3, 8, 2);
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
                    + "\n" + L.T("Przeciągnij: zmień godzinę · dolna krawędź: czas trwania (= estymacja) · prawy przycisk: menu");
                card.Cursor = Cursors.SizeAll;
                // the lower edge: how long
                var grip = new Border { Height = 7, VerticalAlignment = VerticalAlignment.Bottom, Background = Brushes.Transparent, Cursor = Cursors.SizeNS };
                grid.Children.Add(grip);
                card.MouseLeftButtonDown += (_, e) =>
                {
                    if (e.ClickCount > 1) return;
                    e.Handled = true;
                    bool resize = ReferenceEquals(e.OriginalSource, grip) || IsInside(e.OriginalSource as DependencyObject, grip);
                    _drag = new Drag(resize ? "resize" : "move", t, card, e.GetPosition(_canvas).Y, b.From, b.To);
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

    /// <summary>The day's tasks without an hour, as small chips: drag one onto the line to give it its time.</summary>
    void RenderLoose()
    {
        _loose.Children.Clear();
        var loose = _host.TasksOf(_date).Where(t => Hm(t.Time) == null).ToList();
        _looseBox.Visibility = loose.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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

    sealed record Drag(string Kind, TaskItem? Task, FrameworkElement El, double StartY, TimeSpan From, TimeSpan To);
    Drag? _drag;

    void OnMove(object sender, MouseEventArgs e)
    {
        if (_drag is not { } d || e.LeftButton != MouseButtonState.Pressed) return;
        double dy = e.GetPosition(_canvas).Y - d.StartY;
        double snap = Math.Round(dy / HourH * 4) / 4 * HourH; // quarter hours
        switch (d.Kind)
        {
            case "move": d.El.RenderTransform = new TranslateTransform(0, snap); break;
            case "resize": d.El.Height = Math.Max(HourH / 4 - 2, Y(d.To) - Y(d.From) - 2 + snap); break;
            default: d.El.RenderTransform = new TranslateTransform(0, snap); break;
        }
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag is not { } d) return;
        _drag = null;
        _canvas.ReleaseMouseCapture();
        double dy = e.GetPosition(_canvas).Y - d.StartY;
        var delta = TimeSpan.FromMinutes(Math.Round(dy / HourH * 4) * 15);
        if (Math.Abs(dy) < 4 || delta == TimeSpan.Zero) { d.El.RenderTransform = null; Render(); return; } // a click (or no change), not a drag
        var store = _host.Store();
        switch (d.Kind)
        {
            case "move" when d.Task != null:
                var from = Clamp(d.From + delta, TimeSpan.Zero, TimeSpan.FromHours(24) - (d.To - d.From));
                _host.SetTime(d.Task, from, from + (d.To - d.From), false); // moved: the estimate stays
                break;
            case "resize" when d.Task != null:
                var to = Clamp(d.To + delta, d.From + TimeSpan.FromMinutes(15), TimeSpan.FromHours(24));
                _host.SetTime(d.Task, d.From, to, true);
                break;
            case "workStart":
            case "workEnd":
                var (ws, we) = WorkHours();
                var at = Clamp(d.From + delta, TimeSpan.Zero, TimeSpan.FromHours(24));
                if (d.Kind == "workStart") ws = Clamp(at < we ? at : we - TimeSpan.FromMinutes(30), TimeSpan.Zero, TimeSpan.FromHours(23.5));
                else we = Clamp(at > ws ? at : ws + TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30), TimeSpan.FromHours(24));
                // midnight is kept as 23:59 ("24:00" isn't a time of day)
                store.SetWorkHours(Key(_date), ws.ToString(@"hh\:mm"), we >= TimeSpan.FromHours(24) ? "23:59" : we.ToString(@"hh\:mm"));
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
                    store.SetOvertime(Key(_date), periods);
                }
                Render();
                break;
        }
    }

    void CancelDrag()
    {
        if (_drag is { } d) { d.El.RenderTransform = null; _drag = null; Render(); }
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
            _host.SetTime(own, at, at + dur, false);
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
