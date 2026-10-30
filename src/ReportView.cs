using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace TaskWall;

/// <summary>
/// The report as a view of the board (it was a window before 0.41): the week or the month always on screen. On the left
/// the numbers (done, hours, the days, categories, how long multi-day tasks took, held / stuck tasks, the estimate against
/// measured time, the backlog), on the right all the period's tasks – grouped by project or not, sorted by date, category,
/// priority or name, all / done / open. ‹ › of the top bar go to other periods; saved as HTML (the account's "raporty"
/// folder) or CSV.
/// </summary>
public sealed class ReportView : Grid
{
    readonly Func<BoardStore> _store;
    readonly Action<DateTime> _jump;
    readonly Action _periodChanged;
    public bool Month { get; private set; }
    public DateTime From { get; private set; } = BoardWindow.Monday(DateTime.Today);
    DateTime To => Month ? From.AddMonths(1).AddDays(-1) : From.AddDays(6);
    Report _report = null!;
    public string Title => _report?.Title ?? "";

    readonly StackPanel _left = new() { Margin = new Thickness(0, 0, 18, 16) };
    readonly StackPanel _list = new() { Margin = new Thickness(0, 0, 14, 16) };
    readonly WrapPanel _listBar = new() { Margin = new Thickness(0, 0, 0, 8) };
    readonly ToggleButton _weekTab = new() { Content = L.T("Tydzień"), Style = (Style)Application.Current.Resources["Chip"] };
    readonly ToggleButton _monthTab = new() { Content = L.T("Miesiąc"), Style = (Style)Application.Current.Resources["Chip"] };
    readonly TextBlock _title = Label("", 20, "Fg", FontWeights.Bold);

    static AppSettings S => App.Settings;

