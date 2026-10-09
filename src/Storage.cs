using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;

namespace TaskWall;

static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep Polish letters readable
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

static class Log
{
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskWall");

    public static void Error(string what, Exception? ex = null)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(Path.Combine(Dir, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {what}{(ex != null ? ": " + ex : "")}{Environment.NewLine}");
        }
        catch { /* logging must never crash the app */ }
    }
}

static class SettingsStore
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskWall");
    /// <summary>Dev runs (--data) keep their own settings next to the test data, so they never touch the real ones.</summary>
    static string FilePath => Dev.Arg("--data") is { } dev
        ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dev.TrimEnd(Path.DirectorySeparatorChar))) ?? Dir, "settings.dev.json")
        : Path.Combine(Dir, "settings.json");
    const int CurrentVersion = 7;

    /// <summary>The app used to be called DeskWall: its settings folder (settings, Notion token, calendar cache) moves over once.</summary>
    public static void MigrateFromDeskWall()
    {
        try
        {
            var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskWall");
            // decided by the settings file, not the folder: a log or calendar cache may already have created the folder
            if (File.Exists(Path.Combine(Dir, "settings.json")) || !File.Exists(Path.Combine(old, "settings.json"))) return;
            Directory.CreateDirectory(Dir);
            foreach (var f in Directory.GetFiles(old, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(Dir, Path.GetRelativePath(old, f));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // settings always come over; the rest only when not there yet
                if (!File.Exists(target) || Path.GetFileName(f) == "settings.json") File.Copy(f, target, true);
            }
        }
        catch (Exception ex) { Log.Error("migrate settings from DeskWall", ex); }
    }

    public static AppSettings Load()
    {
        AppSettings s = new();
        for (int attempt = 0; attempt < 5 && File.Exists(FilePath); attempt++)
        {
            try
            {
                s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json.Options) ?? new AppSettings();
                break;
            }
            catch (IOException) { Thread.Sleep(200); } // antivirus / sync client holding the file for a moment
            catch (Exception ex)
            {
                // broken file: keep a copy instead of silently replacing the user's settings with defaults
                Log.Error("settings load", ex);
                try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(s.DataFolder)) s.DataFolder = DefaultDataFolder();

        if (s.SettingsVersion < 2)
        {
            // v2: bigger layout like Todowall, blur done by the app itself, accent from wallpaper
            var d = new AppSettings();
            s.UiScale = d.UiScale;
            s.WidthPercent = d.WidthPercent;
            s.WeekHeightPercent = d.WeekHeightPercent;
            s.TintOpacity = d.TintOpacity;
            s.Accent = d.Accent;
            s.BacklogWidth = d.BacklogWidth;
            s.Blur = true;
        }
        if (s.SettingsVersion < 3 && s.Hotkey is "Ctrl+Alt+Space" or "Win+Alt+T") s.Hotkey = "Ctrl+Shift+Space"; // often taken by other apps
        if (s.SettingsVersion < 4) { s.WidthPercent = 90; s.WeekHeightPercent = 20; s.UiScale = 1.35; }
        if (s.SettingsVersion < 5 && s.Calendars.Count == 0)
            s.Calendars.Add(new CalendarFeed { Name = "Święta w Polsce", Url = CalendarFeed.PolishHolidaysUrl, Kind = "holidays" });
        if (s.SettingsVersion < 6) s.View = s.ShowNextWeek ? "week2" : "week1";
        if (string.IsNullOrWhiteSpace(s.PrivateFolder)) s.PrivateFolder = DefaultPrivateFolder(s.DataFolder);
        if (s.SettingsVersion < 7 && s.Accounts.Count == 0)
        {
            // v7: layers became accounts, each with its own folder and calendars
            CalendarFeed Copy(CalendarFeed f) => new() { Name = f.Name, Url = f.Url, Kind = f.Kind, Enabled = f.Enabled };
            s.Accounts.Add(new Account { Id = "work", Name = "Praca", Folder = s.DataFolder, Calendars = s.Calendars.Where(f => f.Layer is "" or "work").Select(Copy).ToList() });
            s.Accounts.Add(new Account { Id = "private", Name = "Prywatne", Folder = s.PrivateFolder, Calendars = s.Calendars.Where(f => f.Layer is "" or "private").Select(Copy).ToList() });
        }
        foreach (var id in new[] { "work", "private" })
        {
            var acc = s.AccountFor(id);
            if (string.IsNullOrWhiteSpace(acc.Folder)) acc.Folder = id == "work" ? s.DataFolder : s.PrivateFolder;
            if (s.SettingsVersion < 7 && acc.Calendars.Count == 0)
                acc.Calendars.Add(new CalendarFeed { Name = "Święta w Polsce", Url = CalendarFeed.PolishHolidaysUrl, Kind = "holidays" });
        }
        s.SettingsVersion = CurrentVersion;
        return s;
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, Json.Options));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex) { Log.Error("settings save", ex); }
    }

    /// <summary>Google Drive for desktop mount ("G:\Mój dysk"), if installed.</summary>
    public static string? GoogleDriveRoot()
    {
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady) continue;
                foreach (var name in new[] { "Mój dysk", "My Drive" })
                {
                    var p = Path.Combine(d.RootDirectory.FullName, name);
                    if (Directory.Exists(p) && (d.VolumeLabel.Contains("Google", StringComparison.OrdinalIgnoreCase) || d.DriveFormat.Equals("FAT32", StringComparison.OrdinalIgnoreCase)))
                        return p;
                }
            }
        }
        catch (Exception ex) { Log.Error("google drive detect", ex); }
        return null;
    }

    public static string? OneDriveRoot()
    {
        var p = Environment.GetEnvironmentVariable("OneDrive");
        return !string.IsNullOrEmpty(p) && Directory.Exists(p) ? p : null;
    }

    /// <summary>Private tasks: personal OneDrive if present, otherwise a sub-folder next to the work data.</summary>
    public static string DefaultPrivateFolder(string workFolder)
    {
        if (OneDriveRoot() is { } od) return Path.Combine(od, "TaskWall Prywatne");
        return Path.Combine(string.IsNullOrWhiteSpace(workFolder) ? Dir : workFolder, "Prywatne");
    }

    public static string DefaultDataFolder()
    {
        var root = GoogleDriveRoot() ?? OneDriveRoot();
        return root != null ? Path.Combine(root, "TaskWall") : Dir;
    }
}

