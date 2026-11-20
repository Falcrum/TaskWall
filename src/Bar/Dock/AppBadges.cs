using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace TaskWall;

/// <summary>
/// How many notifications each program has waiting – the dock's red counters (Settings → Pasek → Dock → Liczniki powiadomień).
/// Windows keeps them in the user's notification database (%LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db):
/// the notifications still in the notification centre (toasts not yet dismissed or read in the program) and the numbers programs
/// put on their taskbar icon (badges, e.g. Teams, WhatsApp, Mail). It is read only, with Windows' own SQLite (winsqlite3.dll), only
/// the program and the count – never what a notification says – and again when the database changes (a watcher, half a second
/// later; and once a minute in case a change slipped by). A window's title adds what web apps and many programs show there:
/// "(3) Messenger", "Telegram (12)", Slack's "… - 2 new items - Slack".
/// A notification left in the centre is not "new" for ever: one counts only if it came after the program was last brought to the
/// front (<see cref="MarkSeen"/>, remembered in the settings of this computer) and within the last 12 hours – programs such as
/// Claude never take their notifications back, so the centre's count alone would stay up for days.
/// </summary>
static class AppBadges
{
    public readonly record struct Count(int N, bool Dot)
    {
        public bool Any => N > 0 || Dot;
    }

    /// <summary>What the database says about one program: its badge, and when each of its notifications still in the centre came (FILETIME, sorted).</summary>
    public sealed record Info(int Badge, bool Dot, long[] Toasts);

    /// <summary>Read last, by the program's id in the database (an AppUserModelID or a known-folder path).</summary>
    static Dictionary<string, Info> _byId = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>A program's path → its id, for ids that are a path ("{known folder}/dir/app.exe" resolved to the full path).</summary>
    static Dictionary<string, string> _exeToId = new(StringComparer.OrdinalIgnoreCase);
    static string _signature = "";

    /// <summary>The longest a notification counts as new when its program was never brought to the front since (12 h in FILETIME units).</summary>
    const long MaxAge = 12L * 3600 * 10_000_000;

    /// <summary>The counts changed (raised on the UI thread).</summary>
    public static event Action? Changed;

    /// <summary>What was read, for the dev check (--open badges): the programs and their counts.</summary>
    public static string Dump() => string.Join(" | ", _byId.Select(kv => $"{kv.Key}: badge {kv.Value.Badge}{(kv.Value.Dot ? "•" : "")}, toasts {kv.Value.Toasts.Length}, unseen {Unseen(kv.Key, kv.Value)}")) + " ;; exe: " + string.Join(" | ", _exeToId.Keys);

    static int _users;
    static FileSystemWatcher? _watcher;
    static DispatcherTimer? _debounce, _fallback;
    static bool _reading, _again;
    /// <summary>When the first change not yet read came (0: none waiting).</summary>
    static long _pendingSince;

    static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Notifications");