    public ReportView(Func<BoardStore> store, Action<DateTime> jump, Action periodChanged)
    {
        _store = store;
        _jump = jump;
        _periodChanged = periodChanged;
        Margin = new Thickness(18, 6, 8, 0);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star), MinWidth = 380 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7, GridUnitType.Star) });

        // ----- header: Tydzień | Miesiąc   Październik 2026  ········  Folder raportów  CSV…  Zapisz i otwórz raport -----
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetColumnSpan(head, 3);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        tabs.Children.Add(_weekTab);
        tabs.Children.Add(_monthTab);
        _weekTab.Click += (_, _) => Go(From, false);
        _monthTab.Click += (_, _) => Go(From, true);
        DockPanel.SetDock(tabs, Dock.Left);
        head.Children.Add(tabs);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(Btn(L.T("Folder raportów"), "LinkButton", (_, _) => { Directory.CreateDirectory(ReportsFolder); GlassWindow.OpenUrl(ReportsFolder); }));
        buttons.Children.Add(Btn(L.T("CSV…"), "SecondaryButton", (_, _) => SaveCsv()));
        buttons.Children.Add(Btn(L.T("Zapisz i otwórz raport"), "PrimaryButton", (_, _) => SaveHtml()));
        DockPanel.SetDock(buttons, Dock.Right);
        head.Children.Add(buttons);
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.Margin = new Thickness(16, 0, 0, 0);
        _title.TextWrapping = TextWrapping.NoWrap;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        head.Children.Add(_title);
        Children.Add(head);

        var left = Scroll(_left);
        Grid.SetRow(left, 1);
        Children.Add(left);
        var right = new DockPanel();
        DockPanel.SetDock(_listBar, Dock.Top);
        right.Children.Add(_listBar);
        right.Children.Add(Scroll(_list));
        Grid.SetRow(right, 1);
        Grid.SetColumn(right, 2);
        Children.Add(right);
    }

    static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

    static DateTime Start(DateTime d, bool month) => month ? new DateTime(d.Year, d.Month, 1) : BoardWindow.Monday(d);

    /// <summary>The week or the month around <paramref name="day"/>, shown on the next <see cref="Refresh"/>.</summary>
    public void Set(DateTime day, bool month)
    {
        Month = month;
        From = Start(day, month);
    }

    /// <summary>Another period, shown now (the board's top bar follows).</summary>
    void Go(DateTime day, bool month)
    {
        Set(day, month);
        Refresh();
        _periodChanged();
    }

    public void Step(int dir) => Go(Month ? From.AddMonths(dir) : From.AddDays(7 * dir), Month);

    public void ThisPeriod() => Go(DateTime.Today, Month);

    // the account's folder (not the local copy used while the cloud drive is away)
    static string ReportsFolder => Path.Combine(App.LayerFolder(S.Layer), "raporty");

    public void Refresh()
    {
        using var _ = Watchdog.Step("the report view");
        _weekTab.IsChecked = !Month;
        _monthTab.IsChecked = Month;
        _report = Report.Build(_store(), S.Layer, From, To, Month);
        _title.Text = _report.Title;
        FillSummary();
        FillList();
    }

    // ---------- the left column: the numbers ----------

    void FillSummary()
    {
        var r = _report;
        var pl = BoardWindow.Pl;
        _left.Children.Clear();

        var stats = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 10) };
        void Stat(string value, string label, bool accent = false)
        {
            var s = new StackPanel { Margin = new Thickness(0, 0, 8, 8) };
            s.Children.Add(Label(value, 20, accent ? "AccentBrush" : "Fg", FontWeights.Bold));
            s.Children.Add(Label(label, 11, "FgFaint"));
            stats.Children.Add(s);
        }
        Stat($"{r.Done}/{r.Tasks}", L.T("zadań zrobionych"), accent: true);
        Stat(BoardWindow.Hours(r.DoneHours), L.F("z {0} zaplanowanych", BoardWindow.Hours(r.Hours)));
        Stat(BoardWindow.Hours(r.Meetings), L.T("spotkań"));
        if (r.Overtime > 0) Stat(BoardWindow.Hours(Math.Round(r.Overtime, 2)), L.T("nadgodzin"));
        if (r.Afk > 0) Stat(BoardWindow.Hours(Math.Round(r.Afk, 2)), L.T("przerw (AFK, lunch, prywatne)"));
        if (r.Tracked > 0) Stat(BoardWindow.Hours(Math.Round(r.Tracked, 1)), L.T("zmierzonego czasu pracy (łącznie na tych zadaniach)"));
        Stat((r.Open.Count() + r.Carried.Count).ToString(), L.T("przeszło dalej / otwarte"));
        foreach (var (code, m) in r.Marks) Stat(m.Days.ToString(), $"{MarkType.Show(code)} · {MarkType.ShowLabel(code, m.Label)}");
        _left.Children.Add(stats);

        // done per day: accent = done, faint = still open; a click opens the day
        _left.Children.Add(Caption(L.T("ZROBIONE W DNIACH")));
        var bars = new Grid { Height = 90, Margin = new Thickness(0, 4, 0, 14) };
        int max = Math.Max(1, r.Days.Max(d => d.Tasks));
        for (int i = 0; i < r.Days.Count; i++)
        {
            var d = r.Days[i];
            bars.ColumnDefinitions.Add(new ColumnDefinition());
            var col = new DockPanel { Margin = new Thickness(r.Month ? 1 : 3, 0, r.Month ? 1 : 3, 0), ToolTip = $"{d.Date.ToString("dddd d MMM", pl)}: {d.Done}/{d.Tasks}" + (d.Hours > 0 ? $" · {BoardWindow.Hours(d.DoneHours)}/{BoardWindow.Hours(d.Hours)}" : ""), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            bool showLabel = !r.Month || d.Date.Day == 1 || d.Date.Day % 5 == 0;
            var name = Label(showLabel ? (r.Month ? d.Date.Day.ToString() : pl.DateTimeFormat.GetAbbreviatedDayName(d.Date.DayOfWeek).TrimEnd('.')) : "", 10, d.Date == DateTime.Today ? "AccentBrush" : "FgFaint");
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.TextWrapping = TextWrapping.NoWrap;
            DockPanel.SetDock(name, Dock.Bottom);
            col.Children.Add(name);
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            if (!r.Month && d.Tasks > 0)
            {
                var n = Label($"{d.Done}/{d.Tasks}", 10, "FgDim");
                n.HorizontalAlignment = HorizontalAlignment.Center;
                stack.Children.Add(n);
            }
            if (d.Tasks > d.Done) stack.Children.Add(new Border { CornerRadius = new CornerRadius(3, 3, 0, 0), Height = 54.0 * (d.Tasks - d.Done) / max, Background = new SolidColorBrush(Th.Ink(0x20)) });
            if (d.Done > 0) stack.Children.Add(new Border { CornerRadius = new CornerRadius(d.Tasks > d.Done ? 0 : 3, d.Tasks > d.Done ? 0 : 3, 2, 2), Height = 54.0 * d.Done / max, Background = Ui.Res("AccentBrush") });
            if (d.Tasks == 0) stack.Children.Add(new Border { Height = 2, CornerRadius = new CornerRadius(1), Background = new SolidColorBrush(Th.Ink(0x14)) });
            col.Children.Add(stack);
            var date = d.Date;
            col.MouseEnter += (_, _) => col.Opacity = 0.7;
            col.MouseLeave += (_, _) => col.Opacity = 1;
            col.MouseLeftButtonUp += (_, _) => _jump(date);
            Grid.SetColumn(col, i);
            bars.Children.Add(col);
        }
        _left.Children.Add(bars);

        if (r.Categories.Count > 0)
        {
            _left.Children.Add(Caption(L.T("KATEGORIE")));
            foreach (var c in r.Categories)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                var name = new StackPanel { Orientation = Orientation.Horizontal };
                Color col;
                try { col = (Color)ColorConverter.ConvertFromString(c.Color); } catch { col = Colors.Gray; }
                name.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), Background = new SolidColorBrush(col), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                name.Children.Add(Label(c.Name, 12, "Fg"));
                row.Children.Add(name);
                var meter = new Grid { Height = 5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                meter.Children.Add(new Border { CornerRadius = new CornerRadius(2.5), Background = new SolidColorBrush(Th.Ink(0x18)) });
                meter.Children.Add(new Border { CornerRadius = new CornerRadius(2.5), Background = Ui.Res("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Left, Width = 98.0 * c.Done / Math.Max(1, c.Tasks) });
                Grid.SetColumn(meter, 1);
                row.Children.Add(meter);
                var nums = Label($"{c.Done}/{c.Tasks}" + (c.Hours > 0 ? $"  ·  {BoardWindow.Hours(c.DoneHours)}/{BoardWindow.Hours(c.Hours)}" : ""), 11.5, "FgDim");
                nums.TextAlignment = TextAlignment.Right;
                Grid.SetColumn(nums, 2);
                row.Children.Add(nums);
                _left.Children.Add(row);
            }
        }

        // how long the finished tasks took, per project, and in which state the days went
        if (r.Projects.Count > 0)
        {
            _left.Children.Add(Caption(L.T("CZAS DO ZROBIENIA (ZADANIA WIELODNIOWE)")));
            foreach (var p in r.Projects)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2), ToolTip = L.F("{0} zadań · średnio {1} dni: w trakcie {2}, review {3}, testy {4}, hold {5}", p.Tasks, Report.D1(p.Days), Report.D1(p.Started), Report.D1(p.Review), Report.D1(p.Tests), Report.D1(p.Hold)) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
                row.Children.Add(Label((p.Project.Length > 0 ? p.Project : L.T("bez projektu")) + $"  ({p.Tasks})", 12, "Fg"));
                // one bar: the average days, split into the states' colours
                var bar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Height = 7 };
                double scale = 150 / Math.Max(1, r.Projects.Max(x => x.Days));
                foreach (var (days, color) in new[] { (p.Started, TaskMarks.InProgressOrange), (p.Review, TaskMarks.ReviewViolet), (p.Tests, TaskMarks.TestsTeal), (p.Hold, TaskMarks.HoldRed) })
                    if (days > 0) bar.Children.Add(new Border { Width = days * scale, Background = new SolidColorBrush(color), CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 1, 0) });
                Grid.SetColumn(bar, 1);
                row.Children.Add(bar);
                var avg = Label(L.F("śr. {0} dni", Report.D1(p.Days)), 11.5, "FgDim");
                avg.TextAlignment = TextAlignment.Right;
                Grid.SetColumn(avg, 2);
                row.Children.Add(avg);
                _left.Children.Add(row);
            }
        }
        void Waiting(string caption, IEnumerable<(TaskItem Task, string What, Color Color)> rows)
        {
            var list = rows.ToList();
            if (list.Count == 0) return;
            _left.Children.Add(Caption(caption));
            foreach (var (t, what, color) in list.Take(12))
            {
                var d = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                var w = Label(what, 11.5, "FgFaint");
                w.Foreground = new SolidColorBrush(color);
                w.Margin = new Thickness(10, 0, 0, 0);
                DockPanel.SetDock(w, Dock.Right);
                d.Children.Add(w);
                var text = Label(t.Text, 12, "Fg");
                text.TextWrapping = TextWrapping.NoWrap;
                text.TextTrimming = TextTrimming.CharacterEllipsis;
                text.ToolTip = t.Text;
                d.Children.Add(text);
                _left.Children.Add(d);
            }
        }
        Waiting(L.F("WSTRZYMANE – HOLD ({0})", r.Held.Count), r.Held.Select(x => (x.Task, L.F("czeka {0} dni · potem {1}", x.Days, TaskMarks.StateName(x.Task, x.Task.WorkState)), TaskMarks.HoldRed)));
        Waiting(L.F("UTKNĘŁY – {0}+ DNI W JEDNYM STANIE ({1})", Insights.StuckDays, r.Stuck.Count), r.Stuck.Select(x => (x.Task, L.F("{0} dni · {1}", x.Days, TaskMarks.StateName(x.Task)), TaskMarks.StateColor(x.Task))));
        if (r.Ratios.Count > 0)
        {
            _left.Children.Add(Caption(L.T("ESTYMACJA A ZMIERZONY CZAS (WSZYSTKIE ZROBIONE Z LICZNIKIEM)")));
            foreach (var x in r.Ratios.Take(10))
            {
                var d = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                var f = Label($"×{x.Factor.ToString("0.0#", pl)}", 12, x.Factor > 1.15 ? "AccentBrush" : "FgDim", FontWeights.Bold);
                DockPanel.SetDock(f, Dock.Right);
                d.Children.Add(f);
                d.Children.Add(Label($"{x.Tag}  ·  " + L.F("{0} zadań", x.Tasks) + "  ·  " + (x.Factor > 1.05 ? L.F("trwa zwykle o {0}% dłużej niż estymacja", Math.Round((x.Factor - 1) * 100)) : x.Factor < 0.95 ? L.F("trwa zwykle o {0}% krócej niż estymacja", Math.Round((1 - x.Factor) * 100)) : L.T("zgodnie z estymacją")), 12, "Fg"));
                _left.Children.Add(d);
            }
        }

        // the backlog: not the period's work, but what waits for the next periods – how much, how much came in, the most important
        if (r.Backlog.Count > 0)
        {
            _left.Children.Add(Caption(L.T("BACKLOG")));
            double bh = r.Backlog.Sum(t => t.Estimate ?? 0);
            var line = new TextBlock { FontSize = 12, Foreground = Ui.Res("FgDim"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            line.Inlines.Add(new System.Windows.Documents.Run(r.Backlog.Count.ToString()) { FontWeight = FontWeights.Bold, Foreground = Ui.Res("Fg") });
            line.Inlines.Add(new System.Windows.Documents.Run(" " + L.T("zadań czeka w backlogu") + (bh > 0 ? "  ·  " + L.F("{0} estymacji", BoardWindow.Hours(Math.Round(bh, 1))) : "")
                + "  ·  " + L.F("dodane w tym okresie: {0}", r.BacklogAdded) + (r.Returned.Count > 0 ? "  ·  " + L.F("wróciły z tego okresu: {0}", r.Returned.Count) : "")));
            _left.Children.Add(line);
            var now = DateTime.UtcNow;
            foreach (var t in r.Backlog.OrderBy(t => BoardWindow.PriorityRank(t.Priority)).ThenBy(t => t.Created ?? DateTime.MinValue).Take(6))
            {
                var d = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                if (t.Created is { } c)
                {
                    var age = Label(L.F("czeka {0} dni", Math.Max(0, (int)(now - c).TotalDays)), 11.5, "FgFaint");
                    age.Margin = new Thickness(10, 0, 0, 0);
                    DockPanel.SetDock(age, Dock.Right);
                    d.Children.Add(age);
                }
                var mark = PriorityMark(t);
                DockPanel.SetDock(mark, Dock.Left);
                d.Children.Add(mark);
                var text = Label(t.Text, 12, "Fg");
                text.TextWrapping = TextWrapping.NoWrap;
                text.TextTrimming = TextTrimming.CharacterEllipsis;
                text.ToolTip = t.Text;
                d.Children.Add(text);
                _left.Children.Add(d);
            }
        }
    }

    // ---------- the right column: the period's tasks ----------

    /// <summary>A task of the list: its day in the period, a later day it went on to (carried), or none (back in the backlog).</summary>
    sealed record Row(TaskItem Task, DateTime? Day, bool Carried);

    void FillList()
    {
        var r = _report;
        var pl = BoardWindow.Pl;
        var rows = r.All.Where(x => x.Task.Text.Length > 0).Select(x => new Row(x.Task, x.Day, false))
            .Concat(r.Carried.Select(t => new Row(t, Report.TryDay(t.Day), true)))
            .Concat(r.Returned.Select(t => new Row(t, null, true)))
            .ToList();

        // ----- toolbar: Wszystkie / Zrobione / Niezrobione · sort: Data / Kategoria / Priorytet / Nazwa · Wg projektów -----
        _listBar.Children.Clear();
        _listBar.Children.Add(Caption(L.F("ZADANIA ({0})", rows.Count), new Thickness(0, 6, 14, 4)));
        void Chip(string text, bool on, Action click, double left = 0)
        {
            var b = new ToggleButton { Content = text, Style = (Style)Application.Current.Resources["Chip"], IsChecked = on, Margin = new Thickness(left, 0, 4, 4) };
            b.Click += (_, _) => { click(); SettingsStore.Save(S); FillList(); };
            _listBar.Children.Add(b);
        }
        Chip(L.T("Wszystkie"), S.ReportFilter == "all", () => S.ReportFilter = "all");
        Chip(L.F("Zrobione ({0})", rows.Count(x => x.Task.Done)), S.ReportFilter == "done", () => S.ReportFilter = "done");
        Chip(L.F("Niezrobione ({0})", rows.Count(x => !x.Task.Done)), S.ReportFilter == "open", () => S.ReportFilter = "open");
        var sortLabel = Label(L.T("Sortuj:"), 11.5, "FgDim");
        sortLabel.Margin = new Thickness(14, 6, 6, 0);
        _listBar.Children.Add(sortLabel);
        foreach (var (key, text) in new[] { ("date", L.T("Data")), ("category", L.T("Kategoria")), ("priority", L.T("Priorytet")), ("name", L.T("Nazwa")) })
            Chip(text, S.ReportSort == key, () => S.ReportSort = key);
        Chip(L.T("Wg projektów"), S.ReportGroup, () => S.ReportGroup = !S.ReportGroup, 14);

        _list.Children.Clear();
        var shown = rows.Where(x => S.ReportFilter switch { "done" => x.Task.Done, "open" => !x.Task.Done, _ => true }).ToList();
        if (shown.Count == 0)
        {
            _list.Children.Add(Label(r.Tasks == 0 ? L.T("W tym okresie nie ma zadań.") : L.T("Żadne zadanie nie pasuje do filtra."), 12, "FgFaint"));
            return;
        }
        if (S.ReportGroup)
        {
            foreach (var g in shown.GroupBy(x => x.Task.NotionSource ?? "").OrderBy(g => g.Key.Length == 0 ? 1 : 0).ThenByDescending(g => g.Count()))
            {
                var color = TaskMarks.ProjectColor(g.First().Task) ?? Color.FromRgb(0x7E, 0x8C, 0x9C);
                var head = new DockPanel { Margin = new Thickness(0, _list.Children.Count == 0 ? 0 : 12, 0, 4) };
                int done = g.Count(x => x.Task.Done);
                double est = g.Sum(x => x.Task.Estimate ?? 0), doneEst = g.Where(x => x.Task.Done).Sum(x => x.Task.Estimate ?? 0);
                var nums = Label($"{done}/{g.Count()}" + (est > 0 ? $"  ·  {BoardWindow.Hours(Math.Round(doneEst, 1))}/{BoardWindow.Hours(Math.Round(est, 1))}" : ""), 11.5, "FgDim");
                DockPanel.SetDock(nums, Dock.Right);
                head.Children.Add(nums);
                head.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2.5), Background = new SolidColorBrush(color), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                head.Children.Add(Label(g.Key.Length > 0 ? g.Key : L.T("bez projektu"), 13, "Fg", FontWeights.Bold));
                _list.Children.Add(head);
                _list.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Th.Ink(0x14)), Margin = new Thickness(0, 0, 0, 3) });
                foreach (var x in Sorted(g)) _list.Children.Add(TaskRow(x, project: false));
            }
        }
        else foreach (var x in Sorted(shown)) _list.Children.Add(TaskRow(x, project: true));
    }

    static string Category(TaskItem t) => TaskItem.Tags(t.Text, out _) is { Count: > 0 } tags ? tags[0] : "";

    static string Name(TaskItem t) { TaskItem.Tags(t.Text, out var rest); return rest.Trim(); }

    static IEnumerable<Row> Sorted(IEnumerable<Row> rows)
    {
        var text = StringComparer.Create(BoardWindow.Pl, true);
        var byDay = rows.OrderBy(x => x.Day ?? DateTime.MaxValue).ThenBy(x => x.Task.Order);
        return S.ReportSort switch
        {
            "category" => rows.OrderBy(x => Category(x.Task).Length == 0 ? 1 : 0).ThenBy(x => Category(x.Task), text).ThenBy(x => x.Day ?? DateTime.MaxValue),
            "priority" => rows.OrderBy(x => BoardWindow.PriorityRank(x.Task.Priority)).ThenBy(x => x.Task.Done ? 1 : 0).ThenBy(x => x.Day ?? DateTime.MaxValue),
            "name" => rows.OrderBy(x => Name(x.Task), text),
            _ => byDay,
        };
    }

    /// <summary>A thin bar in the priority's colour (room kept when there is none, so the rows line up).</summary>
    static FrameworkElement PriorityMark(TaskItem t) => new Border
    {
        Width = 3, Height = 13, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        Background = string.IsNullOrEmpty(t.Priority) ? Brushes.Transparent : new SolidColorBrush(BoardWindow.PriorityColor(t.Priority)),
        ToolTip = string.IsNullOrEmpty(t.Priority) ? null : L.F("Priorytet: {0}", t.Priority),
    };

    FrameworkElement TaskRow(Row x, bool project)
    {
        var t = x.Task;
        var pl = BoardWindow.Pl;
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 0), Background = Brushes.Transparent, MinHeight = 24 };
        // the state: a dot in its colour (green = done)
        row.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(TaskMarks.StateColor(t)), Margin = new Thickness(2, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = TaskMarks.StateName(t) });
        var mark = PriorityMark(t);
        row.Children.Add(mark);
        var when = Label(x.Day is { } d ? d.ToString("ddd d.MM", pl) + (x.Carried ? " ↷" : "") : L.T("backlog"), 11, x.Day == null ? "AccentBrush" : "FgFaint");
        when.Width = 76;
        when.VerticalAlignment = VerticalAlignment.Center;
        when.TextWrapping = TextWrapping.NoWrap;
        if (x.Carried) when.ToolTip = x.Day == null ? L.T("Pracowano nad nim w tym okresie, wróciło do backlogu") : L.T("Pracowano nad nim w tym okresie, przeszło na ten dzień");
        row.Children.Add(when);
        foreach (UIElement e in row.Children) DockPanel.SetDock(e, Dock.Left);

        var wd = _report.WorkDays(t);
        var meta = Label((wd.Days > 1 ? "↷ " + L.F("{0} dni pracy", wd.Days) + (wd.Review > 0 ? " · " + L.F("review: {0}", wd.Review) : "") + (wd.Tests > 0 ? " · " + L.F("testy: {0}", wd.Tests) : "") + (wd.Hold > 0 ? " · " + L.F("hold: {0}", wd.Hold) : "") + "   " : "")
            + (t.TrackedNow > 0 ? "⏱ " + BoardWindow.Duration(t.TrackedNow) + "   " : "")
            + (t.Estimate is > 0 ? BoardWindow.Est(t.Estimate.Value) : ""), 11.5, "FgFaint");
        meta.Margin = new Thickness(10, 0, 0, 0);
        meta.VerticalAlignment = VerticalAlignment.Center;
        meta.TextWrapping = TextWrapping.NoWrap;
        DockPanel.SetDock(meta, Dock.Right);
        row.Children.Add(meta);

        var text = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = t.Text };
        if (project && t.NotionSource is { Length: > 0 } src)
            text.Inlines.Add(new System.Windows.Documents.Run(src + "  ") { FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(TaskMarks.ProjectColor(t) ?? Color.FromRgb(0x7E, 0x8C, 0x9C)) });
        text.Inlines.Add(new System.Windows.Documents.Run(t.Text) { Foreground = Ui.Res(t.Done ? "FgDim" : "Fg") });
        row.Children.Add(text);

        row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(Th.Ink(0x0C));
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        if (x.Day is { } day)
        {
            row.Cursor = Cursors.Hand;
            row.MouseLeftButtonUp += (_, _) => _jump(day);
        }
        return row;
    }

    // ---------- helpers, files ----------

    static TextBlock Label(string text, double size = 12.5, string brush = "Fg", FontWeight? weight = null) => new()
    {
        Text = text, FontSize = size, Foreground = Ui.Res(brush), FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap,
    };

    static Button Btn(string text, string style, RoutedEventHandler click)
    {
        var b = new Button { Content = text, Style = (Style)Application.Current.Resources[style], Margin = new Thickness(8, 0, 0, 0) };
        b.Click += click;
        return b;
    }

    static TextBlock Caption(string text, Thickness? margin = null)
    {
        var c = Label(text, 10.5, "FgFaint", FontWeights.Bold);
        c.Margin = margin ?? new Thickness(0, 10, 0, 4);
        return c;
    }

    /// <summary>Saved in the account folder ("raporty"), so it is there to look at later, on any computer.</summary>
    void SaveHtml()
    {
        try
        {
            Directory.CreateDirectory(ReportsFolder);
            var path = Path.Combine(ReportsFolder, _report.FileName + ".html");
            File.WriteAllText(path, _report.Html(), new UTF8Encoding(false));
            GlassWindow.OpenUrl(path);
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), L.T("Nie udało się zapisać pliku:") + "\n" + ex.Message, "TaskWall"); }
    }

    void SaveCsv()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = L.T("Eksport okresu do CSV"), FileName = _report.FileName + ".csv", Filter = "CSV (*.csv)|*.csv",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try { File.WriteAllText(dlg.FileName, _report.Csv(), new UTF8Encoding(true)); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), L.T("Nie udało się zapisać pliku:") + "\n" + ex.Message, "TaskWall"); }
    }

    /// <summary>On the first start of a week: the board opens on the report of the week before (once – the mark is shared between computers).</summary>
    public static void ShowWeeklyIfDue()
    {
        var s = App.Settings;
        if (!s.WeeklySummary) return;
        var last = BoardWindow.Monday(DateTime.Today).AddDays(-7);
        var id = $"{ISOWeek.GetYear(last)}-W{ISOWeek.GetWeekOfYear(last):00}";
        if (s.LastWeekSummary == id) return;
        bool first = s.LastWeekSummary == null;
        s.LastWeekSummary = id;
        SettingsStore.Save(s);
        if (first) return; // not on the very first start after installing
        var from = last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = last.AddDays(6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (App.Store.Data.Tasks.Any(t => t.Day != null && string.CompareOrdinal(t.Day, from) >= 0 && string.CompareOrdinal(t.Day, to) <= 0))
            App.Board?.ShowReport(last, month: false);
    }
}