/// <summary>
/// Board persisted as board.json inside a (cloud-synced) folder.
/// Every save first merges whatever is on disk, and external changes are merged in
/// as soon as the file changes, so two computers never silently overwrite each other.
/// </summary>
public sealed class BoardStore
{
    public const string FileName = "board.json";
    const int BackupDays = 14, UndoMax = 60;

    public BoardData Data { get; private set; } = new();
    public string Folder { get; private set; } = "";
    /// <summary>"work" or "private" – picks the default categories.</summary>
    public string Layer { get; }

    public BoardStore(string layer = "work") : this() => Layer = layer;
    public string FilePath => Path.Combine(Folder, FileName);
    public string BackupFolder => Path.Combine(Folder, "backup");
    public DateTime? LastSaved { get; private set; }
    public DateTime? LastExternalChange { get; private set; }

    /// <summary>Raised (on the UI thread) when data changed because of another computer.</summary>
    public event Action? ExternalChange;

    string? _lastHash;
    int _unreadableCount;
    FileSystemWatcher? _watcher;
    readonly DispatcherTimer _saveTimer, _reloadTimer, _pollTimer;
    readonly List<string> _undo = new();

    BoardStore()
    {
        Layer = "work";
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) => SaveNow();
        _reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _reloadTimer.Tick += (_, _) => { _reloadTimer.Stop(); CheckDisk(); };
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) }; // backup for missed file events
        _pollTimer.Tick += (_, _) => CheckDisk();
    }

    public void Open(string folder)
    {
        StopWatching();
        Folder = folder;
        Directory.CreateDirectory(folder);
        Data = new BoardData();
        _lastHash = null;
        _undo.Clear();

        var disk = TryRead(FilePath);
        if (disk.Data != null)
        {
            Data = disk.Data;
            _lastHash = disk.Hash;
        }
        MergeConflictCopies();
        EnsureCategories();
        if (!File.Exists(FilePath)) SaveNow();
        StartWatching();
    }

    /// <summary>Default categories carry the oldest possible timestamp, so any real edit (from any PC) wins.</summary>
    void EnsureCategories()
    {
        if (Data.Categories == null)
        {
            Data.Categories = Category.DefaultsFor(Layer);
            Data.CategoriesModified = DateTime.MinValue.AddTicks(1);
        }
        if (Data.Marks == null)
        {
            Data.Marks = MarkType.DefaultsFor(Layer);
            Data.MarksModified = DateTime.MinValue.AddTicks(1);
        }
    }

    public List<Category> Categories => Data.Categories ??= Category.DefaultsFor(Layer);

    public void SetCategories(List<Category> list)
    {
        Data.Categories = list;
        Data.CategoriesModified = Stamp.After(Data.CategoriesModified);
        MarkDirty();
    }

    public Category? CategoryFor(string tag) => Categories.FirstOrDefault(c => c.Key == tag);

    // ---------- kinds of day marks (per account) ----------

    public List<MarkType> MarkTypes => Data.Marks ??= MarkType.DefaultsFor(Layer);

    public void SetMarkTypes(List<MarkType> list)
    {
        Data.Marks = list;
        Data.MarksModified = Stamp.After(Data.MarksModified);
        MarkDirty();
    }

    public MarkType? MarkTypeFor(string? code) => code == null ? null : MarkTypes.FirstOrDefault(m => m.Code == code);

    /// <summary>Switch to another folder, carrying the current tasks over (merged with whatever is there).</summary>
    public void SwitchFolder(string folder)
    {
        SaveNow();
        var current = Data;
        Open(folder);
        Data = Merge(Data, current);
        SaveNow();
        ExternalChange?.Invoke();
    }

    // ---------- mutations (always on the UI thread) ----------

    public void MarkDirty()
    {
        _version++;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void Add(TaskItem t)
    {
        t.Touch();
        Data.Deleted.Remove(t.Id);
        Data.Tasks.Add(t);
        MarkDirty();
    }

    /// <summary>
    /// Deletes for good (the tombstone makes the deletion reach the other computers). A deleted occurrence of a
    /// recurring series is also skipped in the series, otherwise it would come back as a fresh virtual task.
    /// </summary>
    public void Remove(TaskItem t, bool skipOccurrence = true)
    {
        Data.Tasks.Remove(t);
        Data.Deleted[t.Id] = Stamp.After(t.Modified);
        if (skipOccurrence && t.RuleDay != null && Rule(t.RuleId) is { } r && !r.Skips.Contains(t.RuleDay))
        {
            r.Skips.Add(t.RuleDay);
            Changed(r);
        }
        MarkDirty();
    }

    public TaskItem? Find(string id) => Data.Tasks.FirstOrDefault(t => t.Id == id);

    /// <summary>Hides the task from the board but keeps it (search, year view, archive window).</summary>
    public void Archive(TaskItem t)
    {
        t.Archived = true;
        t.ArchivedAt = DateTime.UtcNow;
        Changed(t);
    }

    public void Unarchive(TaskItem t)
    {
        t.Archived = false;
        t.ArchivedAt = null;
        Changed(t);
    }

    // ---------- recurring series ----------

    public RecurringRule? Rule(string? id) => id == null ? null : Data.Rules.FirstOrDefault(r => r.Id == id);

    public void AddRule(RecurringRule r)
    {
        r.Touch();
        Data.Rules.Add(r);
        MarkDirty();
    }

    public void Changed(RecurringRule r)
    {
        r.Touch();
        MarkDirty();
    }

    // "ruleId|day" of every stored occurrence; rebuilt lazily when the task list changed
    HashSet<string>? _occurrences;
    int _occurrencesFor = -1;
    int _version;

    HashSet<string> Occurrences()
    {
        if (_occurrences == null || _occurrencesFor != _version)
        {
            _occurrences = Data.Tasks.Where(t => t.RuleId != null).Select(t => t.RuleId + "|" + t.RuleDay).ToHashSet();
            _occurrencesFor = _version;
        }
        return _occurrences;
    }

    /// <summary>Occurrences of recurring series on <paramref name="d"/> that have no real task yet.</summary>
    public IEnumerable<TaskItem> VirtualTasks(DateTime d)
    {
        if (Data.Rules.Count == 0) yield break;
        var key = d.ToString("yyyy-MM-dd");
        var stored = Occurrences();
        foreach (var r in Data.Rules)
        {
            if (!r.Occurs(d)) continue;
            if (stored.Contains(r.Id + "|" + key)) continue;
            yield return new TaskItem
            {
                Id = $"v:{r.Id}:{key}", Text = r.Text, Day = key, Order = r.Order - 10000, Estimate = r.Estimate,
                RuleId = r.Id, RuleDay = key, IsVirtual = true,
            };
        }
    }

    /// <summary>
    /// The live stored task for <paramref name="v"/>: turns a virtual occurrence into a stored task (first edit /
    /// check / move), and for a stored task returns the current instance (a row may hold an older reference).
    /// </summary>
    public TaskItem Materialize(TaskItem v)
    {
        if (!v.IsVirtual) return Find(v.Id) ?? v;
        var existing = Data.Tasks.FirstOrDefault(t => t.RuleId == v.RuleId && t.RuleDay == v.RuleDay);
        if (existing != null) return existing;
        var t = v.Clone();
        t.Id = $"r:{v.RuleId}:{v.RuleDay}"; // deterministic: two PCs ticking the same occurrence merge into one task
        t.IsVirtual = false;
        Add(t);
        return t;
    }

    public void Changed(TaskItem t)
    {
        t.Touch();
        MarkDirty();
    }


    // ---------- clearing (Settings → Konta → Wyczyść dane) ----------
    // Everything goes through tombstones / "deleted" flags, so the other computers clear the same data instead of
    // bringing it back. Ctrl+Z can undo tasks and series (a checkpoint is taken first).

    /// <summary>All tasks: days, backlog, archive, plus the repeating series.</summary>
    public int ClearTasks()
    {
        Checkpoint();
        var all = Data.Tasks.ToList();
        foreach (var t in all) Remove(t, skipOccurrence: false);
        foreach (var r in Data.Rules.Where(r => !r.Deleted).ToList()) { r.Deleted = true; Changed(r); }
        MarkDirty();
        return all.Count;
    }

    /// <summary>Backups of this account, newest first: daily copies, copies made before a reset, merged conflict copies.</summary>
    public List<(string File, DateTime When, int Tasks)> Backups()
    {
        var list = new List<(string, DateTime, int)>();
        if (!Directory.Exists(BackupFolder)) return list;
        foreach (var f in Directory.GetFiles(BackupFolder, "*.json", SearchOption.AllDirectories))
        {
            var d = TryRead(f).Data;
            if (d != null) list.Add((f, File.GetLastWriteTime(f), d.Tasks.Count));
        }
        return list.OrderByDescending(x => x.Item2).ToList();
    }

    /// <summary>
    /// Brings tasks, day marks and series back to a backup's state – as new edits, so other computers follow and
    /// Ctrl+Z can still undo it.
    /// </summary>
    public bool RestoreFrom(string file)
    {
        var d = TryRead(file).Data;
        if (d == null) return false;
        Checkpoint(); // Ctrl+Z after the restore goes back to how it was
        _undo.Add(JsonSerializer.Serialize(new BoardData { Tasks = d.Tasks, Days = d.Days, Rules = d.Rules, MarkRules = d.MarkRules }, Json.Options));
        return Undo();
    }

    public int ClearAlarms(bool meetings)
    {
        var list = Data.Alarms.Where(a => !a.Deleted && a.IsMeeting == meetings).ToList();
        foreach (var a in list) DeleteAlarm(a);
        return list.Count;
    }

    /// <summary>Day marks set by hand and the repeating ones.</summary>
    public int ClearMarks()
    {
        Checkpoint();
        int n = 0;
        foreach (var key in Data.Days.Where(kv => !string.IsNullOrEmpty(kv.Value.Mark)).Select(kv => kv.Key).ToList())
        {
            Data.Days[key] = new DayInfo { Mark = null, Modified = Stamp.After(Data.Days[key].Modified) };
            n++;
        }
        foreach (var r in Data.MarkRules.Where(r => !r.Deleted).ToList()) { r.Deleted = true; Changed(r); n++; }
        MarkDirty();
        return n;
    }


    // ---------- alarms ----------

    public IEnumerable<Alarm> Alarms => Data.Alarms.Where(x => !x.Deleted);

    public void AddAlarm(Alarm x)
    {
        x.Touch();
        Data.Alarms.Add(x);
        MarkDirty();
    }

    public void Changed(Alarm x)
    {
        x.Touch();
        MarkDirty();
    }

    /// <summary>Kept as a flag (not removed), so the deletion reaches the other computers.</summary>
    public void DeleteAlarm(Alarm x)
    {
        x.Deleted = true;
        Changed(x);
    }

    /// <summary>Remembers that the occurrence rang (no new timestamp: merging keeps the latest one anyway).</summary>
    public void MarkRang(Alarm x, DateTime at)
    {
        var key = Alarm.Key(at);
        if (string.CompareOrdinal(key, x.Rang) <= 0) return;
        x.Rang = key;
        MarkDirty();
    }

    /// <summary>
    /// The mark of a day: set by hand (Days; "" = removed by hand on a day a series would mark) or else from a
    /// repeating mark series.
    /// </summary>
    public string? DayMark(string day)
    {
        if (Data.Days.TryGetValue(day, out var d) && d.Mark != null) return d.Mark.Length == 0 ? null : d.Mark;
        return SeriesMark(day);
    }

    public string? SeriesMark(string day)
    {
        if (Data.MarkRules.Count == 0 || !DateTime.TryParseExact(day, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var date)) return null;
        return MarkRuleOn(date)?.Text;
    }

    public RecurringRule? MarkRuleOn(DateTime date) => Data.MarkRules.FirstOrDefault(r => r.Occurs(date));

    public void SetDayMark(string day, string? mark)
    {
        var before = Data.Days.TryGetValue(day, out var d) ? d.Modified : DateTime.MinValue;
        // same as the series → no override; removing a series mark → "" (explicit "nothing")
        var series = SeriesMark(day);
        string? stored = mark == series ? null : mark ?? "";
        Data.Days[day] = new DayInfo { Mark = stored, Modified = Stamp.After(before) };
        MarkDirty();
    }

    public void AddMarkRule(RecurringRule r)
    {
        r.Touch();
        Data.MarkRules.Add(r);
        MarkDirty();
    }

    // ---------- undo (local, per session) ----------
    // Undo restores the last checkpoint as new edits. Whenever changes from another computer are merged in,
    // the stack is cleared – otherwise Ctrl+Z would also revert (or delete) what the other PC did.

    public bool CanUndo => _undo.Count > 0;

    /// <summary>Call before every user action that changes the board.</summary>
    public void Checkpoint()
    {
        _undo.Add(JsonSerializer.Serialize(new BoardData { Tasks = Data.Tasks, Days = Data.Days, Rules = Data.Rules, MarkRules = Data.MarkRules }, Json.Options));
        if (_undo.Count > UndoMax) _undo.RemoveAt(0);
    }

    /// <summary>Restores the state from the last checkpoint as new (sync-friendly) edits.</summary>
    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var snap = JsonSerializer.Deserialize<BoardData>(_undo[^1], Json.Options);
        _undo.RemoveAt(_undo.Count - 1);
        if (snap == null) return false;

        var old = snap.Tasks.ToDictionary(t => t.Id);
        var current = Data.Tasks.ToDictionary(t => t.Id);
        foreach (var t in Data.Tasks.Where(t => !old.ContainsKey(t.Id)).ToList())
            Remove(t, skipOccurrence: false); // e.g. an occurrence that was just materialized goes back to virtual
        foreach (var o in old.Values)
        {
            if (!current.TryGetValue(o.Id, out var cur)) Add(o);
            else if (Comparable(cur) != Comparable(o))
            {
                var stamp = cur.Modified;
                cur.CopyFrom(o);
                cur.Modified = stamp;
                Changed(cur);
            }
        }
        UndoRules(snap.Rules, Data.Rules);
        UndoRules(snap.MarkRules ?? new(), Data.MarkRules);
        foreach (var key in snap.Days.Keys.Union(Data.Days.Keys).ToList())
        {
            // raw stored values ("" = removed by hand), so series marks stay series marks
            var was = snap.Days.TryGetValue(key, out var a) ? a.Mark : null;
            var now = Data.Days.TryGetValue(key, out var b) ? b.Mark : null;
            if (was != now) Data.Days[key] = new DayInfo { Mark = was, Modified = Stamp.After(b?.Modified ?? DateTime.MinValue) };
        }
        MarkDirty();
        return true;
    }

    void UndoRules(List<RecurringRule> snap, List<RecurringRule> live)
    {
        var oldRules = snap.ToDictionary(r => r.Id);
        foreach (var r in live.Where(r => !oldRules.ContainsKey(r.Id) && !r.Deleted)) { r.Deleted = true; Changed(r); }
        foreach (var o in oldRules.Values)
        {
            var cur = live.FirstOrDefault(r => r.Id == o.Id);
            if (cur == null) { o.Touch(); live.Add(o); }
            else if (RuleKey(cur) != RuleKey(o))
            {
                var stamp = cur.Modified;
                cur.CopyFrom(o);
                cur.Modified = stamp;
                Changed(cur);
            }
        }
    }

    static string RuleKey(RecurringRule r) =>
        $"{r.Text}|{r.Pattern}|{r.Interval}|{r.SkipSundaysHolidays}|{string.Join(",", r.Weekdays ?? new())}|{r.Start}|{r.End}|{r.Estimate}|{r.Order}|{string.Join(",", r.Skips)}|{r.Deleted}";

    static string Comparable(TaskItem t)
    {
        var c = t.Clone();
        c.Modified = default;
        return JsonSerializer.Serialize(c);
    }

    // ---------- persistence ----------

    public void SaveNow()
    {
        _saveTimer.Stop();
        if (string.IsNullOrEmpty(Folder)) return;
        try
        {
            bool merged = false;
            var disk = TryRead(FilePath);
            if (disk.Unreadable)
            {
                // Possibly a half-synced file; give the sync client about a minute before giving up on it.
                if (++_unreadableCount < 20)
                {
                    _saveTimer.Interval = TimeSpan.FromSeconds(3);
                    _saveTimer.Start();
                    return;
                }
                var backup = Path.Combine(Folder, $"unreadable-board-{DateTime.Now:yyyyMMdd-HHmmss}.json.bak");
                try { File.Copy(FilePath, backup, true); } catch { }
                Log.Error($"board.json unreadable, backed up to {backup}");
            }
            _unreadableCount = 0;
            _saveTimer.Interval = TimeSpan.FromMilliseconds(400);

            if (disk.Data != null && disk.Hash != _lastHash)
            {
                Data = Merge(Data, disk.Data);
                merged = true;
            }

            Prune();
            var json = JsonSerializer.Serialize(Data, Json.Options);
            var hash = Hash(json);
            if (disk.Data != null && hash == disk.Hash)
            {
                _lastHash = hash;
            }
            else
            {
                var tmp = FilePath + ".tmp"; // OneDrive / Drive / Dropbox skip *.tmp files
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                // the sync client may have dropped a newer file in meanwhile: merge that first instead of overwriting it
                if (disk.Hash != null && FileHash(FilePath) is { } now && now != disk.Hash)
                {
                    File.Delete(tmp);
                    MarkDirty();
                    return;
                }
                File.Move(tmp, FilePath, true);
                _lastHash = hash;
                LastSaved = DateTime.Now;
            }
            DailyBackup();
            if (merged)
            {
                _undo.Clear();
                LastExternalChange = DateTime.Now;
                ExternalChange?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Log.Error("board save", ex);
            _saveTimer.Start(); // retry
        }
    }

    /// <summary>One copy per day in backup\yyyy-MM-dd.json, the last 14 days are kept.</summary>
    void DailyBackup()
    {
        try
        {
            var target = Path.Combine(BackupFolder, DateTime.Today.ToString("yyyy-MM-dd") + ".json");
            if (File.Exists(target) || !File.Exists(FilePath)) return;
            Directory.CreateDirectory(BackupFolder);
            File.Copy(FilePath, target);
            foreach (var old in Directory.GetFiles(BackupFolder, "????-??-??.json").OrderByDescending(f => f).Skip(BackupDays))
                File.Delete(old);
        }
        catch (Exception ex) { Log.Error("backup", ex); }
    }

    void CheckDisk()
    {
        try
        {
            var disk = TryRead(FilePath);
            if (disk.Data != null && disk.Hash != _lastHash)
            {
                var merged = Merge(Data, disk.Data);
                Data = merged;
                _version++;
                _undo.Clear();
                _lastHash = disk.Hash;
                LastExternalChange = DateTime.Now;
                // If we hold changes the other side doesn't have yet, push them back.
                EnsureCategories();
                if (Hash(JsonSerializer.Serialize(merged, Json.Options)) != disk.Hash) MarkDirty();
                ExternalChange?.Invoke();
            }
            MergeConflictCopies();
        }
        catch (Exception ex) { Log.Error("board reload", ex); }
    }

    /// <summary>
    /// Only real conflict copies: "board-PCNAME.json" (OneDrive), "board (1).json" (Google Drive),
    /// "board (… conflicted copy …).json" (Dropbox). A user's own "board — kopia.json" is left alone.
    /// </summary>
    static readonly System.Text.RegularExpressions.Regex ConflictName = new(
        @"^board(-[A-Za-z0-9_\-]+| \(\d+\)| \([^)]*conflict[^)]*\))\.json$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Merges conflict copies in, then moves them to backup\ (nothing is deleted).</summary>
    void MergeConflictCopies()
    {
        if (!Directory.Exists(Folder)) return;
        bool any = false;
        foreach (var f in Directory.GetFiles(Folder, "board*.json"))
        {
            if (!ConflictName.IsMatch(Path.GetFileName(f))) continue;
            var copy = TryRead(f);
            if (copy.Data == null) continue;
            Data = Merge(Data, copy.Data);
            any = true;
            try
            {
                Directory.CreateDirectory(BackupFolder);
                File.Move(f, Path.Combine(BackupFolder, $"merged-{DateTime.Now:yyyyMMdd-HHmmss}-{Path.GetFileName(f)}"), true);
            }
            catch (Exception ex) { Log.Error("conflict copy move", ex); }
        }
        if (any)
        {
            _undo.Clear();
            LastExternalChange = DateTime.Now;
            MarkDirty();
            ExternalChange?.Invoke();
        }
    }

    public static BoardData Merge(BoardData a, BoardData b)
    {
        var result = new BoardData();
        foreach (var kv in a.Deleted.Concat(b.Deleted))
            if (!result.Deleted.TryGetValue(kv.Key, out var t) || kv.Value > t)
                result.Deleted[kv.Key] = kv.Value;

        // a's (local) instances are kept and updated in place, so references held by the UI stay valid
        var byId = new Dictionary<string, TaskItem>();
        foreach (var t in a.Tasks) byId[t.Id] = t;
        foreach (var t in b.Tasks)
        {
            if (!byId.TryGetValue(t.Id, out var existing)) byId[t.Id] = t;
            else if (t.Modified > existing.Modified || (t.Modified == existing.Modified && string.CompareOrdinal(TieBreak(t), TieBreak(existing)) > 0))
                existing.CopyFrom(t);
        }

        result.Tasks = byId.Values
            .Where(t => !result.Deleted.TryGetValue(t.Id, out var del) || del < t.Modified)
            .ToList();

        foreach (var kv in a.Days.Concat(b.Days))
            if (!result.Days.TryGetValue(kv.Key, out var d) || kv.Value.Modified > d.Modified)
                result.Days[kv.Key] = kv.Value;

        bool takeB = a.Categories == null || (b.Categories != null && b.CategoriesModified > a.CategoriesModified);
        result.Categories = takeB ? b.Categories : a.Categories;
        result.CategoriesModified = takeB ? b.CategoriesModified : a.CategoriesModified;

        bool takeBMarks = a.Marks == null || (b.Marks != null && b.MarksModified > a.MarksModified);
        result.Marks = takeBMarks ? b.Marks : a.Marks;
        result.MarksModified = takeBMarks ? b.MarksModified : a.MarksModified;

        result.Rules = MergeRules(a.Rules, b.Rules);
        result.MarkRules = MergeRules(a.MarkRules, b.MarkRules);

        var alarms = new Dictionary<string, Alarm>();
        foreach (var x in a.Alarms) alarms[x.Id] = x;
        foreach (var x in b.Alarms)
        {
            if (!alarms.TryGetValue(x.Id, out var cur)) { alarms[x.Id] = x; continue; }
            // "already rang" travels independently of edits, so a second PC doesn't ring again later
            var rang = string.CompareOrdinal(x.Rang, cur.Rang) > 0 ? x.Rang : cur.Rang;
            var skips = cur.Skips == null && x.Skips == null ? null : (cur.Skips ?? new()).Union(x.Skips ?? new()).ToList();
            if (x.Modified > cur.Modified) cur.CopyFrom(x);
            cur.Rang = rang;
            cur.Skips = skips;
        }
        result.Alarms = alarms.Values.ToList();
        return result;
    }

    static List<RecurringRule> MergeRules(List<RecurringRule> a, List<RecurringRule> b)
    {
        var rules = new Dictionary<string, RecurringRule>();
        foreach (var r in a) rules[r.Id] = r;
        foreach (var r in b)
        {
            if (!rules.TryGetValue(r.Id, out var cur)) { rules[r.Id] = r; continue; }
            var skips = cur.Skips.Union(r.Skips).ToList(); // a skip made on either PC survives a rename on the other
            if (r.Modified > cur.Modified) cur.CopyFrom(r);
            cur.Skips = skips;
        }
        return rules.Values.ToList();
    }

    /// <summary>Deterministic winner when two edits carry the same timestamp (both PCs must agree).</summary>
    static string TieBreak(TaskItem t) => JsonSerializer.Serialize(t);

    void Prune()
    {
        // tombstones are tiny – keep them for 2 years, so a laptop that was off for months can't resurrect deleted tasks
        var cutoff = DateTime.UtcNow.AddYears(-2);
        foreach (var id in Data.Deleted.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
            Data.Deleted.Remove(id);
        Data.Trash = null;
        foreach (var key in Data.Days.Where(kv => kv.Value.Mark == null && kv.Value.Modified < cutoff).Select(kv => kv.Key).ToList())
            Data.Days.Remove(key);
        Data.Alarms.RemoveAll(x => x.Deleted && x.Modified < cutoff);
    }

    readonly record struct DiskState(BoardData? Data, string? Hash, bool Unreadable);

    static DiskState TryRead(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return new DiskState(null, null, false);
                string json;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                    json = sr.ReadToEnd();
                if (string.IsNullOrWhiteSpace(json)) return new DiskState(null, null, true);
                var data = JsonSerializer.Deserialize<BoardData>(json, Json.Options);
                if (data == null) return new DiskState(null, null, true);
                data.Tasks ??= new();
                data.Deleted ??= new();
                data.Days ??= new();
                data.Rules ??= new();
                data.Alarms ??= new();
                data.MarkRules ??= new();
                MigrateTrash(data);
                return new DiskState(data, Hash(json), false);
            }
            catch (IOException) when (attempt < 4) { Thread.Sleep(80); }
            catch (UnauthorizedAccessException) when (attempt < 4) { Thread.Sleep(80); }
            catch (JsonException ex)
            {
                Log.Error($"parse {path}", ex);
                return new DiskState(null, null, true);
            }
        }
    }

    /// <summary>v1–v3 kept deleted tasks in a trash list: they become archived tasks now.</summary>
    static void MigrateTrash(BoardData data)
    {
        if (data.Trash == null) return;
        foreach (var e in data.Trash)
        {
            if (data.Tasks.Any(t => t.Id == e.Task.Id)) continue;
            var t = e.Task;
            t.Archived = true;
            t.ArchivedAt = e.Deleted;
            // stable stamp (re-reading an old file must not "re-touch" it): just after the deletion
            var tomb = data.Deleted.TryGetValue(t.Id, out var del) ? del : DateTime.MinValue;
            t.Modified = new[] { e.Deleted, tomb, t.Modified }.Max().AddTicks(1);
            data.Deleted.Remove(t.Id);
            data.Tasks.Add(t);
        }
        data.Trash = null;
    }

    static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    static string? FileHash(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            return Hash(sr.ReadToEnd());
        }
        catch { return null; }
    }

    void StartWatching()
    {
        try
        {
            _watcher = new FileSystemWatcher(Folder, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler h = (_, _) => Kick();
            _watcher.Changed += h;
            _watcher.Created += h;
            _watcher.Renamed += (_, _) => Kick();
        }
        catch (Exception ex) { Log.Error("watcher", ex); }
        _pollTimer.Start();
    }

    void Kick() => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        _reloadTimer.Stop();
        _reloadTimer.Start();
    });

    void StopWatching()
    {
        _pollTimer.Stop();
        _reloadTimer.Stop();
        _watcher?.Dispose();
        _watcher = null;
    }
}
