using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DeskWall;

/// <summary>One meeting / holiday from an iCal feed, already in local time.</summary>
public sealed class CalEvent
{
    public string Title { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public bool AllDay { get; set; }
    public string Feed { get; set; } = "";
    /// <summary>"events" or "holidays"</summary>
    public string Kind { get; set; } = "events";
    public string? Location { get; set; }
    /// <summary>"" = both layers, "work" or "private".</summary>
    public string Layer { get; set; } = "";

    public bool IsHoliday => Kind == "holidays";
    public double Hours => AllDay ? 0 : Math.Max(0, (End - Start).TotalHours);
    public string TimeText => AllDay ? L.T("cały dzień") : $"{Start:HH:mm}–{End:HH:mm}";
}

/// <summary>Statutory days off in Poland (computed, so they work without any feed).</summary>
static class PolishHolidays
{
    static readonly Dictionary<int, Dictionary<DateTime, string>> Cache = new();

    public static string? DayOff(DateTime d)
    {
        if (!Cache.TryGetValue(d.Year, out var map)) Cache[d.Year] = map = Build(d.Year);
        return map.TryGetValue(d.Date, out var name) ? name : null;
    }

    static Dictionary<DateTime, string> Build(int y)
    {
        // Easter (anonymous Gregorian algorithm)
        int a = y % 19, b = y / 100, c = y % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3,
            h = (19 * a + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7,
            m = (a + 11 * h + 22 * l) / 451, month = (h + l - 7 * m + 114) / 31, day = (h + l - 7 * m + 114) % 31 + 1;
        var easter = new DateTime(y, month, day);
        var map = new Dictionary<DateTime, string>
        {
            [new DateTime(y, 1, 1)] = "Nowy Rok",
            [new DateTime(y, 1, 6)] = "Trzech Króli",
            [easter] = "Wielkanoc",
            [easter.AddDays(1)] = "Poniedziałek Wielkanocny",
            [new DateTime(y, 5, 1)] = "Święto Pracy",
            [new DateTime(y, 5, 3)] = "Święto Konstytucji 3 Maja",
            [easter.AddDays(49)] = "Zielone Świątki",
            [easter.AddDays(60)] = "Boże Ciało",
            [new DateTime(y, 8, 15)] = "Wniebowzięcie NMP",
            [new DateTime(y, 11, 1)] = "Wszystkich Świętych",
            [new DateTime(y, 11, 11)] = "Święto Niepodległości",
            [new DateTime(y, 12, 25)] = "Boże Narodzenie",
            [new DateTime(y, 12, 26)] = "Drugi dzień Świąt",
        };
        if (y >= 2025) map[new DateTime(y, 12, 24)] = "Wigilia";
        return map;
    }
}

/// <summary>Downloads the configured .ics feeds, keeps them expanded per day and cached on disk.</summary>
static class CalendarService
{
    static readonly string CachePath = Path.Combine(SettingsStore.Dir, "calendar-cache.json");
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };
    static Dictionary<DateTime, List<CalEvent>> _byDay = new();
    static bool _busy;

    static List<CalEvent> _all = new();
    /// <summary>Active layer; events of feeds bound to the other layer are hidden.</summary>
    public static string Layer { get; set; } = "work";
    public static IEnumerable<CalEvent> All => _all.Where(Visible);
    public static int Count(bool holidays) => _all.Count(e => e.IsHoliday == holidays);
    static bool Visible(CalEvent e) => e.Layer == "" || e.Layer == Layer;
    public static DateTime? LastFetch { get; private set; }
    public static string? LastError { get; private set; }
    public static event Action? Changed;

    public static void AddTestEvents(IEnumerable<CalEvent> events)
    {
        TestEvents.AddRange(events);
        Set(_all.Where(e => !TestEvents.Contains(e)).ToList());
        Changed?.Invoke();
    }

    public static IReadOnlyList<CalEvent> On(DateTime day) =>
        _byDay.TryGetValue(day.Date, out var list) ? list.Where(Visible).ToList() : Array.Empty<CalEvent>();

