using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;

namespace TaskWall;

/// <summary>
/// Things dragged in from other programs: a browser / Notion link becomes a linked task (title taken from the
/// link), plain text becomes one task per line, a Notion export (CSV / ZIP) opens the import.
/// </summary>
static class ExternalDrop
{
    public static bool Accepts(IDataObject d) =>
        ImportFile(d) != null || d.GetDataPresent("UniformResourceLocatorW") || d.GetDataPresent(DataFormats.UnicodeText) || d.GetDataPresent(DataFormats.Text);

    /// <summary>The first dropped .csv / .zip file, if any.</summary>
    public static string? ImportFile(IDataObject d) =>
        d.GetData(DataFormats.FileDrop) is string[] files
            ? files.FirstOrDefault(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            : null;

    public static List<TaskItem> Tasks(IDataObject d)
    {
        var url = Url(d);
        if (url != null) return new List<TaskItem> { FromLink(url, AnchorText(d)) };
        var text = (d.GetData(DataFormats.UnicodeText) ?? d.GetData(DataFormats.Text)) as string;
        if (string.IsNullOrWhiteSpace(text)) return new List<TaskItem>();
        var lines = text.Split('\n').Select(l => l.Trim().TrimStart('-', '*', '•', '·').Trim()).Where(l => l.Length > 0).Take(30).ToList();
        if (lines.Count == 1 && IsUrl(lines[0])) return new List<TaskItem> { FromLink(lines[0], null) };
        return lines.Select(l => new TaskItem { Text = KnownTags(l.Length > 300 ? l[..300] : l) }).ToList();
    }

    static bool IsUrl(string s) => Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https" || u.Scheme == "notion");

    static string? Url(IDataObject d)
    {
        try
        {
            if (d.GetData("UniformResourceLocatorW") is MemoryStream ms)
            {
                var s = Encoding.Unicode.GetString(ms.ToArray()).TrimEnd('\0').Trim();
                if (IsUrl(s)) return s;
            }
        }
        catch { /* some sources expose the format but fail to render it */ }
        return null;
    }

    /// <summary>Browsers put the link text into the HTML fragment of the drag.</summary>
    static string? AnchorText(IDataObject d)
    {
        try
        {
            if (d.GetData(DataFormats.Html) is not string html) return null;
            var m = Regex.Match(html, @"<a\b[^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            var text = WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<[^>]+>", " "));
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text.Length > 0 && !IsUrl(text) ? text : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Page titles from the browser carry extras: "(9+) " (unread notifications in the tab title),
    /// " | Notion" / " – Notion" and the page icon emoji. Tags like "[VFX]" stay at the start.
    /// </summary>
    public static string CleanTitle(string title)
    {
        var t = Regex.Replace(title.Trim(), @"^(\(\d+\+?\)\s*)+", "");
        t = Regex.Replace(t, @"\s*[|–—-]\s*Notion\s*$", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"^[^\p{L}\p{N}\[\(]+", ""); // icon emoji / bullets before the name
        return t.Trim();
    }

    /// <summary>"[vfx] Woda" / "Woda #vfx" → "[VFX] Woda", spelled like the account's category.</summary>
    public static string KnownTags(string text)
    {
        var cats = App.Store?.Categories ?? new List<Category>();
        string Spell(string raw) => cats.FirstOrDefault(c => string.Equals(c.Name, raw.Trim(), StringComparison.OrdinalIgnoreCase))?.Name ?? raw.Trim().ToUpperInvariant();
        bool Known(string raw) => cats.Any(c => string.Equals(c.Name, raw.Trim(), StringComparison.OrdinalIgnoreCase));
        // tags already at the start: just the spelling
        var lead = Regex.Match(text, @"^(\s*\[[^\[\]]{1,24}\])+");
        var t = lead.Success ? Regex.Replace(lead.Value, @"\[([^\[\]]+)\]", m => $"[{Spell(m.Groups[1].Value)}]") + text[lead.Length..] : text;
        int start = lead.Success ? lead.Value.Length : 0;
        // known categories later in the title ("Woda [VFX]") and #tags move to the front
        var moved = new List<string>();
        var later = Regex.Matches(t, @"\[([^\[\]]{1,24})\]|(?<!\w)#([\p{L}\p{N}_\-]{1,24})").Cast<Match>()
            .Where(m => m.Index >= start && (m.Groups[2].Success || Known(m.Groups[1].Value))).ToList();
        foreach (var m in later.AsEnumerable().Reverse())
        {
            moved.Insert(0, Spell(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value));
            t = t.Remove(m.Index, m.Length);
        }
        var prefix = string.Concat(moved.Distinct().Select(c => $"[{c}] "));
        return Regex.Replace(prefix + t.Trim(), @"\s{2,}", " ").Trim(' ', '-', '–', '|');
    }

    public static TaskItem FromLink(string url, string? title)
    {
        var t = new TaskItem { Url = url };
        if (url.Contains("notion.so", StringComparison.OrdinalIgnoreCase) || url.StartsWith("notion://", StringComparison.OrdinalIgnoreCase))
        {
            var id = NotionImport.ExtractId(url);
            if (id != null) { t.NotionId = id; t.Url = NotionImport.PageUrl(id); }
            title ??= NotionTitleFromSlug(url);
        }
        if (string.IsNullOrWhiteSpace(title) && Uri.TryCreate(url, UriKind.Absolute, out var u))
            title = (u.Host + u.AbsolutePath).TrimEnd('/');
        t.Text = string.IsNullOrWhiteSpace(title) ? url : KnownTags(CleanTitle(title!));
        return t;
    }

    /// <summary>notion.so/workspace/Nowa-Woda-do-Tuathan-1a2b… → "Nowa Woda do Tuathan".</summary>
    static string? NotionTitleFromSlug(string url)
    {
        var path = url.Split('?', '#')[0].TrimEnd('/');
        var last = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
        last = Regex.Replace(last, @"-?[0-9a-f]{32}$", "", RegexOptions.IgnoreCase);
        last = last.Replace('-', ' ').Trim();
        return last.Length > 0 ? last : null;
    }
}
