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
/// <summary>
/// The week and month views' day cells: the header (date, weather, hours), the progress bar, meetings and alarms, the
/// lanes of multi-day tasks (laid out by <see cref="DayLayout"/>), the day's tasks and the folded done ones.
/// </summary>
public partial class BoardWindow
{
    /// <summary>Week view (1–2 rows) or month view (5–6 compact rows).</summary>
    // multi-day tasks: per week row, each one gets a lane, so it sits in the same line on all its days
    readonly Dictionary<int, Dictionary<string, int>> _lanes = new();

    /// <summary>All days of a multi-day task (earlier days it was worked on + its current day), sorted; null = a one-day task.</summary>
    static List<string>? SpanDays(TaskItem t)
    {
        // asked many times per build (every day cell, every row): worked out once per task and state of it
        var sig = $"{t.Day}|{t.PlanEnd}|{t.Archived}|{t.WorkedDays?.Count}|{t.Modified.Ticks}";
        if (SpanCache.TryGetValue(t, out var hit) && hit.Sig == sig) return hit.Days;
        var days = ComputeSpan(t);
        SpanCache[t] = (sig, days);
        return days;
    }

    static readonly Dictionary<TaskItem, (string Sig, List<string>? Days)> SpanCache = new(ReferenceEqualityComparer.Instance);

