using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TaskWall;

/// <summary>Single task. Day == null means the task lives in the backlog.</summary>
public sealed class TaskItem
{
    /// <summary>Fields written by a newer version of the app: kept as they are, so an older copy on another computer doesn't drop them.</summary>
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    /// <summary>yyyy-MM-dd, or null for backlog.</summary>
    public string? Day { get; set; }
    public double Order { get; set; }
    /// <summary>UTC; used for last-writer-wins merging between computers.</summary>
    public DateTime Modified { get; set; } = DateTime.UtcNow;
    /// <summary>Estimated hours (optional).</summary>
    public double? Estimate { get; set; }
    /// <summary>Hidden from the board but kept (search, year view, archive window).</summary>
    public bool Archived { get; set; }
    public DateTime? ArchivedAt { get; set; }

    /// <summary>Sub-tasks (null = none).</summary>
    public List<CheckItem>? Checklist { get; set; }
    /// <summary>Planned hours (HH:mm), e.g. "14-16" typed with the task.</summary>
    public string? Time { get; set; }
    public string? End { get; set; }
    /// <summary>Earlier days the unfinished task was planned on before it moved on – shown there as "continued", so the board doesn't pretend it took one day.</summary>
    public List<string>? WorkedDays { get; set; }
    /// <summary>Planned last day of a multi-day task (yyyy-MM-dd, after <see cref="Day"/>): the workdays up to it show the task too.</summary>
    public string? PlanEnd { get; set; }
    /// <summary>
    /// The middle state of the circle ("in progress"). Only a started task that isn't done carries on to the next day as a
    /// multi-day one (its earlier days stay as "↷ 1/3"); a task not started yet just moves on.
    /// </summary>
    public bool? Started { get; set; }
    /// <summary>"Review": after in progress, before done (a started task waiting for its review / acceptance).</summary>
    public bool? Review { get; set; }
    /// <summary>"Tests": after review, before done.</summary>
    public bool? Testing { get; set; }
    /// <summary>In progress, in review or in tests (all carry on to the next days until done).</summary>
    [JsonIgnore] public bool IsStarted => !Done && (Started == true || Review == true || Testing == true);
    [JsonIgnore] public bool IsTesting => !Done && Testing == true;
    [JsonIgnore] public bool IsReview => !Done && Review == true && Testing != true;
    /// <summary>"todo", "started", "review", "tests" or "done".</summary>
    [JsonIgnore] public string State => Done ? "done" : IsTesting ? "tests" : IsReview ? "review" : IsStarted ? "started" : "todo";
    /// <summary>The state the task had on each of its earlier days ("started" / "review"), for its history and the reports.</summary>
    public Dictionary<string, string>? DayStates { get; set; }

    /// <summary>Progress 0..1: done = 1, a checklist by its ticked items, otherwise tests ¾, review ½, in progress ¼.</summary>
    [JsonIgnore] public double Progress => Done ? 1 : Checklist is { Count: > 0 } c ? (double)c.Count(x => x.Done) / c.Count : IsTesting ? 0.75 : IsReview ? 0.5 : IsStarted ? 0.25 : 0;

    /// <summary>Moving an unfinished task forward from a past day leaves that day in its history; moving it back takes it out.</summary>
    public void NoteMove(string? from, string? to, string today)
    {
        if (to != null && WorkedDays != null) WorkedDays.RemoveAll(d => string.CompareOrdinal(d, to) >= 0);
        if (PlanEnd != null && (to == null || string.CompareOrdinal(PlanEnd, to) <= 0)) PlanEnd = null; // the plan ran out (or went to the backlog)
        if (!IsStarted) return; // not started: it simply moves, the board doesn't pretend it was worked on
        if (Done || from == null || to == null || string.CompareOrdinal(from, today) >= 0 || string.CompareOrdinal(to, from) <= 0) return;
        WorkedDays ??= new();
        if (!WorkedDays.Contains(from)) WorkedDays.Add(from);
        WorkedDays.Sort(StringComparer.Ordinal);
        (DayStates ??= new()).TryAdd(from, State);
    }

    /// <summary>
    /// Remembers the state the task has on its day now (the last change of the day counts): that day keeps it in the
    /// history when the task carries on to the next day.
    /// </summary>
    public void NoteState(string today)
    {
        if (Day == null || string.CompareOrdinal(Day, today) > 0) return; // planned ahead: nothing happened yet
        if (IsStarted) (DayStates ??= new())[Day] = State;
        else DayStates?.Remove(Day); // back to "to do" (or done): no in-progress / review mark for that day
    }

    /// <summary>
    /// A task in progress / in review on a past day goes on to <paramref name="today"/>: its day and every working day
    /// since stay in its history with the state it had (the reports show the days worked on it). Done = it stops.
    /// </summary>
    public bool Stretch(DateTime today, Func<DateTime, bool> workday)
    {
        var tk = Days.Key(today);
        if (!IsStarted || Archived || Day == null || string.CompareOrdinal(Day, tk) >= 0) return false;
        if (!DateTime.TryParseExact(Day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var from)) return false;
        WorkedDays ??= new();
        DayStates ??= new();
        // each day keeps the state it had (set when it changed that day, see NoteState); the days since the task's own
        // day carry its state on – in progress until the day it became review, review from then on
        for (var d = from; d < today.Date && (today.Date - d).TotalDays <= 366; d = d.AddDays(1))
        {
            if (!workday(d)) continue; // weekends, holidays, leave: not worked (unless overtime was set for them)
            var k = Days.Key(d);
            if (!WorkedDays.Contains(k)) WorkedDays.Add(k);
            DayStates.TryAdd(k, State);
        }
        WorkedDays.Sort(StringComparer.Ordinal);
        if (PlanEnd != null && string.CompareOrdinal(PlanEnd, tk) <= 0) PlanEnd = null; // the plan ran out: today is the day
        Day = tk;
        // the hours were that day's plan: the new day is planned afresh (the task waits in "Bez godziny")
        Time = null;
        End = null;
        return true;
    }

