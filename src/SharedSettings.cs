using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TaskWall;

/// <summary>
/// Settings that follow the user between computers: taskwall-settings.json next to the work account's board.json
/// (the newest version wins). What depends on the computer stays local: folders, screen and size, autostart,
/// the current view, calendar addresses and Notion tokens (encrypted for this Windows user).
/// </summary>
static class SharedSettings
{
    public const string FileName = "taskwall-settings.json";

    static readonly string[] LocalOnly =
    {
        nameof(AppSettings.SettingsVersion), nameof(AppSettings.DataFolder), nameof(AppSettings.PrivateFolder), nameof(AppSettings.Layer),
        nameof(AppSettings.View), nameof(AppSettings.Monitor), nameof(AppSettings.StartWithWindows), nameof(AppSettings.ShowNextWeek),
        nameof(AppSettings.BoardHidden), nameof(AppSettings.SkippedUpdate), nameof(AppSettings.DrawerLocked), nameof(AppSettings.UiScale),
        nameof(AppSettings.WidthPercent), nameof(AppSettings.VerticalPercent), nameof(AppSettings.WeekHeightPercent), nameof(AppSettings.FontSize),
        nameof(AppSettings.BacklogWidth), nameof(AppSettings.Calendars), nameof(AppSettings.Accounts),
        nameof(AppSettings.SharedModified), nameof(AppSettings.SharedHash), nameof(AppSettings.SharedPath), nameof(AppSettings.SharedBaseline),
        // view state that changes by itself (zoom, filters, the last weekly summary): never a reason to overwrite another PC's settings
        nameof(AppSettings.Timeline), nameof(AppSettings.LastWeekSummary), nameof(AppSettings.NotesSource), nameof(AppSettings.DayNotesLeft),
    };

    static string? PathFor(AppSettings s) => Dev.Arg("--data") is { } dev ? Path.Combine(dev, FileName)
        : s.Accounts.FirstOrDefault(a => a.Id == "work")?.Folder is { Length: > 0 } f ? Path.Combine(f, FileName) : null;

    // one file per computer (devices/settings-<COMPUTER>.json), like the board: a file only one computer writes always
    // syncs whole; the newest of all of them (and the old shared file) is what the others take over
    static string? DeviceFor(AppSettings s) => PathFor(s) is { } p ? Path.Combine(Path.GetDirectoryName(p)!, "devices", $"settings-{Device ?? BoardStore.Machine}.json") : null;
    /// <summary>The self-test plays two computers.</summary>
    internal static string? Device;

    /// <summary>Every computer's settings file and the old shared one – the newest is taken.</summary>
    static IEnumerable<string> Candidates(string legacy)
    {
        if (File.Exists(legacy)) yield return legacy;
        var dir = Path.Combine(Path.GetDirectoryName(legacy)!, "devices");
        if (Directory.Exists(dir))
            foreach (var f in Directory.GetFiles(dir, "settings-*.json")) yield return f;
    }

    /// <summary>The folder the last sync came from (older versions kept the file's path there).</summary>
    static string? FolderOf(string? p) => p == null ? null : p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(p) : p;

    public static string? LastError { get; private set; }
    /// <summary>Raised when settings came from another computer during a save.</summary>
    public static event Action<string[]>? Pulled;

    static JsonObject Extract(AppSettings s)
    {
        var all = JsonSerializer.SerializeToNode(s, Json.Options)!.AsObject();
        var o = new JsonObject();
        foreach (var (key, value) in all.ToList())
            if (!LocalOnly.Contains(key)) o[key] = value?.DeepClone();
        var accounts = new JsonArray();
        foreach (var a in s.Accounts)
        {
            var n = a.Notion;
            accounts.Add(new JsonObject
            {
                ["Id"] = a.Id,
                ["Name"] = a.Name,
                ["Notion"] = new JsonObject
                {
                    ["Name"] = n.Name, ["Color"] = n.Color,
                    ["ReadStatuses"] = Arr(n.ReadStatuses), ["Conditions"] = Arr(n.Conditions),
                    ["Database"] = n.Database, ["Auto"] = n.Auto, ["Minutes"] = n.Minutes, ["OnlyMine"] = n.OnlyMine,
                    ["UseViewFilter"] = n.UseViewFilter, ["MaxPages"] = n.MaxPages, ["SyncDone"] = n.SyncDone,
                    ["SkipStatuses"] = n.SkipStatuses == null ? null : new JsonArray(n.SkipStatuses.Select(x => (JsonNode?)x).ToArray()),
                },
                ["NotionMore"] = new JsonArray(a.NotionMore.Select(m => (JsonNode?)new JsonObject
                {
                    ["Name"] = m.Name, ["Color"] = m.Color, ["Database"] = m.Database,
                    ["ReadStatuses"] = Arr(m.ReadStatuses), ["Conditions"] = Arr(m.Conditions),
                    ["SkipStatuses"] = m.SkipStatuses == null ? null : new JsonArray(m.SkipStatuses.Select(x => (JsonNode?)x).ToArray()),
                }).ToArray()),
            });
        }
        o["Accounts"] = accounts;
        return o;
    }

