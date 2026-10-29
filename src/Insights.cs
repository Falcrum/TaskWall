using System;
using System.Collections.Generic;
using System.Linq;

namespace TaskWall;

/// <summary>
/// What the board's history says (no UI here – the report, the rows and the add box show it, the self-test checks it):
/// how long tasks spend in each state, which ones are stuck, and how the measured time compares with the estimate.
/// </summary>
public static class Insights
{
    public static readonly string[] WorkStates = { "started", "review", "tests" };

    /// <summary>The task's state on each day it was worked on (its earlier days from the history, its current day now).</summary>
    public static List<(string Day, string State)> History(TaskItem t)
    {
        var days = new List<(string, string)>();
        foreach (var k in (t.WorkedDays ?? new()).Where(k => k != t.Day).OrderBy(k => k, StringComparer.Ordinal))
            days.Add((k, t.DayStates?.GetValueOrDefault(k) ?? "started"));
        if (t.Day != null) days.Add((t.Day, t.State));
        return days;
    }

    /// <summary>One finished task: working days from the first one to done, and how many of them in each state.</summary>
    public sealed record Cycle(TaskItem Task, int Days, int Started, int Review, int Tests);

    /// <summary>Tasks finished between the two days (by their last day) that took more than one day.</summary>
    public static List<Cycle> Cycles(IEnumerable<TaskItem> tasks, DateTime from, DateTime to)
    {
        string a = Days.Key(from), b = Days.Key(to);
        var list = new List<Cycle>();
        foreach (var t in tasks.Where(t => t.Done && t.Day != null && string.CompareOrdinal(t.Day, a) >= 0 && string.CompareOrdinal(t.Day, b) <= 0))
        {
            var h = History(t);
            if (h.Count < 2) continue;
            // the day it was finished counts as work in the state before done (the work done that day)
            var states = h.Take(h.Count - 1).Select(x => x.State).ToList();
            states.Add(h.Count >= 2 && t.DayStates?.GetValueOrDefault(t.Day!) is { } last && last != "done" ? last : states.LastOrDefault() ?? "started");
            list.Add(new Cycle(t, h.Count, states.Count(s => s is "started" or "todo"), states.Count(s => s == "review"), states.Count(s => s == "tests")));
        }
        return list;
    }

    /// <summary>Averages per project (a task's Notion project, or none): days to done and days in each state.</summary>
    public sealed record ProjectCycle(string Project, int Tasks, double Days, double Started, double Review, double Tests);

    public static List<ProjectCycle> ByProject(List<Cycle> cycles) =>
        cycles.GroupBy(c => c.Task.NotionSource ?? "")
            .Select(g => new ProjectCycle(g.Key, g.Count(), g.Average(c => c.Days), g.Average(c => c.Started), g.Average(c => c.Review), g.Average(c => c.Tests)))
            .OrderByDescending(p => p.Tasks).ToList();

    /// <summary>
    /// How many working days in a row an open task has been in its state, up to its current day (in progress, review or
    /// tests); 0 for a task not started or done.
    /// </summary>
    public static int DaysInState(TaskItem t)
    {
        if (t.Done || t.Archived || !WorkStates.Contains(t.State)) return 0;
        var h = History(t);
        int n = 0;
        for (int i = h.Count - 1; i >= 0 && h[i].State == t.State; i--) n++;
        return n;
    }

    /// <summary>From this many working days in one state a task counts as stuck (a warning on its row and in the report).</summary>
    public const int StuckDays = 3;

    public static bool IsStuck(TaskItem t) => DaysInState(t) >= StuckDays;

    /// <summary>Measured time against the estimate, per category: at least 3 finished tasks with both.</summary>
    public sealed record Ratio(string Tag, double Factor, int Tasks);

    public static List<Ratio> EstimateRatios(IEnumerable<TaskItem> tasks, int min = 3)
    {
        var done = tasks.Where(t => t.Done && t.Estimate is > 0 && t.TrackedMinutes >= 5).ToList();
        var list = new List<Ratio>();
        foreach (var g in done.SelectMany(t => TaskItem.Tags(t.Text, out _).Where(tag => tag != TaskItem.MeetingTag).Select(tag => (tag, t))).GroupBy(x => x.tag, StringComparer.OrdinalIgnoreCase))
        {
            var items = g.Select(x => x.t).Distinct().ToList();
            if (items.Count < min) continue;
            double est = items.Sum(t => t.Estimate!.Value), real = items.Sum(t => t.TrackedMinutes / 60.0);
            list.Add(new Ratio(g.Key, real / est, items.Count));
        }
        return list.OrderByDescending(r => r.Tasks).ToList();
    }

    /// <summary>The factor of a new task's categories (the one with the most history), or null.</summary>
    public static Ratio? RatioFor(IEnumerable<string> tags, List<Ratio> ratios) =>
        ratios.Where(r => tags.Contains(r.Tag, StringComparer.OrdinalIgnoreCase)).OrderByDescending(r => r.Tasks).FirstOrDefault();
}
