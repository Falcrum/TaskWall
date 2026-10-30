using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TaskWall;

/// <summary>Numbers of one period (week or month) of one account – shown in <see cref="ReportWindow"/>, saved as HTML or CSV.</summary>
public sealed class Report
{
    public sealed record Day(DateTime Date, int Tasks, int Done, double Hours, double DoneHours, double Meetings, string? Mark, int Continued, double Overtime = 0, double Afk = 0);
    public sealed record Category(string Name, string Color, int Tasks, int Done, double Hours, double DoneHours);

    public string Account = "";
    public bool Month;
    public DateTime From, To;
    public List<Day> Days = new();
    public List<(DateTime Day, TaskItem Task)> All = new();
    public List<Category> Categories = new();
    public Dictionary<string, (string Label, string Color, int Days)> Marks = new();
    public List<TaskItem> Carried = new();
    /// <summary>Multi-day tasks finished in the period, per project: average working days and the days in each state.</summary>
    public List<Insights.ProjectCycle> Projects = new();
    /// <summary>Open tasks now in one state for several working days (all of the account, not only this period).</summary>
    public List<(TaskItem Task, int Days)> Stuck = new();
    /// <summary>Open tasks on hold now, with the working days they have been waiting.</summary>
    public List<(TaskItem Task, int Days)> Held = new();
    /// <summary>Measured time against the estimate per category (all finished tasks with a timer).</summary>
    public List<Insights.Ratio> Ratios = new();

    public int Tasks => Days.Sum(d => d.Tasks);
    public int Done => Days.Sum(d => d.Done);
    public double Hours => Days.Sum(d => d.Hours);
    public double DoneHours => Days.Sum(d => d.DoneHours);
    public double Meetings => Days.Sum(d => d.Meetings);
    /// <summary>Hours of overtime (the day view's extra working periods).</summary>
    public double Overtime => Days.Sum(d => d.Overtime);
    public double Afk => Days.Sum(d => d.Afk);
    /// <summary>Time measured with the tasks' timers (hours).</summary>
    public double Tracked => All.Select(x => x.Task).Distinct().Sum(t => t.TrackedNow) / 60;
    public IEnumerable<(DateTime Day, TaskItem Task)> Open => All.Where(x => !x.Task.Done && !x.Task.Archived);

    public string Title => Month
        ? BoardWindow.Pl.TextInfo.ToTitleCase(From.ToString("MMMM yyyy", BoardWindow.Pl))
        : L.F("Tydzień {0}  ·  {1} – {2}", ISOWeek.GetWeekOfYear(From), From.ToString("d MMM", BoardWindow.Pl), To.ToString("d MMM yyyy", BoardWindow.Pl));
    public string FileName => $"TaskWall-{string.Concat(Account.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch))}-" + (Month ? From.ToString("yyyy-MM") : $"{ISOWeek.GetYear(From)}-W{ISOWeek.GetWeekOfYear(From):00}");

    static string Key(DateTime d) => TaskWall.Days.Key(d);

    /// <summary>Days of the period the task was worked on (its earlier days and its current one) and how many of them in review.</summary>
    internal static DateTime? TryDay(string? k) => TaskWall.Days.Parse(k);

    public (int Days, int Review, int Tests, int Hold) WorkDays(TaskItem t)
    {
        string from = Key(From), to = Key(To);
        bool In(string k) => string.CompareOrdinal(k, from) >= 0 && string.CompareOrdinal(k, to) <= 0;
        var days = (t.WorkedDays ?? new()).Where(In).ToList();
        if (t.Day != null && In(t.Day) && !days.Contains(t.Day)) days.Add(t.Day);
        string StateOn(string k) => k == t.Day ? t.State : t.DayStates?.GetValueOrDefault(k) ?? "started";
        int review = days.Count(k => StateOn(k) == "review");
        int tests = days.Count(k => StateOn(k) == "tests");
        int hold = days.Count(k => StateOn(k) == "hold"); // waited, not worked
        return (days.Count, review, tests, hold);
    }

