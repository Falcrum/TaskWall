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

public enum ViewMode { Day, Week1, Week2, Month, Year }

/// <summary>The board: week / month / year views, day cells, task rows, editing and drag &amp; drop.
/// Backlog, search, top bar and keyboard live in BoardWindow.Features.cs.</summary>
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

    static ViewMode ParseView(string v) => v switch { "day" => ViewMode.Day, "week1" => ViewMode.Week1, "month" => ViewMode.Month, "year" => ViewMode.Year, _ => ViewMode.Week2 };
    static string ViewKey(ViewMode v) => v switch { ViewMode.Day => "day", ViewMode.Week1 => "week1", ViewMode.Month => "month", ViewMode.Year => "year", _ => "week2" };
    int _weekOffset;
    DateTime _monthFirst = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    int _year = DateTime.Today.Year;
    DateTime _today = DateTime.Today;
    string? _addingKey;                 // day key (or BacklogKey) whose "add task" box is open
    // inline editors currently open (their "cancel" actions); external refreshes wait for them, a Rebuild cancels them
    readonly List<Action> _editors = new();
    int _openEditors => _editors.Count;
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

    public BoardWindow()
    {
        MakeOpaque(); // GPU-rendered window; corners are cut by a region and filled with the wallpaper
        InitializeComponent();
        L.Tree(this); // static XAML texts → English when chosen
        AdoptXamlContent();
        _dayTimer.Tick += (_, _) =>
        {
            if (_openEditors > 0) return; // never close an editor under the user's fingers
            if (DateTime.Today == _today) { if (IsWeekView) RefreshEventsOnly(); return; }
            _today = DateTime.Today;
            App.Instance.OnNewDay();
            Rebuild();
        };
        _dayTimer.Start();
        _syncFlash.Tick += (_, _) => { _syncFlash.Stop(); SyncText.Text = ""; };
        InitFeatures();
    }

    // ---------- helpers ----------

    static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    static AppSettings S => App.Settings;
    static BoardStore Store => App.Store;

    public static DateTime Monday(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));
    public static string DayKey(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    static DateTime ParseKey(string k) => DateTime.ParseExact(k, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    static string DayName(DateTime d) => Pl.DateTimeFormat.GetAbbreviatedDayName(d.DayOfWeek).TrimEnd('.').ToUpper(Pl);
    static bool IsWeekend(DateTime d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
    public static int WeekNo(DateTime d) => ISOWeek.GetWeekOfYear(d);
    public static string Hours(double h) => h.ToString("0.#", Pl) + "h";
    static SolidColorBrush B(Color c) => new(c);
    static Color A(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
    public static Color ParseColor(string hex) { try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return Color.FromRgb(0x9A, 0xA1, 0xB2); } }

    public DateTime FirstMonday => _view == ViewMode.Month ? Monday(_monthFirst) : _view == ViewMode.Day ? Monday(_dayDate) : Monday(DateTime.Today).AddDays(7 * _weekOffset);
    public int WeekCount => _view switch { ViewMode.Month => MonthRows(), ViewMode.Week1 or ViewMode.Day => 1, _ => 2 };
    public ViewMode View => _view;
    int MonthRows() => (int)((Monday(_monthFirst.AddMonths(1).AddDays(-1)) - Monday(_monthFirst)).TotalDays / 7) + 1;

    /// <summary>Board height in "week rows" for the layout (month rows are compact).</summary>
    public double HeightInWeeks => _view switch
    {
        ViewMode.Month => MonthRows() * 0.62,
        ViewMode.Year => 2,
        ViewMode.Week1 => 1,
        ViewMode.Day => 1.5,
        _ => 2,
    };

    /// <summary>Unscaled height of the bar + margins (weeks come on top of that).</summary>
    public const double ChromeHeight = 8 + 34 + 10;

    /// <summary>Wraps a user action: undo checkpoint first, then the change.</summary>
    void Do(Action change)
    {
        Store.Checkpoint();
        change();
        Rebuild();
    }

    /// <summary>Tasks shown on a day: stored (not archived) + recurring occurrences not materialized yet.</summary>
    static List<TaskItem> DayTasks(DateTime d, Dictionary<string, List<TaskItem>> byDay)
    {
        byDay.TryGetValue(DayKey(d), out var stored);
        var list = new List<TaskItem>(stored ?? new List<TaskItem>());
        list.AddRange(Store.VirtualTasks(d));
        list.Sort((a, b) => a.Order.CompareTo(b.Order));
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
        if (viewChanged) { _view = _lastWeekView; S.View = ViewKey(_view); }
        Rebuild();
        if (viewChanged) { Anim.Enter(WeeksGrid, 0, 10, 300); App.Instance.LayoutWindows(); }
    }

    void SetView(ViewMode v)
    {
        if (_view == v) return;
        // the period on screen right now, so the new view opens around it
        var anchor = _view switch
        {
            ViewMode.Year => _year == DateTime.Today.Year ? DateTime.Today : new DateTime(_year, 1, 1),
            ViewMode.Month => _monthFirst.Year == DateTime.Today.Year && _monthFirst.Month == DateTime.Today.Month ? DateTime.Today : _monthFirst,
            ViewMode.Day => _dayDate,
            // into the day view: today when it's on screen
            _ => v == ViewMode.Day && DateTime.Today >= FirstMonday && DateTime.Today < FirstMonday.AddDays(7 * WeekCount) ? DateTime.Today : FirstMonday.AddDays(3),
        };
        _dayDate = anchor.Date;
        _view = v;
        if (IsWeekView) _lastWeekView = v;
        S.View = ViewKey(v);
        SettingsStore.Save(S);
        _weekOffset = (int)Math.Round((Monday(anchor) - Monday(DateTime.Today)).TotalDays / 7);
        _monthFirst = new DateTime(anchor.Year, anchor.Month, 1);
        _year = anchor.Year;
        Rebuild();
        Anim.Enter(v == ViewMode.Year ? YearHost : WeeksGrid, 0, 10, 300);
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
        if (!IsWeekView) { _view = _lastWeekView; S.View = ViewKey(_view); }
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

    public void Rebuild()
    {
        // editors in the old visuals are gone after this: make sure their late focus events do nothing
        foreach (var cancel in _editors.ToList()) cancel();
        _editors.Clear();
        _pressed = null;
        FontSize = S.FontSize;
        ApplyBacklogMode();
        UpdateTopBar();

        foreach (var (key, panel) in _panels)
            if (panel.Parent is ScrollViewer sv) _scrollOffsets[key] = sv.VerticalOffset;
        _panels.Clear();
        _dropLine = null;

        if (_view == ViewMode.Year)
        {
            WeeksGrid.Visibility = Visibility.Collapsed;
            YearHost.Visibility = Visibility.Visible;
            YearHost.Children.Clear();
            // the "fill in" animation only when the year view is entered or the year changes, not on every refresh
            YearHost.Children.Add(YearView.Build(_year, Store, d => JumpTo(d), animate: _builtView != ViewMode.Year || _slideDir != 0));
            if (_slideDir != 0) Anim.Enter(YearHost, 50 * _slideDir, 0, 280);
        }
        else
        {
            YearHost.Visibility = Visibility.Collapsed;
            YearHost.Children.Clear();
            WeeksGrid.Visibility = Visibility.Visible;
            BuildGrid();
            if (_slideDir != 0) Anim.Enter(WeeksGrid, 50 * _slideDir, 0, 280);
        }
        _slideDir = 0;
        _builtView = _view;
        BuildBacklog();
        _justAdded = _justToggled = null;
        _flashId = null;
    }

    void UpdateTopBar()
    {
        var overdue = Overdue().Count;
        OverdueButton.Visibility = overdue > 0 && _view != ViewMode.Year ? Visibility.Visible : Visibility.Collapsed;
        OverdueButton.Content = L.F("⟲ ZALEGŁE: {0} → DZIŚ", overdue);
        OverdueButton.Foreground = B(Color.FromRgb(0xF2, 0xA6, 0x5A));

        YearButton.Visibility = S.ShowYearButton ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (btn, mode) in new[] { (DayViewButton, ViewMode.Day), (OneWeekButton, ViewMode.Week1), (WeekViewButton, ViewMode.Week2), (MonthViewButton, ViewMode.Month), (YearButton, ViewMode.Year) })
        {
            bool on = _view == mode;
            btn.Background = on ? B(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
            btn.Foreground = on ? Res("Fg") : Res("FgDim");
        }
        bool priv = S.Layer == "private";
        WorkLayerButton.Content = App.AccountName("work").ToUpper(Pl);
        PrivateLayerButton.Content = App.AccountName("private").ToUpper(Pl);
        foreach (var (btn, on) in new[] { (WorkLayerButton, !priv), (PrivateLayerButton, priv) })
        {
            btn.Background = on ? B(priv ? Color.FromArgb(0x40, 0x5C, 0xCB, 0x92) : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
            btn.Foreground = on ? Res("Fg") : Res("FgDim");
        }
        ClearDoneButton.Visibility = _view == ViewMode.Year ? Visibility.Collapsed : Visibility.Visible;
        UpdateBacklogButton();

        RangeText.Inlines.Clear();
        switch (_view)
        {
            case ViewMode.Year:
                RangeText.Inlines.Add(new Run(_year.ToString()) { FontWeight = FontWeights.Bold });
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

    /// <summary>Week view (1–2 rows) or month view (5–6 compact rows).</summary>
    void BuildGrid()
    {
        WeeksGrid.Children.Clear();
        WeeksGrid.RowDefinitions.Clear();
        WeeksGrid.ColumnDefinitions.Clear();
        bool single = _view == ViewMode.Day;
        int days = single ? 1 : S.ShowWeekends ? 7 : 5;
        bool month = _view == ViewMode.Month;
        for (int c = 0; c < days; c++) WeeksGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int w = 0; w < WeekCount; w++) WeeksGrid.RowDefinitions.Add(new RowDefinition());

        var byDay = TasksByDay();
        var archivedDone = Store.Data.Tasks.Where(t => t.Archived && t.Done && t.Day != null).GroupBy(t => t.Day!).ToDictionary(g => g.Key, g => g.Count());
        var first = single ? _dayDate : FirstMonday;
        for (int w = 0; w < WeekCount; w++)
            for (int c = 0; c < days; c++)
            {
                var date = first.AddDays(w * 7 + c);
                archivedDone.TryGetValue(DayKey(date), out var arch);
                var cell = BuildDay(date, DayTasks(date, byDay), arch, c == 0, w == 0, month, month && date.Month != _monthFirst.Month);
                Grid.SetRow(cell, w);
                Grid.SetColumn(cell, c);
                WeeksGrid.Children.Add(cell);
            }
    }

    FrameworkElement BuildDay(DateTime date, List<TaskItem> tasks, int archivedDone, bool firstCol, bool firstRow, bool compact, bool outside)
    {
        var key = DayKey(date);
        bool isToday = date == DateTime.Today;
        bool isPast = date < DateTime.Today;
        var dayOff = PolishHolidays.DayOff(date);
        var events = CalendarService.On(date);
        var holidayNames = events.Where(e => e.IsHoliday).Select(e => e.Title).ToList();
        if (dayOff != null && !holidayNames.Any(n => n.Equals(dayOff, StringComparison.OrdinalIgnoreCase))) holidayNames.Insert(0, dayOff);
        bool red = IsWeekend(date) || dayOff != null;
        var mark = Store.DayMark(key);
        var markInfo = Store.MarkTypeFor(mark);
        Color? markColor = markInfo != null ? ParseColor(markInfo.Color) : mark != null ? Color.FromRgb(0x9A, 0xA1, 0xB2) : null;

        var outer = new Border
        {
            BorderBrush = Res("Line"),
            BorderThickness = new Thickness(firstCol ? 0 : 1, firstRow ? 0 : 1, 0, 0),
            Opacity = outside ? 0.45 : 1,
        };
        Brush normalBg = isToday ? B(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF))
            : markColor is { } mc ? B(A(mc, 0x16))
            : Brushes.Transparent;
        var inner = new Border
        {
            CornerRadius = new CornerRadius(compact ? 7 : 10),
            Margin = new Thickness(compact ? 2 : 4),
            Padding = compact ? new Thickness(6, 3, 4, 2) : new Thickness(8, 6, 6, 4),
            Background = normalBg,
            AllowDrop = true,
        };
        if (isToday)
        {
            inner.BorderBrush = B(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));
            inner.BorderThickness = new Thickness(1);
        }
        outer.Child = inner;

        var dock = new DockPanel();
        inner.Child = dock;

        // ----- header -----
        var header = new Grid { Margin = new Thickness(0, 0, 0, compact ? 2 : 5) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        DockPanel.SetDock(header, Dock.Top);
        var nameColor = red ? B(WeekendRed) : isPast && !isToday ? Res("FgDim") : Res("Fg");
        var dateColor = red ? B(WeekendRed) : isToday ? Res("AccentBrush") : Res("FgDim");
        var left = new StackPanel();
        var nameRow = new WrapPanel();
        nameRow.Children.Add(new TextBlock { Text = DayName(date), FontWeight = FontWeights.Bold, FontSize = 11.5, Foreground = nameColor, VerticalAlignment = VerticalAlignment.Center });
        if (compact)
            nameRow.Children.Add(new TextBlock
            {
                Text = " " + (date.Day == 1 ? date.ToString("d MMM", Pl) : date.Day.ToString()),
                FontSize = 11.5, FontWeight = isToday ? FontWeights.Bold : FontWeights.SemiBold, Foreground = dateColor, VerticalAlignment = VerticalAlignment.Center,
            });
        if (markColor is { } mcol)
            nameRow.Children.Add(new Border
            {
                Background = B(mcol),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 0, 5, 1),
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = (markInfo?.Label ?? mark) + (Store.SeriesMark(key) == mark ? L.T("  ·  powtarzane") : ""),
                Child = new TextBlock { Text = mark, FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White },
            });
        left.Children.Add(nameRow);
        if (!compact)
        {
            var dateText = new TextBlock { FontSize = 10.5, FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal, Foreground = dateColor };
            dateText.Inlines.Add(new Run(date.Day == 1 || (firstCol && firstRow) ? date.ToString("d MMM", Pl) : date.Day.ToString()));
            if (firstCol) dateText.Inlines.Add(new Run(L.F("  ·  tydz. {0}", WeekNo(date))) { Foreground = Res("FgFaint"), FontWeight = FontWeights.Normal });
            left.Children.Add(dateText);
        }
        if (holidayNames.Count > 0)
            left.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", holidayNames.Distinct()),
                FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = dayOff != null ? B(A(WeekendRed, 0xD0)) : Res("FgFaint"),
                ToolTip = string.Join("\n", holidayNames.Distinct()) + (dayOff != null ? "\n" + L.T("(dzień ustawowo wolny)") : ""),
            });
        header.Children.Add(left);

        // right: estimate sum (+ today's progress count)
        double hours = tasks.Sum(t => t.Estimate ?? 0);
        double meetingHours = events.Where(e => !e.IsHoliday).Sum(e => e.Hours) + Store.Alarms.Where(a => a.IsMeeting && a.Occurs(date)).Sum(a => a.Hours);
        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 1, 2, 0) };
        Grid.SetColumn(right, 1);
        int total = tasks.Count + archivedDone, done = tasks.Count(t => t.Done) + archivedDone;
        if (total > 0) // the counter always sits in the corner; the hours go below it
            right.Children.Add(new TextBlock
            {
                Text = $"{done}/{total}", FontSize = 10, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = done == total ? Res("AccentBrush") : Res("FgDim"),
                ToolTip = L.F("Zrobione {0} z {1}", done, total) + (archivedDone > 0 ? L.F(" (w tym {0} w archiwum)", archivedDone) : ""),
            });
        if (hours > 0 || meetingHours > 0)
        {
            double left_ = tasks.Where(t => !t.Done).Sum(t => t.Estimate ?? 0);
            right.Children.Add(new TextBlock
            {
                Text = hours > 0 ? Hours(hours) : "",
                FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = hours + meetingHours > 8 ? B(Color.FromRgb(0xF2, 0xA6, 0x5A)) : Res("FgDim"),
                ToolTip = L.F("Zadania: {0} (zostało {1})", Hours(hours), Hours(left_)) + (meetingHours > 0 ? "\n" + L.F("Spotkania: {0}", Hours(meetingHours)) : ""),
            });
        }
        header.Children.Add(right);
        dock.Children.Add(header);

        // ----- progress bar (done / all, archived done included) -----
        if (total > 0)
        {
            var track = new Grid { Height = isToday ? 3 : 2, Margin = new Thickness(0, 0, 2, compact ? 3 : 6) };
            DockPanel.SetDock(track, Dock.Top);
            track.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = B(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)) });
            var fill = new Border
            {
                CornerRadius = new CornerRadius(1.5),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = isToday ? Res("AccentBrush") : B(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)),
                ToolTip = L.F("Zrobione {0} z {1}", done, total),
            };
            double frac = (double)done / total;
            bool known = _progress.TryGetValue(key, out var before);
            _progress[key] = frac;
            bool animate = Anim.On && known && Math.Abs(before - frac) > 0.0001; // only a real change, never on plain rebuilds
            track.SizeChanged += (_, e) =>
            {
                double target = e.NewSize.Width * frac;
                if (animate && e.PreviousSize.Width == 0)
                    fill.BeginAnimation(WidthProperty, new DoubleAnimation(e.NewSize.Width * before, target, TimeSpan.FromMilliseconds(450)) { EasingFunction = new CubicEase() });
                else { fill.BeginAnimation(WidthProperty, null); fill.Width = target; }
            };
            track.Children.Add(fill);
            dock.Children.Add(track);
        }

        // ----- events + tasks -----
        var panel = new StackPanel();
        // alarms, own meetings and calendar meetings in time order, then the tasks
        var timed = Store.Alarms.Where(a => a.Occurs(date)).Select(a => (at: a.TimeOfDay, el: BuildAlarmRow(a, date, compact)))
            .Concat(events.Where(e => !e.IsHoliday).Select(e => (at: e.AllDay ? TimeSpan.Zero : e.Start.TimeOfDay, el: BuildEventRow(e, key, compact))))
            .OrderBy(x => x.at);
        foreach (var (_, el) in timed) panel.Children.Add(el);
        foreach (var t in tasks) panel.Children.Add(BuildTaskRow(t, false, compact));
        panel.Children.Add(BuildAddRow(key, date, compact));
        _panels[key] = panel;

        var sv = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = panel,
        };
        sv.ScrollChanged += (_, _) => UpdateFade(sv);
        sv.SizeChanged += (_, _) => UpdateFade(sv);
        if (_scrollOffsets.TryGetValue(key, out var off)) sv.Loaded += (_, _) => sv.ScrollToVerticalOffset(off);
        dock.Children.Add(sv);

        inner.DragOver += (_, e) => OnDragOverTarget(panel, e);
        inner.Drop += (_, e) => OnDrop(key, panel, e);
        inner.DragEnter += (_, _) => inner.Background = B(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        inner.DragLeave += (_, _) => inner.Background = normalBg;
        inner.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var menu = BuildDayMenu(key, date);
            menu.PlacementTarget = inner;
            menu.IsOpen = true;
        };
        return outer;
    }

    /// <summary>Read-only meeting from Google Calendar.</summary>
    FrameworkElement BuildEventRow(CalEvent e, string dayKey, bool compact)
    {
        bool over = !e.AllDay && e.End < DateTime.Now;
        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(5, compact ? 1 : 3, 4, compact ? 1 : 3),
            Margin = new Thickness(-4, 0, 0, 2),
            Background = B(A(MeetingColor, 0x1C)),
            BorderBrush = B(A(MeetingColor, 0x40)),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Opacity = over ? 0.45 : 1,
            ToolTip = $"{e.Title}\n{e.TimeText}" + (e.Location != null ? $"\n{e.Location}" : "") + $"\n{e.Feed}",
        };
        var tb = new TextBlock { TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = Math.Max(9.5, S.FontSize - (compact ? 2 : 1)) };
        tb.Inlines.Add(new Run("  ") { FontFamily = (FontFamily)FindResource("IconFont"), FontSize = Math.Max(8, S.FontSize - 4), Foreground = B(MeetingColor) });
        tb.Inlines.Add(new Run(e.AllDay ? "" : e.Start.ToString("HH:mm") + "  ") { FontWeight = FontWeights.SemiBold, Foreground = B(MeetingColor) });
        tb.Inlines.Add(new Run(e.Title) { Foreground = Res("Fg") });
        row.Child = tb;
        row.MouseRightButtonUp += (_, ev) =>
        {
            ev.Handled = true;
            var menu = new ContextMenu();
            var add = new MenuItem { Header = L.T("Dodaj jako zadanie [Meeting]") };
            add.Click += (_, _) =>
            {
                var siblings = Store.Data.Tasks.Where(x => x.Day == dayKey);
                var t = new TaskItem
                {
                    Text = $"[Meeting] {(e.AllDay ? "" : e.Start.ToString("HH:mm") + " ")}{e.Title}",
                    Day = dayKey,
                    Estimate = e.Hours > 0 ? Math.Round(e.Hours * 2) / 2 : null,
                    Order = siblings.Any() ? siblings.Min(x => x.Order) - 1 : 0,
                };
                _justAdded = t.Id;
                Do(() => Store.Add(t));
            };
            menu.Items.Add(add);
            menu.PlacementTarget = row;
            menu.IsOpen = true;
        };
        return row;
    }

    static readonly Color AlarmColor = Color.FromRgb(0xF2, 0xB1, 0x4C);

    /// <summary>Alarm or meeting on a day (not a task): click edits, right click deletes.</summary>
    FrameworkElement BuildAlarmRow(Alarm a, DateTime date, bool compact)
    {
        var at = a.At(date);
        var color = a.IsMeeting ? MeetingColor : AlarmColor;
        bool past = (a.IsMeeting ? a.EndAt(date) : at) <= DateTime.Now;
        var what = a.IsMeeting ? L.T("Spotkanie") : L.T("Alarm");
        var time = a.IsMeeting ? $"{a.Time}–{a.EndAt(date):HH:mm}" : a.Time;
        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(5, compact ? 1 : 3, 4, compact ? 1 : 3),
            Margin = new Thickness(-4, 0, 0, 2),
            Background = B(A(color, a.IsMeeting ? (byte)0x1C : (byte)0x18)),
            BorderBrush = B(A(color, 0x50)),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Opacity = past ? 0.45 : 1,
            Cursor = Cursors.Hand,
            ToolTip = $"{what} {time}" + (a.Once ? "" : $"  ·  {a.RepeatLabel.ToLower(Pl)}")
                + (a.IsMeeting && a.Remind is { } m ? "\n" + L.F("przypomnienie {0}", m == 0 ? L.T("o czasie") : L.F("{0} min wcześniej", m)) : "")
                + (past ? "" : $"\n{AlarmService.Until(at)}") + "\n" + L.T("Kliknij, żeby zmienić"),
        };
        var tb = new TextBlock { TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = Math.Max(9.5, S.FontSize - (compact ? 2 : 1)) };
        tb.Inlines.Add(new Run(a.IsMeeting ? "  " : "  ") { FontFamily = (FontFamily)FindResource("IconFont"), FontSize = Math.Max(8, S.FontSize - 4), Foreground = B(color) });
        tb.Inlines.Add(new Run(time + "  ") { FontWeight = FontWeights.SemiBold, Foreground = B(color) });
        tb.Inlines.Add(new Run(a.Text.Length > 0 ? a.Text : what) { Foreground = Res("Fg") });
        if (!a.Once) tb.Inlines.Add(new Run("  ↻") { Foreground = Res("FgFaint") });
        row.Child = tb;
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; AlarmWindow.Edit(Store, a, date); };
        row.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var menu = new ContextMenu();
            var edit = new MenuItem { Header = a.IsMeeting ? L.T("Zmień spotkanie…") : L.T("Zmień alarm…") };
            edit.Click += (_, _) => AlarmWindow.Edit(Store, a, date);
            menu.Items.Add(edit);
            if (!a.Once)
            {
                var skip = new MenuItem { Header = L.T("Usuń tylko ten dzień") };
                skip.Click += (_, _) => { (a.Skips ??= new()).Add(DayKey(date)); Store.Changed(a); AlarmService.Reschedule(); };
                menu.Items.Add(skip);
            }
            var del = new MenuItem { Header = a.Once ? L.T("Usuń") : L.T("Usuń całą serię") };
            del.Click += (_, _) => { Store.DeleteAlarm(a); AlarmService.Reschedule(); };
            menu.Items.Add(del);
            menu.PlacementTarget = row;
            menu.IsOpen = true;
        };
        return row;
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
            var mi = new MenuItem { Header = (mark == code ? "✓  " : "     ") + (m.Label.Length > 0 && m.Label != code ? $"{m.Label} ({code})" : code) };
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
            var repeat = new MenuItem { Header = "     " + L.T("Powtarzaj oznaczenie…") };
            repeat.Click += (_, _) => RepeatWindow.ForMark(Store, null, date, mark);
            menu.Items.Add(repeat);
        }
        else
        {
            var setup = new MenuItem { Header = L.T("Oznaczenia dni (np. Urlop): dodaj w ustawieniach…") };
            setup.Click += (_, _) => App.Instance.ShowSettings();
            menu.Items.Add(setup);
        }
        menu.Items.Add(new Separator());
        var add = new MenuItem { Header = L.T("Dodaj zadanie") };
        add.Click += (_, _) => { _addingKey = key; Rebuild(); };
        menu.Items.Add(add);
        var alarm = new MenuItem { Header = L.T("Dodaj alarm…") };
        alarm.Click += (_, _) => AlarmWindow.Edit(Store, null, date);
        menu.Items.Add(alarm);
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

    static Color TagColor(string tag)
    {
        if (Store.CategoryFor(tag) is { } cat)
            try { return (Color)ColorConverter.ConvertFromString(cat.Color); } catch { }
        if (tag == TaskItem.MeetingTag) return MeetingColor;
        int h = 0;
        foreach (var ch in tag) h = h * 31 + ch;
        return (Color)ColorConverter.ConvertFromString(TagPalette[Math.Abs(h) % TagPalette.Length]);
    }

    // ---------- task row ----------

    FrameworkElement BuildTaskRow(TaskItem t, bool inBacklog, bool compact = false)
    {
        var wrapper = new Border
        {
            Tag = t,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4, compact ? 1 : 3, 1, compact ? 1 : 3),
            Margin = new Thickness(-4, 0, 0, compact ? 0 : 1),
        };
        Anim.HoverBackground(wrapper, Colors.Transparent, Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        wrapper.Child = grid;
        double fs = compact ? Math.Max(9.5, S.FontSize - 1.5) : S.FontSize;

        // completion circle (ring coloured by Notion priority)
        var prioColor = string.IsNullOrEmpty(t.Priority) ? (Color?)null : PriorityColor(t.Priority);
        var ringBrush = B(prioColor ?? Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));
        double cs = compact ? 12 : 14;
        var circle = new Border
        {
            Width = cs, Height = cs,
            CornerRadius = new CornerRadius(cs / 2),
            BorderThickness = new Thickness(prioColor != null ? 1.8 : 1.3),
            BorderBrush = ringBrush,
            Background = Brushes.Transparent,
            Margin = new Thickness(0, Math.Max(0, (fs * 1.33 - cs) / 2), compact ? 6 : 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.Hand,
            ToolTip = (t.Done ? L.T("Oznacz jako niezrobione") : L.T("Oznacz jako zrobione")) + (t.Priority != null ? "\n" + L.F("Priorytet: {0}", t.Priority) : ""),
        };
        if (t.Done)
        {
            circle.Background = Res("AccentBrush");
            circle.BorderBrush = Res("AccentBrush");
            circle.Child = new TextBlock
            {
                Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = compact ? 7.5 : 8.5,
                Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
        }
        circle.MouseEnter += (_, _) => { if (!t.Done) circle.BorderBrush = Res("AccentBrush"); };
        circle.MouseLeave += (_, _) => { if (!t.Done) circle.BorderBrush = ringBrush; };
        circle.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            Do(() =>
            {
                var real = Store.Materialize(t);
                real.Done = !real.Done;
                Store.Changed(real);
                _justToggled = real.Id;
            });
        };
        grid.Children.Add(circle);
        if (_justToggled == t.Id) Anim.Pop(circle);

        // text with tag chips (+ notion meta in backlog)
        var content = new StackPanel();
        Grid.SetColumn(content, 1);
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Res("Fg"), FontSize = fs };
        var tags = TaskItem.Tags(t.Text, out var rest);
        foreach (var tag in tags)
        {
            var c = TagColor(tag);
            var chip = new StackPanel { Orientation = Orientation.Horizontal };
            if (tag == TaskItem.MeetingTag)
                chip.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = Math.Max(7.5, fs - 4), Foreground = B(c), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 4, 0) });
            chip.Children.Add(new TextBlock { Text = tag == TaskItem.MeetingTag ? "Meeting" : tag, FontSize = Math.Max(8.5, fs - 3), FontWeight = FontWeights.Bold, Foreground = B(c) });
            text.Inlines.Add(new InlineUIContainer(new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 0, 4, 1),
                Margin = new Thickness(0, 0, 5, 0),
                Background = B(A(c, 0x2E)),
                Child = chip,
            }) { BaselineAlignment = BaselineAlignment.Center });
        }
        text.Inlines.Add(new Run(tags.Count > 0 ? rest : t.Text));
        if (t.RuleId != null)
            text.Inlines.Add(new Run("  ") { FontFamily = (FontFamily)FindResource("IconFont"), FontSize = Math.Max(8, fs - 4), Foreground = Res("FgFaint") });
        if (t.HasLink)
        {
            text.Inlines.Add(new Run("  ") { FontFamily = (FontFamily)FindResource("IconFont"), FontSize = Math.Max(8, fs - 4), Foreground = Res("AccentBrush") });
            text.Cursor = Cursors.Hand;
            text.ToolTip = L.T("Kliknij, aby otworzyć w Notion") +(t.Status != null ? $"\nStatus: {t.Status}" : "");
        }
        if (t.RuleId != null && Store.Rule(t.RuleId) is { } rule)
            text.ToolTip = (text.ToolTip is string s0 ? s0 + "\n" : "") + L.T("Seria: ") + RecurringRule.Patterns.FirstOrDefault(p => p.Code == rule.Pattern).Label;
        if (t.Checklist is { Count: > 0 } cl)
        {
            int cd = cl.Count(x => x.Done);
            var chip = new Border
            {
                CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 0, 4, 1), Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand,
                Background = B(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)), ToolTip = _expanded.Contains(t.Id) ? L.T("Zwiń listę") : L.T("Rozwiń listę"),
                Child = new TextBlock { Text = $"☑ {cd}/{cl.Count}", FontSize = Math.Max(8.5, fs - 2.5), Foreground = cd == cl.Count ? Res("AccentBrush") : Res("FgDim") },
            };
            chip.PreviewMouseLeftButtonDown += (_, e) => e.Handled = true;
            chip.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (!_expanded.Remove(t.Id)) _expanded.Add(t.Id);
                Rebuild();
            };
            text.Inlines.Add(new InlineUIContainer(chip) { BaselineAlignment = BaselineAlignment.Center });
        }
        if (t.Done)
        {
            text.TextDecorations = TextDecorations.Strikethrough;
            text.Opacity = 0.45;
        }
        content.Children.Add(text);
        if (_expanded.Contains(t.Id)) content.Children.Add(BuildChecklist(t, fs));

        if (inBacklog && (!string.IsNullOrEmpty(t.Priority) || !string.IsNullOrEmpty(t.Status)))
        {
            var meta = new WrapPanel { Margin = new Thickness(0, 3, 0, 1) };
            if (!string.IsNullOrEmpty(t.Priority)) meta.Children.Add(PriorityPill(t.Priority));
            if (!string.IsNullOrEmpty(t.Status))
                meta.Children.Add(new TextBlock { Text = t.Status, FontSize = 10.5, Foreground = Res("FgFaint"), VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(meta);
        }
        grid.Children.Add(content);

        if (t.Estimate is > 0)
        {
            var est = new TextBlock
            {
                Text = Hours(t.Estimate.Value),
                FontSize = Math.Max(9, fs - 2.5),
                Foreground = Res("FgFaint"),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(6, 1, 0, 0),
                Opacity = t.Done ? 0.5 : 1,
            };
            Grid.SetColumn(est, 2);
            grid.Children.Add(est);
        }

        // archive (on hover)
        var del = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = "",
            FontSize = 8,
            Padding = new Thickness(4, 3, 4, 3),
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Hidden,
            ToolTip = t.IsVirtual ? L.T("Pomiń to wystąpienie serii") : L.T("Archiwizuj (albo kliknij kółkiem myszy)"),
        };
        Grid.SetColumn(del, 3);
        del.Click += (_, _) => ArchiveRow(t, wrapper);
        grid.Children.Add(del);

        wrapper.MouseEnter += (_, _) => del.Visibility = Visibility.Visible;
        wrapper.MouseLeave += (_, _) => del.Visibility = Visibility.Hidden;

        // click / drag / middle-click / context menu
        wrapper.MouseLeftButtonDown += (_, e) => { _pressed = t; _pressPoint = e.GetPosition(this); };
        wrapper.MouseMove += (_, e) =>
        {
            if (_pressed != t || e.LeftButton != MouseButtonState.Pressed) return;
            var d = e.GetPosition(this) - _pressPoint;
            if (Math.Abs(d.X) + Math.Abs(d.Y) < 5) return;
            _pressed = null;
            StartDrag(t, wrapper);
        };
        wrapper.MouseLeftButtonUp += (_, e) =>
        {
            if (_pressed != t) return;
            _pressed = null;
            e.Handled = true;
            if (t.HasLink) OpenLink(t);
            else BeginEdit(t, content, text, series: false);
        };
        wrapper.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) { e.Handled = true; ArchiveRow(t, wrapper); }
        };
        wrapper.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var menu = BuildMenu(t, content, text, wrapper);
            menu.PlacementTarget = wrapper;
            menu.IsOpen = true;
        };
        if (_justAdded == t.Id) Anim.Enter(wrapper, 0, -6, 240);
        if (_flashId == t.Id) Flash(wrapper);
        return wrapper;
    }

    readonly HashSet<string> _expanded = new();  // tasks whose checklist is open
    string? _checkAddFor;                         // task whose "+ punkt" box is open

    /// <summary>Sub-task list under a task: tick, add, remove. Every change is a normal (undoable, synced) task edit.</summary>
    FrameworkElement BuildChecklist(TaskItem t, double fs)
    {
        var list = new StackPanel { Margin = new Thickness(0, 3, 0, 1) };
        var items = t.Checklist ?? new List<CheckItem>();
        void Edit(Action<List<CheckItem>> change) => Do(() =>
        {
            var real = Store.Materialize(t);
            real.Checklist ??= new List<CheckItem>();
            change(real.Checklist);
            if (real.Checklist.Count == 0) real.Checklist = null;
            Store.Changed(real);
            _expanded.Add(real.Id);
        });
        for (int i = 0; i < items.Count; i++)
        {
            int index = i;
            var item = items[i];
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var box = new Border
            {
                Width = 11, Height = 11, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1.2), Cursor = Cursors.Hand,
                BorderBrush = item.Done ? Res("AccentBrush") : B(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
                Background = item.Done ? Res("AccentBrush") : Brushes.Transparent, Margin = new Thickness(1, 2, 7, 0), VerticalAlignment = VerticalAlignment.Top,
            };
            var label = new TextBlock { Text = item.Text, TextWrapping = TextWrapping.Wrap, FontSize = Math.Max(9.5, fs - 1.5), Foreground = Res("FgDim"), Cursor = Cursors.Hand };
            if (item.Done) { label.TextDecorations = TextDecorations.Strikethrough; label.Opacity = 0.6; }
            Grid.SetColumn(label, 1);
            var del = new Button { Style = (Style)FindResource("IconButton"), Content = "", FontSize = 7, Padding = new Thickness(3, 2, 3, 2), Visibility = Visibility.Hidden, ToolTip = L.T("Usuń punkt") };
            Grid.SetColumn(del, 2);
            del.Click += (_, _) => Edit(l => l.RemoveAt(index));
            MouseButtonEventHandler toggle = (_, e) => { e.Handled = true; Edit(l => l[index].Done = !l[index].Done); };
            box.MouseLeftButtonDown += toggle;
            label.MouseLeftButtonDown += toggle;
            row.MouseEnter += (_, _) => del.Visibility = Visibility.Visible;
            row.MouseLeave += (_, _) => del.Visibility = Visibility.Hidden;
            row.Children.Add(box);
            row.Children.Add(label);
            row.Children.Add(del);
            list.Children.Add(row);
        }

        // "+ punkt"
        var addHost = new Border { Background = Brushes.Transparent, Margin = new Thickness(0, 1, 0, 0) };
        void OpenAdd()
        {
            var input = new TextBox { Style = (Style)FindResource("InlineBox"), FontSize = Math.Max(9.5, fs - 1.5) };
            addHost.Child = input;
            bool finished = false;
            Action cancel = () => finished = true;
            _editors.Add(cancel);
            void Finish(bool keepOpen)
            {
                if (finished) return;
                finished = true;
                _editors.Remove(cancel);
                var v = input.Text.Trim();
                _checkAddFor = keepOpen && v.Length > 0 ? t.Id : null;
                Commit(v.Length > 0 ? () =>
                {
                    var real = Store.Materialize(t);
                    (real.Checklist ??= new List<CheckItem>()).Add(new CheckItem { Text = v });
                    Store.Changed(real);
                    _expanded.Add(real.Id);
                    if (_checkAddFor != null) _checkAddFor = real.Id;
                } : null, addHost);
            }
            input.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; Finish(true); }
                else if (e.Key == Key.Escape) { e.Handled = true; input.Text = ""; Finish(false); }
            };
            input.LostKeyboardFocus += (_, _) => Finish(false);
            FocusBox(input);
        }
        var addLabel = new TextBlock { Text = L.T("+  punkt"), FontSize = Math.Max(9.5, fs - 2), Foreground = Res("FgFaint"), Cursor = Cursors.IBeam, Margin = new Thickness(1, 0, 0, 0) };
        addHost.Child = addLabel;
        addHost.MouseLeftButtonDown += (_, e) => e.Handled = true; // not a drag / edit of the task
        addHost.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (addHost.Child is TextBlock) OpenAdd(); };
        list.Children.Add(addHost);
        if (_checkAddFor == t.Id) addHost.Loaded += (_, _) => OpenAdd();
        return list;
    }

    /// <summary>Search result highlight: pulse the row and scroll it into view.</summary>
    static void Flash(Border row)
    {
        row.Loaded += (_, _) =>
        {
            row.BringIntoView();
            var brush = B(Colors.Transparent);
            row.Background = brush;
            var accent = ((SolidColorBrush)Res("AccentBrush")).Color;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(A(accent, 0x70), Colors.Transparent, TimeSpan.FromMilliseconds(1800)) { BeginTime = TimeSpan.FromMilliseconds(250) });
        };
    }

    static readonly Regex LeadingSymbols = new(@"^[^\p{L}\p{N}]+");

    public static string CleanLabel(string s) => LeadingSymbols.Replace(s.Trim(), "").Trim();

    public static Color PriorityColor(string priority)
    {
        var p = priority.ToLowerInvariant();
        return p.Contains("high") || p.Contains("wysok") || p.Contains("urgent") || p.Contains("pilne") || p.Contains("krytycz") || p.Contains("critical") ? Color.FromRgb(0xE0, 0x5A, 0x55)
            : p.Contains("medium") || p.Contains("średn") || p.Contains("normal") ? Color.FromRgb(0x4A, 0x8B, 0xE8)
            : p.Contains("low") || p.Contains("nisk") ? Color.FromRgb(0x46, 0xB0, 0x6E)
            : Color.FromRgb(0x8A, 0x90, 0x9E);
    }

    FrameworkElement PriorityPill(string priority) => new Border
    {
        Background = B(PriorityColor(priority)),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(5, 0, 5, 1),
        Margin = new Thickness(0, 0, 7, 0),
        Child = new TextBlock { Text = priority, FontSize = 10.5, Foreground = Brushes.White },
    };

    void OpenLink(TaskItem t)
    {
        if (string.IsNullOrEmpty(t.Url)) return;
        var url = t.Url;
        if (S.OpenNotionInApp && url.StartsWith("https://www.notion.so/", StringComparison.OrdinalIgnoreCase))
            url = "notion://www.notion.so/" + url["https://www.notion.so/".Length..];
        OpenUrl(url);
    }

    /// <summary>✕ / middle click: archive (a virtual series occurrence is skipped instead).</summary>
    void ArchiveRow(TaskItem t, FrameworkElement row) => Anim.Leave(row, () => Do(() =>
    {
        if (t.IsVirtual && Store.Rule(t.RuleId) is { } r)
        {
            if (!r.Skips.Contains(t.RuleDay!)) r.Skips.Add(t.RuleDay!);
            Store.Changed(r);
        }
        else if (Store.Find(t.Id) is { } live) Store.Archive(live); // the live instance (row may be older than a merge)
    }));

    void DeleteForGood(TaskItem t, FrameworkElement row) => Anim.Leave(row, () => Do(() =>
    {
        if (t.IsVirtual && Store.Rule(t.RuleId) is { } r) { if (!r.Skips.Contains(t.RuleDay!)) r.Skips.Add(t.RuleDay!); Store.Changed(r); }
        else if (Store.Find(t.Id) is { } live) Store.Remove(live);
    }));

    // ---------- inline editing ----------

    void BeginEdit(TaskItem t, StackPanel content, TextBlock text, bool series)
    {
        int idx = content.Children.IndexOf(text);
        // a context menu can outlive its row (rebuilt meanwhile): never edit inside detached visuals
        if (idx < 0 || PresentationSource.FromVisual(content) == null) return;
        var rule = series ? Store.Rule(t.RuleId) : null;
        var box = new TextBox { Style = (Style)FindResource("InlineBox"), Text = rule?.Text ?? t.Text, FontSize = S.FontSize };
        content.Children.RemoveAt(idx);
        content.Children.Insert(idx, box);
        bool finished = false;
        Action cancel = () => finished = true;
        _editors.Add(cancel);

        void Finish(bool commit)
        {
            if (finished) return;
            finished = true;
            _editors.Remove(cancel);
            var v = box.Text.Trim();
            Action? change = null;
            if (commit && rule != null && v.Length > 0 && v != rule.Text)
                change = () => { rule.Text = v; Store.Changed(rule); };
            else if (commit && rule == null && v.Length == 0)
                change = () => { if (t.IsVirtual && Store.Rule(t.RuleId) is { } r) { r.Skips.Add(t.RuleDay!); Store.Changed(r); } else Store.Archive(Store.Materialize(t)); };
            else if (commit && rule == null && v != t.Text)
                change = () => { var real = Store.Materialize(t); real.Text = v; Store.Changed(real); };
            Commit(change, null);
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Finish(true); }
            else if (e.Key == Key.Escape) { e.Handled = true; Finish(false); }
        };
        box.LostKeyboardFocus += (_, _) => Finish(true);
        FocusBox(box);
    }

    FrameworkElement BuildAddRow(string key, DateTime? date, bool compact = false)
    {
        var host = new Border { Margin = new Thickness(0, compact ? 1 : 3, 0, 0), Background = Brushes.Transparent };
        if (_addingKey == key)
        {
            host.Child = BuildAddEditor(key);
            return host;
        }
        var label = new TextBlock
        {
            Text = key == BacklogKey ? L.T("+  dodaj do backlogu") : compact ? "+" : L.T("+  dodaj zadanie"),
            Foreground = Res("FgFaint"),
            FontSize = Math.Max(10, S.FontSize - 1),
            Cursor = Cursors.IBeam,
            Padding = new Thickness(1, compact ? 0 : 2, 0, 2),
            Opacity = date.HasValue && date < DateTime.Today ? 0.6 : 1,
            ToolTip = compact ? L.T("Dodaj zadanie") : null,
        };
        host.MouseEnter += (_, _) => label.Foreground = Res("FgDim");
        host.MouseLeave += (_, _) => label.Foreground = Res("FgFaint");
        host.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (host.Child is not TextBlock) return; // already editing (e.g. a category chip was clicked)
            _addingKey = key;
            host.Child = BuildAddEditor(key);
        };
        host.Child = label;
        return host;
    }

    FrameworkElement BuildAddEditor(string key)
    {
        var host = new StackPanel();
        var grid = new Grid();
        host.Children.Add(grid);
        var box = new TextBox { Style = (Style)FindResource("FieldBox"), FontSize = S.FontSize, Padding = new Thickness(6, 3, 6, 3), Text = _addPrefix ?? "" };
        _addPrefix = null;
        var hint = new TextBlock
        {
            Text = L.T("nowe zadanie…  ([Meeting], [ART]…)"), Foreground = Res("FgFaint"), FontSize = S.FontSize,
            Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
            Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        // smart add preview: "jutro … #art 2h" → where / how long / which category
        var preview = new TextBlock { FontSize = 10.5, Foreground = Res("AccentBrush"), Margin = new Thickness(2, 3, 0, 0), Visibility = Visibility.Collapsed };
        box.TextChanged += (_, _) =>
        {
            hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var p = SmartAdd.Parse(box.Text, Store.Categories);
            var desc = SmartAdd.Describe(p);
            if (TaskItem.Tags(p.Text, out var rest).Contains(TaskItem.MeetingTag))
                desc = AlarmService.ParseMeeting(rest, p.Estimate, out var f, out var t, out _)
                    ? L.F("→ spotkanie {0:hh\\:mm}–{1:hh\\:mm}", f, t) + (p.Day is { } d ? $", {d.ToString("ddd d MMM", Pl)}" : "")
                    : L.T("→ dopisz godziny, np. 14-15:30 (albo kliknij Meeting)");
            preview.Text = desc;
            preview.Visibility = desc.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        hint.Text = key == BacklogKey ? L.T("nowe zadanie…  (jutro, pt, 12.10, 2h, #art)") : L.T("nowe zadanie…  (2h, #art, jutro…)");
        grid.Children.Add(box);
        grid.Children.Add(hint);
        host.Children.Add(preview);

        // category chips: click toggles "[Name] " in front of the text (not focusable, so the box keeps focus)
        var chips = new WrapPanel { Margin = new Thickness(0, 4, 0, 2) };
        foreach (var c in Store.Categories)
        {
            Color col;
            try { col = (Color)ColorConverter.ConvertFromString(c.Color); } catch { col = Colors.Gray; }
            var chip = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(0, 0, 4, 3),
                Background = B(A(col, 0x24)), Cursor = Cursors.Hand,
                Child = new TextBlock { Text = c.Name, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = B(col) },
            };
            chip.PreviewMouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true; // the chip isn't focusable: the text box keeps focus and the user keeps typing
                if (string.Equals(c.Name, TaskItem.MeetingTag, StringComparison.OrdinalIgnoreCase))
                {
                    // a meeting is not a task: open the meeting window with what was typed so far
                    TaskItem.Tags(SmartAdd.Parse(box.Text, Store.Categories).Text, out var title);
                    var day = key == BacklogKey ? DateTime.Today : DateTime.ParseExact(key, "yyyy-MM-dd", null);
                    box.Text = "";
                    // the window takes the focus: the empty editor then closes by itself
                    Dispatcher.BeginInvoke(() => AlarmWindow.Edit(Store, null, day, meeting: true, title: title));
                    return;
                }
                box.Text = ToggleCategory(box.Text, c.Name);
                box.CaretIndex = box.Text.Length;
                Keyboard.Focus(box);
            };
            chip.MouseLeftButtonUp += (_, e) => e.Handled = true;
            chips.Children.Add(chip);
        }
        if (chips.Children.Count > 0) host.Children.Add(chips);
        Anim.Enter(host, 0, -4, 180);
        bool finished = false;
        Action cancel = () => finished = true;
        _editors.Add(cancel);

        void Finish(bool keepOpen)
        {
            if (finished) return;
            finished = true;
            _editors.Remove(cancel);
            var parsed = SmartAdd.Parse(box.Text, Store.Categories);
            var v = parsed.Text;
            bool empty = v.Length == 0 || TaskItem.Tags(v, out var rest) is { Count: > 0 } && rest.Length == 0;
            if (_addingKey == key) _addingKey = keepOpen && !empty ? key : null; // another "+" may have taken over meanwhile
            Action? change = null;
            // "[Meeting] 14-15:30 Sprint" / "#meeting o 10 daily 30 min" → a meeting (with its hours), not a task
            var tags = TaskItem.Tags(v, out var withoutTags);
            if (!empty && tags.Contains(TaskItem.MeetingTag) && AlarmService.ParseMeeting(withoutTags, parsed.Estimate, out var from, out var to, out var title))
            {
                var day = parsed.Day ?? (key == BacklogKey ? DateTime.Today : DateTime.ParseExact(key, "yyyy-MM-dd", null));
                var meeting = new Alarm { Kind = "meeting", Text = title, Day = DayKey(day), Time = from.ToString(@"hh\:mm"), End = to.ToString(@"hh\:mm"), Remind = 5 };
                change = () => { Store.AddAlarm(meeting); Dispatcher.BeginInvoke(AlarmService.Reschedule); };
                empty = true;
            }
            if (!empty)
            {
                string? day = parsed.Day is { } pd ? DayKey(pd) : key == BacklogKey ? null : key;
                var siblings = Store.Data.Tasks.Where(x => x.Day == day && !x.Archived);
                var t = new TaskItem { Text = v, Day = day, Estimate = parsed.Estimate, Order = siblings.Any() ? siblings.Max(x => x.Order) + 1 : 0 };
                _justAdded = t.Id;
                change = () => Store.Add(t);
                if (parsed.Day != null && day != key) // went to another day: say where, and show that week if it's off-screen
                {
                    SyncText.Text = L.F("dodano: {0}", parsed.Day.Value.ToString("ddd d MMM", Pl));
                    _syncFlash.Stop();
                    _syncFlash.Start();
                }
            }
            Commit(change, host);
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Finish(keepOpen: true); }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                box.Text = "";
                Finish(keepOpen: false);
                if (IsPeeking) Peek(false);
            }
        };
        box.LostKeyboardFocus += (_, _) => Finish(keepOpen: false);
        FocusBox(box);
        return host;
    }

    /// <summary>
    /// Applies an editor's result. When another editor is still open (the user clicked into a second one),
    /// the data is changed without a rebuild, so the new editor isn't destroyed; it rebuilds when it closes.
    /// </summary>
    void Commit(Action? change, UIElement? editorUi)
    {
        if (_openEditors > 0)
        {
            if (change != null) { Store.Checkpoint(); change(); }
            if (editorUi != null) editorUi.Visibility = Visibility.Collapsed;
            return;
        }
        if (change != null) Do(change);
        else Rebuild();
    }

    void FocusBox(TextBox box)
    {
        void Go()
        {
            Activate();
            box.Focus();
            Keyboard.Focus(box);
            box.CaretIndex = box.Text.Length;
            (box.Parent as FrameworkElement ?? box).BringIntoView(); // a full day scrolls down to the new box
        }
        if (box.IsLoaded) Go();
        else box.Loaded += (_, _) => Go();
    }

    // ---------- drag & drop ----------

    void StartDrag(TaskItem t, FrameworkElement source)
    {
        bool materialized = false;
        if (t.IsVirtual)
        {
            Store.Checkpoint();
            t = Store.Materialize(t);
            materialized = true;
        }
        if (_drawerOpen && IsInside(source, BacklogPanel)) SetDrawer(false); // make room for dropping on the days
        source.Opacity = 0.35;
        try
        {
            var result = DragDrop.DoDragDrop(source, new DataObject(DragFormat, t.Id), DragDropEffects.Move | DragDropEffects.Copy);
            if (result == DragDropEffects.None && materialized && Store.Undo()) Rebuild(); // cancelled: back to a virtual occurrence
        }
        finally
        {
            source.Opacity = 1;
            HideDropLine();
        }
    }

    void Root_DragOver(object sender, DragEventArgs e)
    {
        // Reached only when no day / backlog handled the event: not a valid target – except a Notion export file
        e.Effects = ExternalDrop.ImportFile(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None;
        HideDropLine();
        e.Handled = true;
    }

    void OnDragOverTarget(StackPanel panel, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(DragFormat))
        {
            // from outside: a link / text becomes a task here, a CSV/ZIP opens the import
            e.Effects = ExternalDrop.Accepts(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
            if (e.Effects != DragDropEffects.None && ExternalDrop.ImportFile(e.Data) == null) ShowDropLine(panel, DropIndex(panel, e));
            else HideDropLine();
            return;
        }
        e.Effects = (e.KeyStates & DragDropKeyStates.ControlKey) != 0 ? DragDropEffects.Copy : DragDropEffects.Move;
        ShowDropLine(panel, DropIndex(panel, e));
    }

    static List<FrameworkElement> TaskRows(Panel panel) =>
        panel.Children.OfType<FrameworkElement>().Where(c => c.Tag is TaskItem).ToList();

    static int DropIndex(Panel panel, DragEventArgs e)
    {
        var rows = TaskRows(panel);
        for (int i = 0; i < rows.Count; i++)
            if (e.GetPosition(rows[i]).Y < rows[i].ActualHeight / 2) return i;
        return rows.Count;
    }

    void ShowDropLine(StackPanel panel, int rowIndex)
    {
        _dropLine ??= new Border { Height = 2, CornerRadius = new CornerRadius(1), Margin = new Thickness(0, 1, 4, 1), IsHitTestVisible = false };
        _dropLine.Background = Res("AccentBrush");
        var rows = TaskRows(panel);
        int at = rowIndex < rows.Count ? panel.Children.IndexOf(rows[rowIndex])
               : rows.Count > 0 ? panel.Children.IndexOf(rows[^1]) + 1 : panel.Children.Count - 1;
        if (_dropLine.Parent == panel)
        {
            int cur = panel.Children.IndexOf(_dropLine);
            if (cur == at || cur == at - 1) return; // already there (avoid flicker)
        }
        if (_dropLine.Parent is Panel old) old.Children.Remove(_dropLine);
        rows = TaskRows(panel);
        at = rowIndex < rows.Count ? panel.Children.IndexOf(rows[rowIndex])
           : rows.Count > 0 ? panel.Children.IndexOf(rows[^1]) + 1 : Math.Max(0, panel.Children.Count - 1);
        panel.Children.Insert(at, _dropLine);
    }

    void HideDropLine()
    {
        if (_dropLine?.Parent is Panel p) p.Children.Remove(_dropLine);
    }

    void OnDrop(string? day, StackPanel panel, DragEventArgs e)
    {
        e.Handled = true;
        HideDropLine();
        if (!e.Data.GetDataPresent(DragFormat)) { DropExternal(day, panel, e); return; }
        if (e.Data.GetData(DragFormat) is not string id) return;
        var t = Store.Data.Tasks.FirstOrDefault(x => x.Id == id);
        if (t == null) return;
        bool copy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;

        var visible = TaskRows(panel).Select(r => (TaskItem)r.Tag).ToList();
        int index = DropIndex(panel, e);
        int origin = visible.IndexOf(t);
        if (origin >= 0 && !copy)
        {
            if (origin < index) index--;
            visible.RemoveAt(origin);
            if (origin == index) { Dispatcher.BeginInvoke(Rebuild); return; } // dropped where it already was (clears the highlight)
        }
        double order = OrderBetween(index > 0 ? visible[index - 1] : null, index < visible.Count ? visible[index] : null);

        Store.Checkpoint();
        if (copy)
        {
            var c = t.Clone();
            c.Id = Guid.NewGuid().ToString("N");
            c.Day = day;
            c.Order = order;
            c.RuleId = c.RuleDay = null;
            c.Archived = false; // a copy dragged out of the archive goes onto the board
            c.ArchivedAt = null;
            c.NotionId = null;  // the Notion import keeps updating only the original
            Store.Add(c);
            _justAdded = c.Id;
        }
        else
        {
            if (t.Archived) { t.Archived = false; t.ArchivedAt = null; } // dragged out of the archive tab
            t.Day = day;
            t.Order = order;
            Store.Changed(t);
            _justAdded = t.Id;
        }
        Dispatcher.BeginInvoke(Rebuild); // let DoDragDrop unwind before replacing the visuals
    }

    /// <summary>Link / text / file dropped from another program.</summary>
    void DropExternal(string? day, StackPanel panel, DragEventArgs e)
    {
        if (ExternalDrop.ImportFile(e.Data) is { } file)
        {
            Dispatcher.BeginInvoke(() => App.Instance.ImportNotion(file));
            return;
        }
        var items = ExternalDrop.Tasks(e.Data);
        if (items.Count == 0) return;
        var visible = TaskRows(panel).Select(r => (TaskItem)r.Tag).ToList();
        int index = DropIndex(panel, e);
        double prev = index > 0 ? visible[index - 1].Order : (visible.Count > 0 ? visible[0].Order - 1 : 0);
        double next = index < visible.Count ? visible[index].Order : prev + items.Count + 1;
        Dispatcher.BeginInvoke(() => Do(() =>
        {
            for (int i = 0; i < items.Count; i++)
            {
                var t = items[i];
                t.Day = day;
                t.Order = prev + (next - prev) * (i + 1) / (items.Count + 1);
                Store.Add(t);
                _justAdded = t.Id;
            }
        }));
    }

    static double OrderBetween(TaskItem? prev, TaskItem? next) =>
        prev == null && next == null ? 0 :
        prev == null ? next!.Order - 1 :
        next == null ? prev.Order + 1 :
        (prev.Order + next.Order) / 2;

    static void MoveToEnd(TaskItem t, string? day)
    {
        t = Store.Materialize(t);
        var siblings = Store.Data.Tasks.Where(x => x.Day == day && x != t && !x.Archived).ToList();
        t.Day = day;
        t.Order = siblings.Count > 0 ? siblings.Max(x => x.Order) + 1 : 0;
        Store.Changed(t);
    }
}
