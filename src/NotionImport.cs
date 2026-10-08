using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DeskWall;

public sealed class CsvTable
{
    public List<string> Headers { get; } = new();
    public List<string[]> Rows { get; } = new();

    public string Cell(string[] row, int col) => col >= 0 && col < row.Length ? row[col].Trim() : "";
}

static class Csv
{
    /// <summary>RFC 4180 parser (quoted fields, escaped quotes, newlines inside quotes).</summary>
    public static CsvTable Parse(string text)
    {
        text = text.TrimStart('﻿');
        char sep = DetectSeparator(text);
        var records = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) inQuotes = true; // quotes only open a field
            else if (c == sep) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\r') { }
            else if (c == '\n') { row.Add(field.ToString()); field.Clear(); records.Add(row); row = new(); }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); records.Add(row); }

        var table = new CsvTable();
        if (records.Count == 0) return table;
        table.Headers.AddRange(records[0].Select(h => h.Trim()));
        foreach (var r in records.Skip(1))
            if (r.Any(v => v.Trim().Length > 0))
                table.Rows.Add(r.ToArray());
        return table;
    }

    static char DetectSeparator(string text)
    {
        int nl = text.IndexOf('\n');
        var header = nl < 0 ? text : text[..nl];
        return header.Count(c => c == ';') > header.Count(c => c == ',') ? ';' : ',';
    }
}

/// <summary>Loaded Notion export: the CSV plus (for ZIP exports) page ids recovered from the .md file names.</summary>
public sealed class NotionExport
{
    public required CsvTable Table { get; init; }
    public required string SourceName { get; init; }
    /// <summary>normalized page title -> page id (from "Title 0123…cdef.md" entries in ZIP exports)</summary>
    /// <summary>Value null = ambiguous title (several pages with the same name) – not used.</summary>
    public Dictionary<string, string?> IdsByTitle { get; } = new();
    public bool HasZipIds => IdsByTitle.Count > 0;
}

static class NotionImport
{
    static readonly Regex UuidDashed = new(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase);
    static readonly Regex Hex32 = new(@"(?<![0-9a-f])[0-9a-f]{32}(?![0-9a-f])", RegexOptions.IgnoreCase);
    static readonly Regex WholeLink = new(@"^(https?://(www\.)?notion\.(so|site)/\S+|notion://\S+|[0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$", RegexOptions.IgnoreCase);
    static readonly Regex MdEntry = new(@"^(?<title>.*)\s(?<id>[0-9a-f]{32})\.md$", RegexOptions.IgnoreCase);

    public const string Formula = "\"https://www.notion.so/\" + replaceAll(id(), \"-\", \"\")";

    public static NotionExport Load(string path)
    {
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return LoadZip(path);
        return new NotionExport { Table = Csv.Parse(File.ReadAllText(path, Encoding.UTF8)), SourceName = Path.GetFileName(path) };
    }

    static NotionExport LoadZip(string path)
    {
        var csvs = new List<(string name, int depth, string text, bool all)>();
        var mds = new List<string>();
        using (var zip = ZipFile.OpenRead(path))
            Collect(zip, csvs, mds, 0);

        if (csvs.Count == 0) throw new InvalidDataException(L.T("W archiwum ZIP nie ma pliku CSV."));
        var pick = csvs.OrderBy(c => c.all).ThenBy(c => c.depth).ThenByDescending(c => c.text.Length).First();
        var export = new NotionExport { Table = Csv.Parse(pick.text), SourceName = Path.GetFileName(path) + " › " + pick.name };
        foreach (var md in mds)
        {
            var m = MdEntry.Match(md);
            if (!m.Success) continue;
            var key = Normalize(m.Groups["title"].Value);
            if (key.Length == 0) continue;
            var id = m.Groups["id"].Value.ToLowerInvariant();
            if (export.IdsByTitle.TryGetValue(key, out var known) && known != id) export.IdsByTitle[key] = null; // two pages, same title
            else export.IdsByTitle[key] = id;
        }
        return export;
    }

