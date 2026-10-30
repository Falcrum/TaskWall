using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TaskWall;

public enum ViewMode { Day, Week1, Week2, Month, Year, Timeline, Report }

/// <summary>The board: views, task rows, editing and drag &amp; drop. The day cells live in BoardWindow.Days.cs,
/// backlog, search and the top bar in BoardWindow.Features.cs, the arrows and the palette in BoardWindow.Keys.cs.</summary>
public partial class BoardWindow : GlassWindow
{
    /// <summary>UI culture (Polish or English, see <see cref="L"/>).</summary>
    public static CultureInfo Pl => L.Culture;
    const string BacklogKey = "backlog";
    const string DragFormat = "TaskWallTask";
    static readonly Color WeekendRed = Color.FromRgb(0xF2, 0x6D, 0x6D);
    static readonly Color MeetingColor = Color.FromRgb(0x8C, 0x9B, 0xFF);
    static readonly string[] TagPalette = { "#6EA0FF", "#F07AAE", "#F2A65A", "#5CCB92", "#4CC3D4", "#A08BFF", "#EE6E6E", "#D4C25E" };

    ViewMode _view = ParseView(App.Settings.View);
    ViewMode _lastWeekView = App.Settings.View switch { "week1" => ViewMode.Week1, "day" => ViewMode.Day, _ => ViewMode.Week2 };
    bool IsWeekView => _view is ViewMode.Day or ViewMode.Week1 or ViewMode.Week2;
    DateTime _dayDate = DateTime.Today; // the day of the DZIEŃ view
    readonly Dictionary<string, double> _progress = new(); // last shown progress per day (animate only real changes)

    static ViewMode ParseView(string v) => v switch { "day" => ViewMode.Day, "week1" => ViewMode.Week1, "month" => ViewMode.Month, "year" => ViewMode.Year, "timeline" => ViewMode.Timeline, "report" => ViewMode.Report, _ => ViewMode.Week2 };
    static string ViewKey(ViewMode v) => v switch { ViewMode.Day => "day", ViewMode.Week1 => "week1", ViewMode.Month => "month", ViewMode.Year => "year", ViewMode.Timeline => "timeline", ViewMode.Report => "report", _ => "week2" };
    /// <summary>The view comes back on the next start – each board its own.</summary>
    void SaveView(ViewMode v) { if (IsSecond) S.SecondView = ViewKey(v); else S.View = ViewKey(v); }
    int _weekOffset;
    DateTime _monthFirst = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    int _year = DateTime.Today.Year;
    DateTime _today = DateTime.Today;
    string? _addingKey;                 // day key (or BacklogKey) whose "add task" box is open
    // inline editors currently open (their "cancel" actions); external refreshes wait for them, a Rebuild cancels them
    readonly List<Action> _editors = new();
    // an editor counts as open only while it has the keyboard: one that never got the focus (another window took it)
    // must not block every later rebuild – then added tasks and the "+ dodaj zadanie" row seemed to vanish
    readonly Dictionary<Action, UIElement> _editorBoxes = new();
    int _openEditors => _editors.Count(c => _editorBoxes.TryGetValue(c, out var box) && box.IsKeyboardFocusWithin);
    DateTime _lastEventCheck = DateTime.Now;
    ViewMode? _builtView;
    int _slideDir;                      // navigation animation direction
    string? _justAdded, _justToggled, _flashId;
    readonly Dictionary<string, double> _scrollOffsets = new();
    readonly Dictionary<string, StackPanel> _panels = new();
    readonly DispatcherTimer _dayTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    readonly DispatcherTimer _syncFlash = new() { Interval = TimeSpan.FromSeconds(5) };

    // drag state
    TaskItem? _pressed;
    Point _pressPoint;
    Border? _dropLine;

    /// <summary>The board on the second screen: a view of its own (Settings.SecondView), the whole screen, no new-day work.</summary>
    public bool IsSecond { get; }
    /// <summary>When it was last built: a change made on the other board rebuilds this one only if it is older.</summary>
    public DateTime BuiltAt { get; private set; }

    public BoardWindow() : this(false) { }

    public BoardWindow(bool second)
    {
        IsSecond = second;
        if (second)
        {
            _view = ParseView(S.SecondView);
            if (IsWeekView) _lastWeekView = _view;
        }
        MakeOpaque(); // GPU-rendered window; corners are cut by a region and filled with the wallpaper
        InitializeComponent();
        L.Tree(this); // static XAML texts → English when chosen
        AdoptXamlContent();
        _dayTimer.Tick += (_, _) =>
        {
            if (_openEditors > 0) return; // never close an editor under the user's fingers
            if (DateTime.Today == _today) { if (IsWeekView) RefreshEventsOnly(); return; }
            var was = _today;
            _today = DateTime.Today;
            // the day view and the month that showed "today" follow it to the new day
            if (_dayDate == was) _dayDate = _today;
            if (_monthFirst == new DateTime(was.Year, was.Month, 1)) _monthFirst = new DateTime(_today.Year, _today.Month, 1);
            if (!IsSecond) App.Instance.OnNewDay(); // once, by the main board
            Rebuild();
        };
        _dayTimer.Start();
        // a board closed (the second screen gone): its timers stop with it, or it would go on ticking and rebuilding unseen
        Closed += (_, _) => { _dayTimer.Stop(); _trackTimer?.Stop(); _dayView?.StopTimers(); _timeline?.StopTimers(); };
        _syncFlash.Tick += (_, _) => { _syncFlash.Stop(); SyncText.Text = ""; };
        InitFeatures();
    }

    // ---------- helpers ----------

    static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    static AppSettings S => App.Settings;
    static BoardStore Store => App.Store;

