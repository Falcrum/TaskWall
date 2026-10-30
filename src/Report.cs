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

/// <summary>Numbers of one period (week or month) of one account – shown in <see cref="ReportView"/>, saved as HTML or CSV.</summary>
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
    /// <summary>
    /// The backlog as it is now (open tasks without a day) – not the period's work, but what waits for the next periods:
    /// how many tasks, how many were added in the period, and the ones worked on in the period that went back to it.
    /// </summary>
    public List<TaskItem> Backlog = new();
    public int BacklogAdded;
    public List<TaskItem> Returned = new();
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

    /// <param name="insights">false: only the period's own numbers (the year view's middle part), not the board-wide stuck / held / estimate lists</param>
    public static Report Build(BoardStore store, string layer, DateTime from, DateTime to, bool month, bool insights = true)
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
        r.Backlog = store.Data.Tasks.Where(t => t.Day == null && !t.Done && !t.Archived && t.Text.Length > 0).ToList();
        DateTime fromUtc = r.From.ToUniversalTime(), toUtc = r.To.AddDays(1).ToUniversalTime();
        r.BacklogAdded = r.Backlog.Count(t => t.Created is { } c && c >= fromUtc && c < toUtc);
        r.Returned = r.Backlog.Where(t => t.WorkedDays?.Any(keys.Contains) == true).ToList();
        if (!insights) return r;
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
        if (Backlog.Count > 0)
        {
            // the backlog: what waits for the next periods (the tasks that went back to it from this one by name)
            sb.Append($"<h2>{E(L.T("Backlog"))}</h2><div class=\"tiles\">");
            Tile(Backlog.Count.ToString(), L.T("zadań czeka w backlogu"));
            if (Backlog.Sum(t => t.Estimate ?? 0) is > 0 and var bh) Tile(H(bh), L.T("estymacji w backlogu"));
            Tile(BacklogAdded.ToString(), L.T("dodanych w tym okresie"));
            if (Returned.Count > 0) Tile(Returned.Count.ToString(), L.T("wróciło z tego okresu do backlogu"));
            sb.Append("</div>");
            if (Returned.Count > 0)
            {
                sb.Append($"<h2>{E(L.T("Wróciły do backlogu"))} ({Returned.Count})</h2><table>");
                foreach (var t in Returned) sb.Append($"<tr><td>{E(t.Text)}</td><td class=\"n\">{E(L.F("{0} dni pracy", WorkDays(t).Days))}</td></tr>");
                sb.Append("</table>");
            }
        }

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