    /// <summary>A dock wants the counts: reading and watching start with the first one.</summary>
    public static void Start()
    {
        if (_users++ > 0) return;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _pendingSince = 0; ReadSoon(); };
        _fallback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _fallback.Tick += (_, _) => ReadSoon();
        _fallback.Start();
        try
        {
            if (Directory.Exists(Folder))
            {
                _watcher = new FileSystemWatcher(Folder, "wpndatabase.db*") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size };
                _watcher.Changed += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(Changed_);
                _watcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception ex) { Log.Error("badges watch", ex); }
        ReadSoon();
    }

    /// <summary>
    /// The database changed: read half a second after the last change (a burst of writes is one read), but at the latest 2 s after
    /// the first one (a notification that updates its progress all the time must not hold back a new message's counter).
    /// </summary>
    static void Changed_()
    {
        if (_debounce == null) return;
        long now = Environment.TickCount64;
        if (_pendingSince == 0) _pendingSince = now;
        _debounce.Stop();
        if (now - _pendingSince >= 2000) { _pendingSince = 0; ReadSoon(); }
        else _debounce.Start();
    }

    /// <summary>The last dock is gone: no watcher, no timers.</summary>
    public static void Stop()
    {
        if (_users == 0 || --_users > 0) return;
        _watcher?.Dispose();
        _watcher = null;
        _debounce?.Stop();
        _fallback?.Stop();
        _debounce = _fallback = null;
        _pendingSince = 0;
        // nothing kept from the database once nobody shows it (the counters switched off)
        _byId = new(StringComparer.OrdinalIgnoreCase);
        _exeToId = new(StringComparer.OrdinalIgnoreCase);
        _signature = "";
    }

    /// <summary>Reads on a worker thread (the database can be big and busy); one read at a time, a change during it reads once more.</summary>
    static void ReadSoon()
    {
        if (_reading) { _again = true; return; }
        _reading = true;
        var ui = System.Windows.Application.Current?.Dispatcher;
        Task.Run(() =>
        {
            Dictionary<string, Info>? read = null;
            try { read = Read(); }
            catch (Exception ex) { Log.Error("badges read", ex); }
            ui?.BeginInvoke(() =>
            {
                _reading = false;
                if (read != null && Signature(read) is var sig && sig != _signature)
                {
                    _byId = read;
                    _signature = sig;
                    _exeToId = read.Keys.Where(k => k.StartsWith('{')).Select(k => (Path: KnownPath(k), Id: k)).Where(x => x.Path != null)
                        .GroupBy(x => x.Path!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
                    Changed?.Invoke();
                }
                if (_again && _users > 0) { _again = false; ReadSoon(); }
            });
        });
    }

    /// <summary>What a read found, in one string: nothing to redraw when it is the same as the last one.</summary>
    static string Signature(Dictionary<string, Info> d) =>
        string.Join("|", d.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => $"{kv.Key}:{kv.Value.Badge}:{kv.Value.Dot}:{string.Join(",", kv.Value.Toasts)}"));

    /// <summary>The programs in the database that one dock icon stands for: by its windows' and its packages' app ids, and by its file.</summary>
    static IEnumerable<string> Matched(IEnumerable<string?> appIds, IEnumerable<string?> exes)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in appIds.Where(i => !string.IsNullOrEmpty(i)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_byId.ContainsKey(id!)) found.Add(id!);
            // Firefox notifies as "FirefoxToast-<its id>", its windows carry "<its id>"
            var suffix = "-" + id;
            foreach (var key in _byId.Keys) if (key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) found.Add(key);
        }
        foreach (var exe in exes.Where(e => !string.IsNullOrEmpty(e)).Distinct(StringComparer.OrdinalIgnoreCase))
            if (_exeToId.TryGetValue(exe!, out var key)) found.Add(key);
        return found;
    }

    /// <summary>The program's notifications that came after it was last in front, and within the last 12 hours.</summary>
    static int Unseen(string id, Info info)
    {
        long now = DateTime.UtcNow.ToFileTimeUtc();
        long seen = App.Settings.DockSeen.TryGetValue(id, out var s) ? s : 0;
        long floor = Math.Max(seen, now - MaxAge);
        return info.Toasts.Count(t => t > floor);
    }

    /// <summary>
    /// One of the icon's windows came to the front: what its program had waiting counts as seen. True when that changed
    /// something (the settings are then to be saved); the programs no longer in the database are forgotten at the same time.
    /// </summary>
    public static bool MarkSeen(IEnumerable<string?> appIds, IEnumerable<string?> exes)
    {
        bool changed = false;
        long now = DateTime.UtcNow.ToFileTimeUtc();
        foreach (var id in Matched(appIds, exes))
            if (Unseen(id, _byId[id]) > 0) { App.Settings.DockSeen[id] = now; changed = true; }
        if (changed)
            foreach (var gone in App.Settings.DockSeen.Keys.Where(k => !_byId.ContainsKey(k)).ToList()) App.Settings.DockSeen.Remove(gone);
        return changed;
    }

    /// <summary>
    /// The count for one dock icon: its windows' app ids (and their packages' ids), the pinned program's file, its windows' titles.
    /// The biggest of the numbers wins (a badge of 3 and 3 notifications in the centre are the same 3 messages).
    /// </summary>
    public static Count For(IEnumerable<string?> appIds, IEnumerable<string?> exes, IEnumerable<(string Title, string? Exe)> titles)
    {
        int n = 0;
        bool dot = false;
        foreach (var id in Matched(appIds, exes))
        {
            var info = _byId[id];
            n = Math.Max(n, Math.Max(info.Badge, Unseen(id, info)));
            dot |= info.Dot;
        }
        foreach (var (t, exe) in titles)
            if (!string.Equals(Path.GetFileName(exe), "explorer.exe", StringComparison.OrdinalIgnoreCase)) n = Math.Max(n, TitleCount(t)); // folder names ("Raport (2)")
        return new Count(n, dot && n == 0);
    }

    /// <summary>"(3) Messenger", "Telegram (12)", "… - 2 new items - Slack": the number a window's title shows, or 0 (see <see cref="BadgeLogic"/>).</summary>
    public static int TitleCount(string title) => BadgeLogic.TitleCount(title);

    // ---------- the database ----------

    /// <summary>The program's id → its badge (a number, or a dot for "alert") and when its notifications still in the centre (not expired) came.</summary>
    static Dictionary<string, Info>? Read()
    {
        var result = new Dictionary<string, Info>(StringComparer.OrdinalIgnoreCase);
        var file = Path.Combine(Folder, "wpndatabase.db");
        if (!File.Exists(file)) return result;
        if (Sqlite.sqlite3_open_v2(Encoding.UTF8.GetBytes(file + "\0"), out var db, 0x1 /* READONLY */, IntPtr.Zero) != 0) { Sqlite.sqlite3_close_v2(db); return null; }
        try
        {
            Sqlite.sqlite3_busy_timeout(db, 300);
            // the payload only of badges (<badge value="3"/>): what a notification says is never read
            const string sql = "SELECT h.PrimaryId, n.Type, CASE WHEN n.Type = 'badge' THEN CAST(n.Payload AS TEXT) END, n.ExpiryTime, n.ArrivalTime " +
                               "FROM Notification n JOIN NotificationHandler h ON h.RecordId = n.HandlerId WHERE n.Type IN ('toast', 'badge')";
            if (Sqlite.sqlite3_prepare_v2(db, Encoding.UTF8.GetBytes(sql + "\0"), -1, out var st, IntPtr.Zero) is var rc and not 0)
            {
                // the database looks different (another Windows build): said once, the counters stay as they were
                if (!_schemaLogged) { _schemaLogged = true; Log.Error($"badges: the notification database can't be read (prepare {rc})"); }
                return null;
            }
            try
            {
                long now = DateTime.UtcNow.ToFileTimeUtc();
                var toasts = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
                var badges = new Dictionary<string, Count>(StringComparer.OrdinalIgnoreCase);
                int step;
                while ((step = Sqlite.sqlite3_step(st)) == 100 /* ROW */)
                {
                    var id = Text(st, 0);
                    if (string.IsNullOrEmpty(id)) continue;
                    if (Text(st, 1) == "toast")
                    {
                        long expiry = Sqlite.sqlite3_column_int64(st, 3);
                        if (expiry == 0 || expiry > now)
                        {
                            if (!toasts.TryGetValue(id, out var list)) toasts[id] = list = new();
                            list.Add(Sqlite.sqlite3_column_int64(st, 4));
                        }
                    }
                    else if (Badge(Text(st, 2)) is { Any: true } b) badges[id] = b;
                }
                // busy (Windows writing a checkpoint), an I/O error: not "no notifications" – the next change or minute reads again
                if (step != 101 /* DONE */) return null;
                foreach (var id in toasts.Keys.Union(badges.Keys, StringComparer.OrdinalIgnoreCase))
                {
                    var b = badges.GetValueOrDefault(id);
                    var arrivals = toasts.TryGetValue(id, out var l) ? l.Order().ToArray() : Array.Empty<long>();
                    result[id] = new Info(b.N, b.Dot && b.N == 0, arrivals);
                }
            }
            finally { Sqlite.sqlite3_finalize(st); }
        }
        finally { Sqlite.sqlite3_close_v2(db); }
        return result;
    }

    static bool _schemaLogged;

    static readonly Regex BadgeValue = new(@"value\s*=\s*""([^""]*)""", RegexOptions.Compiled);

    /// <summary>A badge's value: a number, or a glyph – the ones that ask for attention show as a dot, the status ones (busy, away …) not at all.</summary>
    static Count Badge(string? xml)
    {
        if (xml == null || BadgeValue.Match(xml) is not { Success: true } m) return default;
        var v = m.Groups[1].Value;
        if (int.TryParse(v, out var n)) return new Count(Math.Max(0, n), false);
        return v is "alert" or "attention" or "newMessage" or "error" or "alarm" ? new Count(0, true) : default;
    }

    static string? Text(IntPtr st, int col) => Sqlite.sqlite3_column_text(st, col) is var p && p != IntPtr.Zero ? Marshal.PtrToStringUTF8(p) : null;

    /// <summary>"{6D809377-…}/ASUS/App.exe" → "C:\Program Files\ASUS\App.exe" (Windows' id of a program without an AppUserModelID).</summary>
    static string? KnownPath(string id)
    {
        int close = id.IndexOf('}');
        if (close < 0 || !Guid.TryParse(id[..(close + 1)], out var folder)) return null;
        try
        {
            if (SHGetKnownFolderPath(folder, 0, IntPtr.Zero, out var p) != 0) return null;
            try { return Path.Combine(Marshal.PtrToStringUni(p)!, id[(close + 1)..].TrimStart('/', '\\').Replace('/', '\\')); }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        catch { return null; }
    }

    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    /// <summary>The package's AppUserModelID of a process (a Store program such as WhatsApp or Teams), or null.</summary>
    public static string? PackageAppId(uint pid)
    {
        if (_packages.TryGetValue(pid, out var known)) return known;
        string? id = null;
        var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                uint len = 256;
                var sb = new StringBuilder((int)len);
                if (GetApplicationUserModelId(h, ref len, sb) == 0) id = sb.ToString();
            }
            finally { CloseHandle(h); }
        }
        return _packages[pid] = id;
    }

    static readonly Dictionary<uint, string?> _packages = new();

    /// <summary>
    /// Forgets the processes that have no window any more (the dock's refresh after windows came and went): a process id Windows
    /// gives to a new program must not keep the old one's package (WhatsApp's count on another program's icon).
    /// </summary>
    public static void KeepPackages(IEnumerable<uint> pids)
    {
        var alive = pids.ToHashSet();
        foreach (var pid in _packages.Keys.Where(k => !alive.Contains(k)).ToList()) _packages.Remove(pid);
    }

    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder id);

    /// <summary>Windows' own SQLite (System32\winsqlite3.dll) – nothing to ship.</summary>
    static class Sqlite
    {
        const string Dll = "winsqlite3.dll";
        [DllImport(Dll)] public static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Dll)] public static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Dll)] public static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport(Dll)] public static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr stmt, IntPtr tail);
        [DllImport(Dll)] public static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Dll)] public static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Dll)] public static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
        [DllImport(Dll)] public static extern long sqlite3_column_int64(IntPtr stmt, int col);
    }
}