    public static void LoadCache()
    {
        try
        {
            if (File.Exists(CachePath))
                Set(JsonSerializer.Deserialize<List<CalEvent>>(File.ReadAllText(CachePath)) ?? new());
        }
        catch (Exception ex) { Log.Error("calendar cache", ex); }
    }

    public static async Task RefreshAsync(IEnumerable<CalendarFeed> feeds)
    {
        if (_busy) { _again = feeds; return; } // e.g. a feed added during the start-up download
        _busy = true;
        try
        {
            var enabled = feeds.Where(f => f.Enabled && !string.IsNullOrWhiteSpace(f.Url)).ToList();
            var from = new DateTime(DateTime.Today.Year - 1, 1, 1);
            var to = new DateTime(DateTime.Today.Year + 1, 12, 31, 23, 59, 59);
            var list = new List<CalEvent>();
            var errors = new List<string>();
            foreach (var f in enabled)
            {
                try
                {
                    var url = f.Url.Trim();
                    if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[9..];
                    var text = await Http.GetStringAsync(url);
                    var parsed = await Task.Run(() => Ics.Parse(text, f.Name, f.Kind, from, to));
                    foreach (var e in parsed) e.Layer = f.Layer;
                    list.AddRange(parsed);
                }
                catch (Exception ex)
                {
                    errors.Add($"{f.Name}: {ex.Message}");
                    // keep the old events of a feed that failed this time
                    list.AddRange(_all.Where(e => e.Feed == f.Name && e.Layer == f.Layer));
                }
            }
            LastError = errors.Count > 0 ? string.Join("\n", errors) : null;
            LastFetch = DateTime.Now;
            Set(list);
            try { File.WriteAllText(CachePath, JsonSerializer.Serialize(list)); } catch (Exception ex) { Log.Error("calendar cache save", ex); }
            Changed?.Invoke();
        }
        finally { _busy = false; }
        if (_again is { } next) { _again = null; await RefreshAsync(next); }
    }

    static IEnumerable<CalendarFeed>? _again;

    /// <summary>Dev only (--ics file): extra events kept across refreshes.</summary>
    public static List<CalEvent> TestEvents { get; } = new();

    static void Set(List<CalEvent> events)
    {
        if (TestEvents.Count > 0) events = events.Concat(TestEvents).ToList();
        _all = events;
        var map = new Dictionary<DateTime, List<CalEvent>>();
        foreach (var e in events)
        {
            var last = e.AllDay ? e.End.Date.AddDays(-1) : (e.End > e.Start ? e.End.AddTicks(-1).Date : e.Start.Date);
            if (last < e.Start.Date) last = e.Start.Date;
            for (var d = e.Start.Date; d <= last && (d - e.Start.Date).TotalDays < 62; d = d.AddDays(1))
            {
                if (!map.TryGetValue(d, out var l)) map[d] = l = new List<CalEvent>();
                l.Add(e);
            }
        }
        foreach (var l in map.Values) l.Sort((x, y) => x.AllDay != y.AllDay ? (x.AllDay ? -1 : 1) : x.Start.CompareTo(y.Start));
        _byDay = map;
    }
}

/// <summary>Small iCalendar (RFC 5545) reader: VEVENT, all-day / UTC / TZID times, RRULE, EXDATE, RECURRENCE-ID.</summary>
static class Ics
{
    sealed class Prop
    {
        public required string Value;
        public Dictionary<string, string> Params = new(StringComparer.OrdinalIgnoreCase);
    }

    sealed class Time
    {
        public DateTime Wall;           // as written in the file
        public TimeZoneInfo? Zone;      // TZID
        public bool Utc, AllDay;

