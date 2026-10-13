using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TaskWall;

/// <summary>
/// "Podsumowanie tygodnia": what got done, the hours, meetings, what carried on and the categories. Shown once a week
/// for the week before (Monday, first start; Settings → Tablica can turn it off) and from the ⋯ menu any time.
/// </summary>
public sealed class WeekSummaryWindow : DarkWindow
{
    static string Key(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    static string WeekId(DateTime monday) => $"{ISOWeek.GetYear(monday)}-W{ISOWeek.GetWeekOfYear(monday):00}";

    WeekSummaryWindow(BoardStore store, DateTime monday, string layer)
    {
        var pl = BoardWindow.Pl;
        var days = Enumerable.Range(0, 7).Select(i => monday.AddDays(i)).ToList();
        var keys = days.Select(Key).ToHashSet();
        var tasks = store.Data.Tasks.Where(t => t.Day != null && keys.Contains(t.Day) && t.Text.Length > 0).ToList();
        var done = tasks.Where(t => t.Done).ToList();
        var open = tasks.Where(t => !t.Done && !t.Archived).ToList();
        // unfinished tasks that were on these days and carried on to a later one
        var carried = store.Data.Tasks.Where(t => !t.Done && t.WorkedDays?.Any(keys.Contains) == true && t.Day != null && !keys.Contains(t.Day)).ToList();
        double doneHours = done.Sum(t => t.Estimate ?? 0), planned = tasks.Sum(t => t.Estimate ?? 0);
        double meetings = days.Sum(d => CalendarService.On(d).Where(e => !e.IsHoliday).Sum(e => e.Hours) + store.Alarms.Where(a => a.IsMeeting && a.Occurs(d)).Sum(a => a.Hours));

        Title = L.T("Podsumowanie tygodnia");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height * 0.9;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;

        var p = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        p.Children.Add(Label(L.Up(L.T("Podsumowanie tygodnia")) + "  ·  " + L.Up(App.AccountName(layer)), 11.5, "FgDim", FontWeights.Bold));
        var title = Label(L.F("Tydzień {0}  ·  {1} – {2}", ISOWeek.GetWeekOfYear(monday), monday.ToString("d MMM", pl), monday.AddDays(6).ToString("d MMM yyyy", pl)), 17, "Fg", FontWeights.Bold);
        title.Margin = new Thickness(0, 2, 0, 14);
        p.Children.Add(title);

        // ----- numbers -----
        var stats = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 16) };
        void Stat(string value, string label, bool accent = false)
        {
            var s = new StackPanel();
            s.Children.Add(Label(value, 20, accent ? "AccentBrush" : "Fg", FontWeights.Bold));
            s.Children.Add(Label(label, 11, "FgFaint"));
            stats.Children.Add(s);
        }
        Stat($"{done.Count}/{tasks.Count}", L.T("zadań zrobionych"), accent: true);
        Stat(BoardWindow.Hours(doneHours), L.F("z {0} zaplanowanych", BoardWindow.Hours(planned)));
        Stat(BoardWindow.Hours(meetings), L.T("spotkań"));
        Stat((open.Count + carried.Count).ToString(), L.T("przeszło dalej / otwarte"));
        p.Children.Add(stats);

