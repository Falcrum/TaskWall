using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TaskWall;

/// <summary>What the timeline shows and how (kept in the settings, shared between computers).</summary>
public sealed class TimelineSettings
{
    /// <summary>Width of one day in px (zoom): 8 … 160.</summary>
    public double DayWidth { get; set; } = 38;
    /// <summary>"none", "project", "category", "priority", "state".</summary>
    public string Group { get; set; } = "project";
    /// <summary>"start", "priority", "name", "added", "length".</summary>
    public string Sort { get; set; } = "start";
    public string? Project { get; set; }
    public string? Category { get; set; }
    public string? Priority { get; set; }
    /// <summary>"all", "open", "started", "done".</summary>
    public string State { get; set; } = "all";
}

/// <summary>
/// Timeline: the same tasks as the other views on an endless horizontal line of days. A task is a bar over the days it
/// was worked on and its current day; its planned days (Potrwa do… / estimate over 8 h) continue it dashed, like the
/// planned days elsewhere. Weekends and public holidays are hatched at 45°. Drag (or Shift + wheel) to move along,
/// Ctrl + wheel to zoom, wheel to scroll the rows. Filters, sorting and grouping in its own bar.
/// </summary>
public sealed class TimelineView : Grid
{
    readonly Func<BoardStore> _store;
    readonly Func<TaskItem, FrameworkElement, ContextMenu> _menu;
    readonly Action<DateTime> _open;
    readonly Action<TaskItem, int> _moveBy;
    readonly Action<TaskItem, DateTime> _setEnd;
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMinutes(1) };
    readonly Canvas _header = new() { ClipToBounds = true, Height = 62, Background = Brushes.Transparent };
    readonly Canvas _days = new() { ClipToBounds = true, IsHitTestVisible = false };
    readonly Canvas _bars = new() { Background = Brushes.Transparent };
    readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly Canvas _overlay = new() { IsHitTestVisible = false, ClipToBounds = true };
    readonly WrapPanel _toolbar = new() { Margin = new Thickness(2, 0, 0, 6) };
    readonly DispatcherTimer _renderSoon = new() { Interval = TimeSpan.FromMilliseconds(15) };

    /// <summary>Left edge of the view, in days from <see cref="Origin"/> (fractional while dragging).</summary>
    double _left;
    static readonly DateTime Origin = new(2000, 1, 3); // a Monday
    List<Item> _items = new();
    double _contentHeight;
    Point? _dragFrom;
    double _dragLeft;
    bool _dragged;

    sealed record Item(TaskItem Task, DateTime Start, DateTime Current, DateTime End, int Row, string Group, int Workdays);

    static TimelineSettings S => App.Settings.Timeline;
    static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    static SolidColorBrush B(Color c) => new(c);
    static Color A(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
    static SolidColorBrush Tx(Color c) => new(Th.Light ? Color.FromArgb(c.A, (byte)(c.R * 0.6), (byte)(c.G * 0.6), (byte)(c.B * 0.6)) : c);
    static bool DayOff(DateTime d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || PolishHolidays.DayOff(d) != null;
    static DateTime Key(string k) => DateTime.ParseExact(k, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    double W => Math.Clamp(S.DayWidth, 8, 160);
    double RowHeight => Math.Round(App.Settings.FontSize * 2.5 + 6);

    public TimelineView(Func<BoardStore> store, Func<TaskItem, FrameworkElement, ContextMenu> menu, Action<DateTime> open, Action<TaskItem, int> moveBy, Action<TaskItem, DateTime> setEnd)
    {
        _store = store;
        _menu = menu;
        _open = open;
        _moveBy = moveBy;
        _setEnd = setEnd;
        // the "now" line follows the clock
        _clock.Tick += (_, _) => { if (IsVisible) Render(); };
        _clock.Start();
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        SetRow(_toolbar, 0);
        Children.Add(_toolbar);
        SetRow(_header, 1);
        Children.Add(_header);

        var body = new Grid { ClipToBounds = true };
        SetRow(body, 2);
        body.Children.Add(_days);           // day columns, hatched days off (not scrolled: they go the whole height)
        var host = new Grid();
        host.Children.Add(_bars);
        _scroll.Content = host;
        body.Children.Add(_scroll);
        body.Children.Add(_overlay);        // the "today" line on top
        Children.Add(body);

        _renderSoon.Tick += (_, _) => { _renderSoon.Stop(); Render(); };
        SizeChanged += (_, _) => Render();
        Loaded += (_, _) => { if (_items.Count == 0) Refresh(); };

        // move along: drag the empty area or the dates; zoom: Ctrl + wheel (around the cursor), Shift + wheel: move
        _header.MouseLeftButtonDown += StartDrag;
        _bars.MouseLeftButtonDown += StartDrag;
        PreviewMouseMove += (_, e) =>
        {
            if (_barDrag is { } bd && e.LeftButton == MouseButtonState.Pressed)
            {
                double bdx = e.GetPosition(this).X - bd.StartX;
                double snapped = Math.Round(bdx / W) * W; // whole days
                if (bd.Resize) bd.Bar.Width = Math.Max(W * 0.8, bd.Width + snapped);
                else bd.Bar.RenderTransform = new TranslateTransform(snapped, -1);
                return;
            }
            if (_dragFrom is not { } from || e.LeftButton != MouseButtonState.Pressed) return;
            var dx = e.GetPosition(this).X - from.X;
            if (Math.Abs(dx) > 3) _dragged = true;
            _left = _dragLeft - dx / W;
            RenderSoon();
        };
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_barDrag is { } bd)
            {
                _barDrag = null;
                ReleaseMouseCapture();
                int days = (int)Math.Round((e.GetPosition(this).X - bd.StartX) / W);
                if (days == 0) { bd.Bar.RenderTransform = null; bd.Bar.Width = bd.Width; return; }
                var span = Span(bd.Task);
                if (bd.Resize) _setEnd(bd.Task, (span.End.AddDays(days) < span.Current ? span.Current : span.End.AddDays(days)));
                else _moveBy(bd.Task, days);
                return;
            }
            if (_dragFrom != null) { _dragFrom = null; ReleaseMouseCapture(); Cursor = null; }
        };
        PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                Zoom(e.Delta > 0 ? 1.18 : 1 / 1.18, e.GetPosition(this).X);
            }
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                _left -= e.Delta / 120.0 * Math.Max(1, 120 / W);
                RenderSoon();
            }
        };
        BuildToolbar();
        ScrollToToday();
    }

    void StartDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { Tag: TaskItem }) return;
        _dragFrom = e.GetPosition(this);
        _dragLeft = _left;
        _dragged = false;
        CaptureMouse();
        Cursor = Cursors.SizeWE;
        e.Handled = true;
    }

    void RenderSoon() { if (!_renderSoon.IsEnabled) _renderSoon.Start(); }

    // ---------- navigation (the top bar's ‹ › and Today, the keys) ----------

    public void ScrollToToday()
    {
        double visible = Math.Max(7, (ActualWidth > 0 ? ActualWidth : 1200) / W);
        _left = (DateTime.Today - Origin).TotalDays - visible * 0.25; // today a quarter in
        Render();
    }

    public void Step(int dir)
    {
        double visible = Math.Max(7, ActualWidth / W);
        _left += dir * Math.Max(7, Math.Round(visible / 2 / 7) * 7);
        Render();
    }

    public void Zoom(double factor, double? atX = null)
    {
        double x = atX ?? ActualWidth / 2;
        double dayAtX = _left + x / W;
        S.DayWidth = Math.Clamp(W * factor, 8, 160);
        _left = dayAtX - x / W;
        SettingsStore.Save(App.Settings);
        Refresh();
        App.Board?.UpdateTimelineRange();
    }

    /// <summary>First and last day on screen (the top bar shows them).</summary>
    public (DateTime From, DateTime To) Visible
    {
        get
        {
            var from = Origin.AddDays(Math.Floor(_left));
            return (from, from.AddDays(Math.Max(1, Math.Ceiling((ActualWidth > 0 ? ActualWidth : 1200) / W)) - 1));
        }
    }

    // ---------- data ----------

    /// <summary>Re-reads the tasks (after any change): filters, sorting, groups and rows.</summary>
    public void Refresh()
    {
        var store = _store();
        var all = store.Data.Tasks.Where(t => t.Day != null && !t.Archived && t.Text.Length > 0).Select(t => Span(t)).ToList();
        IEnumerable<(TaskItem T, DateTime Start, DateTime Current, DateTime End, int Workdays)> list = all;
        if (S.Project != null) list = list.Where(x => string.Equals(x.T.NotionSource, S.Project == "" ? null : S.Project, StringComparison.OrdinalIgnoreCase));
        if (S.Category != null) list = list.Where(x => TaskItem.Tags(x.T.Text, out _).Contains(S.Category));
        if (S.Priority != null) list = list.Where(x => S.Priority == "" ? string.IsNullOrEmpty(x.T.Priority) : string.Equals(x.T.Priority, S.Priority, StringComparison.OrdinalIgnoreCase));
        list = S.State switch
        {
            "open" => list.Where(x => !x.T.Done),
            "started" => list.Where(x => x.T.IsStarted),
            "done" => list.Where(x => x.T.Done),
            _ => list,
        };
        list = S.Sort switch
        {
            "priority" => list.OrderBy(x => Rank(x.T.Priority)).ThenBy(x => x.Start),
            "name" => list.OrderBy(x => Title(x.T), StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.Start),
            "added" => list.OrderBy(x => x.T.Created ?? DateTime.MinValue).ThenBy(x => x.Start).ThenBy(x => x.T.Order),
            "length" => list.OrderByDescending(x => (x.End - x.Start).TotalDays).ThenBy(x => x.Start),
            _ => list.OrderBy(x => x.Start).ThenBy(x => x.T.Order),
        };
        var sorted = list.ToList();

        // groups in a fixed order; inside each, rows packed: a task takes the first row free over all its days
        var groups = sorted.GroupBy(x => GroupOf(x.T)).OrderBy(g => GroupRank(g.Key)).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);
        var items = new List<Item>();
        int row = 0;
        foreach (var g in groups)
        {
            var ends = new List<DateTime>();
            foreach (var x in g)
            {
                var reach = x.End;
                int lane = ends.FindIndex(e => e < x.Start);
                if (lane < 0) { lane = ends.Count; ends.Add(reach); } else ends[lane] = reach;
                items.Add(new Item(x.T, x.Start, x.Current, x.End, row + lane, g.Key, x.Workdays));
            }
            row += Math.Max(1, ends.Count) + (S.Group == "none" ? 0 : 1); // a gap row under each group (its label sits in it)
        }
        _items = items;
        _contentHeight = (row + 1) * RowHeight;
        Render();
    }

    /// <summary>Its days: from the first day worked on, through its current day, to the planned end.</summary>
    static (TaskItem T, DateTime Start, DateTime Current, DateTime End, int Workdays) Span(TaskItem t)
    {
        var current = Key(t.Day!);
        var start = t.WorkedDays is { Count: > 0 } w ? new[] { current }.Concat(w.Select(Key)).Min() : current;
        var end = current;
        if (t.PlanEnd != null && DateTime.TryParseExact(t.PlanEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var pe) && pe > current) end = pe;
        int workdays = 0;
        for (var d = start; d <= end; d = d.AddDays(1)) if (!DayOff(d)) workdays++;
        return (t, start, current, end, Math.Max(1, workdays));
    }

    static string Title(TaskItem t) { TaskItem.Tags(t.Text, out var rest); return rest.Length > 0 ? rest : t.Text; }
    static int Rank(string? p) => BoardWindow.PriorityRank(p);

    string GroupOf(TaskItem t) => S.Group switch
    {
        "project" => t.NotionSource ?? "",
        "category" => TaskItem.Tags(t.Text, out _).FirstOrDefault() is { } tag ? (_store().CategoryFor(tag)?.Name ?? tag) : "",
        "priority" => t.Priority ?? "",
        "state" => t.Done ? L.T("Zrobione") : t.IsStarted ? L.T("W trakcie") : L.T("Do zrobienia"),
        _ => "",
    };

    int GroupRank(string g) => S.Group switch
    {
        "state" => g == L.T("W trakcie") ? 0 : g == L.T("Do zrobienia") ? 1 : 2,
        "priority" => Rank(g),
        _ => g.Length == 0 ? 1 : 0, // "no project / category" last
    };

    // ---------- drawing ----------

    static readonly Brush Hatch = MakeHatch();
    static Brush MakeHatch()
    {
        // 45° lines for weekends and public holidays (like the striped days of a planner)
        var pen = new Pen(new SolidColorBrush(Th.Ink(Th.Light ? (byte)0x2A : (byte)0x16)), 1.2);
        var brush = new DrawingBrush(new GeometryDrawing(null, pen, Geometry.Parse("M0,10 L10,0 M-3,3 L3,-3 M7,13 L13,7")))
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 10, 10), ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 10, 10), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
    }

    const double DateRows = 40, EventStrip = 20; // header: months + days, then a strip with day marks and meetings

    void Render()
    {
        double width = ActualWidth, w = W;
        if (width <= 0) return;
        _header.Children.Clear();
        _days.Children.Clear();
        _bars.Children.Clear();
        _overlay.Children.Clear();
        int first = (int)Math.Floor(_left), count = (int)Math.Ceiling(width / w) + 2;
        double X(DateTime d) => ((d - Origin).TotalDays - _left) * w;
        double bodyHeight = Math.Max(_scroll.ActualHeight, 50);
        var store = _store();

        // ----- days: columns, hatched days off, month lines -----
        for (int i = 0; i < count; i++)
        {
            var d = Origin.AddDays(first + i);
            double x = X(d);
            if (DayOff(d))
                _days.Children.Add(At(new Rectangle { Width = w, Height = 4000, Fill = Hatch }, x, 0));
            bool monthStart = d.Day == 1, weekStart = d.DayOfWeek == DayOfWeek.Monday;
            if (monthStart || (w >= 14 && (w >= 26 || weekStart)))
                _days.Children.Add(At(new Rectangle { Width = 1, Height = 4000, Fill = B(Th.Ink(monthStart ? (byte)0x30 : weekStart ? (byte)(Th.Light ? 0x22 : 0x18) : (byte)(Th.Light ? 0x14 : 0x0C))) }, x, 0));
        }

        // ----- header: months on top, then days (wide) / weeks (narrow) -----
        var pl = BoardWindow.Pl;
        for (int i = -40; i < count; i++)
        {
            var d = Origin.AddDays(first + i);
            if (d.Day != 1) continue;
            var m = d;
            double x = Math.Max(X(m), 0);
            var next = X(m.AddMonths(1));
            if (next < 0 || x > width) continue;
            var text = new TextBlock
            {
                Text = (m.Year == DateTime.Today.Year ? m.ToString("MMMM", pl) : m.ToString("MMMM yyyy", pl)).ToUpper(pl),
                FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = m.Year == DateTime.Today.Year && m.Month == DateTime.Today.Month ? Res("AccentBrush") : Res("FgDim"),
                MaxWidth = Math.Max(0, next - x - 6), TextTrimming = TextTrimming.CharacterEllipsis,
            };
            _header.Children.Add(At(text, x + 6, 2));
        }
        for (int i = 0; i < count; i++)
        {
            var d = Origin.AddDays(first + i);
            double x = X(d);
            bool today = d == DateTime.Today;
            if (w >= 22)
            {
                var label = new TextBlock
                {
                    Text = w >= 34 ? $"{d.Day} {pl.DateTimeFormat.GetAbbreviatedDayName(d.DayOfWeek).TrimEnd('.')}" : d.Day.ToString(),
                    FontSize = 10, Width = w, TextAlignment = TextAlignment.Center, FontWeight = today ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = today ? Brushes.White : DayOff(d) ? B(A(Color.FromRgb(0xF2, 0x6D, 0x6D), 0xC0)) : Res("FgFaint"),
                };
                if (today)
                    _header.Children.Add(At(new Border { Width = w - 4, Height = 17, CornerRadius = new CornerRadius(8.5), Background = Res("AccentBrush") }, x + 2, 19));
                _header.Children.Add(At(label, x, 20));
            }
            else if (d.DayOfWeek == DayOfWeek.Monday)
                _header.Children.Add(At(new TextBlock { Text = L.F("T {0}", ISOWeek.GetWeekOfYear(d)), FontSize = 10, Foreground = Res("FgFaint") }, x + 3, 20));
        }
        if (w < 22) // narrow: today as a pill over the week numbers
        {
            var x = X(DateTime.Today) + w / 2;
            var pill = new Border { CornerRadius = new CornerRadius(8.5), Background = Res("AccentBrush"), Padding = new Thickness(7, 1, 7, 2),
                Child = new TextBlock { Text = L.T("Dziś"), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White } };
            pill.Measure(new Size(200, 40));
            _header.Children.Add(At(pill, x - pill.DesiredSize.Width / 2, 19));
        }

        // ----- the strip under the dates: day marks (HO, BŚU, leave …) as runs of days, meetings as marks at their hour -----
        _header.Children.Add(At(new Rectangle { Width = width, Height = 1, Fill = Res("Line") }, 0, DateRows));
        for (int i = 0; i < count; i++)
        {
            var d = Origin.AddDays(first + i);
            var code = store.DayMark(BoardWindow.DayKey(d));
            if (code == null) continue;
            // a run of the same mark: drawn once, from its first visible day
            if (i > 0 && store.DayMark(BoardWindow.DayKey(d.AddDays(-1))) == code) continue;
            int len = 1;
            while (i + len < count + 30 && store.DayMark(BoardWindow.DayKey(d.AddDays(len))) == code) len++;
            var mt = store.MarkTypeFor(code);
            var mc = mt != null ? BoardWindow.ParseColor(mt.Color) : Color.FromRgb(0x9A, 0xA1, 0xB2);
            var run = new Border
            {
                Width = Math.Max(4, len * w - 4), Height = 14, CornerRadius = new CornerRadius(4),
                Background = mt?.Ring == true ? Brushes.Transparent : B(A(mc, 0x70)), BorderBrush = B(mc), BorderThickness = new Thickness(mt?.Ring == true ? 1.2 : 0),
                ToolTip = (mt?.Label is { Length: > 0 } lab && lab != code ? $"{code} · {lab}" : code) + "\n" + (len == 1 ? d.ToString("dddd d MMM", pl) : $"{d.ToString("d MMM", pl)} – {d.AddDays(len - 1).ToString("d MMM", pl)}"),
                Child = w * len >= 26 ? new TextBlock { Text = code, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = mt?.Ring == true ? Tx(mc) : Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } : null,
            };
            _header.Children.Add(At(run, X(d) + 2, DateRows + 3));
        }
        for (int i = 0; i < count; i++)
        {
            var d = Origin.AddDays(first + i);
            var meetings = CalendarService.On(d).Where(e => !e.IsHoliday && !e.AllDay).Select(e => (At: e.Start.TimeOfDay, End: e.End.TimeOfDay, e.Title))
                .Concat(store.Alarms.Where(a => a.IsMeeting && a.Occurs(d)).Select(a => (At: a.TimeOfDay, End: a.EndAt(d).TimeOfDay, Title: a.Text)))
                .OrderBy(m => m.At).ToList();
            if (meetings.Count == 0) continue;
            var tip = d.ToString("dddd d MMM", pl) + "\n" + string.Join("\n", meetings.Select(m => $"{m.At:hh\\:mm}–{m.End:hh\\:mm}  {m.Title}"));
            var violet = Color.FromRgb(0x8C, 0x9B, 0xFF);
            foreach (var m in meetings)
            {
                // where in the day it is (the day is 24 h wide); a short bar for its length
                double mx = X(d) + w * m.At.TotalHours / 24, mw = Math.Max(3, w * Math.Max(0.25, (m.End - m.At).TotalHours) / 24);
                _header.Children.Add(At(new Border { Width = mw, Height = 5, CornerRadius = new CornerRadius(2.5), Background = B(A(violet, 0xD0)), ToolTip = tip }, mx, DateRows + 7.5));
            }
            if (w >= 30)
                _header.Children.Add(At(new TextBlock { Text = meetings.Count.ToString(), FontSize = 8.5, FontWeight = FontWeights.Bold, Foreground = Tx(violet), ToolTip = tip }, X(d) + w - 9, DateRows + 4));
        }

        // ----- now: the line at the current hour of today -----
        {
            double x = X(DateTime.Today) + w * DateTime.Now.TimeOfDay.TotalHours / 24;
            var accent = ((SolidColorBrush)Res("AccentBrush")).Color;
            _overlay.Children.Add(At(new Rectangle { Width = 1.5, Height = 4000, Fill = B(A(accent, 0xD0)) }, x - 0.75, 0));
            _header.Children.Add(At(new Polygon { Points = new PointCollection { new(0, 0), new(8, 0), new(4, 5) }, Fill = B(accent) }, x - 4, DateRows + 1));
            _header.Children.Add(At(new Rectangle { Width = 1.5, Height = EventStrip - 6, Fill = B(A(accent, 0xD0)) }, x - 0.75, DateRows + 6));
        }

        // ----- bars (only those on screen), groups with their label and a dashed line in the project's colour -----
        _bars.Height = Math.Max(_contentHeight, bodyHeight);
        _bars.Width = width;
        var viewFrom = Origin.AddDays(first - 1);
        var viewTo = Origin.AddDays(first + count + 1);
        string? lastGroup = null;
        bool firstGroup = true;
        foreach (var it in _items.OrderBy(i => i.Row))
        {
            if (S.Group != "none" && it.Group != lastGroup)
            {
                lastGroup = it.Group;
                var name = it.Group.Length > 0 ? it.Group : S.Group switch { "project" => L.T("bez projektu"), "category" => L.T("bez kategorii"), "priority" => L.T("bez priorytetu"), _ => "" };
                Color gc = S.Group == "project" && it.Group.Length > 0 ? ProjectColor(it.Group)
                    : S.Group == "priority" && it.Group.Length > 0 ? BoardWindow.PriorityColor(it.Group) : Th.Ink(0x90);
                double gy = it.Row * RowHeight - 4;
                if (!firstGroup)
                    _bars.Children.Add(At(new Line { X1 = 0, X2 = width, Stroke = B(A(gc, 0x70)), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 5, 4 } }, 0, gy));
                firstGroup = false;
                var head = new Border
                {
                    CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 0, 6, 1), Background = B(A(gc, 0x26)),
                    Child = new TextBlock { Text = L.Up(name), FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Tx(gc) },
                };
                _bars.Children.Add(At(head, 8, gy + 3));
            }
            if (it.End < viewFrom || it.Start > viewTo) continue;
            BuildBar(it, X, w);
        }
        if (_items.Count == 0)
            _bars.Children.Add(At(new TextBlock { Text = L.T("Brak zadań do pokazania – zmień filtry albo dodaj zadania na dni."), Foreground = Res("FgFaint"), FontSize = 12 }, 12, 14));
        App.Board?.UpdateTimelineRange();
    }

    static Color ProjectColor(string name) =>
        App.Settings.Accounts.SelectMany(a => a.Projects).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is { } p
            ? BoardWindow.ParseColor(p.Color) : Color.FromRgb(0x8A, 0x94, 0xA6);

    static T At<T>(T el, double x, double y) where T : UIElement
    {
        Canvas.SetLeft(el, x);
        Canvas.SetTop(el, y);
        return el;
    }

    // ---------- a task's bar: only its name inside; the rest in the tool tip. Drag = move, the right edge = its end ----------

    (TaskItem Task, Grid Bar, bool Resize, double StartX, double Width)? _barDrag;

    void BuildBar(Item it, Func<DateTime, double> X, double w)
    {
        var t = it.Task;
        double rh = RowHeight, top = it.Row * rh + 14, height = rh - 10;
        double x0 = X(it.Start) + 2, xCur = X(it.Current.AddDays(1)) - 2, x1 = X(it.End.AddDays(1)) - 2;
        double fullWidth = Math.Max(6, x1 - x0);
        var prio = string.IsNullOrEmpty(t.Priority) ? (Color?)null : BoardWindow.PriorityColor(t.Priority);
        var accent = ((SolidColorBrush)Res("AccentBrush")).Color;

        var bar = new Grid { Width = fullWidth, Height = height, Tag = t, Cursor = Cursors.Hand, Opacity = t.Done ? 0.5 : 1, Background = Brushes.Transparent };
        // the part already worked on / its day: a solid card; the planned days after it: dashed, lighter
        double solid = Math.Clamp(xCur - x0, 6, fullWidth);
        bar.Children.Add(new Border
        {
            Width = solid, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(7, solid >= fullWidth - 1 ? 7 : 0, solid >= fullWidth - 1 ? 7 : 0, 7),
            Background = B(t.IsStarted ? A(accent, 0x30) : Th.Ink(0x1C)), BorderBrush = B(Th.Ink(0x1A)), BorderThickness = new Thickness(1),
        });
        if (fullWidth - solid > 1)
            bar.Children.Add(new Rectangle
            {
                Width = fullWidth - solid, HorizontalAlignment = HorizontalAlignment.Right, RadiusX = 7, RadiusY = 7,
                Fill = B(Th.Ink(0x08)), Stroke = B(Th.Ink(0x40)), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 },
            });
        // the priority stripe at its start (no priority: a neutral one)
        bar.Children.Add(new Border
        {
            Width = 4, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(7, 0, 0, 7),
            Background = prio is { } pc ? B(pc) : B(Th.Ink(0x40)),
        });

        // just the name, inside; it stays readable when the bar starts left of the screen (sticky)
        double shift = Math.Max(0, -x0);
        double fs = Math.Max(10, App.Settings.FontSize - 1);
        if (fullWidth - shift > 22)
        {
            var title = new TextBlock
            {
                Text = Title(t), FontSize = fs, Foreground = Res("Fg"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10 + shift, 0, 8, 1), IsHitTestVisible = false,
            };
            if (t.Done) title.TextDecorations = TextDecorations.Strikethrough;
            bar.Children.Add(title);
        }
        // the right edge: drag it to change how long the task lasts (Potrwa do…)
        var grip = new Border { Width = 7, HorizontalAlignment = HorizontalAlignment.Right, Background = Brushes.Transparent, Cursor = Cursors.SizeWE, ToolTip = L.T("Przeciągnij, żeby zmienić koniec") };
        bar.Children.Add(grip);

        var pl = BoardWindow.Pl;
        var tags = TaskItem.Tags(t.Text, out _);
        bar.ToolTip = (t.NotionSource != null ? t.NotionSource + " · " : "") + (tags.Count > 0 ? string.Join(" · ", tags) + " · " : "") + Title(t)
            + "\n" + (t.Done ? "✓ " + L.T("zrobione") : t.IsStarted ? "◐ " + L.T("w trakcie") : "○ " + L.T("do zrobienia"))
            + "\n" + (it.Start == it.End ? it.Start.ToString("dddd d MMM", pl) : $"{it.Start.ToString("d MMM", pl)} – {it.End.ToString("d MMM", pl)}")
            + "  ·  " + (it.Workdays == 1 ? L.T("1 dzień") : L.F("{0} dni", it.Workdays))
            + (it.End > it.Current ? "\n" + L.F("zaplanowane do {0}", it.End.ToString("ddd d MMM", pl)) : "")
            + (t.Estimate is > 0 ? "\n" + L.F("Estymacja: {0}", BoardWindow.Est(t.Estimate.Value, true)) : "")
            + (t.Priority != null ? "\n" + L.F("Priorytet: {0}", t.Priority) : "")
            + (t.Status != null ? "\nStatus: " + t.Status : "")
            + "\n\n" + L.T("Przeciągnij: przesuń · prawa krawędź: zmień koniec · dwuklik: pokaż w tygodniu · prawy przycisk: menu");
        bar.MouseEnter += (_, _) => { if (_barDrag == null) bar.RenderTransform = new TranslateTransform(0, -1); };
        bar.MouseLeave += (_, _) => { if (_barDrag == null) bar.RenderTransform = null; };
        bar.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (e.ClickCount == 2) { _barDrag = null; _open(it.Current); return; }
            _barDrag = (t, bar, e.OriginalSource == grip || IsInside(e.OriginalSource as DependencyObject, grip), e.GetPosition(this).X, fullWidth);
            CaptureMouse();
        };
        bar.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var menu = _menu(t, bar);
            menu.PlacementTarget = bar;
            menu.IsOpen = true;
        };
        _bars.Children.Add(At(bar, x0, top));
    }

    static bool IsInside(DependencyObject? el, DependencyObject parent)
    {
        for (; el != null; el = VisualTreeHelper.GetParent(el)) if (ReferenceEquals(el, parent)) return true;
        return false;
    }

    // ---------- the bar with filters, sorting, grouping and zoom ----------

    public void BuildToolbar()
    {
        _toolbar.Children.Clear();
        var store = _store();
        ComboBox Combo(string label, string? current, IEnumerable<(string? Value, string Text)> options, Action<string?> set, double minWidth = 130)
        {
            var box = new ComboBox { MinWidth = minWidth, Margin = new Thickness(0, 0, 6, 0), ToolTip = label };
            foreach (var (value, text) in options) box.Items.Add(new ComboBoxItem { Content = text, Tag = value });
            box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == current) ?? box.Items[0];
            box.SelectionChanged += (_, _) =>
            {
                set((box.SelectedItem as ComboBoxItem)?.Tag as string);
                SettingsStore.Save(App.Settings);
                Refresh();
            };
            _toolbar.Children.Add(box);
            return box;
        }
        var tasks = store.Data.Tasks.Where(t => t.Day != null && !t.Archived).ToList();
        var projects = App.Settings.AccountFor(App.Settings.Layer).Projects.Select(p => p.Name).Concat(tasks.Select(t => t.NotionSource).Where(p => p != null).Select(p => p!))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p).ToList();
        Combo(L.T("Projekt"), S.Project, new (string?, string)[] { (null, L.T("Projekt: wszystkie")) }.Concat(projects.Select(p => ((string?)p, p))).Append(("", L.T("bez projektu"))), v => S.Project = v);
        var cats = tasks.SelectMany(t => TaskItem.Tags(t.Text, out _)).Distinct().OrderBy(c => c).ToList();
        Combo(L.T("Kategoria"), S.Category, new (string?, string)[] { (null, L.T("Kategoria: wszystkie")) }.Concat(cats.Select(c => ((string?)c, store.CategoryFor(c)?.Name ?? c))), v => S.Category = v);
        var prios = tasks.Select(t => t.Priority).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(Rank).ToList();
        Combo(L.T("Priorytet"), S.Priority, new (string?, string)[] { (null, L.T("Priorytet: wszystkie")) }.Concat(prios.Select(p => ((string?)p, p))).Append(("", L.T("bez priorytetu"))), v => S.Priority = v);
        Combo(L.T("Stan"), S.State, new (string?, string)[] { ("all", L.T("Stan: wszystkie")), ("open", L.T("niezrobione")), ("started", L.T("w trakcie")), ("done", L.T("zrobione")) }, v => S.State = v ?? "all", 120);
        _toolbar.Children.Add(new Border { Width = 1, Height = 18, Margin = new Thickness(6, 0, 12, 0), Background = Res("Line"), VerticalAlignment = VerticalAlignment.Center });
        Combo(L.T("Sortuj"), S.Sort, new (string?, string)[] { ("start", L.T("Sortuj: data rozpoczęcia")), ("priority", L.T("Sortuj: priorytet")), ("name", L.T("Sortuj: nazwa")), ("added", L.T("Sortuj: kolejność dodania")), ("length", L.T("Sortuj: długość")) }, v => S.Sort = v ?? "start", 170);
        Combo(L.T("Grupuj"), S.Group, new (string?, string)[] { ("project", L.T("Grupuj: projekt")), ("category", L.T("Grupuj: kategoria")), ("priority", L.T("Grupuj: priorytet")), ("state", L.T("Grupuj: stan")), ("none", L.T("Bez grup")) }, v => S.Group = v ?? "none", 150);

        Button SmallIcon(FrameworkElement icon, string tip, Action a)
        {
            var b = new Button { Style = (Style)Application.Current.Resources["BarIcon"], Content = icon, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center, Foreground = Res("FgDim") };
            b.Click += (_, _) => a();
            return b;
        }
        Button Small(string text, string tip, Action a)
        {
            var b = new Button { Style = (Style)Application.Current.Resources["IconButton"], Content = text, FontSize = 13, Padding = new Thickness(8, 2, 8, 3), ToolTip = tip, VerticalAlignment = VerticalAlignment.Center };
            b.Click += (_, _) => a();
            return b;
        }
        _toolbar.Children.Add(new Border { Width = 1, Height = 18, Margin = new Thickness(6, 0, 6, 0), Background = Res("Line"), VerticalAlignment = VerticalAlignment.Center });
        _toolbar.Children.Add(SmallIcon(Icons.Zoom(false), L.T("Oddal (Ctrl + kółko myszy)"), () => Zoom(1 / 1.4)));
        _toolbar.Children.Add(SmallIcon(Icons.Zoom(true), L.T("Przybliż (Ctrl + kółko myszy)"), () => Zoom(1.4)));
        _toolbar.Children.Add(new TextBlock
        {
            Text = L.T("przeciągnij, żeby przesunąć · Shift + kółko: w bok · Ctrl + kółko: skala"), FontSize = 10.5, Foreground = Res("FgFaint"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        });
    }
}
