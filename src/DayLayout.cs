using System;
using System.Collections.Generic;
using System.Linq;

namespace TaskWall;

/// <summary>
/// How a week's days are laid out, without any UI (the board draws it, the self-test checks it): which lane each
/// multi-day task gets, which lanes are free on a day, and which done tasks fold away there.
/// </summary>
public static class DayLayout
{
    /// <summary>A multi-day task in one week: its id, the days of this week it is on, its order on the board.</summary>
    public sealed record Span(string Id, IReadOnlyCollection<string> Days, double Order);

    /// <summary>
    /// One lane per task, the same on all of its days that week, so it stays in one line. Longer ones first, then by the
    /// first day and the order; a lane is reused when the days don't overlap. Only tasks on 2+ days of the week get one.
    /// </summary>
    public static Dictionary<string, int> Lanes(IEnumerable<Span> spans)
    {
        var lanes = new Dictionary<string, int>();
        var used = new List<HashSet<string>>(); // lane → days taken
        foreach (var s in spans.Where(s => s.Days.Count > 1)
                     .OrderByDescending(s => s.Days.Count).ThenBy(s => s.Days.Min(StringComparer.Ordinal), StringComparer.Ordinal).ThenBy(s => s.Order))
        {
            int lane = used.FindIndex(u => !u.Overlaps(s.Days));
            if (lane < 0) { lane = used.Count; used.Add(new HashSet<string>()); }
            used[lane].UnionWith(s.Days);
            lanes[s.Id] = lane;
        }
        return lanes;
    }

    /// <summary>Lanes under the day's last used one that nobody uses that day: the day's own tasks move up into them.</summary>
    public static List<int> FreeLanes(IEnumerable<int> taken)
    {
        var set = taken.ToHashSet();
        return set.Count == 0 ? new List<int>() : Enumerable.Range(0, set.Max()).Where(l => !set.Contains(l)).ToList();
    }

    /// <summary>
    /// A task folds into "✓ N zrobione" on a day only when it was finished that day: done and on that day (a multi-day
    /// task on its last day). Its earlier days show the state it had then.
    /// </summary>
    public static bool Folds(TaskItem t, string day, bool folding) => folding && t.Done && t.Day == day;
}