    public static Report Build(BoardStore store, string layer, DateTime from, DateTime to, bool month)
    {
        var r = new Report { Account = App.AccountName(layer), Month = month, From = from.Date, To = to.Date };
        var keys = new HashSet<string>();
        for (var d = r.From; d <= r.To; d = d.AddDays(1))
        {
            var key = Key(d);
            keys.Add(key);
            var tasks = store.Data.Tasks.Where(t => t.Day == key && t.Text.Length > 0).Concat(store.VirtualTasks(d)).ToList(); // archived ones too
            r.All.AddRange(tasks.Select(t => (d, t)));
            double meet = CalendarService.On(d).Where(x => !x.IsHoliday && !x.AllDay).Sum(x => Math.Max(0, ((x.End < d.AddDays(1) ? x.End : d.AddDays(1)) - (x.Start > d ? x.Start : d)).TotalHours)) + store.Alarms.Sum(a => a.HoursOn(d)); // calendar events: only their part on this day
            var mark = store.DayMark(key);
            if (mark != null)
            {
                var mt = store.MarkTypeFor(mark);
                var cur = r.Marks.GetValueOrDefault(mark, (mt?.Label ?? mark, mt?.Color ?? "#9AA1B2", 0));
                r.Marks[mark] = (cur.Item1, cur.Item2, cur.Item3 + 1);
            }
            r.Days.Add(new Day(d, tasks.Count, tasks.Count(t => t.Done), tasks.Sum(t => t.Estimate ?? 0), tasks.Where(t => t.Done).Sum(t => t.Estimate ?? 0), meet, mark,
                store.Data.Tasks.Count(t => t.WorkedDays?.Contains(key) == true), store.OvertimeHours(key), d <= DateTime.Today ? store.BreakHours(key) : 0)); // breaks: only days already lived (the default lunch would fill the future)
        }
        r.Carried = store.Data.Tasks.Where(t => !t.Done && t.WorkedDays?.Any(keys.Contains) == true && t.Day != null && !keys.Contains(t.Day)).ToList();
        r.Projects = Insights.ByProject(Insights.Cycles(store.Data.Tasks, r.From, r.To));
        r.Stuck = store.Data.Tasks.Select(t => (t, Insights.DaysInState(t))).Where(x => x.Item2 >= Insights.StuckDays).OrderByDescending(x => x.Item2).ToList();
        r.Held = store.Data.Tasks.Where(t => t.IsHold && !t.Archived).Select(t => (t, Insights.DaysOnHold(t))).OrderByDescending(x => x.Item2).ToList();
        r.Ratios = Insights.EstimateRatios(store.Data.Tasks);
        r.Categories = r.All
            .SelectMany(x => (TaskItem.Tags(x.Task.Text, out _) is { Count: > 0 } tags ? tags : new List<string> { "" }).Select(c => (c, x.Task)))
            .GroupBy(x => x.c)
            .Select(g => new Category(g.Key.Length == 0 ? L.T("bez kategorii") : store.CategoryFor(g.Key)?.Name ?? g.Key,
                g.Key.Length == 0 ? "#9AA1B2" : store.CategoryFor(g.Key)?.Color ?? "#9AA1B2",
                g.Count(), g.Count(x => x.Task.Done), g.Sum(x => x.Task.Estimate ?? 0), g.Where(x => x.Task.Done).Sum(x => x.Task.Estimate ?? 0)))
            .OrderByDescending(c => c.Tasks).ThenByDescending(c => c.Hours).ToList();
        return r;
    }

    static string H(double h) => BoardWindow.Hours(h);
    internal static string D1(double d) => d.ToString("0.#", BoardWindow.Pl);

    // ---------- HTML (opens in any browser, prints well) ----------