    static void Collect(ZipArchive zip, List<(string, int, string, bool)> csvs, List<string> mds, int nesting)
    {
        foreach (var e in zip.Entries)
        {
            var name = e.Name;
            int depth = e.FullName.Count(ch => ch == '/');
            if (name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                using var sr = new StreamReader(e.Open(), Encoding.UTF8);
                csvs.Add((name, depth, sr.ReadToEnd(), name.EndsWith("_all.csv", StringComparison.OrdinalIgnoreCase)));
            }
            else if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) mds.Add(name);
            else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && nesting < 2)
            {
                // Notion wraps big exports as "Export-…-Part-1.zip" inside the downloaded zip.
                var ms = new MemoryStream();
                using (var s = e.Open()) s.CopyTo(ms);
                ms.Position = 0;
                using var inner = new ZipArchive(ms, ZipArchiveMode.Read);
                Collect(inner, csvs, mds, nesting + 1);
            }
        }
    }

    /// <summary>Extracts a Notion page id (32 hex chars, no dashes) from a URL or raw id.</summary>
    public static string? ExtractId(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var q = s.IndexOfAny(new[] { '?', '#' }); // "#blockid" would otherwise win over the page id
        if (q >= 0) s = s[..q];
        var dashed = UuidDashed.Matches(s);
        if (dashed.Count > 0) return dashed[^1].Value.Replace("-", "").ToLowerInvariant();
        var hex = Hex32.Matches(s);
        return hex.Count > 0 ? hex[^1].Value.ToLowerInvariant() : null;
    }

    public static string PageUrl(string id) => "https://www.notion.so/" + id;

    public static string Normalize(string s) =>
        new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static string? IdFromZip(NotionExport export, string title)
    {
        if (!export.HasZipIds) return null;
        var key = Normalize(title);
        if (key.Length == 0) return null;
        if (export.IdsByTitle.TryGetValue(key, out var id)) return id;
        // Notion truncates long file names; accept a unique prefix match.
        var candidates = export.IdsByTitle.Where(kv => kv.Value != null && kv.Key.Length >= 20 && key.StartsWith(kv.Key)).ToList();
        return candidates.Count == 1 ? candidates[0].Value : null;
    }

    // ---------- column detection ----------

    public static int DetectTitle(CsvTable t)
    {
        int i = FindHeader(t, h => h is "nazwa" or "name" or "title" or "tytuł" or "zadanie" or "task");
        return i >= 0 ? i : 0; // Notion always exports the title property first
    }

    public static int DetectStatus(CsvTable t) => FindHeader(t, h => h.Contains("status"));
    public static int DetectPriority(CsvTable t) => FindHeader(t, h => h.Contains("prior"));
    public static int DetectEstimate(CsvTable t)
    {
        int i = FindHeader(t, h => h.Contains("estym") || h.Contains("estimat") || h.Contains("godzin") || h.Contains("hours") || h == "h");
        return i >= 0 ? i : FindHeader(t, h => h.Contains("czas") && !h.Contains("utworz") && !h.Contains("edycj") && !h.Contains("modyf"));
    }

    static readonly Regex HoursRx = new(@"(\d+(?:\.\d+)?)\s*(h|godz|g|min|m)?", RegexOptions.IgnoreCase);

    /// <summary>"2", "1,5", "2h", "90 min", "1h 30m" → hours.</summary>
    public static double? ParseHours(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var hm = Regex.Match(s.Trim(), @"^(\d+):(\d{2})$"); // 1:30
        if (hm.Success) return Math.Round(int.Parse(hm.Groups[1].Value) + int.Parse(hm.Groups[2].Value) / 60.0, 2);
        double total = 0;
        bool any = false;
        foreach (Match m in HoursRx.Matches(s.Replace(',', '.')))
        {
            if (!double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) continue;
            var unit = m.Groups[2].Value.ToLowerInvariant();
            total += unit.StartsWith('m') ? v / 60 : v;
            any = true;
        }
        return any && total > 0 ? Math.Round(total, 2) : null;
    }

    public static int DetectLink(CsvTable t)
    {
        int best = -1; double bestRatio = 0;
        for (int c = 0; c < t.Headers.Count; c++)
        {
            int filled = 0, links = 0;
            foreach (var r in t.Rows)
            {
                var v = t.Cell(r, c);
                if (v.Length == 0) continue;
                filled++;
                if (WholeLink.IsMatch(v)) links++;
            }
            if (filled == 0) continue;
            double ratio = (double)links / filled;
            if (ratio >= 0.5 && ratio > bestRatio) { best = c; bestRatio = ratio; }
        }
        return best;
    }

    static int FindHeader(CsvTable t, Func<string, bool> match)
    {
        for (int i = 0; i < t.Headers.Count; i++)
            if (match(t.Headers[i].Trim().ToLowerInvariant())) return i;
        return -1;
    }

    static readonly string[] DoneWords = { "done", "zrobion", "zakończon", "ukończon", "complete", "closed", "zamknię", "archiw", "anulowan", "cancel" };

    public static bool LooksDone(string? status) =>
        !string.IsNullOrEmpty(status) && DoneWords.Any(w => status.Contains(w, StringComparison.OrdinalIgnoreCase));
}