        public DateTime Local(DateTime wall)
        {
            if (AllDay) return wall;
            if (Utc) return DateTime.SpecifyKind(wall, DateTimeKind.Utc).ToLocalTime();
            if (Zone != null)
            {
                try { return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), Zone, TimeZoneInfo.Local); }
                catch { return wall; }
            }
            return wall;
        }
    }

    public static List<CalEvent> Parse(string text, string feed, string kind, DateTime from, DateTime to)
    {
        var events = new List<Dictionary<string, List<Prop>>>();
        Dictionary<string, List<Prop>>? cur = null;
        int nested = 0; // VALARM etc. inside an event: their properties are not the event's
        foreach (var line in Unfold(text))
        {
            if (line == "BEGIN:VEVENT") { cur = new(StringComparer.OrdinalIgnoreCase); nested = 0; continue; }
            if (line == "END:VEVENT") { if (cur != null) events.Add(cur); cur = null; continue; }
            if (cur == null) continue;
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase)) { nested++; continue; }
            if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase)) { if (nested > 0) nested--; continue; }
            if (nested > 0) continue;
            int colon = ValueColon(line);
            if (colon < 0) continue;
            var head = line[..colon].Split(';');
            var p = new Prop { Value = line[(colon + 1)..] };
            foreach (var kv in head.Skip(1))
            {
                int eq = kv.IndexOf('=');
                if (eq > 0) p.Params[kv[..eq]] = kv[(eq + 1)..].Trim('"');
            }
            if (!cur.TryGetValue(head[0], out var list)) cur[head[0]] = list = new List<Prop>();
            list.Add(p);
        }

        // moved / cancelled single occurrences of recurring events
        var overridden = new HashSet<string>();
        foreach (var ev in events)
            if (Get(ev, "RECURRENCE-ID") is { } rid && Get(ev, "UID") is { } uid && ParseTime(rid) is { } rt)
                overridden.Add(uid.Value + "|" + rt.Local(rt.Wall).ToString("s"));

        var result = new List<CalEvent>();
        foreach (var ev in events)
        {
            bool cancelled = string.Equals(Get(ev, "STATUS")?.Value, "CANCELLED", StringComparison.OrdinalIgnoreCase);
            if (cancelled) continue;
            if (Get(ev, "DTSTART") is not { } ds || ParseTime(ds) is not { } start) continue;
            var endTime = Get(ev, "DTEND") is { } de ? ParseTime(de) : null;
            TimeSpan dur = endTime != null ? endTime.Local(endTime.Wall) - start.Local(start.Wall)
                : Get(ev, "DURATION") is { } du ? ParseDuration(du.Value)
                : start.AllDay ? TimeSpan.FromDays(1) : TimeSpan.Zero;
            if (dur < TimeSpan.Zero) dur = TimeSpan.Zero;

            var title = Unescape(Get(ev, "SUMMARY")?.Value ?? L.T("(bez tytułu)"));
            var location = Get(ev, "LOCATION") is { } loc ? Unescape(loc.Value) : null;
            var uidValue = Get(ev, "UID")?.Value ?? "";
            bool isOverride = Get(ev, "RECURRENCE-ID") != null;

            void AddAt(DateTime wall)
            {
                var s = start.Local(wall);
                var e = s + dur;
                if (e < from || s > to) return;
                result.Add(new CalEvent { Title = title, Start = s, End = e, AllDay = start.AllDay, Feed = feed, Kind = kind, Location = location });
            }

            if (Get(ev, "RRULE") is { } rr && !isOverride)
            {
                var ex = new HashSet<string>();
                var exDays = new HashSet<DateTime>(); // EXDATE;VALUE=DATE removes the whole day
                if (ev.TryGetValue("EXDATE", out var exdates))
                    foreach (var exd in exdates)
                        foreach (var v in exd.Value.Split(','))
                            if (ParseTime(new Prop { Value = v, Params = exd.Params }) is { } xt)
                            {
                                if (xt.AllDay) exDays.Add(xt.Wall.Date);
                                else ex.Add(xt.Local(xt.Wall).ToString("s"));
                            }
                foreach (var wall in Expand(start.Wall, rr.Value, start, from, to))
                {
                    var key = start.Local(wall).ToString("s");
                    if (ex.Contains(key) || exDays.Contains(wall.Date) || overridden.Contains(uidValue + "|" + key)) continue;
                    AddAt(wall);
                }
            }
            else AddAt(start.Wall);
        }
        return result;
    }

    static Prop? Get(Dictionary<string, List<Prop>> ev, string name) => ev.TryGetValue(name, out var l) && l.Count > 0 ? l[0] : null;

    /// <summary>The ':' separating name/params from the value (params may contain quoted ':').</summary>
    static int ValueColon(string line)
    {
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') q = !q;
            else if (line[i] == ':' && !q) return i;
        }
        return -1;
    }

    static IEnumerable<string> Unfold(string text)
    {
        var sb = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t')) { sb.Append(raw, 1, raw.Length - 1); continue; }
            if (sb.Length > 0) yield return sb.ToString();
            sb.Clear().Append(raw);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    static string Unescape(string s) => s.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();

    static Time? ParseTime(Prop p)
    {
        var v = p.Value.Trim();
        var t = new Time();
        if ((p.Params.TryGetValue("VALUE", out var vt) && vt.Equals("DATE", StringComparison.OrdinalIgnoreCase)) || v.Length == 8)
        {
            if (!DateTime.TryParseExact(v[..Math.Min(8, v.Length)], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out t.Wall)) return null;
            t.AllDay = true;
            return t;
        }
        t.Utc = v.EndsWith('Z');
        if (!DateTime.TryParseExact(v.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t.Wall)) return null;
        if (!t.Utc && p.Params.TryGetValue("TZID", out var tz)) t.Zone = FindZone(tz);
        return t;
    }

    static readonly Dictionary<string, TimeZoneInfo?> Zones = new();

    static TimeZoneInfo? FindZone(string id)
    {
        if (Zones.TryGetValue(id, out var z)) return z;
        try { z = TimeZoneInfo.FindSystemTimeZoneById(id); } // IANA ids work on Windows 10+ (.NET 6+)
        catch { z = null; }
        return Zones[id] = z;
    }

    static TimeSpan ParseDuration(string s)
    {
        // e.g. PT1H30M, P1D
        var m = System.Text.RegularExpressions.Regex.Match(s, @"P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?");
        int G(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0;
        return new TimeSpan(G(1) * 7 + G(2), G(3), G(4), G(5));
    }

    static readonly string[] WeekdayCodes = { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };

    /// <summary>Occurrence start times (wall clock) of an RRULE, in chronological order.</summary>
    static IEnumerable<DateTime> Expand(DateTime start, string rrule, Time startInfo, DateTime fromLocal, DateTime toLocal)
    {
        var r = rrule.Split(';').Select(x => x.Split('=')).Where(x => x.Length == 2)
            .ToDictionary(x => x[0].ToUpperInvariant(), x => x[1].ToUpperInvariant());
        var freq = r.GetValueOrDefault("FREQ", "DAILY");
        int interval = int.TryParse(r.GetValueOrDefault("INTERVAL"), out var iv) && iv > 0 ? iv : 1;
        int? count = int.TryParse(r.GetValueOrDefault("COUNT"), out var cnt) ? cnt : null;
        DateTime? until = null;
        if (r.TryGetValue("UNTIL", out var u) && ParseTime(new Prop { Value = u }) is { } ut)
            until = ut.AllDay ? ut.Wall.AddDays(1).AddTicks(-1) : ut.Local(ut.Wall); // a DATE until is inclusive
        // rule parts we don't understand: show only the first occurrence instead of guessing wrong
        if (r.Keys.Any(k => k is "BYWEEKNO" or "BYYEARDAY" or "BYHOUR" or "BYMINUTE" or "BYSECOND") || freq is not ("DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY"))
        {
            yield return start;
            yield break;
        }
        var byDay = r.TryGetValue("BYDAY", out var bd)
            ? bd.Split(',').Where(x => x.Length >= 2).Select(x =>
            {
                var code = x[^2..];
                int.TryParse(x[..^2], out var ord);
                return (ord, dow: (DayOfWeek)Array.IndexOf(WeekdayCodes, code));
            }).Where(x => (int)x.dow >= 0).ToList()
            : null;
        static List<int>? Ints(string? s) => s == null ? null : s.Split(',').Select(x => int.TryParse(x, out var n) ? n : 0).Where(n => n != 0).ToList();
        var byMonthDay = Ints(r.GetValueOrDefault("BYMONTHDAY"));
        var byMonth = Ints(r.GetValueOrDefault("BYMONTH"))?.Where(m => m is >= 1 and <= 12).OrderBy(m => m).ToList();
        var bySetPos = Ints(r.GetValueOrDefault("BYSETPOS"));
        var tod = start.TimeOfDay;

        IEnumerable<DateTime> InMonth(int y, int m)
        {
            int dim = DateTime.DaysInMonth(y, m);
            var days = new List<int>();
            if (byMonthDay != null) days.AddRange(byMonthDay.Select(d => d > 0 ? d : dim + d + 1).Where(d => d >= 1 && d <= dim));
            else if (byDay != null)
            {
                foreach (var (ord, dow) in byDay)
                {
                    var all = Enumerable.Range(1, dim).Where(d => new DateTime(y, m, d).DayOfWeek == dow).ToList();
                    if (ord == 0) days.AddRange(all);
                    else if (ord > 0 && ord <= all.Count) days.Add(all[ord - 1]);
                    else if (ord < 0 && -ord <= all.Count) days.Add(all[all.Count + ord]);
                }
            }
            else if (start.Day <= dim) days.Add(start.Day);
            var sorted = days.Distinct().OrderBy(d => d).ToList();
            if (bySetPos != null) // e.g. last working day: BYDAY=MO..FR;BYSETPOS=-1
                sorted = bySetPos.Select(p => p > 0 ? (p <= sorted.Count ? sorted[p - 1] : -1) : (-p <= sorted.Count ? sorted[sorted.Count + p] : -1))
                    .Where(d => d > 0).Distinct().OrderBy(d => d).ToList();
            return sorted.Select(d => new DateTime(y, m, d) + tod);
        }

        // without COUNT we may jump straight to the window instead of walking from DTSTART (old daily series)
        int skip = 0;
        if (count == null && fromLocal > start)
        {
            var gap = fromLocal - start;
            skip = freq switch
            {
                "DAILY" => (int)(gap.TotalDays / interval) - 1,
                "WEEKLY" => (int)(gap.TotalDays / 7 / interval) - 1,
                "MONTHLY" => ((fromLocal.Year - start.Year) * 12 + fromLocal.Month - start.Month) / interval - 1,
                _ => (fromLocal.Year - start.Year) / interval - 1,
            };
            skip = Math.Max(0, skip);
        }

        IEnumerable<DateTime> Periods()
        {
            for (int p = skip; p < skip + 5000; p++)
            {
                switch (freq)
                {
                    case "DAILY":
                        var d = start.AddDays((double)p * interval);
                        if ((byDay == null || byDay.Any(b => b.dow == d.DayOfWeek)) && (byMonth == null || byMonth.Contains(d.Month))) yield return d;
                        break;
                    case "WEEKLY":
                        var monday = BoardWindow.Monday(start.Date).AddDays(7.0 * p * interval);
                        var dows = byDay?.Select(b => b.dow).ToList() ?? new List<DayOfWeek> { start.DayOfWeek };
                        foreach (var dow in dows.OrderBy(x => ((int)x + 6) % 7))
                            yield return monday.AddDays(((int)dow + 6) % 7) + tod;
                        break;
                    case "MONTHLY":
                        var mm = new DateTime(start.Year, start.Month, 1).AddMonths(p * interval);
                        foreach (var x in InMonth(mm.Year, mm.Month)) yield return x;
                        break;
                    case "YEARLY":
                        int y = start.Year + p * interval;
                        foreach (var m in byMonth ?? new List<int> { start.Month })
                            foreach (var x in (byMonthDay == null && byDay == null)
                                         ? (start.Day <= DateTime.DaysInMonth(y, m) ? new[] { new DateTime(y, m, start.Day) + tod } : Array.Empty<DateTime>())
                                         : InMonth(y, m))
                                yield return x;
                        break;
                    default:
                        yield break;
                }
            }
        }

        int n = 0;
        foreach (var occ in Periods())
        {
            if (occ < start) continue;
            if (until != null && startInfo.Local(occ) > until.Value) yield break;
            if (count != null && n >= count) yield break;
            n++;
            if (startInfo.Local(occ) > toLocal) yield break;
            yield return occ;
        }
    }
}
