using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DeskWall;

/// <summary>Single task. Day == null means the task lives in the backlog.</summary>
public sealed class TaskItem
{
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
        return c;
    }

    static List<CheckItem>? CopyList(List<CheckItem>? l) => l?.Select(x => new CheckItem { Text = x.Text, Done = x.Done }).ToList();

    /// <summary>Takes over all stored fields (merges keep the local instance, so UI references stay valid).</summary>
    public void CopyFrom(TaskItem o)
    {
        Text = o.Text; Done = o.Done; Day = o.Day; Order = o.Order; Modified = o.Modified; Estimate = o.Estimate;
        Archived = o.Archived; ArchivedAt = o.ArchivedAt; RuleId = o.RuleId; RuleDay = o.RuleDay;
        NotionId = o.NotionId; Url = o.Url; Status = o.Status; Priority = o.Priority; NotionTitle = o.NotionTitle;
        Checklist = CopyList(o.Checklist);
    }

    static readonly Regex TagRx = new(@"^\s*\[([^\[\]]{1,24})\]");

    /// <summary>Leading "[ART]" / "[VFX]"… tags, normalized to upper case.</summary>
    public static List<string> Tags(string text, out string rest)
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
    public string Start { get; set; } = "";
    public string? End { get; set; }
    public double? Estimate { get; set; }
    public double Order { get; set; }
    /// <summary>Occurrences removed by the user.</summary>
    public List<string> Skips { get; set; } = new();
    public bool Deleted { get; set; }
    public DateTime Modified { get; set; } = DateTime.UtcNow;

    public static readonly (string Code, string Label)[] Patterns =
    {
        ("daily", "Codziennie"),
        ("weekdays", "W dni robocze"),
        ("weekly", "Co tydzień"),
        ("biweekly", "Co 2 tygodnie"),
        ("monthly", "Co miesiąc"),
        ("yearly", "Co rok"),
    };

    string? _parsedFrom;
    DateTime _start;

    [JsonIgnore] public DateTime StartDate => Parse() ? _start : DateTime.MinValue;

    bool Parse()
    {
        if (_parsedFrom == Start) return true;
        if (!DateTime.TryParseExact(Start, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out _start)) return false;
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
        var key = d.ToString("yyyy-MM-dd");
        if (End != null && string.CompareOrdinal(key, End) > 0) return false;
        if (Skips.Contains(key)) return false;
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
    [JsonIgnore] public string Summary => Describe(Pattern, Interval, Weekdays, StartDate, End);

    public static string Describe(string pattern, int interval, List<int>? weekdays, DateTime start, string? end)
    {
        var pl = new System.Globalization.CultureInfo("pl-PL");
        int n = Math.Max(1, interval);
        string Every(string one, string few, string many) => n == 1 ? one : $"co {n} " + (n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? few : many);
        var text = pattern switch
        {
            "daily" => Every("codziennie", "dni", "dni"),
            "weekdays" => "w dni robocze",
            "weekly" => Every("co tydzień", "tygodnie", "tygodni"),
            "biweekly" => "co 2 tygodnie",
            "monthly" => Every("co miesiąc", "miesiące", "miesięcy"),
            "yearly" => Every("co rok", "lata", "lat"),
            _ => pattern,
        };
        if (pattern is "weekly" or "biweekly")
        {
            var days = weekdays is { Count: > 0 } w ? w : new List<int> { (int)start.DayOfWeek };
            text += ": " + string.Join(", ", days.OrderBy(x => (x + 6) % 7).Select(x => pl.DateTimeFormat.AbbreviatedDayNames[x]));
        }
        if (start > DateTime.MinValue) text += $" · od {start.ToString("d MMM yyyy", pl)}";
        if (end != null && DateTime.TryParse(end, out var e)) text += $" do {e.ToString("d MMM yyyy", pl)}";
        return text;
    }

    public void Touch() => Modified = Stamp.After(Modified);

    public void CopyFrom(RecurringRule o)
    {
        Text = o.Text; Pattern = o.Pattern; Interval = o.Interval; Weekdays = o.Weekdays == null ? null : new List<int>(o.Weekdays);
        Start = o.Start; End = o.End; Estimate = o.Estimate; Order = o.Order;
        Skips = new List<string>(o.Skips); Deleted = o.Deleted; Modified = o.Modified;
    }
}

/// <summary>
/// Reminder at a given time (not a task): shows a notification with a sound. One-off or repeating.
/// Synced with the account's board, so it rings on every PC running DeskWall.
/// </summary>
public sealed class Alarm
{
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
    /// <summary>Days (yyyy-MM-dd) of a repeating entry removed by the user.</summary>
    public List<string>? Skips { get; set; }
    /// <summary>Last ring ("yyyy-MM-dd HH:mm") – on this or another PC.</summary>
    public string? Rang { get; set; }
    /// <summary>Local time of the last edit: occurrences before it never ring as "missed".</summary>
    public DateTime Armed { get; set; } = DateTime.Now;
    public bool Deleted { get; set; }
    public DateTime Modified { get; set; } = DateTime.UtcNow;

    public static readonly (string Code, string Label)[] Repeats =
    {
        ("", "Jednorazowo"),
        ("daily", "Codziennie"),
        ("weekdays", "W dni robocze"),
        ("weekly", "Co tydzień"),
        ("biweekly", "Co 2 tygodnie"),
        ("monthly", "Co miesiąc"),
    };

    [JsonIgnore] public bool Once => string.IsNullOrEmpty(Repeat);
    [JsonIgnore] public bool IsMeeting => Kind == "meeting";