    /// <summary>Recurring series this task came from, and the occurrence day it replaces.</summary>
    public string? RuleId { get; set; }
    public string? RuleDay { get; set; }

    // Notion metadata (only for imported tasks)
    public string? NotionId { get; set; }
    public string? Url { get; set; }
    public string? Status { get; set; }
    public string? Priority { get; set; }
    /// <summary>Title as last read from Notion (sync renames the task only while its text still matches it).</summary>
    public string? NotionTitle { get; set; }
    /// <summary>Notion sub-item: page id of its main task (shown indented under it).</summary>
    public string? ParentNotionId { get; set; }
    /// <summary>Name of the Notion database the task comes from (when an account reads more than one).</summary>
    public string? NotionSource { get; set; }
    /// <summary>When the task was added (UTC; null for tasks older than 0.18) – the timeline can sort by it.</summary>
    public DateTime? Created { get; set; }
    /// <summary>Time measured with the task's timer (minutes), and when the running timer was started (UTC, null = stopped).</summary>
    public double TrackedMinutes { get; set; }
    public DateTime? TrackStart { get; set; }
    [JsonIgnore] public bool IsTracking => TrackStart != null;
    /// <summary>Measured time including the running part.</summary>
    [JsonIgnore] public double TrackedNow => TrackedMinutes + (TrackStart is { } s ? Math.Max(0, (DateTime.UtcNow - s).TotalMinutes) : 0);

    [JsonIgnore] public bool IsBacklog => Day == null;
    [JsonIgnore] public bool HasLink => !string.IsNullOrEmpty(Url);
    /// <summary>Generated on the fly from a recurring rule; becomes a real task on first interaction.</summary>
    [JsonIgnore] public bool IsVirtual { get; set; }

    /// <summary>Never goes backwards, even when this PC's clock is behind the one that wrote the last change.</summary>
    public void Touch() => Modified = Stamp.After(Modified);
    public TaskItem Clone()
    {
        var c = (TaskItem)MemberwiseClone();
        c.Checklist = CopyList(Checklist); // never share the list between two tasks
        c.WorkedDays = WorkedDays == null ? null : new List<string>(WorkedDays);
        c.DayStates = DayStates == null ? null : new Dictionary<string, string>(DayStates);
        return c;
    }

    static List<CheckItem>? CopyList(List<CheckItem>? l) => l?.Select(x => new CheckItem { Text = x.Text, Done = x.Done }).ToList();

    /// <summary>Takes over all stored fields (merges keep the local instance, so UI references stay valid).</summary>
    public void CopyFrom(TaskItem o)
    {
        Text = o.Text; Done = o.Done; Day = o.Day; Order = o.Order; Modified = o.Modified; Estimate = o.Estimate;
        Archived = o.Archived; ArchivedAt = o.ArchivedAt; RuleId = o.RuleId; RuleDay = o.RuleDay;
        NotionId = o.NotionId; Url = o.Url; Status = o.Status; Priority = o.Priority; NotionTitle = o.NotionTitle; ParentNotionId = o.ParentNotionId; NotionSource = o.NotionSource; Created = o.Created; TrackedMinutes = o.TrackedMinutes; TrackStart = o.TrackStart;
        Checklist = CopyList(o.Checklist);
        Time = o.Time; End = o.End; WorkedDays = o.WorkedDays == null ? null : new List<string>(o.WorkedDays); PlanEnd = o.PlanEnd;
        Started = o.Started; Review = o.Review; Testing = o.Testing;
        DayStates = o.DayStates == null ? null : new Dictionary<string, string>(o.DayStates);
        Extra = o.Extra;
    }

    static readonly Regex TagRx = new(@"^\s*\[([^\[\]]{1,24})\]");

    /// <summary>Leading "[ART]" / "[VFX]"… tags, normalized to upper case.</summary>
    public static List<string> Tags(string text, out string rest)
    {
        // asked for every row, chip, menu and filter: the regex runs once per text (callers get their own list)
        lock (TagCache)
        {
            if (TagCache.TryGetValue(text, out var hit)) { rest = hit.Remainder; return new List<string>(hit.Tags); }
            var tags = ParseTags(text, out rest);
            if (TagCache.Count > 5000) TagCache.Clear();
            TagCache[text] = (tags.ToArray(), rest);
            return tags;
        }
    }

    static readonly Dictionary<string, (string[] Tags, string Remainder)> TagCache = new();

    static List<string> ParseTags(string text, out string rest)
    {
        var tags = new List<string>();
        rest = text;
        for (Match m; (m = TagRx.Match(rest)).Success;)
        {
            tags.Add(m.Groups[1].Value.Trim().ToUpperInvariant());
            rest = rest[m.Length..];
        }
        rest = rest.TrimStart(' ', '-', '–', ':');
        return tags;
    }

