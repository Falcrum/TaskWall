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
/// "(3) Messenger", "Telegram (12)".
/// </summary>
static class AppBadges
{
    public readonly record struct Count(int N, bool Dot)
    {
        public bool Any => N > 0 || Dot;
    }

    /// <summary>The counts read last, by the program's id in the database (an AppUserModelID or a known-folder path).</summary>
    static Dictionary<string, Count> _byId = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The same, for ids that are a program's path ("{known folder}/dir/app.exe" resolved to the full path).</summary>
    static Dictionary<string, Count> _byExe = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The counts changed (raised on the UI thread).</summary>
    public static event Action? Changed;

    /// <summary>What was read, for the dev check (--open badges): the programs and their counts.</summary>
    public static string Dump() => string.Join(" | ", _byId.Select(kv => $"{kv.Key}={kv.Value.N}{(kv.Value.Dot ? "•" : "")}")) + " ;; exe: " + string.Join(" | ", _byExe.Keys);

    static int _users;
    static FileSystemWatcher? _watcher;
    static DispatcherTimer? _debounce, _fallback;
    static bool _reading, _again;

    static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Notifications");

    /// <summary>A dock wants the counts: reading and watching start with the first one.</summary>
    public static void Start()
    {
        if (_users++ > 0) return;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); ReadSoon(); };
        _fallback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _fallback.Tick += (_, _) => ReadSoon();
        _fallback.Start();
        try
        {
            if (Directory.Exists(Folder))
            {
                _watcher = new FileSystemWatcher(Folder, "wpndatabase.db*") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size };
                _watcher.Changed += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { if (_debounce == null) return; _debounce.Stop(); _debounce.Start(); });
                _watcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception ex) { Log.Error("badges watch", ex); }
        ReadSoon();
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
    }

    /// <summary>Reads on a worker thread (the database can be big and busy); one read at a time, a change during it reads once more.</summary>
    static void ReadSoon()
    {
        if (_reading) { _again = true; return; }
        _reading = true;
        var ui = System.Windows.Application.Current?.Dispatcher;
        Task.Run(() =>
        {
            Dictionary<string, Count>? read = null;
            try { read = Read(); }
            catch (Exception ex) { Log.Error("badges read", ex); }
            ui?.BeginInvoke(() =>
            {
                _reading = false;
                if (read != null && !Same(read, _byId))
                {
                    _byId = read;
                    _byExe = read.Where(kv => kv.Key.StartsWith('{')).Select(kv => (Path: KnownPath(kv.Key), kv.Value)).Where(x => x.Path != null)
                        .GroupBy(x => x.Path!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
                    Changed?.Invoke();
                }
                if (_again && _users > 0) { _again = false; ReadSoon(); }
            });
        });
    }

    static bool Same(Dictionary<string, Count> a, Dictionary<string, Count> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>
    /// The count for one dock icon: its windows' app ids (and their packages' ids), the pinned program's file, its windows' titles.
    /// The biggest of the numbers wins (a badge of 3 and 3 notifications in the centre are the same 3 messages).
    /// </summary>
    public static Count For(IEnumerable<string?> appIds, IEnumerable<string?> exes, IEnumerable<string> titles)
    {
        int n = 0;
        bool dot = false;
        void Take(Count c) { n = Math.Max(n, c.N); dot |= c.Dot; }
        foreach (var id in appIds.Where(i => !string.IsNullOrEmpty(i)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_byId.TryGetValue(id!, out var c)) Take(c);
            // Firefox notifies as "FirefoxToast-<its id>", its windows carry "<its id>"
            foreach (var kv in _byId) if (kv.Key.EndsWith("-" + id, StringComparison.OrdinalIgnoreCase)) Take(kv.Value);
        }
        foreach (var exe in exes.Where(e => !string.IsNullOrEmpty(e)).Distinct(StringComparer.OrdinalIgnoreCase))
            if (_byExe.TryGetValue(exe!, out var c)) Take(c);
        foreach (var t in titles) n = Math.Max(n, TitleCount(t));
        return new Count(n, dot && n == 0);
    }

    static readonly Regex TitleStart = new(@"^\((\d{1,4})\)\s", RegexOptions.Compiled);
    static readonly Regex TitleEnd = new(@"\s\((\d{1,4})\)$", RegexOptions.Compiled);

    /// <summary>"(3) Messenger", "Telegram (12)": the number a window's title shows, or 0.</summary>
    public static int TitleCount(string title)
    {
        var m = TitleStart.Match(title);
        if (!m.Success) m = TitleEnd.Match(title);
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;
    }

    // ---------- the database ----------

    /// <summary>The program's id → the notifications waiting in the centre (not expired) and its badge (a number, or a dot for "alert").</summary>
    static Dictionary<string, Count> Read()
    {
        var result = new Dictionary<string, Count>(StringComparer.OrdinalIgnoreCase);
        var file = Path.Combine(Folder, "wpndatabase.db");
        if (!File.Exists(file)) return result;
        if (Sqlite.sqlite3_open_v2(Encoding.UTF8.GetBytes(file + "\0"), out var db, 0x1 /* READONLY */, IntPtr.Zero) != 0) { Sqlite.sqlite3_close_v2(db); return result; }
        try
        {
            Sqlite.sqlite3_busy_timeout(db, 300);
            // the payload only of badges (<badge value="3"/>): what a notification says is never read
            const string sql = "SELECT h.PrimaryId, n.Type, CASE WHEN n.Type = 'badge' THEN CAST(n.Payload AS TEXT) END, n.ExpiryTime " +
                               "FROM Notification n JOIN NotificationHandler h ON h.RecordId = n.HandlerId WHERE n.Type IN ('toast', 'badge')";
            if (Sqlite.sqlite3_prepare_v2(db, Encoding.UTF8.GetBytes(sql + "\0"), -1, out var st, IntPtr.Zero) != 0) return result;
            try
            {
                long now = DateTime.UtcNow.ToFileTimeUtc();
                var toasts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var badges = new Dictionary<string, Count>(StringComparer.OrdinalIgnoreCase);
                while (Sqlite.sqlite3_step(st) == 100 /* ROW */)
                {
                    var id = Text(st, 0);
                    if (string.IsNullOrEmpty(id)) continue;
                    if (Text(st, 1) == "toast")
                    {
                        long expiry = Sqlite.sqlite3_column_int64(st, 3);
                        if (expiry == 0 || expiry > now) toasts[id] = toasts.GetValueOrDefault(id) + 1;
                    }
                    else if (Badge(Text(st, 2)) is { Any: true } b) badges[id] = b;
                }
                foreach (var id in toasts.Keys.Union(badges.Keys, StringComparer.OrdinalIgnoreCase))
                {
                    var b = badges.GetValueOrDefault(id);
                    int n = Math.Max(toasts.GetValueOrDefault(id), b.N);
                    result[id] = new Count(n, b.Dot && n == 0);
                }
            }
            finally { Sqlite.sqlite3_finalize(st); }
        }
        finally { Sqlite.sqlite3_close_v2(db); }
        return result;
    }

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
        if (_packages.Count > 400) _packages.Clear();
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