    static List<string>? ComputeSpan(TaskItem t)
    {
        // archived: gone from the board on all its days at once
        if (t.Day == null || t.Archived || (t.WorkedDays is not { Count: > 0 } && t.PlanEnd == null)) return null;
        var all = new List<string>(t.WorkedDays ?? new()) { t.Day };
        // planned days ahead: the workdays after its day up to the planned end
        if (t.PlanEnd != null && DateTime.TryParseExact(t.PlanEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            for (var d = ParseKey(t.Day).AddDays(1); d <= end; d = d.AddDays(1))
                if (!IsWeekend(d) && DaysOff.Name(d) == null) all.Add(DayKey(d));
        all = all.Distinct().ToList();
        all.Sort(StringComparer.Ordinal);
        return all.Count > 1 ? all : null;
    }

    void AssignLanes(DateTime first, int weeks, int days)
    {
        _lanes.Clear();
        var spans = Store.Data.Tasks.Select(t => (t, days: SpanDays(t))).Where(x => x.days != null).ToList();
        for (int w = 0; w < weeks; w++)
        {
            var keys = Enumerable.Range(0, days).Select(c => DayKey(first.AddDays(w * 7 + c))).ToHashSet();
            _lanes[w] = DayLayout.Lanes(spans.Select(x => new DayLayout.Span(x.t.Id, x.days!.Where(keys.Contains).ToList(), x.t.Order)));
        }
    }

    /// <param name="only">Just these week rows (a change touched only their days); null = the whole grid.</param>
    void BuildGrid(ICollection<int>? only = null)
    {
        bool single = _view == ViewMode.Day;
        int days = single ? 1 : Weekends ? 7 : 5;
        bool month = _view == ViewMode.Month;
        if (only == null)
        {
            WeeksGrid.Children.Clear();
            WeeksGrid.RowDefinitions.Clear();
            WeeksGrid.ColumnDefinitions.Clear();
            for (int c = 0; c < days; c++) WeeksGrid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int w = 0; w < WeekCount; w++) WeeksGrid.RowDefinitions.Add(new RowDefinition());
        }
        else
            foreach (var old in WeeksGrid.Children.OfType<UIElement>().Where(c => only.Contains(Grid.GetRow(c))).ToList()) WeeksGrid.Children.Remove(old);

        var byDay = TasksByDay();
        var archivedDone = Store.Data.Tasks.Where(t => t.Archived && t.Done && t.Day != null).GroupBy(t => t.Day!).ToDictionary(g => g.Key, g => g.Count());
        var first = single ? _dayDate : FirstMonday;
        Grid.SetIsSharedSizeScope(WeeksGrid, true); // lanes of multi-day tasks line up across the days of a week
        AssignLanes(first, WeekCount, days);
        for (int w = 0; w < WeekCount; w++)
            if (only == null || only.Contains(w))
            for (int c = 0; c < days; c++)
            {
                var date = first.AddDays(w * 7 + c);
                archivedDone.TryGetValue(DayKey(date), out var arch);
                var cell = BuildDay(date, DayTasks(date, byDay), arch, c == 0, w == 0, month, month && date.Month != _monthFirst.Month, w);
                Grid.SetRow(cell, w);
                Grid.SetColumn(cell, c);
                WeeksGrid.Children.Add(cell);
            }
    }

    /// <summary>On hold that day: a past day it was held, or its current day and the planned ones while it is held.</summary>
    static bool HeldOn(TaskItem t, string key) => t.DayStates?.GetValueOrDefault(key) == "hold" || (t.IsHold && string.CompareOrdinal(key, t.Day ?? "") >= 0);

    /// <summary>
    /// Estimated hours of a day: one-day tasks in full, a multi-day task with its share on each of its days
    /// (16 h over 2 days = 8 h a day) – not on the days it is on hold.
    /// </summary>
    static double DayLoad(string key, IEnumerable<TaskItem> dayTasks, bool openOnly)
    {
        // a held task: no work on it those days – its hours don't fill them
        double sum = dayTasks.Where(t => (!openOnly || !t.Done) && SpanDays(t) == null && !HeldOn(t, key)).Sum(t => t.Estimate ?? 0);
        foreach (var t in Store.Data.Tasks.Where(t => t.Estimate is > 0 && (!openOnly || !t.Done)))
            if (SpanDays(t) is { } days && days.Contains(key) && !HeldOn(t, key)) sum += t.Estimate!.Value / days.Count;
        return sum;
    }

    /// <summary>How far the day's list reaches past the cell's padding on each side, so the marks' light isn't cut off.</summary>
    const double GlowRoom = 6;

    FrameworkElement BuildDay(DateTime date, List<TaskItem> tasks, int archivedDone, bool firstCol, bool firstRow, bool compact, bool outside, int week = 0)
    {
        var key = DayKey(date);
        bool isToday = date == DateTime.Today;
        bool isPast = date < DateTime.Today;
        var dayOff = DaysOff.Name(date);
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
        // today: a frame in the accent and its header (above the progress bar) tinted – no line that competes with the bar
        var accentNow = ((SolidColorBrush)Res("AccentBrush")).Color;
        // days off (weekends, public holidays) tinted like a BŚU day, in their red
        Brush normalBg = markColor is { } mc && markInfo?.Ring != true ? B(A(mc, 0x16)) // a "ring" mark (HO) only shows its badge
            : red ? B(A(WeekendRed, 0x0E))
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
            inner.BorderBrush = B(A(accentNow, 0x90));
            inner.BorderThickness = new Thickness(1.2);
        }
        outer.Child = inner;
        // Poświata: today's frame glows (a frame of its own over the cell's edge, so the tasks in it don't)
        if (isToday && Glow.Halo(inner.CornerRadius, inner.Margin, blur: 14, opacity: 0.6) is { } halo)
        {
            var cell = new Grid();
            outer.Child = cell;
            cell.Children.Add(inner);
            cell.Children.Add(halo);
        }

        var dock = new DockPanel();
        inner.Child = dock;

        // ----- header -----
        var header = new Grid { Margin = new Thickness(0, 0, 0, compact ? 2 : 5) };
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, SharedSizeGroup = $"W{week}H" }); // same header height across the week
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
                Child = new TextBlock { Text = MarkType.Show(mark), FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White },
            });
        left.Children.Add(nameRow);
        if (!compact)
        {
            var dateText = new TextBlock { FontSize = 10.5, FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal, Foreground = dateColor };
            dateText.Inlines.Add(new Run(date.Day == 1 || (firstCol && firstRow) ? date.ToString("d MMM", Pl) : date.Day.ToString()));
            left.Children.Add(dateText);
        }
        if (holidayNames.Count > 0)
        {
            // the holiday goes into the date line (the header keeps its three lines)
            var holiday = new TextBlock
            {
                Text = string.Join(" · ", holidayNames.Distinct()),
                FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
                Foreground = dayOff != null ? B(A(WeekendRed, 0xD0)) : Res("FgFaint"),
                ToolTip = string.Join("\n", holidayNames.Distinct()) + (dayOff != null ? "\n" + L.T("(dzień ustawowo wolny)") : ""),
            };
            if (left.Children.Count > 1 && left.Children[^1] is TextBlock dateLine)
            {
                left.Children.RemoveAt(left.Children.Count - 1);
                var line = new DockPanel { LastChildFill = true };
                DockPanel.SetDock(dateLine, Dock.Left);
                holiday.Margin = new Thickness(6, 0, 0, 0);
                holiday.VerticalAlignment = VerticalAlignment.Bottom;
                line.Children.Add(dateLine);
                line.Children.Add(holiday);
                left.Children.Add(line);
            }
            else left.Children.Add(holiday);
        }
        // weather (Settings → Pogoda): the third line on the left – "☁ 13° / 4°   ☂ 35%   ≋ 12 km/h"
        if (WeatherService.On(date) is { } wd)
        {
            var (symbol, desc) = WeatherService.Describe(wd.Code);
            double fsw = compact ? 9.5 : 10;
            var w = new TextBlock { FontSize = fsw, Foreground = Res("FgDim"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, compact ? 0 : 1, 0, 0) };
            w.Inlines.Add(new Run(symbol + " ") { FontFamily = new FontFamily("Segoe UI Symbol"), Foreground = Res("AccentBrush") });
            w.Inlines.Add(new Run($"{Math.Round(wd.Max):0}°" + (double.IsNaN(wd.Min) ? "" : $"/{Math.Round(wd.Min):0}°")));
            if (S.Weather.ShowRain && wd.Rain is { } rain) // chance and the expected amount: "☂ 88% · 3,2 mm"
                w.Inlines.Add(new Run($"  ☂ {rain}%" + (wd.Mm is >= 0.1 and var mm ? " · " + mm.ToString(mm < 10 ? "0.#" : "0", Pl) + "mm" : "")) { Foreground = Res("FgFaint") });
            if (S.Weather.ShowWind && wd.Wind is { } wind)
            {
                w.Inlines.Add(new Run("  "));
                w.Inlines.Add(new InlineUIContainer(Icons.Wind(Res("FgFaint"), fsw + 1)) { BaselineAlignment = BaselineAlignment.Center });
                w.Inlines.Add(new Run($" {Math.Round(wind):0}km/h") { Foreground = Res("FgFaint") });
            }
            w.ToolTip = $"{desc}\n{L.F("max {0}°, min {1}°", Math.Round(wd.Max), Math.Round(wd.Min))}"
                + (wd.Rain is { } r ? "\n" + L.F("szansa opadów {0}%", r) : "")
                + (wd.Mm is { } mmTip ? "\n" + L.F("opady: {0} mm", mmTip.ToString("0.#", Pl)) : "")
                + (wd.Wind is { } wi ? "\n" + L.F("wiatr do {0} km/h", Math.Round(wi)) : "") + $"\n{S.Weather.City}";
            left.Children.Add(w);
        }
        header.Children.Add(left);

        // right: estimate sum (+ today's progress count)
        double hours = DayLoad(key, tasks, openOnly: false);
        double meetingHours = events.Where(e => !e.IsHoliday).Sum(e => e.Hours) + Store.Alarms.Sum(a => a.HoursOn(date));
        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4, 1, 2, 0) };
        Grid.SetColumn(right, 1);
        int total = tasks.Count + archivedDone, done = tasks.Count(t => t.Done) + archivedDone;
        if (total > 0) // the counter always sits in the corner; the hours go below it
            right.Children.Add(new TextBlock
            {
                Text = $"{done}/{total}", FontSize = 10, FontWeight = FontWeights.Normal, HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = done == total ? Res("FgDim") : Res("FgFaint"), // plain numbers: the progress bar is the signal
                ToolTip = L.F("Zrobione {0} z {1}", done, total) + (archivedDone > 0 ? L.F(" (w tym {0} w archiwum)", archivedDone) : ""),
            });
        bool overloaded = false;
        if (hours > 0 || meetingHours > 0)
        {
            double left_ = DayLoad(key, tasks, openOnly: true);
            bool workday = !red; // weekends and public holidays: just the hours, no 8 h limit
            bool over = workday && hours + meetingHours > DayHours + 0.01;
            overloaded = over;
            right.Children.Add(new TextBlock
            {
                // the workday limit: tasks + meetings against 8 h ("6,5 / 8h"), orange when over
                Text = workday ? $"{(hours + meetingHours).ToString("0.#", Pl)} / {Hours(DayHours)}" : Hours(hours + meetingHours),
                FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = Res("FgFaint"), // an overloaded day shows in its progress bar (orange), not in the number
                ToolTip = L.F("Zadania: {0} (zostało {1})", Hours(hours), Hours(left_)) + (meetingHours > 0 ? "\n" + L.F("Spotkania: {0}", Hours(meetingHours)) : "")
                    + (!workday ? "" : "\n" + (over ? L.F("Ponad dzień pracy o {0}", Hours(hours + meetingHours - DayHours)) : L.F("Wolne: {0}", Hours(DayHours - hours - meetingHours)))),
            });
        }
        header.Children.Add(right);
        if (isToday)
        {
            // the header of today (name, date, weather, numbers) on an accent tint, out to the frame; the progress bar under it
            var p0 = inner.Padding;
            var headBox = new Border
            {
                Background = B(A(accentNow, 0x1E)), CornerRadius = new CornerRadius(compact ? 6 : 9, compact ? 6 : 9, 0, 0),
                Margin = new Thickness(-p0.Left, -p0.Top, -p0.Right, header.Margin.Bottom - (compact ? 1 : 3)), Padding = new Thickness(p0.Left, p0.Top, p0.Right, compact ? 1 : 3), // same overall height: the bar stays level with the other days
            };
            header.Margin = new Thickness(0);
            DockPanel.SetDock(headBox, Dock.Top);
            headBox.Child = header;
            dock.Children.Add(headBox);
        }
        else dock.Children.Add(header);

        // ----- progress bar (done / all, archived done included) – always there (empty without tasks), the same height
        // everywhere, so the lanes of multi-day tasks start at the same height on every day -----
        {
            var track = new Grid { Height = 3, Margin = new Thickness(0, 0, 2, compact ? 3 : 6) };
            DockPanel.SetDock(track, Dock.Top);
            // more than a workday planned: the track turns orange (the numbers stay plain)
            var orange = Color.FromRgb(0xF2, 0xA6, 0x5A);
            track.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = overloaded && !isPast ? B(A(orange, 0x70)) : total > 0 ? B(Th.Ink(isPast ? (byte)0x10 : (byte)0x1A)) : Brushes.Transparent, ToolTip = overloaded ? L.T("Ponad dzień pracy (8 h)") : null });
            var fill = new Border
            {
                CornerRadius = new CornerRadius(1.5),
                HorizontalAlignment = HorizontalAlignment.Left,
                // past days in grey (done is history), today and ahead in the accent; a day over 8 h ahead in orange
                Background = isPast ? B(Th.Ink(0x58)) : B(A(overloaded ? orange : ((SolidColorBrush)Res("AccentBrush")).Color, done == total ? (byte)0xFF : isToday ? (byte)0xFF : (byte)0x90)),
                Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed,
                ToolTip = L.F("Zrobione {0} z {1}", done, total),
            };
            // checklists count too: a task with 2 of 4 items ticked fills half of its share
            double frac = total > 0 ? (tasks.Sum(t => t.Progress) + archivedDone) / total : 0;
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
        var timed = Store.Alarms.Where(a => a.Occurs(date) || a.ContinuesInto(date)).Select(a => (at: a.Occurs(date) ? a.TimeOfDay : TimeSpan.Zero, el: BuildAlarmRow(a, date, compact)))
            .Concat(events.Where(e => !e.IsHoliday).Select(e => (at: e.AllDay ? TimeSpan.Zero : e.Start.TimeOfDay, el: BuildEventRow(e, key, compact))))
            .OrderBy(x => x.at);
        // multi-day tasks of this day (the active one and the days it continued through) go right under the timed ones,
        // each in its week lane; timed block + lanes share their heights across the week, so a task stays in one line
        var lanes = _lanes.TryGetValue(week, out var lw) ? lw : new Dictionary<string, int>();
        // done tasks fold into one "✓ 3 zrobione" line at the top of the day (Settings → Tablica: always, or on days
        // that are over) – only the ones finished that day: earlier days of a multi-day task show the state it had then
        bool folding = S.CollapseDone || (S.FoldPastDone && isPast);
        bool fold = folding && !_showDone.Contains(key);
        int doneHere = tasks.Count(t => t.Done);
        var doneToggle = folding && doneHere > 0 ? DoneToggle(key, doneHere, fold, compact) : null;
        var spanAll = Store.Data.Tasks.Where(t => lanes.ContainsKey(t.Id))
            .Select(t => (t, days: SpanDays(t)!)).Where(x => x.days.Contains(key)).ToList();
        var spanIds = spanAll.Where(x => x.t.Day == key && !x.t.Archived).Select(x => x.t.Id).ToHashSet();
        var spanHere = spanAll.Where(x => !DayLayout.Folds(x.t, key, fold)).ToList(); // folded away: its lane is free that day
        var nested = Nest(tasks.Where(t => !spanIds.Contains(t.Id) && !DayLayout.Folds(t, key, fold)));
        if (spanHere.Count > 0)
        {
            var block = new Grid();
            block.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, SharedSizeGroup = $"W{week}T" });
            var timedPanel = new StackPanel();
            if (doneToggle != null) timedPanel.Children.Add(doneToggle); // inside the shared block: the lanes stay level across the week
            foreach (var (_, el) in timed) timedPanel.Children.Add(el);
            block.Children.Add(timedPanel);
            int maxLane = spanHere.Max(x => lanes[x.t.Id]);
            for (int l = 0; l <= maxLane; l++) block.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, SharedSizeGroup = $"W{week}L{l}" });
            foreach (var (t, days) in spanHere)
            {
                var span = (days.IndexOf(key) + 1, days.Count);
                var el = t.Day == key && !t.Archived ? BuildTaskRow(t, false, compact, span, inLane: true)
                    : BuildContinuedRow(t, span, compact, planned: string.CompareOrdinal(key, t.Day) > 0, dayKey: key);
                Grid.SetRow(el, lanes[t.Id] + 1);
                block.Children.Add(el);
            }
            // a lane nobody uses that day (a multi-day task ended or was shortened, or folded away): the day's own tasks
            // move up into it, one line each like the lanes, instead of an empty slot
            var free = DayLayout.FreeLanes(spanHere.Select(x => lanes[x.t.Id]));
            var parents = nested.Where(n => n.Depth > 0).Select(n => n.Task.ParentNotionId).ToHashSet();
            var fillers = _addingKey == key ? new List<TaskItem>() // typing a new task: the list keeps still
                : nested.Where(n => n.Depth == 0 && (n.Task.NotionId == null || !parents.Contains(n.Task.NotionId)) && !_expanded.Contains(n.Task.Id))
                    .Take(free.Count).Select(n => n.Task).ToList();
            for (int i = 0; i < fillers.Count; i++)
            {
                var el = BuildTaskRow(fillers[i], false, compact, null, inLane: true);
                Grid.SetRow(el, free[i] + 1);
                block.Children.Add(el);
            }
            if (fillers.Count > 0) nested = nested.Where(n => !fillers.Contains(n.Task)).ToList();
            panel.Children.Add(block);
            // subtasks of a multi-day task open under the lanes, on its current day only: the shared lane heights stay one line,
            // so the other days of the week get no empty space
            foreach (var (t, _) in spanHere.Where(x => x.t.Day == key && !x.t.Archived && _expanded.Contains(x.t.Id) && (x.t.Checklist is { Count: > 0 } || _checkAddFor == x.t.Id)).OrderBy(x => lanes[x.t.Id]))
            {
                var fsl = compact ? Math.Max(9.5, S.FontSize - 1.5) : S.FontSize;
                TaskItem.Tags(t.Text, out var restText);
                var head = new TextBlock { Text = "↳ " + (restText.Length > 0 ? restText : t.Text), FontSize = Math.Max(9, fsl - 2.5), Foreground = Res("FgFaint"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(compact ? 18 : 22, 4, 0, 0) };
                var sub = BuildChecklist(t, fsl);
                sub.Margin = new Thickness(compact ? 18 : 22, 0, 0, 2);
                panel.Children.Add(head);
                panel.Children.Add(sub);
            }
        }
        else
        {
            if (doneToggle != null) panel.Children.Add(doneToggle);
            foreach (var (_, el) in timed) panel.Children.Add(el);
        }
        // multi-day tasks without a lane in this week (only one of their days is here): a plain row with "↷ k/n"
        foreach (var t in Store.Data.Tasks.Where(t => !lanes.ContainsKey(t.Id) && t.Day != key && SpanDays(t)?.Contains(key) == true))
        {
            var days = SpanDays(t)!;
            panel.Children.Add(BuildContinuedRow(t, (days.IndexOf(key) + 1, days.Count), compact, planned: string.CompareOrdinal(key, t.Day) > 0, dayKey: key));
        }
        foreach (var (t, depth) in nested)
            panel.Children.Add(BuildTaskRow(t, false, compact, SpanDays(t) is { } sd ? (sd.IndexOf(key) + 1, sd.Count) : null, depth));
        var addRow = BuildAddRow(key, date, compact);
        if (isToday && !compact && tasks.Count == 0 && spanHere.Count == 0 && _addingKey != key) // an empty today: one line of what can be typed
            panel.Children.Add(new TextBlock
            {
                Text = L.T("Pusto. Wpisz np. „Raport 2h #art !!” albo przeciągnij zadanie z backlogu."), TextWrapping = TextWrapping.Wrap,
                FontSize = Math.Max(9.5, S.FontSize - 2), Foreground = Res("FgFaint"), Opacity = 0.8, Margin = new Thickness(2, 2, 0, 2),
            });
        if (isPast && _addingKey != key) // past days: "+ dodaj zadanie" only on hover (less clutter)
        {
            addRow.Opacity = 0;
            inner.MouseEnter += (_, _) => addRow.Opacity = 1;
            inner.MouseLeave += (_, _) => addRow.Opacity = 0;
        }
        panel.Children.Add(addRow);
        _panels[key] = panel;

        var sv = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = panel,
            // room for the marks' light (Poświata ikon): the list reaches past the cell's padding (sides, top), its rows don't
            Margin = new Thickness(-GlowRoom, -GlowRoom, -GlowRoom, 0),
        };
        panel.Margin = new Thickness(panel.Margin.Left + GlowRoom, panel.Margin.Top + GlowRoom, panel.Margin.Right + GlowRoom, panel.Margin.Bottom);
        sv.ScrollChanged += (_, _) => UpdateFade(sv);
        sv.SizeChanged += (_, _) => UpdateFade(sv);
        if (_scrollOffsets.TryGetValue(key, out var off)) sv.Loaded += (_, _) => sv.ScrollToVerticalOffset(off);
        dock.Children.Add(sv);

        inner.DragOver += (_, e) => OnDragOverTarget(panel, e);
        inner.Drop += (_, e) => OnDrop(key, panel, e);
        inner.DragEnter += (_, _) => inner.Background = B(Th.Ink(0x14));
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

    /// <summary>"✓ 3 zrobione ▸" – the first line of a day with done tasks folded away; a click shows / folds them.</summary>
    FrameworkElement DoneToggle(string key, int count, bool fold, bool compact)
    {
        var green = TaskMarks.DoneGreen;
        var tb = new TextBlock { FontSize = Math.Max(9.5, S.FontSize - (compact ? 2 : 1.5)), TextTrimming = TextTrimming.CharacterEllipsis };
        tb.Inlines.Add(new Run("✓  ") { FontWeight = FontWeights.Bold, Foreground = Tx(green) });
        tb.Inlines.Add(new Run(L.F("{0} zrobione", count)) { Foreground = Res("FgDim") });
        tb.Inlines.Add(new Run(fold ? "   ▸" : "   ▾") { Foreground = Res("FgFaint") });
        var pill = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(5, compact ? 1 : 2, 4, compact ? 1 : 2),
            Margin = new Thickness(-4, 0, 0, 2),
            Background = B(A(green, 0x16)),
            BorderBrush = B(A(green, 0x50)),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Cursor = Cursors.Hand,
            Child = tb,
            ToolTip = fold ? L.T("Pokaż zrobione zadania") : L.T("Zwiń zrobione zadania"),
        };
        Anim.HoverBackground(pill, A(green, 0x16), A(green, 0x2C));
        pill.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (!_showDone.Remove(key)) _showDone.Add(key); Rebuild(); };
        return pill;
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
        tb.Inlines.Add(new Run("  ") { FontFamily = (FontFamily)FindResource("IconFont"), FontSize = Math.Max(8, S.FontSize - 4), Foreground = Tx(MeetingColor) });
        tb.Inlines.Add(new Run(e.AllDay ? "" : e.Start.ToString("HH:mm") + "  ") { FontWeight = FontWeights.SemiBold, Foreground = Tx(MeetingColor) });
        tb.Inlines.Add(new Run(e.Title) { Foreground = Res("Fg") });
        if (!e.AllDay) MarkOverlap(row, tb, ParseKey(dayKey), e.Start, e.End, e);
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

    /// <summary>The day's meetings with their hours: the board's own and the calendars'.</summary>
    /// <summary>The day's meetings (own and from the calendars) – once per day and build, however many rows ask.</summary>
    List<(object Source, string Title, DateTime From, DateTime To)> MeetingsOf(DateTime date) =>
        _meetingsCache.TryGetValue(date, out var cached) ? cached : _meetingsCache[date] = ComputeMeetings(date);

    readonly Dictionary<DateTime, List<(object Source, string Title, DateTime From, DateTime To)>> _meetingsCache = new();

    List<(object Source, string Title, DateTime From, DateTime To)> ComputeMeetings(DateTime date) =>
        Store.Alarms.SelectMany(a => a.PartsOn(date).Select(p => ((object)a, a.Text, p.From, p.To)))
            .Concat(CalendarService.On(date).Where(e => !e.IsHoliday && !e.AllDay).Select(e => ((object)e, e.Title, e.Start, e.End)))
            .ToList();

    /// <summary>A meeting that overlaps another one that day: a warning mark and who it collides with.</summary>
    void MarkOverlap(Border row, TextBlock tb, DateTime date, DateTime from, DateTime to, object self)
    {
        var clash = MeetingsOf(date).Where(m => !ReferenceEquals(m.Source, self) && m.From < to && from < m.To).ToList();
        if (clash.Count == 0) return;
        var warn = Color.FromRgb(0xF2, 0xA6, 0x5A);
        tb.Inlines.Add(new Run("  ⚠") { Foreground = Tx(warn), FontWeight = FontWeights.Bold });
        row.BorderBrush = B(warn);
        row.ToolTip = (row.ToolTip as string ?? "") + "\n\n⚠ " + L.T("Nakłada się z:") + "\n" + string.Join("\n", clash.Select(c => $"{c.From:HH:mm}–{c.To:HH:mm}  {c.Title}"));
    }

    /// <summary>Alarm or meeting on a day (not a task): click edits, right click deletes.</summary>
    FrameworkElement BuildAlarmRow(Alarm a, DateTime date, bool compact)
    {
        // the tail of yesterday's meeting that runs past midnight: from 00:00 to its end, marked "↩"
        bool tail = !a.Occurs(date) && a.ContinuesInto(date);
        var at = tail ? date.Date : a.At(date);
        var endAt = tail ? a.EndAt(date.AddDays(-1)) : a.EndAt(date);
        var color = a.IsMeeting ? MeetingColor : AlarmColor;
        bool past = (a.IsMeeting ? endAt : at) <= DateTime.Now;
        var what = a.IsMeeting ? L.T("Spotkanie") : L.T("Alarm");
        var time = tail ? $"↩ 00:00–{endAt:HH:mm}" : a.IsMeeting ? $"{a.Time}–{endAt:HH:mm}" : a.Time;
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
        tb.Inlines.Add(new Run(a.IsMeeting ? "  " : "  ") { FontFamily = (FontFamily)FindResource("IconFont"), FontSize = Math.Max(8, S.FontSize - 4), Foreground = Tx(color) });
        tb.Inlines.Add(new Run(time + "  ") { FontWeight = FontWeights.SemiBold, Foreground = Tx(color) });
        tb.Inlines.Add(new Run(a.Text.Length > 0 ? a.Text : what) { Foreground = Res("Fg") });
        if (!a.Once) tb.Inlines.Add(new Run("  ↻") { Foreground = Res("FgFaint") });
        row.Child = tb;
        if (a.IsMeeting) MarkOverlap(row, tb, date, at, endAt, a);
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; AlarmWindow.Edit(Store, a, tail ? date.AddDays(-1) : date); }; // the tail: its own day
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

    /// <summary>
    /// A multi-day task on one of its other days (earlier work or planned ahead): "↷ 1/3", one line (the lanes line up),
    /// in the same lane as on its other days. Done = done on all its days (tick + strike); before that, earlier days are
    /// a plain greyed row and planned days a dashed circle. Right click: start / end of the task.
    /// </summary>
    FrameworkElement BuildContinuedRow(TaskItem t, (int Index, int Total) span, bool compact, bool planned = false, string? dayKey = null)
    {
        double fs = compact ? Math.Max(9.5, S.FontSize - 1.5) : S.FontSize;
        // an earlier day: the state it had that day (in progress / review / tests), also once the task is done – it is
        // crossed out on the day it was finished; a planned day: dashed (done: done on all of them)
        var dayState = !planned && dayKey != null ? t.DayStates?.GetValueOrDefault(dayKey) ?? "started" : null;
        bool doneHere = planned ? t.Done : dayState == "done";
        var grid = new Grid { Margin = new Thickness(0, 0, 0, compact ? 1 : 3), Opacity = doneHere ? 0.5 : planned ? 0.55 : 0.7, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(TaskMarks.Status(t, TaskMarks.BarWidth, dashed: planned && !t.Done, gap: compact ? 5 : 7, state: dayState));
        if (TaskMarks.Right(t) is { } proj) { Grid.SetColumn(proj, 2); proj.Margin = new Thickness(3, 1, 0, 1); grid.Children.Add(proj); }
        var text = new TextBlock { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = fs, Foreground = Res("Fg") };
        var tags = TaskItem.Tags(t.Text, out var rest);
        var main = new Run(tags.Count > 0 ? rest : t.Text);
        if (doneHere) main.TextDecorations = TextDecorations.Strikethrough;
        text.Inlines.Add(main);
        // like the task's own row: the name first, the day / project / categories on a small second line
        var meta = new TextBlock { FontSize = Math.Max(8, fs - 3.5), TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Res("FgFaint"), Margin = new Thickness(0, 1, 0, 0), Height = MetaHeight(fs) };
        // project, categories, then the day of the run – the same order as on the task's own row
        foreach (var tag in tags) meta.Inlines.Add(new Run((tag == TaskItem.MeetingTag ? "Meeting" : tag) + "   ") { FontWeight = FontWeights.Bold, Foreground = Tx(TagColor(tag)) });
        meta.Inlines.Add(new Run($"↷ {span.Index}/{span.Total}") { FontWeight = FontWeights.SemiBold, Foreground = Res("AccentBrush") });
        if (t.Checklist is { Count: > 0 } cl) // the subtasks themselves open on the task's current day
            meta.Inlines.Add(new Run($"   ☑ {cl.Count(x => x.Done)}/{cl.Count}"));
        var lines = new StackPanel();
        lines.Children.Add(text);
        lines.Children.Add(meta);
        Grid.SetColumn(lines, 1);
        grid.Children.Add(lines);
        var where = t.Archived ? L.T("w archiwum") : ParseKey(t.Day!).ToString("dddd d MMM", Pl);
        grid.ToolTip = t.Text + "\n" + L.F("Zadanie wielodniowe: dzień {0} z {1}. Teraz: {2}{3}", span.Index, span.Total, where, t.Done ? " – " + L.T("zrobione") : "")
            + (dayState != null ? "\n" + L.F("Tego dnia: {0}", TaskMarks.StateName(t, dayState)) : "")
            + (planned ? "\n" + L.T("Zaplanowane (Potrwa do…). Shift + przeciągnięcie na dzień zmienia koniec.") : "");
        grid.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (t.Day != null && !t.Archived) { _flashId = t.Id; JumpTo(ParseKey(t.Day)); }
        };
        grid.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (Store.Find(t.Id) is not { } live) return;
            var menu = BuildMenu(live, null, null, grid); // the same menu as on its current day: one task, every day of it
            menu.PlacementTarget = grid;
            menu.IsOpen = true;
        };
        return grid;
    }

    /// <summary>"Potrwa do…": the planned last day – the task shows on the workdays up to it (planning ahead).</summary>
    MenuItem PlannedMenu(TaskItem t)
    {
        var item = new MenuItem { Header = L.T("Potrwa do…") };
        if (t.Day == null) { item.IsEnabled = false; return item; }
        var day = ParseKey(t.Day);
        void Add(string header, string? end)
        {
            var mi = new MenuItem { Header = (t.PlanEnd == end ? "✓  " : "     ") + header };
            mi.Click += (_, _) => Do(() => { var real = Store.Materialize(t); real.PlanEnd = end; Store.Changed(real); });
            item.Items.Add(mi);
        }
        Add(L.F("Tylko {0}", day.ToString("ddd d MMM", Pl)), null);
        item.Items.Add(new Separator());
        int n = 1;
        for (var d = day.AddDays(1); n < 15; d = d.AddDays(1))
        {
            if (IsWeekend(d) || DaysOff.Name(d) != null) continue;
            n++;
            Add(L.F("do {0}  ({1} dni)", d.ToString("ddd d MMM", Pl), n), DayKey(d));
        }
        return item;
    }

    /// <summary>
    /// "Rozpoczęte…": when work on the task began – every workday from then until its day becomes part of it
    /// (or "only its own day": no history, e.g. taken from the backlog and not touched yet).
    /// </summary>
    MenuItem StartedMenu(TaskItem t)
    {
        var item = new MenuItem { Header = L.T("Rozpoczęte…") };
        if (t.Day == null) { item.IsEnabled = false; return item; }
        var day = ParseKey(t.Day);
        var first = SpanDays(t)?[0];
        void Add(string header, Action change)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => Do(() => { var real = Store.Materialize(t); change(); Store.Changed(real); });
            item.Items.Add(mi);
        }
        Add((first == null ? "✓  " : "     ") + L.F("Tylko {0} (bez wcześniejszych dni)", day.ToString("ddd d MMM", Pl)), () => Store.Materialize(t).WorkedDays = null);
        item.Items.Add(new Separator());
        // the last two weeks of workdays before the task's day
        var d = day.AddDays(-1);
        for (int n = 0; n < 10; d = d.AddDays(-1))
        {
            if (IsWeekend(d) || DaysOff.Name(d) != null) continue;
            n++;
            var from = d;
            var span = WorkdaysBetween(from, day);
            Add((first == DayKey(from) ? "✓  " : "     ") + L.F("od {0}  ({1} dni)", from.ToString("ddd d MMM", Pl), span.Count + 1), () =>
            {
                var real = Store.Materialize(t);
                var keep = real.WorkedDays?.Where(k => string.CompareOrdinal(k, DayKey(from)) >= 0) ?? Enumerable.Empty<string>(); // weekend days worked on stay
                real.WorkedDays = span.Select(DayKey).Concat(keep).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
                if (!real.Done) real.Started = true; // started earlier = in progress
            });
        }
        return item;
    }
}