    public const string MeetingTag = "MEETING";
    [JsonIgnore] public bool IsMeeting => Tags(Text, out _).Contains(MeetingTag);
}

public sealed class CheckItem
{
    public string Text { get; set; } = "";
    public bool Done { get; set; }
}

/// <summary>
/// Recurring series of tasks or day marks: every N days / weeks (chosen weekdays) / months / years, or every
/// workday – optionally only within a period (Start … End).
/// </summary>
public sealed class RecurringRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Task text, or the mark code (HO …) for a day-mark series.</summary>
    public string Text { get; set; } = "";
    /// <summary>daily | weekdays | weekly | monthly | yearly (biweekly = legacy weekly ×2)</summary>
    public string Pattern { get; set; } = "weekly";
    /// <summary>Every N days / weeks / months / years.</summary>
    public int Interval { get; set; } = 1;
    /// <summary>Weekly: days of the week (0 = Sunday … 6 = Saturday); null = the start's weekday.</summary>
    public List<int>? Weekdays { get; set; }
    /// <summary>Not on Sundays and public holidays.</summary>
    public bool SkipSundaysHolidays { get; set; }
    public string Start { get; set; } = "";
    public string? End { get; set; }
    public double? Estimate { get; set; }
    public double Order { get; set; }
    /// <summary>Occurrences removed by the user.</summary>
    public List<string> Skips { get; set; } = new();
    public bool Deleted { get; set; }
    public DateTime Modified { get; set; } = DateTime.UtcNow;

    /// <summary>Labels in the UI language (built on each access, so the language chosen at start-up applies).</summary>
    public static (string Code, string Label)[] Patterns => new[]
    {
        ("daily", L.T("Codziennie")),
        ("weekdays", L.T("W dni robocze")),
        ("weekly", L.T("Co tydzień")),
        ("biweekly", L.T("Co 2 tygodnie")),
        ("monthly", L.T("Co miesiąc")),
        ("yearly", L.T("Co rok")),
    };

    string? _parsedFrom;
    DateTime _start;

    [JsonIgnore] public DateTime StartDate => Parse() ? _start : DateTime.MinValue;

    bool Parse()
    {
        if (_parsedFrom == Start) return true;
        if (!DateTime.TryParseExact(Start, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _start)) return false;
        _parsedFrom = Start;
        return true;
    }

    static DateTime Monday(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    public bool Occurs(DateTime d)
    {
        if (Deleted || string.IsNullOrEmpty(Start) || !Parse()) return false;
        var s = _start;
        d = d.Date;
        if (d < s) return false;
        var key = Days.Key(d);
        if (End != null && string.CompareOrdinal(key, End) > 0) return false;
        if (Skips.Contains(key)) return false;
        if (SkipSundaysHolidays && (d.DayOfWeek == DayOfWeek.Sunday || DaysOff.Is(d))) return false; // Sundays, holidays, BŚU / leave
        int n = Math.Max(1, Interval);
        switch (Pattern)
        {
            case "daily": return (d - s).Days % n == 0;
            case "weekdays": return d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
            case "weekly":
            case "biweekly":
            {
                if (Pattern == "biweekly") n = 2;
                bool day = Weekdays is { Count: > 0 } w ? w.Contains((int)d.DayOfWeek) : d.DayOfWeek == s.DayOfWeek;
                return day && ((Monday(d) - Monday(s)).Days / 7) % n == 0;
            }
            case "monthly":
            {
                int months = (d.Year - s.Year) * 12 + d.Month - s.Month;
                return months % n == 0 && d.Day == Math.Min(s.Day, DateTime.DaysInMonth(d.Year, d.Month));
            }
            case "yearly":
                return (d.Year - s.Year) % n == 0 && d.Month == s.Month && d.Day == Math.Min(s.Day, DateTime.DaysInMonth(d.Year, d.Month));
            default: return false;
        }
    }

    /// <summary>"co 2 tygodnie: pn, śr · od 5 paź do 30 lis"</summary>
    [JsonIgnore] public string Summary => Describe(Pattern, Interval, Weekdays, StartDate, End) + (SkipSundaysHolidays ? " · " + L.T("bez niedziel, świąt i BŚU") : "");

    public static string Describe(string pattern, int interval, List<int>? weekdays, DateTime start, string? end)
    {
        var c = L.Culture;
        int n = Math.Max(1, interval);
        // Polish: co 2/3/4/22 tygodnie, co 5/12/25 tygodni; English: every 2 weeks
        string Every(string one, string few, string many, string en) => n == 1 ? L.T(one)
            : L.En ? $"every {n} {en}"
            : $"co {n} " + (n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? few : many);
        var text = pattern switch
        {
            "daily" => Every("codziennie", "dni", "dni", "days"),
            "weekdays" => L.T("w dni robocze"),
            "weekly" => Every("co tydzień", "tygodnie", "tygodni", "weeks"),
            "biweekly" => L.T("co 2 tygodnie"),
            "monthly" => Every("co miesiąc", "miesiące", "miesięcy", "months"),
            "yearly" => Every("co rok", "lata", "lat", "years"),
            _ => pattern,
        };
        if (pattern is "weekly" or "biweekly")
        {
            var days = weekdays is { Count: > 0 } w ? w : new List<int> { (int)start.DayOfWeek };
            text += ": " + string.Join(", ", days.OrderBy(x => (x + 6) % 7).Select(x => c.DateTimeFormat.AbbreviatedDayNames[x]));
        }
        if (start > DateTime.MinValue) text += " · " + L.F("od {0}", start.ToString("d MMM yyyy", c));
        if (end != null && DateTime.TryParse(end, out var e)) text += " " + L.F("do {0}", e.ToString("d MMM yyyy", c));
        return text;
    }

    public void Touch() => Modified = Stamp.After(Modified);

    public void CopyFrom(RecurringRule o)
    {
        Text = o.Text; Pattern = o.Pattern; Interval = o.Interval; Weekdays = o.Weekdays == null ? null : new List<int>(o.Weekdays);
        Start = o.Start; End = o.End; Estimate = o.Estimate; Order = o.Order;
        Skips = new List<string>(o.Skips); Deleted = o.Deleted; Modified = o.Modified; SkipSundaysHolidays = o.SkipSundaysHolidays;
    }
}

