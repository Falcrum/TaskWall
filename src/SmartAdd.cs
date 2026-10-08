using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace DeskWall;

/// <summary>
/// "jutro Review enviro #art 2h" → day = tomorrow, text = "[ART] Review enviro", estimate = 2 h.
/// Date words are only taken from the start or the end of the text (so "pt" in the middle of a sentence stays text);
/// hours (2h, 1,5h, 30 min, 1h30) and #categories are taken from anywhere.
/// </summary>
static class SmartAdd
{
    public sealed record Result(string Text, DateTime? Day, double? Estimate, List<string> Categories)
    {
        public bool HasAny => Day != null || Estimate != null || Categories.Count > 0;
    }

    static readonly Dictionary<string, DayOfWeek> Weekdays = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pon"] = DayOfWeek.Monday, ["pn"] = DayOfWeek.Monday, ["poniedziałek"] = DayOfWeek.Monday, ["poniedzialek"] = DayOfWeek.Monday,
        ["wt"] = DayOfWeek.Tuesday, ["wtorek"] = DayOfWeek.Tuesday,
        ["śr"] = DayOfWeek.Wednesday, ["sr"] = DayOfWeek.Wednesday, ["środa"] = DayOfWeek.Wednesday, ["sroda"] = DayOfWeek.Wednesday, ["środę"] = DayOfWeek.Wednesday,
        ["czw"] = DayOfWeek.Thursday, ["czwartek"] = DayOfWeek.Thursday,
        ["pt"] = DayOfWeek.Friday, ["piątek"] = DayOfWeek.Friday, ["piatek"] = DayOfWeek.Friday,
        ["sob"] = DayOfWeek.Saturday, ["sobota"] = DayOfWeek.Saturday, ["sobotę"] = DayOfWeek.Saturday,
        ["nd"] = DayOfWeek.Sunday, ["niedz"] = DayOfWeek.Sunday, ["niedziela"] = DayOfWeek.Sunday, ["niedzielę"] = DayOfWeek.Sunday,
    };

    static readonly Regex Hours = new(@"(?<![\w#])(?:(\d+)\s*h\s*(\d{1,2})\s*(?:m|min)?|(\d+(?:[.,]\d+)?)\s*(?:h|godz\.?|godziny|godzin)|(\d+)\s*(?:min|minut))(?!\w)", RegexOptions.IgnoreCase);
    static readonly Regex Hash = new(@"(?<!\w)#([\p{L}\p{N}_\-]{1,24})", RegexOptions.IgnoreCase);
    static readonly Regex DateNum = new(@"^(\d{1,2})[./](\d{1,2})(?:[./](\d{2,4}))?$");

    public static Result Parse(string input, IReadOnlyList<Category> categories, DateTime? today = null)
    {
        var now = (today ?? DateTime.Today).Date;
        var text = input.Trim();
        DateTime? day = null;
        double? estimate = null;
        var cats = new List<string>();

        // estimate (first one wins)
        var hm = Hours.Match(text);
        if (hm.Success)
        {
            if (hm.Groups[1].Success) estimate = int.Parse(hm.Groups[1].Value) + int.Parse(hm.Groups[2].Value) / 60.0;
            else if (hm.Groups[3].Success) estimate = double.Parse(hm.Groups[3].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            else estimate = int.Parse(hm.Groups[4].Value) / 60.0;
            estimate = Math.Round(estimate.Value, 2);
            if (estimate <= 0 || estimate > 24) estimate = null;
            else text = Remove(text, hm);
        }

        // #categories → [Name] (an existing category keeps its spelling)
        foreach (Match m in Hash.Matches(text).Cast<Match>().Reverse())
        {
            var raw = m.Groups[1].Value;
            var known = categories.FirstOrDefault(c => string.Equals(c.Name, raw, StringComparison.OrdinalIgnoreCase));
            cats.Insert(0, known?.Name ?? raw.ToUpperInvariant());
            text = Remove(text, m);
        }

        // date words at the start or the end
        for (int pass = 0; pass < 2 && day == null; pass++)
        {
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (words.Count == 0) break;
            bool atEnd = pass == 1;
            int take = TryDate(atEnd ? words.Skip(Math.Max(0, words.Count - 3)).ToList() : words.Take(3).ToList(), atEnd, now, out var d);
            if (take > 0 && words.Count - take >= 1)
            {
                day = d;
                words = atEnd ? words.Take(words.Count - take).ToList() : words.Skip(take).ToList();
                text = string.Join(' ', words);
            }
        }

        text = Regex.Replace(text, @"\s{2,}", " ").Trim(' ', ',', '-', '–');
        var existing = TaskItem.Tags(text, out _);
        var prefix = string.Concat(cats.Where(c => !existing.Contains(c.ToUpperInvariant())).Distinct().Select(c => $"[{c}] "));
        return new Result((prefix + text).Trim(), day, estimate, cats);
    }

    static string Remove(string text, Match m) => text.Remove(m.Index, m.Length);

    /// <summary>A whole string that is a date ("jutro", "pt", "14.10", "za 3 dni"), or null.</summary>
    public static DateTime? ParseDay(string input, DateTime? today = null)
    {
        var words = input.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count == 0) return null;
        int take = TryDate(words, false, (today ?? DateTime.Today).Date, out var d);
        return take == words.Count ? d : null;
    }

    /// <summary>How many words (1–3) at the start / end form a date; 0 = none.</summary>
    static int TryDate(List<string> w, bool atEnd, DateTime now, out DateTime day)
    {
        day = default;
        string W(int i) => (atEnd ? w[w.Count - 1 - i] : w[i]).Trim(',', '.', ':').ToLowerInvariant();
        if (w.Count == 0) return 0;
        var first = W(0);
        switch (first)
        {
            case "dziś": case "dzis": case "dzisiaj": day = now; return 1;
            case "jutro": day = now.AddDays(1); return 1;
            case "pojutrze": day = now.AddDays(2); return 1;
        }
        if (Weekdays.TryGetValue(first, out var dow))
        {
            int delta = ((int)dow - (int)now.DayOfWeek + 7) % 7;
            day = now.AddDays(delta);
            return 1;
        }
        if (w.Count >= 2 && (first is "w" or "we" or "na") && Weekdays.TryGetValue(W(1), out var dow2) && !atEnd)
        {
            day = now.AddDays(((int)dow2 - (int)now.DayOfWeek + 7) % 7);
            return 2;
        }
        // "za 3 dni", "za tydzień" (reading order, so only at the start)
        if (!atEnd && first == "za" && w.Count >= 2)
        {
            if (W(1) is "tydzień" or "tydzien") { day = now.AddDays(7); return 2; }
            if (w.Count >= 3 && int.TryParse(W(1), out var n) && W(2) is "dni" or "dzień" or "dzien") { day = now.AddDays(n); return 3; }
        }
        if (atEnd && w.Count >= 3 && W(2) == "za" && int.TryParse(W(1), out var n2) && first is "dni" or "dzień" or "dzien") { day = now.AddDays(n2); return 3; }
        if (atEnd && w.Count >= 2 && W(1) == "za" && first is "tydzień" or "tydzien") { day = now.AddDays(7); return 2; }
        // 12.10 / 12.10.2026
        var dm = DateNum.Match(first);
        if (dm.Success)
        {
            int d = int.Parse(dm.Groups[1].Value), m = int.Parse(dm.Groups[2].Value);
            int y = dm.Groups[3].Success ? int.Parse(dm.Groups[3].Value) : now.Year;
            if (y < 100) y += 2000;
            if (m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m))
            {
                day = new DateTime(y, m, d);
                if (!dm.Groups[3].Success && day < now.AddDays(-30)) day = day.AddYears(1); // "5.01" in December = next January
                return 1;
            }
        }
        return 0;
    }

    /// <summary>Short preview, e.g. "→ czw 9 paź · 2h · ART".</summary>
    public static string Describe(Result r)
    {
        var parts = new List<string>();
        if (r.Day is { } d)
        {
            var rel = d == DateTime.Today ? "dziś" : d == DateTime.Today.AddDays(1) ? "jutro" : null;
            parts.Add((rel != null ? rel + ", " : "") + d.ToString("ddd d MMM", BoardWindow.Pl));
        }
        if (r.Estimate is { } h) parts.Add(h.ToString("0.#", BoardWindow.Pl) + "h");
        parts.AddRange(r.Categories);
        return parts.Count > 0 ? "→ " + string.Join("  ·  ", parts) : "";
    }
}