        // ----- done per day (small bars) -----
        p.Children.Add(Caption(L.T("ZROBIONE W DNIACH")));
        var bars = new Grid { Height = 64, Margin = new Thickness(0, 4, 0, 14) };
        int max = Math.Max(1, days.Max(d => done.Count(t => t.Day == Key(d))));
        for (int i = 0; i < 7; i++)
        {
            bars.ColumnDefinitions.Add(new ColumnDefinition());
            int n = done.Count(t => t.Day == Key(days[i]));
            var col = new DockPanel { Margin = new Thickness(3, 0, 3, 0) };
            var name = Label(pl.DateTimeFormat.GetAbbreviatedDayName(days[i].DayOfWeek).TrimEnd('.'), 10, "FgFaint");
            name.HorizontalAlignment = HorizontalAlignment.Center;
            DockPanel.SetDock(name, Dock.Bottom);
            col.Children.Add(name);
            var count = Label(n > 0 ? n.ToString() : "", 10, "FgDim");
            count.HorizontalAlignment = HorizontalAlignment.Center;
            DockPanel.SetDock(count, Dock.Top);
            col.Children.Add(count);
            col.Children.Add(new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(3), Height = Math.Max(2, 36.0 * n / max),
                Background = n > 0 ? Ui.Res("AccentBrush") : new SolidColorBrush(Th.Ink(0x18)),
            });
            Grid.SetColumn(col, i);
            bars.Children.Add(col);
        }
        p.Children.Add(bars);

        // ----- categories -----
        var cats = done.SelectMany(t => { var tags = TaskItem.Tags(t.Text, out _); return (tags.Count > 0 ? tags : new List<string> { "" }).Select(c => (c, t)); })
            .GroupBy(x => x.c).Select(g => (Name: g.Key.Length == 0 ? L.T("bez kategorii") : store.CategoryFor(g.Key)?.Name ?? g.Key, Count: g.Count(), Hours: g.Sum(x => x.t.Estimate ?? 0)))
            .OrderByDescending(x => x.Count).Take(6).ToList();
        if (cats.Count > 0)
        {
            p.Children.Add(Caption(L.T("KATEGORIE (ZROBIONE)")));
            foreach (var c in cats)
                p.Children.Add(Line(c.Name, c.Count + (c.Hours > 0 ? "  ·  " + BoardWindow.Hours(c.Hours) : "")));
        }

        // ----- lists -----
        void List(string caption, IEnumerable<TaskItem> items, int limit)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            p.Children.Add(Caption(caption));
            foreach (var t in list.Take(limit))
                p.Children.Add(Line(t.Text, t.Estimate is > 0 ? BoardWindow.Est(t.Estimate.Value) : ""));
            if (list.Count > limit) p.Children.Add(Label(L.F("… i {0} więcej", list.Count - limit), 11, "FgFaint"));
        }
        List(L.T("ZROBIONE"), done.OrderBy(t => t.Day), 12);
        List(L.T("NIEZROBIONE / PRZESZŁY DALEJ"), open.Concat(carried).Distinct(), 8);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(Btn(L.T("Zamknij"), "PrimaryButton", (_, _) => Close()));
        p.Children.Add(buttons);
        Content = new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    static TextBlock Caption(string text)
    {
        var c = Label(text, 10.5, "FgFaint", FontWeights.Bold);
        c.Margin = new Thickness(0, 6, 0, 4);
        return c;
    }

    static FrameworkElement Line(string left, string right)
    {
        var d = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
        var r = Label(right, 11.5, "FgFaint");
        r.Margin = new Thickness(10, 0, 0, 0);
        DockPanel.SetDock(r, Dock.Right);
        d.Children.Add(r);
        var l = Label(left, 12, "Fg");
        l.TextWrapping = TextWrapping.NoWrap;
        l.TextTrimming = TextTrimming.CharacterEllipsis;
        d.Children.Add(l);
        return d;
    }

    public static void Show(BoardStore store, DateTime monday, string layer) => new WeekSummaryWindow(store, monday, layer).Show();

    /// <summary>On the first start of a week: the summary of the week before (once, on one computer – the mark is shared).</summary>
    public static void ShowWeeklyIfDue()
    {
        var s = App.Settings;
        if (!s.WeeklySummary) return;
        var last = BoardWindow.Monday(DateTime.Today).AddDays(-7);
        var id = WeekId(last);
        if (s.LastWeekSummary == id) return;
        bool first = s.LastWeekSummary == null;
        s.LastWeekSummary = id;
        SettingsStore.Save(s);
        if (first) return; // not on the very first start after installing
        var store = App.Store;
        if (store.Data.Tasks.Any(t => t.Day != null && string.CompareOrdinal(t.Day, Key(last)) >= 0 && string.CompareOrdinal(t.Day, Key(last.AddDays(6))) <= 0))
            Show(store, last, s.Layer);
    }
}