/// <summary>
/// Reminder at a given time (not a task): shows a notification with a sound. One-off or repeating.
/// Synced with the account's board, so it rings on every PC running TaskWall.
/// </summary>
public sealed class Alarm
{
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>"alarm" (rings at <see cref="Time"/>) or "meeting" (Time–End, rings <see cref="Remind"/> minutes before).</summary>
    public string Kind { get; set; } = "alarm";
    public string Text { get; set; } = "";
    /// <summary>yyyy-MM-dd: the day of a one-off entry, the first day of a repeating one.</summary>
    public string Day { get; set; } = "";
    /// <summary>HH:mm (meeting: start)</summary>
    public string Time { get; set; } = "09:00";
    /// <summary>Meeting end, HH:mm.</summary>
    public string? End { get; set; }
    /// <summary>Meeting reminder: minutes before the start; null = none.</summary>
    public int? Remind { get; set; }
    /// <summary>"" (once) | daily | weekdays | weekly | biweekly | monthly</summary>
    public string Repeat { get; set; } = "";
    /// <summary>A repeating alarm / meeting skips public holidays and BŚU / leave days.</summary>
    public bool SkipDaysOff { get; set; }
    /// <summary>Days (yyyy-MM-dd) of a repeating entry removed by the user.</summary>
    public List<string>? Skips { get; set; }
    /// <summary>Last ring ("yyyy-MM-dd HH:mm") – on this or another PC.</summary>
    public string? Rang { get; set; }
    /// <summary>Local time of the last edit: occurrences before it never ring as "missed".</summary>
    public DateTime Armed { get; set; } = DateTime.Now;
    public bool Deleted { get; set; }
    public DateTime Modified { get; set; } = DateTime.UtcNow;

    /// <summary>Labels in the UI language (built on each access, so the language chosen at start-up applies).</summary>
    public static (string Code, string Label)[] Repeats => new[]
    {
        ("", L.T("Jednorazowo")),
        ("daily", L.T("Codziennie")),
        ("weekdays", L.T("W dni robocze")),
        ("weekly", L.T("Co tydzień")),
        ("biweekly", L.T("Co 2 tygodnie")),
        ("monthly", L.T("Co miesiąc")),
        ("days", L.T("W wybrane dni tygodnia")),
    };

    /// <summary>"days:1,2,3" → the ISO weekdays (1 = Monday … 7 = Sunday) of a repeat on chosen days.</summary>
    public static int[] RepeatDays(string? repeat) =>
        repeat != null && repeat.StartsWith("days:") ? repeat[5..].Split(',').Select(s => int.TryParse(s, out var x) ? x : 0).Where(x => x is >= 1 and <= 7).Distinct().OrderBy(x => x).ToArray() : Array.Empty<int>();

    static int Iso(DateTime d) => d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;

    /// <summary>The repeat in words: "Co tydzień", or the chosen days "Pn, Śr, Pt".</summary>
    public static string LabelFor(string? repeat)
    {
        if (RepeatDays(repeat) is { Length: > 0 } days)
        {
            string[] names = L.En ? new[] { "Mo", "Tu", "We", "Th", "Fr", "Sa", "Su" } : new[] { "Pn", "Wt", "Śr", "Cz", "Pt", "So", "Nd" };
            return string.Join(", ", days.Select(x => names[x - 1]));
        }
        return Repeats.FirstOrDefault(r => r.Code == (repeat ?? "")).Label ?? "";
    }

    [JsonIgnore] public bool Once => string.IsNullOrEmpty(Repeat);
    [JsonIgnore] public bool IsMeeting => Kind == "meeting";

    public bool Occurs(DateTime d)
    {
        if (Deleted || !DateTime.TryParseExact(Day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var start)) return false;
        d = d.Date;
        if (d < start) return false;
        if (Skips != null && Skips.Contains(Days.Key(d))) return false;
        if (SkipDaysOff && DaysOff.Is(d)) return false;
        return Repeat switch
        {
            "" or null => d == start,
            "daily" => true,
            "weekdays" => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
            "weekly" => d.DayOfWeek == start.DayOfWeek,
            "biweekly" => d.DayOfWeek == start.DayOfWeek && ((d - start).Days / 7) % 2 == 0,
            "monthly" => d.Day == Math.Min(start.Day, DateTime.DaysInMonth(d.Year, d.Month)),
            { } r when r.StartsWith("days:") => RepeatDays(r).Contains(Iso(d)),
            _ => false,
        };
    }