    public bool Occurs(DateTime d)
    {
        if (Deleted || !DateTime.TryParseExact(Day, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var start)) return false;
        d = d.Date;
        if (d < start) return false;
        if (Skips != null && Skips.Contains(d.ToString("yyyy-MM-dd"))) return false;
        return Repeat switch
        {
            "" or null => d == start,
            "daily" => true,
            "weekdays" => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
            "weekly" => d.DayOfWeek == start.DayOfWeek,
            "biweekly" => d.DayOfWeek == start.DayOfWeek && ((d - start).Days / 7) % 2 == 0,
            "monthly" => d.Day == Math.Min(start.Day, DateTime.DaysInMonth(d.Year, d.Month)),
            _ => false,
        };
    }

    static TimeSpan ParseTime(string? s, TimeSpan fallback) => TimeSpan.TryParseExact(s, @"hh\:mm", null, out var t) ? t : fallback;

    [JsonIgnore] public TimeSpan TimeOfDay => ParseTime(Time, TimeSpan.FromHours(9));

    public DateTime At(DateTime day) => day.Date + TimeOfDay;

    /// <summary>Meeting end on <paramref name="day"/> (at least the start).</summary>
    public DateTime EndAt(DateTime day)
    {
        var end = day.Date + ParseTime(End, TimeOfDay + TimeSpan.FromHours(1));
        return end < At(day) ? At(day) : end;
    }

    [JsonIgnore] public double Hours => IsMeeting ? (EndAt(DateTime.Today) - At(DateTime.Today)).TotalHours : 0;

    /// <summary>When the occurrence on <paramref name="day"/> rings: alarm at its time, meeting before it (or never).</summary>
    public DateTime? RingAt(DateTime day) => !IsMeeting ? At(day) : Remind is { } m ? At(day).AddMinutes(-m) : null;

    public static string Key(DateTime at) => at.ToString("yyyy-MM-dd HH:mm");

    /// <summary>Next ring strictly after <paramref name="after"/>, or null.</summary>
    public DateTime? Next(DateTime after)
    {
        if (Deleted || !DateTime.TryParseExact(Day, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var start)) return null;
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

    [JsonIgnore] public string RepeatLabel => Repeats.FirstOrDefault(r => r.Code == (Repeat ?? "")).Label ?? "";

    public void Touch() => Modified = Stamp.After(Modified);

    public void CopyFrom(Alarm o)
    {
        Kind = o.Kind; Text = o.Text; Day = o.Day; Time = o.Time; End = o.End; Remind = o.Remind; Repeat = o.Repeat;
        Skips = o.Skips == null ? null : new List<string>(o.Skips);
        Rang = o.Rang; Armed = o.Armed; Deleted = o.Deleted; Modified = o.Modified;
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
    public string? Mark { get; set; }
    public DateTime Modified { get; set; } = DateTime.UtcNow;
}

/// <summary>Legacy (v1–v3 trash); converted to archived tasks on load.</summary>
public sealed class TrashEntry
{
    public TaskItem Task { get; set; } = new();
    public DateTime Deleted { get; set; } = DateTime.UtcNow;
}

public sealed class BoardData
{
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
    /// <summary>Repeating day marks ("HO every Friday", "Urlop 3–14 Aug"); Text = mark code.</summary>
    public List<RecurringRule> MarkRules { get; set; } = new();
    public List<TrashEntry>? Trash { get; set; }
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

    public static List<MarkType> DefaultsFor(string layer) => layer == "private"
        ? new()
        : new() { new() { Code = "HO", Label = "Home Office", Color = "#3BB8C9" }, new() { Code = "BŚU", Label = "Brak Świadczenia Usług", Color = "#A47CF0" } };
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
    public NotionLink Notion { get; set; } = new();
}

/// <summary>Automatic read-only sync of one Notion database into the account's backlog (personal access token).</summary>
public sealed class NotionLink
{
    /// <summary>Link to the database (or its id).</summary>
    public string Database { get; set; } = "";
    /// <summary>The token, encrypted for this Windows user (DPAPI) – never stored in plain text or in the cloud folder.</summary>
    public string? TokenProtected { get; set; }
    public bool Auto { get; set; } = true;
    public int Minutes { get; set; } = 10;
    /// <summary>Only pages where a person property contains me.</summary>
    public bool OnlyMine { get; set; }
    /// <summary>Statuses that are not imported (still updated when already on the board).</summary>
    public List<string>? SkipStatuses { get; set; }
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
    public bool ShowClock { get; set; } = true;
    /// <summary>Legacy (v1–v5): replaced by View.</summary>
    public bool ShowNextWeek { get; set; } = true;
    public bool ShowWeekends { get; set; } = true;
    public bool ShowYearButton { get; set; } = true;
    /// <summary>"drawer" (slides out on click), "pinned" (always visible column).</summary>
    public string BacklogMode { get; set; } = "drawer";
    public bool OpenNotionInApp { get; set; }
    public bool AutoRollover { get; set; }
    public bool AlarmSound { get; set; } = true;
    public bool BoardHidden { get; set; }
    /// <summary>"pl" or "en".</summary>
    public string Language { get; set; } = "pl";
    /// <summary>"Ctrl+Shift+Space", "Ctrl+Alt+D", "Ctrl+Alt+T", "Win+Shift+D" or "" (off).</summary>
    public string Hotkey { get; set; } = "Ctrl+Shift+Space";

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
