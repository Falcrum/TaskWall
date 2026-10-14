using System;
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
    };

    static string? PathFor(AppSettings s) => Dev.Arg("--data") is { } dev ? Path.Combine(dev, FileName)
        : s.Accounts.FirstOrDefault(a => a.Id == "work")?.Folder is { Length: > 0 } f ? Path.Combine(f, FileName) : null;

    public static DateTime? LastRead { get; private set; }
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
                    ["Database"] = n.Database, ["Auto"] = n.Auto, ["Minutes"] = n.Minutes, ["OnlyMine"] = n.OnlyMine,
                    ["UseViewFilter"] = n.UseViewFilter, ["MaxPages"] = n.MaxPages, ["SyncDone"] = n.SyncDone,
                    ["SkipStatuses"] = n.SkipStatuses == null ? null : new JsonArray(n.SkipStatuses.Select(x => (JsonNode?)x).ToArray()),
                },
            });
        }
        o["Accounts"] = accounts;
        return o;
    }

    static string Hash(JsonObject o) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(o.ToJsonString())));

    /// <summary>
    /// Writes the shared part when it changed on this computer (called from every settings save). The file is read first:
    /// a newer version from another computer is merged in (its settings win, except those changed here since the last sync).
    /// </summary>
    public static void Push(AppSettings s)
    {
        if (PathFor(s) is not { } path || !Directory.Exists(Path.GetDirectoryName(path))) return;
        try
        {
            if (Pull(s) is { Length: > 0 } changed) Pulled?.Invoke(changed);
            var shared = Extract(s);
            var hash = Hash(shared);
            if (hash == s.SharedHash && File.Exists(path)) return;
            s.SharedHash = hash;
            s.SharedModified = Stamp.After(s.SharedModified ?? DateTime.MinValue);
            s.SharedPath = path;
            s.SharedBaseline = shared.ToJsonString();
            var file = new JsonObject { ["Modified"] = s.SharedModified, ["Computer"] = Environment.MachineName, ["Settings"] = shared };
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
    public static string[] Pull(AppSettings s)
    {
        if (PathFor(s) is not { } path) return Array.Empty<string>();
        try
        {
            if (!File.Exists(path)) return Array.Empty<string>();
            var file = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
            LastRead = DateTime.Now;
            LastError = null;
            if (file?["Settings"] is not JsonObject theirs || file["Modified"]?.GetValue<DateTime>() is not { } modified) return Array.Empty<string>();
            bool samePath = string.Equals(s.SharedPath, path, StringComparison.OrdinalIgnoreCase);
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
                    link.Auto = n["Auto"]?.GetValue<bool>() ?? link.Auto;
                    link.Minutes = n["Minutes"]?.GetValue<int>() ?? link.Minutes;
                    link.OnlyMine = n["OnlyMine"]?.GetValue<bool>() ?? link.OnlyMine;
                    link.UseViewFilter = n["UseViewFilter"]?.GetValue<bool>() ?? link.UseViewFilter;
                    link.MaxPages = n["MaxPages"]?.GetValue<int>() ?? link.MaxPages;
                    link.SyncDone = n["SyncDone"]?.GetValue<bool>() ?? link.SyncDone;
                    link.SkipStatuses = n["SkipStatuses"] is JsonArray skip ? skip.Select(x => x?.GetValue<string>() ?? "").ToList() : null;
                }
            }
    }
}