    static TimeSpan ParseTime(string? s, TimeSpan fallback) => TimeSpan.TryParseExact(s, @"h\:mm", System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : fallback;

    [JsonIgnore] public TimeSpan TimeOfDay => ParseTime(Time, TimeSpan.FromHours(9));

    public DateTime At(DateTime day) => day.Date + TimeOfDay;

    /// <summary>Meeting end of its occurrence on <paramref name="day"/>: an end before the start is after midnight (23:00–02:00).</summary>
    public DateTime EndAt(DateTime day)
    {
        var end = day.Date + ParseTime(End, TimeOfDay + TimeSpan.FromHours(1));
        return end < At(day) ? end.AddDays(1) : end;
    }

    [JsonIgnore] public double Hours => IsMeeting ? (EndAt(DateTime.Today) - At(DateTime.Today)).TotalHours : 0;

    /// <summary>When the occurrence on <paramref name="day"/> rings: alarm at its time, meeting before it (or never).</summary>
    public DateTime? RingAt(DateTime day) => !IsMeeting ? At(day) : Remind is { } m ? At(day).AddMinutes(-m) : null;

    public static string Key(DateTime at) => at.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Next ring strictly after <paramref name="after"/>, or null.</summary>
    public DateTime? Next(DateTime after)
    {
        if (Deleted || !DateTime.TryParseExact(Day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var start)) return null;
        if (Once) return RingAt(start) is { } r && r > after ? r : null;
        for (int i = -1; i <= 62; i++) // a reminder may fall on the evening before
        {
            var d = after.Date.AddDays(i);
            if (Occurs(d) && RingAt(d) is { } at && at > after) return at;
        }
        return null;
    }

    /// <summary>Next occurrence (start) after <paramref name="after"/> – for lists of upcoming entries.</summary>
    public DateTime? NextStart(DateTime after)
    {
        for (int i = 0; i <= 62; i++)
        {
            var d = after.Date.AddDays(i);
            if (Occurs(d) && At(d) > after) return At(d);
        }
        return null;
    }

    [JsonIgnore] public string RepeatLabel => LabelFor(Repeat);

    public void Touch() => Modified = Stamp.After(Modified);

    public void CopyFrom(Alarm o)
    {
        Kind = o.Kind; Text = o.Text; Day = o.Day; Time = o.Time; End = o.End; Remind = o.Remind; Repeat = o.Repeat;
        Skips = o.Skips == null ? null : new List<string>(o.Skips);
        Rang = o.Rang; Armed = o.Armed; Deleted = o.Deleted; Modified = o.Modified;
        SkipDaysOff = o.SkipDaysOff;
        Extra = o.Extra;
    }
}

static class Stamp
{
    /// <summary>UtcNow, but always later than <paramref name="previous"/> (clock skew between PCs).</summary>
    public static DateTime After(DateTime previous)
    {
        var now = DateTime.UtcNow;
        return now > previous ? now : previous.AddTicks(1);
    }
}

/// <summary>Per-day info shown in the day header (HO / BŚU …).</summary>
public sealed class DayInfo
{
    /// <summary>Fields of a newer version, kept as they are.</summary>
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
    public string? Mark { get; set; }
    /// <summary>This day's working hours ("HH:mm"), set on the day view's timeline; null = the default from the settings.</summary>
    public string? WorkStart { get; set; }
    public string? WorkEnd { get; set; }
    /// <summary>Extra working periods of the day (overtime), "20:00-22:00" each, besides the regular hours.</summary>
    public List<string>? Overtime { get; set; }
    public DateTime Modified { get; set; } = DateTime.UtcNow;
}

/// <summary>The day's notes (the notebook next to the day view's timeline), as light Markdown.</summary>
public sealed class DayNote
{
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
    public string Text { get; set; } = "";
    public DateTime Modified { get; set; } = DateTime.UtcNow;
}

/// <summary>Legacy (v1–v3 trash); converted to archived tasks on load.</summary>
public sealed class TrashEntry
{
    public TaskItem Task { get; set; } = new();
    public DateTime Deleted { get; set; } = DateTime.UtcNow;
    /// <summary>"Opróżnij kosz": hidden, kept until 30 days after <see cref="Deleted"/> so other computers don't bring it back.</summary>
    public bool Purged { get; set; }
}

public sealed class BoardData
{
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
    public int Version { get; set; } = 3;
    public List<TaskItem> Tasks { get; set; } = new();
    /// <summary>Tombstones: task id -> deletion time (UTC). Needed so deletions survive merging.</summary>
    public Dictionary<string, DateTime> Deleted { get; set; } = new();
    /// <summary>yyyy-MM-dd -> day marks.</summary>
    public Dictionary<string, DayInfo> Days { get; set; } = new();
    public List<RecurringRule> Rules { get; set; } = new();
    /// <summary>Predefined categories ([ART], [Meeting] …) with colours; null = defaults not created yet.</summary>
    public List<Category>? Categories { get; set; }
    public DateTime CategoriesModified { get; set; }
    public List<Alarm> Alarms { get; set; } = new();
    /// <summary>Kinds of day marks of this account; null = defaults not created yet.</summary>
    public List<MarkType>? Marks { get; set; }
    public DateTime MarksModified { get; set; }
    /// <summary>Which one-time update of the day marks this board has had (1 = 0.29: BŚU blue with 27 days, sick leave ZL).</summary>
    public int MarkSet { get; set; }
    /// <summary>Repeating day marks ("HO every Friday", "Urlop 3–14 Aug"); Text = mark code.</summary>
    public List<RecurringRule> MarkRules { get; set; } = new();
    public List<TrashEntry>? Trash { get; set; }
    /// <summary>Deleted tasks, restorable for 30 days (the task itself is gone from <see cref="Tasks"/> with a tombstone).</summary>
    public List<TrashEntry>? Bin { get; set; }
    /// <summary>yyyy-MM-dd → the day's notes.</summary>
    public Dictionary<string, DayNote>? Notes { get; set; }
    /// <summary>yyyy-MM-dd → a day off set by the company ("Wolne za 15 sierpnia"); an empty label = removed.</summary>
    public Dictionary<string, CompanyDay>? CompanyDays { get; set; }
}

/// <summary>A day off the company gives (an event, a holiday that fell on a Saturday …), named by the user.</summary>
public sealed class CompanyDay
{
    public string Label { get; set; } = "";
    public DateTime Modified { get; set; } = DateTime.UtcNow;
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

/// <summary>A category = a "[Name]" prefix with a colour.</summary>
public sealed class Category
{
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#6EA0FF";