    public string Html()
    {
        string E(string s) => WebUtility.HtmlEncode(s);
        var pl = BoardWindow.Pl;
        var sb = new StringBuilder();
        sb.Append($$$"""
            <!doctype html><html lang="{{{(L.En ? "en" : "pl")}}}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{{E(Title)}}} · {{{E(Account)}}}</title>
            <style>
            :root{--bg:#f5f4f1;--card:#fff;--fg:#1d222c;--dim:#5b6272;--faint:#8b919d;--line:#e3e1dc;--accent:#e8822e}
            @media (prefers-color-scheme:dark){:root{--bg:#141619;--card:#1c1f24;--fg:#e9ecf3;--dim:#a3a9b6;--faint:#6e7482;--line:#2a2e35}}
            *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.45 "Segoe UI",system-ui,sans-serif}
            main{max-width:960px;margin:0 auto;padding:32px 20px 48px}
            .kicker{font-size:12px;font-weight:700;letter-spacing:.06em;color:var(--dim);text-transform:uppercase}
            h1{font-size:26px;margin:4px 0 20px}h2{font-size:12px;letter-spacing:.06em;text-transform:uppercase;color:var(--faint);margin:28px 0 10px}
            .tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:12px}
            .tile{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:14px 16px}
            .tile b{display:block;font-size:24px}.tile span{color:var(--faint);font-size:12px}.accent{color:var(--accent)}
            .bars{display:flex;gap:4px;align-items:flex-end;height:120px;background:var(--card);border:1px solid var(--line);border-radius:12px;padding:12px 12px 26px;position:relative}
            .bar{flex:1;display:flex;flex-direction:column;justify-content:flex-end;align-items:center;height:100%;position:relative}
            .bar i{display:block;width:100%;max-width:38px;border-radius:4px 4px 2px 2px;background:var(--accent)}
            .bar i.plan{background:var(--line)}.bar em{position:absolute;bottom:-20px;font-style:normal;font-size:11px;color:var(--faint)}
            .bar small{font-size:11px;color:var(--dim)}
            table{width:100%;border-collapse:collapse;background:var(--card);border:1px solid var(--line);border-radius:12px;overflow:hidden}
            th,td{text-align:left;padding:7px 12px;border-bottom:1px solid var(--line);vertical-align:top}th{font-size:11px;color:var(--faint);text-transform:uppercase;letter-spacing:.05em}
            tr:last-child td{border-bottom:0}td.n{text-align:right;white-space:nowrap;color:var(--dim)}
            .dot{display:inline-block;width:9px;height:9px;border-radius:50%;margin-right:7px}
            .meter{height:6px;border-radius:3px;background:var(--line);overflow:hidden;min-width:80px}.meter i{display:block;height:100%;background:var(--accent)}
            .tag{font-size:11px;font-weight:700;padding:1px 6px;border-radius:4px;margin-right:6px;background:var(--line)}
            .done{color:var(--dim)}.day{color:var(--faint);white-space:nowrap;width:1%;padding-right:18px}th.n{text-align:right}
            footer{margin-top:32px;color:var(--faint);font-size:12px}
            </style></head><body><main>
            """);
        sb.Append($"<div class=\"kicker\">{E(L.T(Month ? "Raport miesięczny" : "Raport tygodniowy"))} · {E(Account)}</div><h1>{E(Title)}</h1>");

        // tiles
        sb.Append("<div class=\"tiles\">");
        void Tile(string value, string label, bool accent = false) => sb.Append($"<div class=\"tile\"><b{(accent ? " class=\"accent\"" : "")}>{E(value)}</b><span>{E(label)}</span></div>");
        Tile($"{Done}/{Tasks}", L.T("zadań zrobionych"), true);
        Tile(H(DoneHours), L.F("z {0} zaplanowanych", H(Hours)));
        Tile(H(Meetings), L.T("spotkań"));
        if (Overtime > 0) Tile(H(Math.Round(Overtime, 2)), L.T("nadgodzin"));
        if (Afk > 0) Tile(H(Math.Round(Afk, 2)), L.T("przerw (AFK, lunch, prywatne)"));
        if (Tracked > 0) Tile(H(Math.Round(Tracked, 1)), L.T("zmierzonego czasu pracy (łącznie na tych zadaniach)"));
        Tile((Open.Count() + Carried.Count).ToString(), L.T("przeszło dalej / otwarte"));
        foreach (var (code, m) in Marks) Tile(m.Days.ToString(), $"{MarkType.Show(code)} · {MarkType.ShowLabel(code, m.Label)}");
        sb.Append("</div>");

        // done per day
        sb.Append($"<h2>{E(L.T("Zrobione w dniach"))}</h2><div class=\"bars\">");
        int max = Math.Max(1, Days.Max(d => d.Tasks));
        foreach (var d in Days)
        {
            var label = Month ? d.Date.Day.ToString() : pl.DateTimeFormat.GetAbbreviatedDayName(d.Date.DayOfWeek).TrimEnd('.');
            sb.Append($"<div class=\"bar\" title=\"{E(d.Date.ToString("dddd d MMM", pl))}: {d.Done}/{d.Tasks}\">");
            if (!Month && d.Tasks > 0) sb.Append($"<small>{d.Done}/{d.Tasks}</small>");
            if (d.Tasks > d.Done) sb.Append($"<i class=\"plan\" style=\"height:{80.0 * (d.Tasks - d.Done) / max:0.#}%\"></i>");
            if (d.Done > 0) sb.Append($"<i style=\"height:{80.0 * d.Done / max:0.#}%\"></i>");
            sb.Append($"<em>{E(label)}</em></div>");
        }
        sb.Append("</div>");