    public static DateTime Monday(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));
    public static string DayKey(DateTime d) => Days.Key(d);
    /// <summary>A day key; a malformed one (from another version's file) reads as today instead of breaking the whole board.</summary>
    static DateTime ParseKey(string k) => Days.Parse(k) ?? DateTime.Today;
    static string DayName(DateTime d) => Pl.DateTimeFormat.GetAbbreviatedDayName(d.DayOfWeek).TrimEnd('.').ToUpper(Pl);
    static bool IsWeekend(DateTime d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
    /// <summary>Weekends on screen: as set – and always on a Saturday or Sunday itself (from Monday the 5-day week is back).</summary>
    static bool Weekends => S.ShowWeekends || IsWeekend(DateTime.Today);
    public static int WeekNo(DateTime d) => ISOWeek.GetWeekOfYear(d);
    public enum TaskState { Todo, Started, Review, Tests, Done, Hold }

    /// <summary>The next state on a click: to do → in progress → review → tests → done → to do (Shift: straight to done).</summary>
    public static TaskState NextState(TaskItem t, bool shift = false) =>
        t.Done ? TaskState.Todo : t.IsHold ? WorkState(t) /* released: back to where it was */ : shift ? TaskState.Done : t.IsTesting ? TaskState.Done : t.IsReview ? TaskState.Tests : t.IsStarted ? TaskState.Review : TaskState.Started;

    /// <summary>The state under a hold.</summary>
    static TaskState WorkState(TaskItem t) => t.IsTesting ? TaskState.Tests : t.IsReview ? TaskState.Review : t.IsStarted ? TaskState.Started : TaskState.Todo;

    public void SetState(TaskItem t, TaskState state) => Do(() =>
    {
        var real = Store.Materialize(t);
        if (state == TaskState.Hold)
        {
            // on hold: the state under it stays (it comes back on release), the work timer stops – nothing is done now
            real.Hold = true;
            if (real.IsTracking) Store.StopTracking(real);
        }
        else
        {
            real.Hold = null;
            real.Done = state == TaskState.Done;
            if (real.Done && real.IsTracking) Store.StopTracking(real);
            if (state != TaskState.Done) real.Started = state is TaskState.Started or TaskState.Review or TaskState.Tests;
            real.Review = state == TaskState.Review ? true : null;
            real.Testing = state == TaskState.Tests ? true : null;
        }
        real.NoteState(DayKey(DateTime.Today)); // this day keeps the state it ends with (its history)
        // finished before the planned end: the plan ends today (no future days left hanging)
        var todayKey = DayKey(DateTime.Today);
        if (real.Done && real.PlanEnd != null && real.Day != null && string.CompareOrdinal(todayKey, real.PlanEnd) < 0)
            real.PlanEnd = string.CompareOrdinal(todayKey, real.Day) > 0 ? todayKey : null;
        Store.Changed(real);
        _justToggled = real.Id;
    });

    public static string Hours(double h) => h is > 0 and < 1 ? $"{Math.Round(h * 60)}min" : h.ToString("0.#", Pl) + "h";
    public const double DayHours = 8;
    /// <summary>A task's estimate: minutes, hours, or whole working days (8 h) from 2 days up.</summary>
    public static string Est(double h, bool longForm = false) => h >= 2 * DayHours && h % DayHours == 0
        ? (longForm ? L.F("{0} dni", h / DayHours) : L.F("{0}d", h / DayHours)) : Hours(h);

    /// <summary>"45min", "1,5h", "1h30", "2d", "3 dni", "2 days"; a bare number = hours.</summary>
    public static double? ParseEstimate(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text.Trim().ToLowerInvariant(),
            @"^(\d+(?:[.,]\d+)?)\s*(min\w*|m|h|godz\w*|hours?|d|dni|dzie\w*|days?)?\s*(?:(\d{1,2})\s*(?:min\w*|m)?)?$");
        if (!m.Success) return null;
        var n = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        var unit = m.Groups[2].Value;
        double h = unit.StartsWith("m") ? n / 60 : unit.StartsWith("d") ? n * DayHours : n;
        if (m.Groups[3].Success) h += int.Parse(m.Groups[3].Value) / 60.0;
        return h is > 0 and <= 2000 ? Math.Round(h, 3) : null;
    }
    static SolidColorBrush B(Color c) => Ui.Brush(c); // shared, frozen
    /// <summary>Coloured text: as is on the dark glass, darker on the light theme (pastels are unreadable on white).</summary>
    static SolidColorBrush Tx(Color c) => new(Th.Light ? Color.FromArgb(c.A, (byte)(c.R * 0.6), (byte)(c.G * 0.6), (byte)(c.B * 0.6)) : c);
    static Color A(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
    public static Color ParseColor(string hex) => Ui.Parse(hex, Color.FromRgb(0x9A, 0xA1, 0xB2)); // parsed once per text

    public DateTime FirstMonday => _view == ViewMode.Month ? Monday(_monthFirst) : _view == ViewMode.Day ? Monday(_dayDate) : Monday(DateTime.Today).AddDays(7 * _weekOffset);
    public int WeekCount => _view switch { ViewMode.Month => MonthRows(), ViewMode.Week1 or ViewMode.Day => 1, _ => 2 };
    public ViewMode View => _view;
    int MonthRows() => (int)((Monday(_monthFirst.AddMonths(1).AddDays(-1)) - Monday(_monthFirst)).TotalDays / 7) + 1;

    /// <summary>Board height in "week rows" for the layout (month rows are compact).</summary>
    public double HeightInWeeks => _view switch
    {
        ViewMode.Month => MonthRows() * 0.62,
        ViewMode.Year => 2,
        ViewMode.Timeline => 2,
        ViewMode.Week1 => 1,
        ViewMode.Day => 3.6, // the agenda: as tall as the screen allows
        ViewMode.Report => 3.6,
        _ => 2,
    };

    /// <summary>Unscaled height of the bar + margins (weeks come on top of that).</summary>
    public const double ChromeHeight = 8 + 34 + 10;

    /// <summary>Wraps a user action: undo checkpoint first, then the change.</summary>
    void Do(Action change, Action? after = null)
    {
        var before = WeekSigs();
        Store.Checkpoint();
        change();
        // the week / month views: only the week rows whose days changed are built again (a tick on Tuesday doesn't
        // rebuild the whole month); anything wider (alarms, series, another view, an open editor) builds it all
        if (before != null && WeekSigs() is { } now && before.Global == now.Global && _openEditors == 0)
        {
            var weeks = now.Days.Where(kv => !before.Days.TryGetValue(kv.Key, out var was) || was != kv.Value).Select(kv => kv.Key.Week).ToHashSet();
            RebuildWeeks(weeks);
        }
        else Rebuild();
        after?.Invoke();
    }

    /// <summary>What each day on screen shows (its tasks and the multi-day ones over it, its mark and hours), as text, and the rest.</summary>
    sealed record Sigs(string Global, Dictionary<(int Week, string Day), string> Days);

    Sigs? WeekSigs()
    {
        if (_view is not (ViewMode.Week1 or ViewMode.Week2 or ViewMode.Month) || WeeksGrid.Visibility != Visibility.Visible) return null;
        var first = FirstMonday;
        int perWeek = Weekends ? 7 : 5;
        var keys = new Dictionary<string, (int, string)>();
        for (int w = 0; w < WeekCount; w++)
            for (int c = 0; c < perWeek; c++) { var k = DayKey(first.AddDays(w * 7 + c)); keys[k] = (w, k); }
        var text = keys.Values.ToDictionary(x => x, _ => new System.Text.StringBuilder());
        foreach (var t in Store.Data.Tasks)
        {
            void Put(string? day) { if (day != null && keys.TryGetValue(day, out var at)) text[at].Append(t.Id).Append(':').Append(t.Modified.Ticks).Append(t.Archived ? 'a' : '-').Append(';'); }
            Put(t.Day);
            if (ComputeSpan(t) is { } span) foreach (var d in span) if (d != t.Day) Put(d); // a lane on its other days too
        }
        foreach (var (k, at) in keys) if (Store.Data.Days.TryGetValue(k, out var info)) text[at].Append('|').Append(info.Modified.Ticks);
        // alarms, series, marks' series, the account: a change there rebuilds everything
        var global = $"{S.Layer}|{Store.Data.Alarms.Count}|{Store.Data.Alarms.Select(a => a.Modified.Ticks).DefaultIfEmpty().Max()}|{Store.Data.Rules.Count}|{Store.Data.Rules.Select(r => r.Modified.Ticks).DefaultIfEmpty().Max()}"
            + $"|{Store.Data.MarkRules?.Count}|{Store.Data.MarkRules?.Select(r => r.Modified.Ticks).DefaultIfEmpty().Max()}|{Store.Data.Tasks.Count(t => t.Day == null)}|{_view}|{FirstMonday:yyyyMMdd}|{WeekCount}|{Weekends}";
        return new Sigs(global, text.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()));
    }

    /// <summary>A rebuild of just some week rows (and the bar, the backlog) – see <see cref="Do"/>.</summary>
    void RebuildWeeks(HashSet<int> weeks)
    {
        BuiltAt = DateTime.UtcNow;
        foreach (var cancel in _editors.ToList()) cancel();
        _editors.Clear();
        _editorBoxes.Clear();
        SpanCache.Clear();
        TaskMarks.ResetCaches();
        _meetingsCache.Clear();
        _pressed = null;
        UpdateTopBar();
        if (weeks.Count > 0)
        {
            var first = FirstMonday;
            int perWeek = Weekends ? 7 : 5;
            foreach (var w in weeks)
                for (int c = 0; c < perWeek; c++)
                {
                    var key = DayKey(first.AddDays(w * 7 + c));
                    if (_panels.TryGetValue(key, out var panel) && panel.Parent is ScrollViewer sv) _scrollOffsets[key] = sv.VerticalOffset;
                    _panels.Remove(key);
                }
            _dropLine = null;
            BuildGrid(weeks);
        }
        if (S.BacklogMode == "drawer" ? _drawerOpen : _pinnedVisible) BuildBacklog(); else _backlogStale = true;
        _justAdded = _justToggled = null;
        _flashId = null;
        _editOnBuild = null;
        _selFrame = null;
        if (_sel != null) Dispatcher.BeginInvoke(ApplySelection, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    readonly HashSet<string> _showDone = new(); // days whose folded done tasks are unfolded
    readonly DispatcherTimer _undoHide = new() { Interval = TimeSpan.FromSeconds(7) };

    /// <summary>"Zarchiwizowano: … · COFNIJ" at the bottom for a few seconds (Ctrl+Z works too).</summary>
    void ShowUndo(string message)
    {
        UndoText.Text = message;
        UndoBar.Visibility = Visibility.Visible;
        Anim.Enter(UndoBar, 0, 10, 220);
        _undoHide.Stop();
        _undoHide.Tick -= HideUndo;
        _undoHide.Tick += HideUndo;
        _undoHide.Start();
    }

    void HideUndo(object? s, EventArgs e) { _undoHide.Stop(); UndoBar.Visibility = Visibility.Collapsed; }

    void UndoBar_Click(object sender, RoutedEventArgs e)
    {
        HideUndo(null, EventArgs.Empty);
        Undo();
    }

    static string Short(string text) => text.Length > 40 ? text[..38] + "…" : text;

    /// <summary>Notion sub-items right under their main task (when it's in the same list), with their depth for the indent.</summary>
    static List<(TaskItem Task, int Depth)> Nest(IEnumerable<TaskItem> list)
    {
        var items = list.ToList();
        var main = items.Where(t => t.NotionId != null).GroupBy(t => t.NotionId!).ToDictionary(g => g.Key, g => g.First());
        bool HasMain(TaskItem t) => t.ParentNotionId != null && t.ParentNotionId != t.NotionId && main.ContainsKey(t.ParentNotionId);
        var children = items.Where(HasMain).GroupBy(t => t.ParentNotionId!).ToDictionary(g => g.Key, g => g.ToList());
        var result = new List<(TaskItem, int)>();
        var seen = new HashSet<string>();
        void Add(TaskItem t, int depth)
        {
            if (!seen.Add(t.Id)) return;
            result.Add((t, depth));
            if (depth < 4 && t.NotionId != null && children.TryGetValue(t.NotionId, out var sub))
                foreach (var c in sub) Add(c, depth + 1);
        }
        foreach (var t in items) if (!HasMain(t)) Add(t, 0);
        foreach (var t in items) Add(t, 0); // anything left (a cycle of parents)
        return result;
    }

    /// <summary>Tasks shown on a day: stored (not archived) + recurring occurrences not materialized yet.</summary>
    static List<TaskItem> DayTasks(DateTime d, Dictionary<string, List<TaskItem>> byDay)
    {
        byDay.TryGetValue(DayKey(d), out var stored);
        var list = new List<TaskItem>(stored ?? new List<TaskItem>());
        list.AddRange(Store.VirtualTasks(d));
        // tasks with planned hours first, by the hour; the rest in their own (dragged) order
        list.Sort((a, b) => a.Time != null && b.Time != null ? string.CompareOrdinal(a.Time, b.Time)
            : a.Time != null ? -1 : b.Time != null ? 1 : a.Order.CompareTo(b.Order));
        return list;
    }

    static Dictionary<string, List<TaskItem>> TasksByDay() =>
        Store.Data.Tasks.Where(t => t.Day != null && !t.Archived).GroupBy(t => t.Day!).ToDictionary(g => g.Key, g => g.ToList());

    public void JumpTo(DateTime date)
    {
        int target = (int)Math.Round((Monday(date) - Monday(DateTime.Today)).TotalDays / 7);
        bool viewChanged = !IsWeekView;
        _slideDir = viewChanged ? 0 : _view == ViewMode.Day ? Math.Sign((date.Date - _dayDate).TotalDays) : Math.Sign(target - _weekOffset);
        _weekOffset = target;
        _dayDate = date.Date;
        if (viewChanged) { _view = _lastWeekView; SaveView(_view); }
        Rebuild();
        if (viewChanged) { Anim.Enter(WeeksGrid, 0, 10, 300); App.Instance.LayoutWindows(); }
    }

    ReportView? _reportView;
    bool _reportChosen;

    ReportView ReportPane()
    {
        if (_reportView == null)
        {
            _reportView = new ReportView(() => Store, d => JumpTo(d), () => { if (_view == ViewMode.Report) UpdateTopBar(); });
            ReportHost.Children.Add(_reportView);
        }
        return _reportView;
    }

    /// <summary>The report view on the week (or the month) of <paramref name="day"/> – R, the palette, the menu, the year view.</summary>
    public void ShowReport(DateTime day, bool month)
    {
        ReportPane().Set(day, month);
        if (_view == ViewMode.Report) { Rebuild(); return; }
        _reportChosen = true;
        SetView(ViewMode.Report);
    }

    void SetView(ViewMode v)
    {
        if (_view == v) return;
        // the period on screen right now, so the new view opens around it
        var anchor = _view switch
        {
            ViewMode.Year => _year == DateTime.Today.Year ? DateTime.Today : new DateTime(_year, 1, 1),
            ViewMode.Timeline => _timeline?.Visible is { } tv && DateTime.Today >= tv.From && DateTime.Today <= tv.To ? DateTime.Today : _timeline?.Visible.From.AddDays(3) ?? DateTime.Today,
            ViewMode.Month => _monthFirst.Year == DateTime.Today.Year && _monthFirst.Month == DateTime.Today.Month ? DateTime.Today : _monthFirst,
            ViewMode.Day => _dayDate,
            ViewMode.Report => _reportView is { } rv && DateTime.Today >= rv.From && DateTime.Today < (rv.Month ? rv.From.AddMonths(1) : rv.From.AddDays(7)) ? DateTime.Today : _reportView?.From ?? DateTime.Today,
            // into the day view and the report: today when it's on screen
            _ => v is ViewMode.Day or ViewMode.Report && DateTime.Today >= FirstMonday && DateTime.Today < FirstMonday.AddDays(7 * WeekCount) ? DateTime.Today : FirstMonday.AddDays(3),
        };
        // the report opens on the week (from the month and the year views: the month) on screen, unless ShowReport chose it
        if (v == ViewMode.Report && !_reportChosen) ReportPane().Set(anchor, _view is ViewMode.Month or ViewMode.Year);
        _reportChosen = false;
        _dayDate = anchor.Date;
        _view = v;
        if (IsWeekView) _lastWeekView = v;
        SaveView(v);
        SettingsStore.Save(S);
        _weekOffset = (int)Math.Round((Monday(anchor) - Monday(DateTime.Today)).TotalDays / 7);
        _monthFirst = new DateTime(anchor.Year, anchor.Month, 1);
        _year = anchor.Year;
        Rebuild();
        Anim.Enter(v == ViewMode.Year ? YearHost : v == ViewMode.Timeline ? TimelineHost : v == ViewMode.Report ? ReportHost : WeeksGrid, 0, 10, 300);
        App.Instance.LayoutWindows();
        App.Instance.ScheduleTrim();
    }

    public void OnExternalChange(bool quiet = false)
    {
        if (!quiet)
        {
            SyncText.Text = L.T("↻ zsynchronizowano");
            _syncFlash.Stop();
            _syncFlash.Start();
        }
        if (_openEditors == 0) Rebuild(); // otherwise the open editor rebuilds when it closes
    }

    /// <summary>Commits whatever is being typed (e.g. before switching the data layer).</summary>
    public void EndEditing()
    {
        if (_openEditors > 0) Keyboard.ClearFocus(); // LostKeyboardFocus commits into the current store
    }

    /// <summary>Global hotkey: today's "add task" box, ready to type.</summary>
    public void QuickAdd()
    {
        if (!IsWeekView) { _view = _lastWeekView; SaveView(_view); }
        _weekOffset = 0;
        _dayDate = DateTime.Today;
        SetDrawer(false);
        CloseSearch();
        _addingKey = DayKey(DateTime.Today);
        Rebuild();
        App.Instance.LayoutWindows();
        Activate();
    }

    /// <summary>Rebuild only when a meeting has just ended (it gets dimmed).</summary>
    void RefreshEventsOnly()
    {
        var now = DateTime.Now;
        bool ended = CalendarService.On(DateTime.Today).Any(e => !e.AllDay && !e.IsHoliday && e.End > _lastEventCheck && e.End <= now);
        _lastEventCheck = now;
        if (ended && _openEditors == 0) Rebuild();
    }

    // ---------- building ----------

    TimelineView? _timeline;
    DayView? _dayView;

    /// <summary>
    /// Task blocks moved / stretched on the day view (one, or with the tasks after it that made room – one Ctrl+Z): their
    /// hours; stretched (Length), the length becomes the estimate – a move keeps it (a 2-day task drawn as 8 h stays 2 days).
    /// </summary>
    void DaySetTimes(IReadOnlyList<(TaskItem T, TimeSpan From, TimeSpan To, bool Length)> changes) => Do(() =>
    {
        foreach (var (t, from, to, length) in changes) SetBlockTime(t, from, to, length);
    });

    void SetBlockTime(TaskItem t, TimeSpan from, TimeSpan to, bool length)
    {
        var real = Store.Materialize(t);
        real.Time = from.ToString(@"hh\:mm");
        real.End = to >= TimeSpan.FromHours(24) ? "23:59" : to.ToString(@"hh\:mm");
        if (length)
        {
            var old = real.Estimate;
            real.Estimate = Math.Round((to - from).TotalHours, 2);
            ApplyAutoPlan(real, real.Day, old);
        }
        Store.Changed(real);
    }

    /// <summary>A task dropped on the day view (from the backlog or another day): that day, at that hour.</summary>
    void DayDropTask(string id, DateTime day, TimeSpan at)
    {
        if (Store.Find(id) is not { } t) return;
        var dur = TimeSpan.FromHours(Math.Clamp(t.Estimate ?? 0.5, 0.25, 8));
        Store.Checkpoint();
        if (t.Archived) Store.Unarchive(t); // dragged from the archive: back on the board
        MoveToEnd(t, DayKey(day));
        var real = Store.Find(id) ?? t;
        real.Time = at.ToString(@"hh\:mm");
        var end = at + dur;
        real.End = end >= TimeSpan.FromHours(24) ? "23:59" : end.ToString(@"hh\:mm");
        Store.Changed(real);
        Dispatcher.BeginInvoke(Rebuild);
    }

    /// <summary>
    /// A bar dragged on the timeline: the whole bar moves by whole days – the days worked on, its day and its planned end
    /// (as the drag showed it). The dates are read first, so a bad one changes nothing.
    /// </summary>
    void TimelineMove(TaskItem t, int days)
    {
        static DateTime? Parse(string? k) => DateTime.TryParseExact(k, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        if (days == 0 || Parse(t.Day) is not { } day) return;
        var plan = Parse(t.PlanEnd);
        var worked = t.WorkedDays?.Select(Parse).Where(d => d != null).Select(d => DayKey(d!.Value.AddDays(days))).ToList();
        Do(() =>
        {
            var real = Store.Materialize(t);
            real.Day = DayKey(day.AddDays(days));
            real.WorkedDays = worked is { Count: > 0 } ? worked : null;
            real.PlanEnd = plan is { } p && p > day ? DayKey(p.AddDays(days)) : null;
            Store.Changed(real);
        });
    }

    /// <summary>A bar's right edge dragged: how long it lasts (Potrwa do…).</summary>
    void TimelineSetEnd(TaskItem t, DateTime end) => Do(() =>
    {
        var real = Store.Materialize(t);
        if (!DateTime.TryParseExact(real.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return;
        real.PlanEnd = end.Date > day ? DayKey(end) : null;
        Store.Changed(real);
    });

    /// <summary>The top bar's range text for the timeline (it changes while dragging).</summary>
    void AddTimelineRange()
    {
        var (from, to) = _timeline?.Visible ?? (DateTime.Today, DateTime.Today.AddDays(30));
        RangeText.Inlines.Add(new Run(L.T("TIMELINE")) { FontWeight = FontWeights.Bold });
        RangeText.Inlines.Add(new Run("   |   ") { Foreground = Res("FgFaint") });
        var fmt = from.Year == to.Year ? "d MMM" : "d MMM yyyy";
        RangeText.Inlines.Add(new Run($"{from.ToString(fmt, Pl)} – {to.ToString("d MMM yyyy", Pl)}".ToUpper(Pl)) { FontWeight = FontWeights.SemiBold, Foreground = Res("FgDim") });
    }

    public void UpdateTimelineRange()
    {
        if (_view != ViewMode.Timeline) return;
        RangeText.Inlines.Clear();
        AddTimelineRange();
    }

    public void Rebuild()
    {
        using var watch = Watchdog.Step("rebuilding the board (" + _view + ")");
        BuiltAt = DateTime.UtcNow;
        // editors in the old visuals are gone after this: make sure their late focus events do nothing
        foreach (var cancel in _editors.ToList()) cancel();
        _editors.Clear();
        _editorBoxes.Clear();
        _trackLabels.Clear();
        SpanCache.Clear(); // tasks that are gone don't stay referenced
        TaskMarks.ResetCaches();
        _meetingsCache.Clear();
        if (_trackTimer == null)
        {
            _trackTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _trackTimer.Tick += (_, _) => { _trackLabels.RemoveAll(x => !x.El.IsLoaded); foreach (var (_, show) in _trackLabels.ToList()) show(); }; // rows rebuilt away (the backlog) drop out
            _trackTimer.Start();
        }
        _pressed = null;
        FontSize = S.FontSize;
        ApplyBacklogMode();
        UpdateTopBar();

        foreach (var (key, panel) in _panels)
            if (panel.Parent is ScrollViewer sv) _scrollOffsets[key] = sv.VerticalOffset;
        _panels.Clear();
        _dropLine = null;

        if (_view != ViewMode.Day) DayHost.Visibility = Visibility.Collapsed;
        if (_view != ViewMode.Report) ReportHost.Visibility = Visibility.Collapsed;
        if (_view == ViewMode.Day)
        {
            WeeksGrid.Visibility = Visibility.Collapsed;
            YearHost.Visibility = Visibility.Collapsed;
            YearHost.Children.Clear();
            TimelineHost.Visibility = Visibility.Collapsed;
            DayHost.Visibility = Visibility.Visible;
            if (_dayView == null)
            {
                _dayView = new DayView(new DayViewHost(
                    () => Store,
                    d => Store.Data.Tasks.Where(t => t.Day == DayKey(d) && !t.Archived).Concat(Store.VirtualTasks(d)).OrderBy(t => t.Order).ToList(),
                    (t, el) => BuildMenu(t, null, null, el),
                    t => SetState(t, NextState(t, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))),
                    DaySetTimes,
                    DayDropTask,
                    (key, d) => BuildAddRow(key, d),
                    (a, d) => AlarmWindow.Edit(Store, a, d),
                    t => Do(() => { var real = Store.Materialize(t); real.Time = null; real.End = null; Store.Changed(real); })));
                DayHost.Children.Add(_dayView);
            }
            _dayView.Show(_dayDate);
            if (_slideDir != 0) Anim.Enter(DayHost, 50 * _slideDir, 0, 280);
        }
        else if (_view == ViewMode.Timeline)
        {
            WeeksGrid.Visibility = Visibility.Collapsed;
            YearHost.Visibility = Visibility.Collapsed;
            YearHost.Children.Clear();
            TimelineHost.Visibility = Visibility.Visible;
            if (_timeline == null)
            {
                _timeline = new TimelineView(() => Store, (t, el) => BuildMenu(t, null, null, el), d => JumpTo(d), TimelineMove, TimelineSetEnd);
                TimelineHost.Children.Add(_timeline);
            }
            else _timeline.BuildToolbar();
            _timeline.Refresh();
        }
        else if (_view == ViewMode.Report)
        {
            TimelineHost.Visibility = Visibility.Collapsed;
            WeeksGrid.Visibility = Visibility.Collapsed;
            YearHost.Visibility = Visibility.Collapsed;
            YearHost.Children.Clear();
            ReportHost.Visibility = Visibility.Visible;
            ReportPane().Refresh();
            if (_slideDir != 0) Anim.Enter(ReportHost, 50 * _slideDir, 0, 280);
        }
        else if (_view == ViewMode.Year)
        {
            TimelineHost.Visibility = Visibility.Collapsed;
            WeeksGrid.Visibility = Visibility.Collapsed;
            YearHost.Visibility = Visibility.Visible;
            YearHost.Children.Clear();
            // the "fill in" animation only when the year view is entered or the year changes, not on every refresh
            YearHost.Children.Add(YearView.Build(_year, Store, d => JumpTo(d), animate: _builtView != ViewMode.Year || _slideDir != 0));
            if (_slideDir != 0) Anim.Enter(YearHost, 50 * _slideDir, 0, 280);
        }
        else
        {
            TimelineHost.Visibility = Visibility.Collapsed;
            YearHost.Visibility = Visibility.Collapsed;
            YearHost.Children.Clear();
            WeeksGrid.Visibility = Visibility.Visible;
            BuildGrid();
            if (_slideDir != 0) Anim.Enter(WeeksGrid, 50 * _slideDir, 0, 280);
        }
        _slideDir = 0;
        _builtView = _view;
        // the backlog only when it is on screen (a closed drawer is built when it opens)
        if (S.BacklogMode == "drawer" ? _drawerOpen : _pinnedVisible) BuildBacklog(); else _backlogStale = true;
        _justAdded = _justToggled = null;
        _flashId = null;
        _editOnBuild = null; // not used by this build (e.g. the row is folded away): never later, by surprise
        _selFrame = null; // the old rows are gone
        if (_sel != null) Dispatcher.BeginInvoke(ApplySelection, System.Windows.Threading.DispatcherPriority.Loaded); // the selected task, framed again
    }

    Border? _topGlow;

    void UpdateTopBar()
    {
        var overdue = Overdue().Count;
        OverdueButton.Visibility = overdue > 0 && _view is not (ViewMode.Year or ViewMode.Timeline or ViewMode.Report) ? Visibility.Visible : Visibility.Collapsed;
        OverdueButton.Content = L.F("↷ NA DZIŚ ({0})", overdue);
        OverdueButton.ToolTip = L.F("Niezrobione zadania z minionych dni ({0}) przejdą na dziś. Zadania w trakcie (◐) zostają też w dniach, w których były, jako „↷ 1/3”; nierozpoczęte (○) po prostu się przenoszą.", overdue);
        OverdueButton.Foreground = Tx(Color.FromRgb(0xF2, 0xA6, 0x5A));

        YearButton.Visibility = S.ShowYearButton ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (btn, mode) in new[] { (DayViewButton, ViewMode.Day), (OneWeekButton, ViewMode.Week1), (WeekViewButton, ViewMode.Week2), (MonthViewButton, ViewMode.Month), (YearButton, ViewMode.Year), (TimelineButton, ViewMode.Timeline), (ReportButton, ViewMode.Report) })
        {
            bool on = _view == mode;
            btn.Background = on ? B(Th.Ink(0x26)) : Brushes.Transparent;
            btn.Foreground = on ? Res("Fg") : Res("FgDim");
        }
        // Poświata: the light from the top of the board, behind everything
        if (_topGlow == null)
        {
            _topGlow = new Border { Height = 280, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
            Grid.SetColumnSpan(_topGlow, 2);
            Panel.SetZIndex(_topGlow, -1);
            BodyRoot.Children.Insert(0, _topGlow);
        }
        _topGlow.Background = Glow.Enabled ? Glow.Top() : null;
        // the layer (account) on the right: icon + its name; the private one tinted green so it's obvious
        if (_barCaptions != S.BarCaptions) SetupBarIcons();
        bool priv = S.Layer == "private";
        if (_layerName != null) _layerName.Text = L.Up(App.AccountName(S.Layer));
        LayerButton.Background = priv ? B(Color.FromArgb(0x40, 0x5C, 0xCB, 0x92)) : B(Th.Ink(0x14));
        LayerButton.Foreground = Res("Fg");
        UpdateBacklogButton();

        RangeText.Inlines.Clear();
        switch (_view)
        {
            case ViewMode.Year:
                RangeText.Inlines.Add(new Run(_year.ToString()) { FontWeight = FontWeights.Bold });
                break;
            case ViewMode.Timeline:
                AddTimelineRange();
                break;
            case ViewMode.Report when _reportView is { } rv:
                // T 44 | 26 paź – 1 lis   or   T 40–44 | PAŹDZIERNIK 2026
                var rto = rv.Month ? rv.From.AddMonths(1).AddDays(-1) : rv.From.AddDays(6);
                RangeText.Inlines.Add(new Run(rv.Month ? L.F("T {0}–{1}", WeekNo(rv.From), WeekNo(rto)) : L.F("T {0}", WeekNo(rv.From))) { FontWeight = FontWeights.Bold });
                RangeText.Inlines.Add(new Run("   |   ") { Foreground = Res("FgFaint") });
                RangeText.Inlines.Add(new Run(rv.Month ? rv.From.ToString("MMMM yyyy", Pl).ToUpper(Pl) : $"{rv.From.ToString("d MMM", Pl)} – {rto.ToString("d MMM", Pl)}") { FontWeight = FontWeights.SemiBold, Foreground = Res("FgDim") });
                break;
            case ViewMode.Day:
                RangeText.Inlines.Add(new Run(L.F("T {0}", WeekNo(_dayDate))) { FontWeight = FontWeights.Bold });
                RangeText.Inlines.Add(new Run("   |   ") { Foreground = Res("FgFaint") });
                RangeText.Inlines.Add(new Run(AlarmWindow.DayTitle(_dayDate)) { FontWeight = FontWeights.SemiBold, Foreground = Res("FgDim") });
                break;
            case ViewMode.Month:
            {
                var first = Monday(_monthFirst);
                var last = first.AddDays(7 * MonthRows() - 1);
                RangeText.Inlines.Add(new Run(L.F("T {0}–{1}", WeekNo(first), WeekNo(last))) { FontWeight = FontWeights.Bold });
                RangeText.Inlines.Add(new Run("   |   ") { Foreground = Res("FgFaint") });
                RangeText.Inlines.Add(new Run(_monthFirst.ToString("MMMM yyyy", Pl).ToUpper(Pl)) { FontWeight = FontWeights.SemiBold, Foreground = Res("FgDim") });
                break;
            }
            default:
            {
                // ‹ T 41–42 | 5 paź – 18 paź ›
                var first = FirstMonday;
                var last = first.AddDays(7 * WeekCount - 1);
                var fmt = first.Year == DateTime.Today.Year && last.Year == DateTime.Today.Year ? "d MMM" : "d MMM yyyy";
                RangeText.Inlines.Add(new Run(WeekCount == 2 ? L.F("T {0}–{1}", WeekNo(first), WeekNo(first.AddDays(7))) : L.F("T {0}", WeekNo(first))) { FontWeight = FontWeights.Bold });
                RangeText.Inlines.Add(new Run("   |   ") { Foreground = Res("FgFaint") });
                RangeText.Inlines.Add(new Run($"{first.ToString(fmt, Pl)} – {last.ToString(fmt, Pl)}".ToUpper(Pl)) { FontWeight = FontWeights.SemiBold, Foreground = Res("FgDim") });
                break;
            }
        }
    }

    /// <summary>A task longer than a workday (8 h) is planned over the next workdays: 12 h → 2 days, 2d → 2 days.</summary>
    public static string? AutoPlanEnd(string? day, double? estimate) =>
        day != null && estimate is > DayHours ? PlanEndAfter(day, (int)Math.Ceiling(estimate.Value / DayHours - 1e-9)) : null;

    /// <summary>After the estimate or the day changed: the planned end follows the estimate, unless it was set by hand.</summary>
    public static void ApplyAutoPlan(TaskItem t, string? oldDay, double? oldEstimate)
    {
        if (t.Done || t.Day == null) return;
        if (t.PlanEnd != null && t.PlanEnd != AutoPlanEnd(oldDay, oldEstimate)) return; // "Potrwa do…" chosen by hand
        t.PlanEnd = AutoPlanEnd(t.Day, t.Estimate);
    }

    /// <summary>The last of <paramref name="workdays"/> workdays starting on <paramref name="day"/> ("3d").</summary>
    public static string PlanEndAfter(string day, int workdays)
    {
        var d = ParseKey(day);
        workdays = Math.Min(workdays, 520); // two years at most: a wild estimate (seconds, a typo from Notion) must not walk to the year 9999
        for (int n = 1; n < workdays;)
        {
            d = d.AddDays(1);
            if (!IsWeekend(d) && DaysOff.Name(d) == null) n++;
        }
        return DayKey(d);
    }

    /// <summary>Workdays (no weekends / public holidays) from <paramref name="from"/> up to the day before <paramref name="to"/>.</summary>
    static List<DateTime> WorkdaysBetween(DateTime from, DateTime to)
    {
        var list = new List<DateTime>();
        for (var d = from.Date; d < to.Date; d = d.AddDays(1))
            if (!IsWeekend(d) && DaysOff.Name(d) == null) list.Add(d);
        return list;
    }

    ContextMenu BuildDayMenu(string key, DateTime date)
    {
        var menu = new ContextMenu();
        var mark = Store.DayMark(key);
        var series = Store.MarkRuleOn(date);
        menu.Items.Add(new MenuItem { Header = date.ToString("dddd, d MMMM", Pl) + L.F("  ·  tydz. {0}", WeekNo(date)), IsEnabled = false });
        menu.Items.Add(new Separator());
        foreach (var m in Store.MarkTypes)
        {
            var code = m.Code;
            var mi = new MenuItem { Header = (mark == code ? "✓  " : "     ") + (m.Label.Length > 0 && m.Label != code ? $"{MarkType.ShowLabel(code, m.Label)} ({MarkType.Show(code)})" : MarkType.Show(code)) };
            mi.Click += (_, _) => Do(() => Store.SetDayMark(key, mark == code ? null : code));
            menu.Items.Add(mi);
        }
        if (Store.MarkTypes.Count > 0)
        {
            var clear = new MenuItem { Header = "     " + L.T("Usuń oznaczenie") + (series != null && mark == series.Text ? L.T(" (tylko ten dzień)") : ""), IsEnabled = mark != null };
            clear.Click += (_, _) => Do(() => Store.SetDayMark(key, null));
            menu.Items.Add(clear);
            if (series != null)
            {
                var edit = new MenuItem { Header = "     " + L.F("Seria „{0}”: {1}…", series.Text, series.Summary) };
                edit.Click += (_, _) => RepeatWindow.ForMark(Store, series, date);
                menu.Items.Add(edit);
            }
            var repeat = new MenuItem { Header = "     " + L.T("Oznaczenie w cyklu (np. HO co czwartek i piątek)…") };
            repeat.Click += (_, _) => RepeatWindow.ForMark(Store, null, date, mark);
            menu.Items.Add(repeat);
        }
        else
        {
            var setup = new MenuItem { Header = L.T("Oznaczenia dni (np. Urlop): dodaj w ustawieniach…") };
            setup.Click += (_, _) => App.Instance.ShowSettings();
            menu.Items.Add(setup);
        }
        // a day off the company gives (an event, a holiday that fell on a Saturday): like a public holiday, named by hand
        var company = Store.CompanyDay(key);
        var free = new MenuItem { Header = company != null ? "✓  " + L.F("Dzień wolny: {0} – zmień opis…", company) : "     " + L.T("Dzień wolny od firmy…") };
        free.Click += (_, _) =>
        {
            var name = AskWindow.Ask(L.T("Dzień wolny od firmy"), L.T("Opis przy dacie (np. Wolne za 15 sierpnia):"), company ?? L.T("Wolne za "));
            if (name != null && name.Trim().Length > 0) Do(() => Store.SetCompanyDay(key, name));
        };
        menu.Items.Add(free);
        if (company != null)
        {
            var unfree = new MenuItem { Header = "     " + L.T("Usuń dzień wolny") };
            unfree.Click += (_, _) => Do(() => Store.SetCompanyDay(key, null));
            menu.Items.Add(unfree);
        }
        menu.Items.Add(new Separator());
        var add = new MenuItem { Header = L.T("Dodaj zadanie") };
        add.Click += (_, _) => { _addingKey = key; Rebuild(); };
        menu.Items.Add(add);
        var alarm = new MenuItem { Header = L.T("Dodaj alarm…") };
        alarm.Click += (_, _) => AlarmWindow.Edit(Store, null, date);
        menu.Items.Add(alarm);
        var meeting = new MenuItem { Header = L.T("Dodaj spotkanie…") };
        meeting.Click += (_, _) => AlarmWindow.Edit(Store, null, date, meeting: true);
        menu.Items.Add(meeting);
        if (_view == ViewMode.Month)
        {
            var open = new MenuItem { Header = L.T("Pokaż ten tydzień") };
            open.Click += (_, _) => JumpTo(date);
            menu.Items.Add(open);
        }
        return menu;
    }

    string? _addPrefix;

    /// <summary>Fade the bottom edge of a day when more tasks are hidden below.</summary>
    static void UpdateFade(ScrollViewer sv)
    {
        if (sv.ScrollableHeight > sv.VerticalOffset + 1 && sv.ActualHeight > 30)
        {
            double fadeStart = Math.Max(0, 1 - 22 / sv.ActualHeight);
            sv.OpacityMask = new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(Colors.Black, 0),
                new GradientStop(Colors.Black, fadeStart),
                new GradientStop(Colors.Transparent, 1),
            }, 90);
        }
        else sv.OpacityMask = null;
    }

    /// <summary>A category on a task's second line: its name in bold on a light fill of its colour (Meeting with its icon).</summary>
    public static Border TagChip(string tag, double mfs)
    {
        var c = TagColor(tag);
        var chip = new StackPanel { Orientation = Orientation.Horizontal };
        if (tag == TaskItem.MeetingTag)
            chip.Children.Add(new TextBlock { Text = "",FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = Math.Max(7, mfs - 1), Foreground = Tx(c), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 3, 0) });
        chip.Children.Add(new TextBlock { Text = tag == TaskItem.MeetingTag ? "Meeting" : tag, FontSize = mfs, FontWeight = FontWeights.Bold, Foreground = Tx(c) });
        return new Border
        {
            CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent, Background = B(A(c, 0x2A)), Child = chip,
        };
    }

    static Color TagColor(string tag)
    {
        if (Store.CategoryFor(tag) is { } cat)
            return Ui.Parse(cat.Color, MeetingColor);
        if (tag == TaskItem.MeetingTag) return MeetingColor;
        int h = 0;
        foreach (var ch in tag) h = h * 31 + ch;
        return Ui.Parse(TagPalette[Math.Abs(h) % TagPalette.Length], MeetingColor);
    }

}