    public string Key => Name.Trim().ToUpperInvariant();

    public static readonly string[] Palette =
    {
        "#8C9BFF", "#6EA0FF", "#4CC3D4", "#5CCB92", "#A6CF5A", "#D4C25E",
        "#F2A65A", "#EE6E6E", "#F07AAE", "#C58BFF", "#A08BFF", "#9AA1B2",
    };

    public static List<Category> DefaultsFor(string layer) => layer == "private"
        ? new() { new() { Name = "Dom", Color = "#5CCB92" }, new() { Name = "Zakupy", Color = "#F2A65A" }, new() { Name = "Zdrowie", Color = "#EE6E6E" }, new() { Name = "Rodzina", Color = "#F07AAE" }, new() { Name = "Hobby", Color = "#4CC3D4" } }
        : new() { new() { Name = "Meeting", Color = "#8C9BFF" }, new() { Name = "ART", Color = "#F07AAE" }, new() { Name = "VFX", Color = "#4CC3D4" }, new() { Name = "Review", Color = "#F2A65A" }, new() { Name = "Bug", Color = "#EE6E6E" } };
}

/// <summary>A kind of day mark (HO, BŚU, Urlop …) – a list per account, editable in the settings.</summary>
public sealed class MarkType
{
    public string Code { get; set; } = "";
    public string Label { get; set; } = "";
    public string Color { get; set; } = "#3BB8C9";
    /// <summary>"ring" = only a coloured frame (the day keeps showing its tasks), "fill" = coloured day; null = HO ring, others fill.</summary>
    public string? Style { get; set; }
    [JsonIgnore] public bool Ring => Style == null ? Code == "HO" : Style == "ring";
    /// <summary>Fields of a newer version, kept as they are.</summary>
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
    /// <summary>A copy for editing (every field: the days a year, a newer version's fields).</summary>
    public MarkType Clone() => (MarkType)MemberwiseClone();
    /// <summary>Days of this kind available in a year (BŚU 27, sick leave 33 …); null = not counted.</summary>
    public int? Allowance { get; set; }
    /// <summary>Days left from earlier years at the start of <see cref="CarryYear"/> (what the company says is still to take); later years carry the rest on by themselves.</summary>
    public int? CarryOver { get; set; }
    public int? CarryYear { get; set; }

    /// <summary>The colours of the default marks: apart from the board's own (green done, orange accent, violet review / meetings, teal HO and tests).</summary>
    public const string LeaveBlue = "#4F7DE3", SickPink = "#D46BB0";

    public static List<MarkType> DefaultsFor(string layer) => layer == "private"
        ? new()
        : new()
        {
            new() { Code = "HO", Label = "Home Office", Color = "#3BB8C9" },
            new() { Code = "BŚU", Label = "Brak Świadczenia Usług", Color = LeaveBlue, Allowance = 27 },
            new() { Code = "ZL", Label = "Zwolnienie lekarskie", Color = SickPink, Allowance = 33 },
        };

    /// <summary>The mark's short name as shown: in English BŚU = PTO (paid time off), ZL = SL (sick leave).</summary>
    public static string Show(string? code) => code == null ? "" : !L.En ? code : code switch { "BŚU" => "PTO", "ZL" => "SL", _ => code };

    public static string ShowLabel(string code, string label) => !L.En ? label : code switch { "BŚU" => "Paid time off", "ZL" => "Sick leave", _ => label };
}

/// <summary>An iCal (.ics) feed, e.g. Google Calendar's "secret address in iCal format".</summary>
public sealed class CalendarFeed
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>"events" (meetings) or "holidays".</summary>
    public string Kind { get; set; } = "events";
    /// <summary>"" = both layers, "work" or "private".</summary>
    public string Layer { get; set; } = "";
    public bool Enabled { get; set; } = true;