        // categories
        if (Categories.Count > 0)
        {
            sb.Append($"<h2>{E(L.T("Kategorie"))}</h2><table><tr><th>{E(L.T("Kategoria"))}</th><th class=\"n\">{E(L.T("Zrobione"))}</th><th></th><th class=\"n\">{E(L.T("Godziny"))}</th></tr>");
            foreach (var c in Categories)
                sb.Append($"<tr><td><span class=\"dot\" style=\"background:{E(c.Color)}\"></span>{E(c.Name)}</td><td class=\"n\">{c.Done}/{c.Tasks}</td>"
                    + $"<td style=\"width:30%\"><div class=\"meter\"><i style=\"width:{100.0 * c.Done / Math.Max(1, c.Tasks):0}%\"></i></div></td><td class=\"n\">{E(H(c.DoneHours))} / {E(H(c.Hours))}</td></tr>");
            sb.Append("</table>");
        }

        // how long multi-day tasks took, per project, and the days in each state
        if (Projects.Count > 0)
        {
            sb.Append($"<h2>{E(L.T("Czas do zrobienia (zadania wielodniowe)"))}</h2><table><tr><th>{E(L.T("Projekt"))}</th><th class=\"n\">{E(L.T("Zadania"))}</th><th></th><th class=\"n\">{E(L.T("Średnio dni"))}</th></tr>");
            double top = Math.Max(1, Projects.Max(x => x.Days));
            foreach (var p in Projects)
            {
                string Seg(double d, System.Windows.Media.Color c) => d > 0 ? $"<i style=\"display:inline-block;height:7px;border-radius:2px;margin-right:1px;width:{100 * d / top:0.#}%;background:#{c.R:X2}{c.G:X2}{c.B:X2}\" title=\"{d:0.#}\"></i>" : "";
                sb.Append($"<tr><td>{E(p.Project.Length > 0 ? p.Project : L.T("bez projektu"))}</td><td class=\"n\">{p.Tasks}</td><td style=\"width:40%\" title=\"{E(L.F("w trakcie {0}, review {1}, testy {2}, hold {3}", D1(p.Started), D1(p.Review), D1(p.Tests), D1(p.Hold)))}\">"
                    + Seg(p.Started, TaskMarks.InProgressOrange) + Seg(p.Review, TaskMarks.ReviewViolet) + Seg(p.Tests, TaskMarks.TestsTeal) + Seg(p.Hold, TaskMarks.HoldRed) + $"</td><td class=\"n\">{D1(p.Days)}</td></tr>");
            }
            sb.Append("</table>");
        }
        if (Held.Count > 0)
        {
            sb.Append($"<h2>{E(L.T("Wstrzymane (hold)"))} ({Held.Count})</h2><table>");
            foreach (var (t, days) in Held) sb.Append($"<tr><td>{E(t.Text)}</td><td class=\"n\">{E(L.F("czeka {0} dni · potem {1}", days, TaskMarks.StateName(t, t.WorkState)))}</td></tr>");
            sb.Append("</table>");
        }
        if (Stuck.Count > 0)
        {
            sb.Append($"<h2>{E(L.F("Utknęły – {0}+ dni w jednym stanie", Insights.StuckDays))} ({Stuck.Count})</h2><table>");
            foreach (var (t, days) in Stuck) sb.Append($"<tr><td>{E(t.Text)}</td><td class=\"n\">{E(L.F("{0} dni · {1}", days, TaskMarks.StateName(t)))}</td></tr>");
            sb.Append("</table>");
        }
        if (Ratios.Count > 0)
        {
            sb.Append($"<h2>{E(L.T("Estymacja a zmierzony czas"))}</h2><table><tr><th>{E(L.T("Kategoria"))}</th><th class=\"n\">{E(L.T("Zadania"))}</th><th class=\"n\">{E(L.T("Zmierzony / estymacja"))}</th></tr>");
            foreach (var x in Ratios) sb.Append($"<tr><td>{E(x.Tag)}</td><td class=\"n\">{x.Tasks}</td><td class=\"n\">×{x.Factor.ToString("0.0#", pl)}</td></tr>");
            sb.Append("</table>");
        }

        void TaskTable(string caption, IEnumerable<(DateTime Day, TaskItem Task)> rows)
        {
            var list = rows.ToList();
            if (list.Count == 0) return;
            sb.Append($"<h2>{E(caption)} ({list.Count})</h2><table>");
            foreach (var (day, t) in list)
            {
                var tags = TaskItem.Tags(t.Text, out var rest);
                sb.Append($"<tr><td class=\"day\">{E(day.ToString("ddd d MMM", pl))}</td><td{(t.Done ? " class=\"done\"" : "")}>");
                if (t.NotionSource != null) sb.Append($"<span class=\"tag\" style=\"background:none;border:1px solid var(--line)\">{E(t.NotionSource)}</span>");
                foreach (var tag in tags) sb.Append($"<span class=\"tag\">{E(tag)}</span>");
                sb.Append(t.Url is { Length: > 0 } url ? $"<a href=\"{E(url)}\" style=\"color:inherit\">{E(rest)}</a>" : E(rest));
                if (t.Checklist is { Count: > 0 } cl) sb.Append($" <span class=\"day\">☑ {cl.Count(x => x.Done)}/{cl.Count}</span>");
                if (WorkDays(t) is { Days: > 1 } wd) // worked on over several days (in progress / review carried on)
                    sb.Append($" <span class=\"day\">↷ {E(L.F("{0} dni pracy", wd.Days))}{(wd.Review > 0 ? E(" · " + L.F("review: {0}", wd.Review)) : "")}{(wd.Tests > 0 ? E(" · " + L.F("testy: {0}", wd.Tests)) : "")}{(wd.Hold > 0 ? E(" · " + L.F("hold: {0}", wd.Hold)) : "")}</span>");
                sb.Append($"</td><td class=\"n\">{(t.Estimate is > 0 ? E(BoardWindow.Est(t.Estimate.Value)) : "")}{(t.TrackedNow > 0 ? " · ⏱ " + E(BoardWindow.Duration(t.TrackedNow)) : "")}</td></tr>");
            }
            sb.Append("</table>");
        }
        TaskTable(L.T("Zrobione"), All.Where(x => x.Task.Done).OrderBy(x => x.Day));
        TaskTable(L.T("Niezrobione / przeszły dalej"), Open.Concat(Carried.Where(t => TryDay(t.Day) != null).Select(t => (TryDay(t.Day)!.Value, t))).OrderBy(x => x.Item1));