    static JsonArray? Arr(List<string>? l) => l == null ? null : new JsonArray(l.Select(x => (JsonNode?)x).ToArray());
    static List<string>? List(JsonNode? n) => n is JsonArray a ? a.Select(x => x?.GetValue<string>() ?? "").ToList() : null;

    static string Hash(JsonObject o) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(o.ToJsonString())));

    /// <summary>
    /// Writes the shared part when it changed on this computer (called from every settings save). The file is read first:
    /// a newer version from another computer is merged in (its settings win, except those changed here since the last sync).
    /// </summary>
    public static void Push(AppSettings s)
    {
        if (PathFor(s) is not { } legacy || DeviceFor(s) is not { } path || !Directory.Exists(Path.GetDirectoryName(legacy))) return;
        try
        {
            if (Pull(s) is { Length: > 0 } changed) Pulled?.Invoke(changed);
            var shared = Extract(s);
            var hash = Hash(shared);
            if (hash == s.SharedHash && File.Exists(path)) return;
            s.SharedHash = hash;
            s.SharedModified = Stamp.After(s.SharedModified ?? DateTime.MinValue);
            s.SharedPath = Path.GetDirectoryName(legacy);
            s.SharedBaseline = shared.ToJsonString();
            var file = new JsonObject { ["Modified"] = s.SharedModified, ["Computer"] = Environment.MachineName, ["Settings"] = shared };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, file.ToJsonString(Json.Options), new UTF8Encoding(false));
            File.Move(tmp, path, true);
            LastError = null;
        }
        catch (Exception ex) { Log.Error("shared settings save", ex); LastError = ex.Message; }
    }

    /// <summary>
    /// Takes over the shared file when it is newer than what this computer has (or comes from another folder). Settings
    /// changed here since the last sync (<see cref="AppSettings.SharedBaseline"/>) are kept – e.g. edits made while the
    /// cloud drive was away – and pushed on the next save. Returns the names of the settings that changed.
    /// </summary>
    static string? _lastSignature;

    public static string[] Pull(AppSettings s)
    {
        if (PathFor(s) is not { } legacy) return Array.Empty<string>();
        try
        {
            // nothing written since the last look (every settings save asks): no reading and parsing of the files
            var sig = string.Join("|", Candidates(legacy).Select(f => f + ":" + File.GetLastWriteTimeUtc(f).Ticks + ":" + new FileInfo(f).Length)) + "|" + s.SharedModified?.Ticks;
            if (sig == _lastSignature) return Array.Empty<string>();
            _lastSignature = sig;
            // the newest version among all computers' files
            JsonObject? file = null;
            DateTime newest = DateTime.MinValue;
            foreach (var f in Candidates(legacy))
            {
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(f))?.AsObject() is { } o && o["Modified"]?.GetValue<DateTime>() is { } m && m > newest) { newest = m; file = o; }
                }
                catch (Exception ex) { Log.Error("shared settings read " + f, ex); }
            }
            LastError = null;
            if (file?["Settings"] is not JsonObject theirs || file["Modified"]?.GetValue<DateTime>() is not { } modified) return Array.Empty<string>();
            var path = Path.GetDirectoryName(legacy)!;
            bool samePath = string.Equals(FolderOf(s.SharedPath), path, StringComparison.OrdinalIgnoreCase);
            if (samePath && s.SharedModified is { } mine && modified <= mine) return Array.Empty<string>();

            var before = Extract(s);
            // three-way: a setting changed here since the last sync stays, everything else comes from the file
            var baseline = samePath && s.SharedBaseline is { } b ? JsonNode.Parse(b) as JsonObject : null;
            var merged = new JsonObject();
            bool keptLocal = false;
            foreach (var key in before.Select(kv => kv.Key).Union(theirs.Select(kv => kv.Key)))
            {
                bool changedHere = baseline != null && before.ContainsKey(key) && !JsonNode.DeepEquals(before[key], baseline[key]);
                if (changedHere && !JsonNode.DeepEquals(before[key], theirs[key])) keptLocal = true;
                merged[key] = (changedHere || !theirs.ContainsKey(key) ? before[key] : theirs[key])?.DeepClone();
            }
            Apply(s, merged);

            var after = Extract(s);
            s.SharedModified = modified;
            s.SharedPath = path;
            s.SharedBaseline = theirs.ToJsonString();
            // local changes kept: the next save writes them out (the hash no longer matches)
            s.SharedHash = keptLocal ? null : Hash(after);
            return after.Where(kv => !JsonNode.DeepEquals(kv.Value, before[kv.Key])).Select(kv => kv.Key).ToArray();
        }
        catch (Exception ex) { Log.Error("shared settings read", ex); LastError = ex.Message; return Array.Empty<string>(); }
    }

    static void Apply(AppSettings s, JsonObject shared)
    {
        var all = JsonSerializer.SerializeToNode(s, Json.Options)!.AsObject();
        foreach (var (key, value) in shared)
            if (!LocalOnly.Contains(key) && key != "Accounts") all[key] = value?.DeepClone();
        var theirs = all.Deserialize<AppSettings>(Json.Options) ?? s;
        foreach (var p in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.CanWrite && !LocalOnly.Contains(p.Name) && shared.ContainsKey(p.Name)) p.SetValue(s, p.GetValue(theirs));

        if (shared["Accounts"] is JsonArray accounts)
            foreach (var node in accounts.OfType<JsonObject>())
            {
                if (node["Id"]?.GetValue<string>() is not { } id) continue;
                var acc = s.AccountFor(id);
                if (node["Name"]?.GetValue<string>() is { Length: > 0 } name) acc.Name = name;
                if (node["Notion"] is JsonObject n)
                {
                    var link = acc.Notion;
                    var database = n["Database"]?.GetValue<string>() ?? link.Database;
                    if (database != link.Database) { link.LastSync = null; link.LastResult = null; link.KnownStatuses = null; link.ViewName = null; }
                    link.Database = database;
                    link.Name = n["Name"]?.GetValue<string>() ?? link.Name;
                    link.Color = n["Color"]?.GetValue<string>() ?? link.Color;
                    link.ReadStatuses = List(n["ReadStatuses"]);
                    link.Conditions = List(n["Conditions"]);
                    link.Auto = n["Auto"]?.GetValue<bool>() ?? link.Auto;
                    link.Minutes = n["Minutes"]?.GetValue<int>() ?? link.Minutes;
                    link.OnlyMine = n["OnlyMine"]?.GetValue<bool>() ?? link.OnlyMine;
                    link.UseViewFilter = n["UseViewFilter"]?.GetValue<bool>() ?? link.UseViewFilter;
                    link.MaxPages = n["MaxPages"]?.GetValue<int>() ?? link.MaxPages;
                    link.SyncDone = n["SyncDone"]?.GetValue<bool>() ?? link.SyncDone;
                    link.SkipStatuses = n["SkipStatuses"] is JsonArray skip ? skip.Select(x => x?.GetValue<string>() ?? "").ToList() : null;
                }
                if (node["NotionMore"] is JsonArray more)
                {
                    // the same databases as on the other computer; what this one learnt about each (statuses, results) stays
                    var old = acc.NotionMore.ToList(); // each one matched once: two projects without a database stay two
                    acc.NotionMore = more.OfType<JsonObject>().Select(m =>
                    {
                        var db = m["Database"]?.GetValue<string>() ?? "";
                        var name = m["Name"]?.GetValue<string>();
                        var l = old.FirstOrDefault(o => o.Database == db && (db.Length > 0 || o.Name == name)) ?? old.FirstOrDefault(o => o.Database == db) ?? new NotionLink { Database = db };
                        old.Remove(l);
                        l.Name = m["Name"]?.GetValue<string>() ?? l.Name;
                        l.Color = m["Color"]?.GetValue<string>() ?? l.Color;
                        l.ReadStatuses = List(m["ReadStatuses"]);
                        l.Conditions = List(m["Conditions"]);
                        l.SkipStatuses = m["SkipStatuses"] is JsonArray sk ? sk.Select(x => x?.GetValue<string>() ?? "").ToList() : null;
                        return l;
                    }).ToList();
                }
            }
    }
}