    public const string PolishHolidaysUrl = "https://calendar.google.com/calendar/ical/pl.polish%23holiday%40group.v.calendar.google.com/public/basic.ics";
}

/// <summary>
/// An account (Praca / Prywatne): its own data folder (board.json with tasks, categories, archive) and its own
/// Google calendars, e.g. a company calendar for Praca and a private Gmail calendar for Prywatne.
/// </summary>
public sealed class Account
{
    /// <summary>"work" or "private" (fixed ids; the name is free).</summary>
    public string Id { get; set; } = "work";
    public string Name { get; set; } = "Praca";
    public string Folder { get; set; } = "";
    public List<CalendarFeed> Calendars { get; set; } = new();
    /// <summary>The first Notion database; it also holds the token and the sync options shared by all of them.</summary>
    public NotionLink Notion { get; set; } = new();
    /// <summary>More databases of this account (e.g. a second project), read with the same token.</summary>
    public List<NotionLink> NotionMore { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore] public IEnumerable<NotionLink> NotionSources => new[] { Notion }.Concat(NotionMore).Where(l => l.Database.Length > 0);
    /// <summary>Projects of the account: every named database, also one without a Notion link yet (only a mark for own tasks).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public IEnumerable<NotionLink> Projects => new[] { Notion }.Concat(NotionMore).Where(l => l.Name.Length > 0);
}

/// <summary>Automatic read-only sync of one Notion database into the account's backlog (personal access token).</summary>
public sealed class NotionLink
{
    /// <summary>Short name shown on its tasks ("FoA", "Tuathan"); empty = no mark.</summary>
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#8A94A6";
    /// <summary>Link to the database (or its id).</summary>
    public string Database { get; set; } = "";
    /// <summary>The token, encrypted for this Windows user (DPAPI) – never stored in plain text or in the cloud folder.</summary>
    public string? TokenProtected { get; set; }
    public bool Auto { get; set; } = true;
    public int Minutes { get; set; } = 60;
    /// <summary>Only pages where a person property contains me.</summary>
    public bool OnlyMine { get; set; }
    /// <summary>Use the filter and sorts of the view from the link ("Copy link to view": …?v=…).</summary>
    public bool UseViewFilter { get; set; } = true;
    /// <summary>Safety limit: a sync that would bring more pages is refused (a whole team database).</summary>
    public int MaxPages { get; set; } = 500;
    public string? ViewName { get; set; }
    /// <summary>Statuses that are not imported (still updated when already on the board).</summary>
    public List<string>? SkipStatuses { get; set; }
    /// <summary>Only these statuses are read (like the status filter of a Notion view); null = all but <see cref="SkipStatuses"/>.</summary>
    public List<string>? ReadStatuses { get; set; }
    /// <summary>Extra conditions, one per line: "Dział != PM", "Projekt = Mnich DLC" (any of the property's values).</summary>
    public List<string>? Conditions { get; set; }
    /// <summary>Columns of the database seen at the last sync ("Dział: multi_select") – a hint for the conditions.</summary>
    public List<string>? KnownColumns { get; set; }
    /// <summary>A page done in Notion ticks its task off.</summary>
    public bool SyncDone { get; set; } = true;
    public DateTime? LastSync { get; set; }
    public string? LastResult { get; set; }
    /// <summary>Statuses seen in the database at the last sync (for the settings).</summary>
    public List<string>? KnownStatuses { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool Configured => Database.Length > 0 && TokenProtected != null;
}

/// <summary>Per-computer settings (stored locally, not in the cloud folder).</summary>
public sealed class AppSettings
{
    public int SettingsVersion { get; set; }
    /// <summary>Folder of the "Praca" layer.</summary>
    public string DataFolder { get; set; } = "";
    /// <summary>Folder of the "Prywatne" layer.</summary>
    public string PrivateFolder { get; set; } = "";
    /// <summary>"work" or "private".</summary>
    public string Layer { get; set; } = "work";
    /// <summary>"week1", "week2", "month" or "year" – restored on start.</summary>
    public string View { get; set; } = "week2";
    /// <summary>Screen device name (\\.\DISPLAY2); empty = primary.</summary>
    public string Monitor { get; set; } = "";