        sb.Append($"<footer>TaskWall {E(Installer.Version)} · {E(DateTime.Now.ToString("d MMM yyyy, HH:mm", pl))}</footer></main></body></html>");
        return sb.ToString();
    }

    // ---------- CSV (spreadsheets) ----------

    public string Csv()
    {
        string Hn(double h) => h.ToString("0.##", BoardWindow.Pl);
        static string Q(string s) => s.Contains(';') || s.Contains('\n') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        var sb = new StringBuilder();
        sb.AppendLine($"{Q(Account)};{From:yyyy-MM-dd} – {To:yyyy-MM-dd}");
        sb.AppendLine();
        sb.AppendLine(L.T("Data;Dzień tygodnia;Tydzień;Oznaczenie;Zadania;Zrobione;Kontynuowane;Estymacja [h];Zrobione [h];Spotkania [h];Nadgodziny [h]"));
        foreach (var d in Days)
            sb.AppendLine($"{Key(d.Date)};{d.Date.ToString("dddd", BoardWindow.Pl)};{ISOWeek.GetWeekOfYear(d.Date)};{d.Mark};{d.Tasks};{d.Done};{d.Continued};{Hn(d.Hours)};{Hn(d.DoneHours)};{Hn(d.Meetings)};{Hn(d.Overtime)}");
        sb.AppendLine($"{L.T("Razem")};;;{string.Join(" ", Marks.Select(kv => $"{kv.Key} {kv.Value.Days}"))};{Tasks};{Done};;{Hn(Hours)};{Hn(DoneHours)};{Hn(Meetings)};{Hn(Overtime)}");
        sb.AppendLine();
        sb.AppendLine(L.T("Kategoria;Zadania;Zrobione;Estymacja [h];Zrobione [h]"));
        foreach (var c in Categories) sb.AppendLine($"{Q(c.Name)};{c.Tasks};{c.Done};{Hn(c.Hours)};{Hn(c.DoneHours)}");
        sb.AppendLine();
        sb.AppendLine(L.T("Data;Zadanie;Kategorie;Zrobione;Estymacja [h];Link"));
        foreach (var (d, t) in All.OrderBy(x => x.Day).ThenBy(x => x.Task.Order))
        {
            var tags = TaskItem.Tags(t.Text, out var rest);
            sb.AppendLine($"{Key(d)};{Q(rest)};{Q(string.Join(", ", tags))};{(t.Done ? L.T("tak") : L.T("nie"))};{(t.Estimate is { } est ? Hn(est) : "")};{Q(t.Url ?? "")}");
        }
        return sb.ToString();
    }
}

/// <summary>
/// Weekly / monthly report in the app (‹ › to other periods), saved as a readable HTML page (kept in the account's
/// "raporty" folder, so it can be opened any time, also on the other computer) or as CSV for a spreadsheet.
/// </summary>
public sealed class ReportWindow : DarkWindow
{
    readonly BoardStore _store;
    readonly string _layer;
    bool _month;
    DateTime _from;
    readonly StackPanel _body = new() { Margin = new Thickness(22, 4, 22, 8) };
    readonly ToggleButton _weekTab = new() { Content = L.T("Tydzień"), Style = (Style)Application.Current.Resources["Chip"] };
    readonly ToggleButton _monthTab = new() { Content = L.T("Miesiąc"), Style = (Style)Application.Current.Resources["Chip"] };
    readonly TextBlock _title = Label("", 17, "Fg", FontWeights.Bold);
    Report _report = null!;

    ReportWindow(BoardStore store, string layer, DateTime day, bool month)
    {
        _store = store;
        _layer = layer;
        _month = month;
        _from = Start(day);
        Title = L.T("Raport");
        Width = 640;
        Height = Math.Min(860, SystemParameters.WorkArea.Height * 0.9);
        MinWidth = 520;
        Topmost = true;

        var top = new StackPanel { Margin = new Thickness(22, 16, 22, 6) };
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        tabs.Children.Add(_weekTab);
        tabs.Children.Add(_monthTab);
        _weekTab.Click += (_, _) => { _month = false; _from = Start(_from); Fill(); };
        _monthTab.Click += (_, _) => { _month = true; _from = Start(_from); Fill(); };
        top.Children.Add(tabs);
        var nav = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        Button Nav(string text, string tip, Action a)
        {
            var b = new Button { Style = (Style)Application.Current.Resources["IconButton"], Content = text, FontSize = 12, Padding = new Thickness(8, 4, 8, 4), ToolTip = tip };
            b.Click += (_, _) => a();
            return b;
        }
        var prev = Nav("", L.T("Poprzedni"), () => { _from = _month ? _from.AddMonths(-1) : _from.AddDays(-7); Fill(); });
        var next = Nav("", L.T("Następny"), () => { _from = _month ? _from.AddMonths(1) : _from.AddDays(7); Fill(); });
        var now = Btn(L.T("Bieżący"), "LinkButton", (_, _) => { _from = Start(DateTime.Today); Fill(); });
        DockPanel.SetDock(prev, Dock.Left);
        DockPanel.SetDock(next, Dock.Left);
        DockPanel.SetDock(now, Dock.Right);
        nav.Children.Add(prev);
        nav.Children.Add(next);
        nav.Children.Add(now);
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.Margin = new Thickness(6, 0, 0, 0);
        nav.Children.Add(_title);
        top.Children.Add(nav);

        var footer = new DockPanel { Margin = new Thickness(22, 8, 22, 16) };
        var folder = Btn(L.T("Folder raportów"), "LinkButton", (_, _) => { Directory.CreateDirectory(ReportsFolder); GlassWindow.OpenUrl(ReportsFolder); });
        folder.Margin = new Thickness(-8, 0, 0, 0);
        DockPanel.SetDock(folder, Dock.Left);
        footer.Children.Add(folder);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(Btn(L.T("CSV…"), "SecondaryButton", (_, _) => SaveCsv()));
        buttons.Children.Add(Btn(L.T("Zapisz i otwórz raport"), "PrimaryButton", (_, _) => SaveHtml()));
        footer.Children.Add(buttons);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = root;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) Close();
            else if (e.Key == System.Windows.Input.Key.Left) { _from = _month ? _from.AddMonths(-1) : _from.AddDays(-7); Fill(); }
            else if (e.Key == System.Windows.Input.Key.Right) { _from = _month ? _from.AddMonths(1) : _from.AddDays(7); Fill(); }
        };
        Fill();
    }

    DateTime Start(DateTime d) => _month ? new DateTime(d.Year, d.Month, 1) : BoardWindow.Monday(d);
    DateTime End => _month ? _from.AddMonths(1).AddDays(-1) : _from.AddDays(6);
    // the account's folder (not the local copy used while the cloud drive is away)
    string ReportsFolder => Path.Combine(App.LayerFolder(_layer), "raporty");

    void Fill()
    {
        _weekTab.IsChecked = !_month;
        _monthTab.IsChecked = _month;
        _report = Report.Build(_store, _layer, _from, End, _month);
        var r = _report;
        _title.Text = r.Title;
        _body.Children.Clear();
        var pl = BoardWindow.Pl;

        var stats = new UniformGrid { Columns = 4, Margin = new Thickness(0, 6, 0, 14) };
        void Stat(string value, string label, bool accent = false)
        {
            var s = new StackPanel { Margin = new Thickness(0, 0, 8, 6) };
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
        _body.Children.Add(stats);

        // done per day: accent = done, faint = still open
        _body.Children.Add(Caption(L.T("ZROBIONE W DNIACH")));
        var bars = new Grid { Height = 86, Margin = new Thickness(0, 4, 0, 14) };
        int max = Math.Max(1, r.Days.Max(d => d.Tasks));
        for (int i = 0; i < r.Days.Count; i++)
        {
            var d = r.Days[i];
            bars.ColumnDefinitions.Add(new ColumnDefinition());
            var col = new DockPanel { Margin = new Thickness(r.Month ? 1 : 3, 0, r.Month ? 1 : 3, 0), ToolTip = $"{d.Date.ToString("dddd d MMM", pl)}: {d.Done}/{d.Tasks}" + (d.Hours > 0 ? $" · {BoardWindow.Hours(d.DoneHours)}/{BoardWindow.Hours(d.Hours)}" : ""), Background = Brushes.Transparent };
            bool showLabel = !r.Month || d.Date.Day == 1 || d.Date.Day % 5 == 0;
            var name = Label(showLabel ? (r.Month ? d.Date.Day.ToString() : pl.DateTimeFormat.GetAbbreviatedDayName(d.Date.DayOfWeek).TrimEnd('.')) : "", 10, "FgFaint");
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
            if (d.Tasks > d.Done) stack.Children.Add(new Border { CornerRadius = new CornerRadius(3, 3, 0, 0), Height = 52.0 * (d.Tasks - d.Done) / max, Background = new SolidColorBrush(Th.Ink(0x20)) });
            if (d.Done > 0) stack.Children.Add(new Border { CornerRadius = new CornerRadius(d.Tasks > d.Done ? 0 : 3, d.Tasks > d.Done ? 0 : 3, 2, 2), Height = 52.0 * d.Done / max, Background = Ui.Res("AccentBrush") });
            if (d.Tasks == 0) stack.Children.Add(new Border { Height = 2, CornerRadius = new CornerRadius(1), Background = new SolidColorBrush(Th.Ink(0x14)) });
            col.Children.Add(stack);
            Grid.SetColumn(col, i);
            bars.Children.Add(col);
        }
        _body.Children.Add(bars);

        if (r.Categories.Count > 0)
        {
            _body.Children.Add(Caption(L.T("KATEGORIE")));
            foreach (var c in r.Categories)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
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
                _body.Children.Add(row);
            }
        }

        void List(string caption, IEnumerable<(DateTime Day, TaskItem Task)> items, int limit)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            _body.Children.Add(Caption($"{caption} ({list.Count})"));
            foreach (var (day, t) in list.Take(limit))
            {
                var d = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                var when = Label(day.ToString("ddd d.MM", pl), 11, "FgFaint");
                when.Width = 62;
                DockPanel.SetDock(when, Dock.Left);
                d.Children.Add(when);
                var wd = r.WorkDays(t);
                var est = Label((wd.Days > 1 ? "↷ " + L.F("{0} dni pracy", wd.Days) + (wd.Review > 0 ? " · " + L.F("review: {0}", wd.Review) : "") + (wd.Tests > 0 ? " · " + L.F("testy: {0}", wd.Tests) : "") + (wd.Hold > 0 ? " · " + L.F("hold: {0}", wd.Hold) : "") + "   " : "")
                    + (t.Estimate is > 0 ? BoardWindow.Est(t.Estimate.Value) : ""), 11.5, "FgFaint");
                est.Margin = new Thickness(10, 0, 0, 0);
                DockPanel.SetDock(est, Dock.Right);
                d.Children.Add(est);
                var text = Label(t.Text, 12, t.Done ? "FgDim" : "Fg");
                text.TextWrapping = TextWrapping.NoWrap;
                text.TextTrimming = TextTrimming.CharacterEllipsis;
                text.ToolTip = t.Text;
                d.Children.Add(text);
                _body.Children.Add(d);
            }
            if (list.Count > limit) _body.Children.Add(Label(L.F("… i {0} więcej (pełna lista w zapisanym raporcie)", list.Count - limit), 11, "FgFaint"));
        }
        // how long the finished tasks took, per project, and in which state the days went
        if (r.Projects.Count > 0)
        {
            _body.Children.Add(Caption(L.T("CZAS DO ZROBIENIA (ZADANIA WIELODNIOWE)")));
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
                _body.Children.Add(row);
            }
        }
        if (r.Held.Count > 0)
        {
            _body.Children.Add(Caption(L.F("WSTRZYMANE – HOLD ({0})", r.Held.Count)));
            foreach (var (t, days) in r.Held.Take(12))
            {
                var d = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                var what = Label(L.F("czeka {0} dni · potem {1}", days, TaskMarks.StateName(t, t.WorkState)), 11.5, "FgFaint");
                what.Foreground = new SolidColorBrush(TaskMarks.HoldRed);
                DockPanel.SetDock(what, Dock.Right);
                d.Children.Add(what);
                var text = Label(t.Text, 12, "Fg");
                text.TextTrimming = TextTrimming.CharacterEllipsis;
                d.Children.Add(text);
                _body.Children.Add(d);
            }
        }
        if (r.Stuck.Count > 0)
        {
            _body.Children.Add(Caption(L.F("UTKNĘŁY – {0}+ DNI W JEDNYM STANIE ({1})", Insights.StuckDays, r.Stuck.Count)));
            foreach (var (t, days) in r.Stuck.Take(12))
            {
                var d = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                var what = Label(L.F("{0} dni · {1}", days, TaskMarks.StateName(t)), 11.5, "FgFaint");
                what.Foreground = new SolidColorBrush(TaskMarks.StateColor(t));
                DockPanel.SetDock(what, Dock.Right);
                d.Children.Add(what);
                var text = Label(t.Text, 12, "Fg");
                text.TextTrimming = TextTrimming.CharacterEllipsis;
                d.Children.Add(text);
                _body.Children.Add(d);
            }
        }
        if (r.Ratios.Count > 0)
        {
            _body.Children.Add(Caption(L.T("ESTYMACJA A ZMIERZONY CZAS (WSZYSTKIE ZROBIONE Z LICZNIKIEM)")));
            foreach (var x in r.Ratios.Take(10))
            {
                var d = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                var f = Label($"×{x.Factor.ToString("0.0#", pl)}", 12, x.Factor > 1.15 ? "AccentBrush" : "FgDim", FontWeights.Bold);
                DockPanel.SetDock(f, Dock.Right);
                d.Children.Add(f);
                d.Children.Add(Label($"{x.Tag}  ·  " + L.F("{0} zadań", x.Tasks) + "  ·  " + (x.Factor > 1.05 ? L.F("trwa zwykle o {0}% dłużej niż estymacja", Math.Round((x.Factor - 1) * 100)) : x.Factor < 0.95 ? L.F("trwa zwykle o {0}% krócej niż estymacja", Math.Round((1 - x.Factor) * 100)) : L.T("zgodnie z estymacją")), 12, "Fg"));
                _body.Children.Add(d);
            }
        }
        List(L.T("ZROBIONE"), r.All.Where(x => x.Task.Done).OrderBy(x => x.Day), r.Month ? 25 : 15);
        List(L.T("NIEZROBIONE / PRZESZŁY DALEJ"), r.Open.Concat(r.Carried.Where(t => Report.TryDay(t.Day) != null).Select(t => (Report.TryDay(t.Day)!.Value, t))).OrderBy(x => x.Item1), 12);
        if (r.Tasks == 0) _body.Children.Add(Label(L.T("W tym okresie nie ma zadań."), 12, "FgFaint"));
    }

    static TextBlock Caption(string text)
    {
        var c = Label(text, 10.5, "FgFaint", FontWeights.Bold);
        c.Margin = new Thickness(0, 8, 0, 4);
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
        catch (Exception ex) { MessageBox.Show(this, L.T("Nie udało się zapisać pliku:") + "\n" + ex.Message, "TaskWall"); }
    }

    void SaveCsv()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = L.T("Eksport okresu do CSV"), FileName = _report.FileName + ".csv", Filter = "CSV (*.csv)|*.csv",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };
        if (dlg.ShowDialog(this) != true) return;
        try { File.WriteAllText(dlg.FileName, _report.Csv(), new UTF8Encoding(true)); }
        catch (Exception ex) { MessageBox.Show(this, L.T("Nie udało się zapisać pliku:") + "\n" + ex.Message, "TaskWall"); }
    }

    public static void Show(BoardStore store, string layer, DateTime day, bool month)
    {
        GlassWindow.EndOverlays();
        new ReportWindow(store, layer, day, month).Show();
    }

    /// <summary>On the first start of a week: the report of the week before (once – the mark is shared between computers).</summary>
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
            Show(App.Store, s.Layer, last, month: false);
    }
}