    public bool StartWithWindows { get; set; }
    public bool Use24h { get; set; } = true;
    /// <summary>The top bar: the clock and date (the calendar under them), the weather now, the computer's load.</summary>
    public bool ShowClock { get; set; } = true;
    /// <summary>The weather now on the left of the top bar (needs a place in Settings → Pogoda).</summary>
    public bool BarWeather { get; set; } = true;
    /// <summary>The top bar's load rings on the right, in order: cpu, gpu, ram, disk, net (empty = none).</summary>
    public string Meters { get; set; } = "cpu,gpu,ram,disk,net";
    /// <summary>The board fills the whole screen under the top bar (the button next to "hide").</summary>
    public bool BoardMax { get; set; }
    /// <summary>A second board on another screen (its device name; empty = none) and the view it shows.</summary>
    public string SecondMonitor { get; set; } = "";
    public string SecondView { get; set; } = "timeline";
    /// <summary>Legacy (v1–v5): replaced by View.</summary>
    public bool ShowNextWeek { get; set; } = true;
    public bool ShowWeekends { get; set; } = true;
    public bool ShowYearButton { get; set; } = true;
    /// <summary>"drawer" (slides out on click), "pinned" (always visible column).</summary>
    public string BacklogMode { get; set; } = "drawer";
    public bool OpenNotionInApp { get; set; }
    public bool AutoRollover { get; set; }
    /// <summary>Tasks in progress / in review carry on to the next working days by themselves (until done).</summary>
    public bool StretchActive { get; set; } = true;
    public bool AlarmSound { get; set; } = true;
    /// <summary>Minutes before a task with planned hours starts to remind (-1 = off).</summary>
    public int TaskReminder { get; set; } = -1; // off: hours on tasks plan the day, they don't ring (alarms and meetings do)
    public bool BoardHidden { get; set; }
    /// <summary>Captions next to the top-bar icons.</summary>
    public bool BarCaptions { get; set; }
    /// <summary>The backlog / archive drawer stays open while dragging and clicking elsewhere.</summary>
    public bool DrawerLocked { get; set; }
    /// <summary>Done tasks of a day folded into one line.</summary>
    public bool CollapseDone { get; set; }
    /// <summary>Done tasks folded on past days only (with <see cref="CollapseDone"/> off).</summary>
    public bool FoldPastDone { get; set; }
    /// <summary>On the first start of a week: the summary of the week before.</summary>
    public bool WeeklySummary { get; set; } = true;
    public string? LastWeekSummary { get; set; }
    /// <summary>"dark", "light" or "auto" (from the wallpaper brightness).</summary>
    public string Theme { get; set; } = "dark";
    /// <summary>Default working hours (the day view's timeline; a day can have its own).</summary>
    public string WorkStart { get; set; } = "09:00";
    public string WorkEnd { get; set; } = "17:00";
    /// <summary>The day view: the notebook on the left, the line of hours on the right.</summary>
    public bool DayNotesLeft { get; set; }
    /// <summary>The notebook shows the Markdown source instead of the formatted text.</summary>
    public bool NotesSource { get; set; }
    /// <summary>No glass at all: the board, the clock and the panels on a solid background.</summary>
    public bool Opaque { get; set; }
    /// <summary>A fine pattern over the board's background: "none", "fabric", "dots" or "grid".</summary>
    public string Pattern { get; set; } = "none";
    /// <summary>How visible the pattern is (1 = as designed, 0..3) and how big (1 = as designed, 0.25..4).</summary>
    public double PatternVisibility { get; set; } = 1;
    public double PatternScale { get; set; } = 1;
    public WeatherSettings Weather { get; set; } = new();
    public TimelineSettings Timeline { get; set; } = new();
    /// <summary>Update the user said "no" to (not offered again; Settings → Info can still install it).</summary>
    public string? SkippedUpdate { get; set; }
    /// <summary>"pl" or "en".</summary>
    public string Language { get; set; } = "pl";
    /// <summary>"Ctrl+Shift+Space", "Ctrl+Alt+D", "Ctrl+Alt+T", "Win+Shift+D" or "" (off).</summary>
    public string Hotkey { get; set; } = "Ctrl+Shift+Space";
    /// <summary>Last version of the shared settings (taskwall-settings.json in the work folder) this computer has – UTC.</summary>
    public DateTime? SharedModified { get; set; }
    public string? SharedHash { get; set; }
    /// <summary>The shared file those came from, and its settings at that sync (to tell what changed here since).</summary>
    public string? SharedPath { get; set; }
    public string? SharedBaseline { get; set; }

    /// <summary>"#RRGGBB" or "auto" (picked from the wallpaper).</summary>
    public string Accent { get; set; } = "auto";
    public bool Blur { get; set; } = true;
    public double BlurStrength { get; set; } = 60;
    public double TintOpacity { get; set; } = 0.42;
    public bool Animations { get; set; } = true;

    public double UiScale { get; set; } = 1.35;
    public double WidthPercent { get; set; } = 90;
    public double VerticalPercent { get; set; } = 50;
    /// <summary>One week row as % of the screen's work-area height.</summary>
    public double WeekHeightPercent { get; set; } = 20;
    public double FontSize { get; set; } = 12.5;
    /// <summary>Pinned backlog only; the drawer is as wide as Saturday + Sunday.</summary>
    public double BacklogWidth { get; set; } = 320;

    /// <summary>Legacy (≤ v6): moved into Accounts.</summary>
    public List<CalendarFeed> Calendars { get; set; } = new();

    public List<Account> Accounts { get; set; } = new();

    public Account AccountFor(string id)
    {
        var a = Accounts.FirstOrDefault(x => x.Id == id);
        if (a == null) Accounts.Add(a = new Account { Id = id, Name = id == "private" ? "Prywatne" : "Praca" });
        return a;
    }
}

/// <summary>
/// Days off for "skip days off" (alarms, meetings, repeating tasks): public holidays and days marked with a filled mark
/// such as BŚU or leave (a frame mark like HO is a working day). The marks come from the open accounts (set by App).
/// </summary>
static class DaysOff
{
    public static Func<DateTime, bool>? Leave;
    /// <summary>A day off the company gives, with its name (from the open accounts; set by App).</summary>
    public static Func<DateTime, string?>? Company;

    /// <summary>The name of a day off – a public holiday or one the company gives ("Wolne za …"); null on a working day.</summary>
    public static string? Name(DateTime d) => PolishHolidays.DayOff(d) ?? Company?.Invoke(d.Date);
    [ThreadStatic] static bool _inside; // a repeating mark rule asks too – no endless loop through the marks

    public static bool Is(DateTime d)
    {
        if (Name(d) != null) return true;
        if (_inside || Leave == null) return false;
        _inside = true;
        try { return Leave(d); }
        finally { _inside = false; }
    }
}

/// <summary>Day keys ("yyyy-MM-dd", the same on every computer whatever its language or calendar) in one place.</summary>
static class Days
{
    public static string Key(DateTime d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryParse(string? key, out DateTime day) =>
        DateTime.TryParseExact(key, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out day);

    public static DateTime? Parse(string? key) => TryParse(key, out var d) ? d : null;
}
