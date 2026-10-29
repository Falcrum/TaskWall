using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TaskWall;

public partial class App : Application
{
    public static App Instance => (App)Current;
    public static AppSettings Settings { get; private set; } = new();
    /// <summary>Data of the active account (Praca / Prywatne).</summary>
    public static BoardStore Store { get; private set; } = null!;
    /// <summary>Store of any account (opened on demand, e.g. to edit its categories in Settings).</summary>
    public static BoardStore StoreFor(string layer) => Instance.OpenLayer(layer);
    static readonly System.Collections.Generic.Dictionary<string, BoardStore> Stores = new();
    public static string LayerName(string layer) => AccountName(layer);
    /// <summary>The account's own name; the default names ("Praca" / "Prywatne") follow the UI language.</summary>
    public static string AccountName(string layer) => Settings.AccountFor(layer).Name is { Length: > 0 } n
        ? (n is "Praca" or "Prywatne" ? L.T(n) : n)
        : L.T(layer == "private" ? "Prywatne" : "Praca");
    public static BoardWindow? Board { get; private set; }
    /// <summary>The board on the second screen (Settings → Ekran) with a view of its own; null when there is none.</summary>
    public static BoardWindow? Second => Instance._second;
    BoardWindow? _second;
    /// <summary>Both boards (the main one first).</summary>
    public static System.Collections.Generic.List<BoardWindow> Boards => new[] { Board, Instance._second }.OfType<BoardWindow>().ToList();

    const int HotkeyId = 0xD35;
    public bool HotkeyTaken { get; private set; }
    static readonly Color DefaultAccent = Color.FromRgb(0x5B, 0x8C, 0xFF);

    ClockWindow? _clock;
    public static ClockWindow? Clock => Instance._clock;
    SettingsWindow? _settingsWindow;
    TrayIcon? _tray;
    Mutex? _mutex;
    bool _importOpen;
    Color? _autoAccent;
    Color? _surfaceTint;
    bool _glassReady;
    string _cornerKey = "";
    HwndSource? _hotkeySource;
    readonly DispatcherTimer _glassTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly DispatcherTimer _trimTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly DispatcherTimer _calendarTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    readonly DispatcherTimer _notionTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    bool _started;
    readonly DispatcherTimer _folderRetry = new() { Interval = TimeSpan.FromSeconds(10) };
    readonly System.Collections.Generic.Dictionary<string, string> _pendingFolders = new(); // layer -> folder not reachable at start
    bool _lastAutostart;
    readonly DispatcherTimer _applySoon = new() { Interval = TimeSpan.FromMilliseconds(150) };

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (Dev.Arg("--data") == null) SettingsStore.MigrateFromDeskWall();
        try { var boot = SettingsStore.Load(); SharedSettings.Pull(boot); L.Set(boot.Language); } catch { /* Polish */ }
        // installer switches (TaskWall-Setup.exe, --install, --uninstall) run before anything else
        try { if (Installer.HandleCommandLine()) { Shutdown(); return; } }
        catch (Exception ex) { Log.Error("installer", ex); Shutdown(); return; }
        // started by the installer / "Zainstaluj w systemie": let the old copy exit first
        if (Dev.Arg("--wait-for") is { } pidText && int.TryParse(pidText, out var pid))
            try { System.Diagnostics.Process.GetProcessById(pid).WaitForExit(15000); } catch { /* already gone */ }
        // dev runs (--data) live next to the installed copy
        _mutex = new Mutex(true, Dev.Arg("--data") != null ? @"Local\TaskWall.Dev" : @"Local\TaskWall.SingleInstance", out bool first);
        if (!first) { Shutdown(); return; }

        // after start-up a stray exception is logged and the app keeps running; during start-up it must not leave
        // an invisible process holding the single-instance mutex
        DispatcherUnhandledException += (_, a) =>
        {
            Log.Error("unhandled", a.Exception);
            if (_started) { a.Handled = true; return; }
            a.Handled = true;
            MessageBox.Show(L.T("TaskWall nie mógł się uruchomić:") + "\n" + a.Exception.Message + "\n\n" + L.T("Szczegóły:") + " %APPDATA%\\TaskWall\\error.log", "TaskWall", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        };
        try { Start(); _started = true; }
        catch (Exception ex)
        {
            Log.Error("startup", ex);
            MessageBox.Show(L.T("TaskWall nie mógł się uruchomić:") + "\n" + ex.Message + "\n\n" + L.T("Szczegóły:") + " %APPDATA%\\TaskWall\\error.log", "TaskWall", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    void Start()
    {

        Settings = SettingsStore.Load();
        DaysOff.Leave = d => Stores.Values.Any(s => s.IsLeave(d)); // BŚU / leave in any open account
        DaysOff.Company = d => Stores.Values.Select(s => s.CompanyDay(Days.Key(d))).FirstOrDefault(x => x != null); // the company's days off
        SharedSettings.Pull(Settings); // settings saved on another computer (language, theme, weather, Notion…)
        SharedSettings.Pulled += _ => Dispatcher.BeginInvoke(ApplySharedSettings);
        Th.Apply(Resources, Settings.Theme); // before any window is built
        if (string.IsNullOrWhiteSpace(Settings.DataFolder)) Settings.DataFolder = SettingsStore.DefaultDataFolder();
        SettingsStore.Save(Settings);
        ApplyTheme();

        if (Dev.Arg("--selftest") is { } testOut) { Dev.SelfTest(testOut); Shutdown(); return; }

        if (Settings.Layer != "private") Settings.Layer = "work";
        Store = OpenLayer(Settings.Layer);
        CalendarService.Layer = Settings.Layer;

        CalendarService.LoadCache();
        Board = new BoardWindow();
        _clock = new ClockWindow { KeepAbove = Board };
        CalendarService.Changed += () => { foreach (var b in Boards) b.OnExternalChange(quiet: true); };

        SetupTray();
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(ApplySettings);
        // no polling: the wallpaper is read at start-up, after settings changes, on "Odśwież tapetę"
        // and when Windows itself reports a new wallpaper (free event)
        SystemEvents.UserPreferenceChanged += (_, a) =>
        {
            // Desktop also covers work-area changes (taskbar moved/resized), on any monitor
            if (a.Category == UserPreferenceCategory.Desktop) Dispatcher.BeginInvoke(() => { RefreshWallpaper(); LayoutWindows(); });
        };
        SystemParameters.StaticPropertyChanged += (_, a) =>
        {
            if (a.PropertyName == nameof(SystemParameters.WorkArea)) Dispatcher.BeginInvoke(LayoutWindows);
        };
        _glassTimer.Tick += (_, _) => { _glassTimer.Stop(); RefreshGlass(); };
        _applySoon.Tick += (_, _) => { _applySoon.Stop(); ApplySettings(); };
        // a change on one board shows on the other (second screen) a moment later
        BoardStore.Dirtied += () => { if (_second == null) return; if (_dirtyAt == default) _dirtyAt = DateTime.UtcNow; _boardsSync.Stop(); _boardsSync.Start(); };
        _boardsSync.Tick += (_, _) =>
        {
            _boardsSync.Stop();
            foreach (var b in Boards.Where(b => b.BuiltAt < _dirtyAt)) b.OnExternalChange(quiet: true);
            _dirtyAt = default;
        };
        _folderRetry.Tick += (_, _) => RetryPendingFolders();
        _trimTimer.Tick += (_, _) => { _trimTimer.Stop(); TrimMemory(); };
        _calendarTimer.Tick += (_, _) => RefreshCalendars();
        _calendarTimer.Start();

        // start-up animation: board rises in, clock drops down
        Anim.Enter(Board.FrameElement, 0, 22, 520);
        Anim.Enter(_clock.FrameElement, 0, -30, 520, 150);
        ApplySettings();
        if (Settings.AutoRollover && IsWorkingDay(DateTime.Today)) Board.RollOverdue(); // never onto a weekend or a day off
        RefreshCalendars();
        // alarms of the other account ring too: open its data once the board is up (only when its drive is there)
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var a in Settings.Accounts.Where(a => !Stores.ContainsKey(a.Id)))
            {
                var root = Path.GetPathRoot(LayerFolder(a.Id));
                if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                    try { OpenLayer(a.Id); } catch (Exception ex) { Log.Error("open account " + a.Id, ex); }
            }
            AlarmService.Changed += () => { foreach (var b in Boards) b.OnExternalChange(quiet: true); };
            AlarmService.Start();
            Updater.Start();
            if (Dev.Arg("--data") == null) ReportWindow.ShowWeeklyIfDue();
            WeatherService.Changed += () => { foreach (var b in Boards) b.OnExternalChange(quiet: true); };
            WeatherService.Start();
            // Notion: each account with a token syncs on its own interval (first run right after start-up)
            _notionTimer.Tick += (_, _) => { PullSharedSettings(); NotionSync.Tick(); FullSync(); };
            NotionSync.StateChanged += () => { foreach (var b in Boards) b.UpdateSyncIcon(); };
            _notionTimer.Start();
            NotionSync.Tick(startup: true); // always on start, then on each account's interval
            FullSync(startup: true);
        }, DispatcherPriority.ApplicationIdle);
        Dev.AfterStartup();
    }

    /// <summary>Starts a fresh copy (it waits for this one to exit) – e.g. after changing the language.</summary>
    public void Restart()
    {
        foreach (var s in Stores.Values) try { s.SaveNow(); } catch { }
        var all = Environment.GetCommandLineArgs().Skip(1).ToList();
        int w = all.IndexOf("--wait-for");
        if (w >= 0) all.RemoveRange(w, Math.Min(2, all.Count - w));
        var args = string.Join(" ", all.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        if (Environment.ProcessPath is { } exe)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, $"{args} --wait-for {Environment.ProcessId}") { UseShellExecute = false });
        Shutdown();
    }

    /// <summary>Hides the whole board (the clock follows its own setting); the tray icon / hotkey bring it back.</summary>
    public void SetBoardHidden(bool hidden)
    {
        if (Settings.BoardHidden == hidden || Board == null) return;
        Settings.BoardHidden = hidden;
        SettingsStore.Save(Settings);
        if (hidden)
        {
            if (GlassWindow.IsPeeking) GlassWindow.Peek(false);
            foreach (var b in Boards) // both screens
            {
                b.EndEditing();
                var frame = b.FrameElement;
                var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(Settings.Animations ? 200 : 1));
                fade.Completed += (_, _) =>
                {
                    b.Hide();
                    frame.BeginAnimation(UIElement.OpacityProperty, null);
                    frame.Opacity = 1;
                    ScheduleTrim();
                };
                frame.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            _tray?.Balloon("TaskWall", L.F("Tablica ukryta. Pokażesz ją z menu ikony w zasobniku albo skrótem {0}.", Settings.Hotkey));
        }
        else
        {
            LayoutWindows();
            foreach (var b in Boards) Anim.Enter(b.FrameElement, 0, 22, 420);
        }
    }


    public bool NotionRunning => NotionSync.AnyRunning;
    internal Task<NotionSync.Result> RunNotion(string layer) => NotionSync.Run(layer);

    readonly System.Collections.Generic.Dictionary<string, DateTime> _lastFullSync = new();

    /// <summary>
    /// Board data: besides the file watcher and the 30 s check, a full sync (save + read + conflict copies) on start and
    /// on the same interval as the account's Notion sync (10 min when Notion isn't set up).
    /// </summary>
    void FullSync(bool startup = false)
    {
        foreach (var (layer, store) in OpenStores())
        {
            var n = Settings.AccountFor(layer).Notion;
            int minutes = n.Configured && n.Auto ? Math.Max(2, n.Minutes) : 10;
            if (!startup && _lastFullSync.TryGetValue(layer, out var last) && DateTime.Now - last < TimeSpan.FromMinutes(minutes)) continue;
            _lastFullSync[layer] = DateTime.Now;
            store.SyncNow();
        }
    }
    /// <summary>Settings changed on another computer (checked every minute).</summary>
    void PullSharedSettings()
    {
        if (SharedSettings.Pull(Settings).Length > 0) ApplySharedSettings();
    }

    /// <summary>Language and theme take effect after a restart (like when changed here); everything else right away.</summary>
    void ApplySharedSettings()
    {
        SettingsStore.Save(Settings);
        WeatherService.Refresh();
        ApplySettings();
        foreach (var b in Boards) b.UpdateSyncIcon();
    }

    /// <summary>Accounts whose data is loaded (for alarms).</summary>
    public static System.Collections.Generic.IEnumerable<(string Layer, BoardStore Store)> OpenStores() =>
        Stores.Select(kv => (kv.Key, kv.Value)).ToList();

    /// <summary>Sliders: apply at most every 150 ms.</summary>
    public void ApplySettingsSoon()
    {
        _applySoon.Stop();
        _applySoon.Start();
    }

    /// <summary>A cloud folder that wasn't reachable at start (e.g. Google Drive mounting after logon) is picked up later.</summary>
    void RetryPendingFolders()
    {
        foreach (var (layer, saved) in _pendingFolders.ToList())
        {
            var folder = SettingsStore.ResolveCloudFolder(saved);
            if (!FolderReady(folder)) continue;
            Settings.AccountFor(layer).Folder = folder;
            try
            {
                Stores[layer].SwitchFolder(folder); // merges what was written to the local fallback meanwhile
                Stores[layer].Offline = null;
                if (Settings.StretchActive) Stores[layer].StretchActive(DateTime.Today); // now on the real data: carry yesterday's work on
                Stores[layer].FillMissing(Settings.AccountFor(layer).Notion.Name);
                Stores[layer].MirrorFolder = SettingsStore.CacheFolder(layer);
                Stores[layer].WriteMirror(null);
                if (layer == "work") PullSharedSettings();
                _pendingFolders.Remove(layer);
                foreach (var b in Boards) b.UpdateSyncIcon();
                Log.Error($"folder {folder} is reachable again – data merged back");
            }
            catch (Exception ex) { Log.Error("retry folder " + folder, ex); }
        }
        if (_pendingFolders.Count == 0) _folderRetry.Stop();
    }

    /// <summary>Folder of a layer (dev runs: --data, private layer in its "private" sub-folder).</summary>
    public static string LayerFolder(string layer)
    {
        if (Dev.Arg("--data") is { } dev) return layer == "private" ? Path.Combine(dev, "private") : dev;
        return Settings.AccountFor(layer).Folder;
    }

    BoardStore OpenLayer(string layer)
    {
        if (Stores.TryGetValue(layer, out var s)) return s;
        s = new BoardStore(layer);
        var folder = LayerFolder(layer);
        bool dev = Dev.Arg("--data") != null;
        if (!dev && SettingsStore.ResolveCloudFolder(folder) is var resolved && resolved != folder)
            folder = Settings.AccountFor(layer).Folder = resolved;
        try
        {
            // never wait at start: a cloud drive that isn't mounted yet (logon) means this computer's copy for now
            if (!dev && !FolderReady(folder)) throw new IOException("cloud folder not reachable yet: " + folder);
            bool fresh = !File.Exists(Path.Combine(folder, BoardStore.FileName));
            s.Open(folder);
            // this computer's copy always goes in (newer wins per task): edits made while the drive was away survive a
            // restart, and an empty cloud folder (new, or the wrong drive) never means an empty board
            if (!dev) MergeLocalCopy(s, layer, folder, announce: fresh);
            if (!dev) { s.MirrorFolder = SettingsStore.CacheFolder(layer); s.WriteMirror(null); }
            if (!dev) MergeLegacyOffline(s, layer);
        }
        catch (Exception ex)
        {
            Log.Error("open data folder " + folder, ex);
            s.Open(SettingsStore.CacheFolder(layer)); // the last state this computer saw
            s.Offline = folder;
            _pendingFolders[layer] = folder; // checked quietly every few seconds; once there, merged into it
            _folderRetry.Start();
        }
        s.ExternalChange += () => { if (ReferenceEquals(s, Store)) foreach (var b in Boards) b.OnExternalChange(); };
        s.SyncStateChanged += () => { foreach (var b in Boards) b.UpdateSyncIcon(); };
        Stores[layer] = s;
        if (Settings.StretchActive) s.StretchActive(DateTime.Today); // in progress / in review: on to today (after it is in Stores: its BŚU days count)
        s.FillMissing(Settings.AccountFor(layer).Notion.Name); // older tasks get what newer ones have (a project, an "added" date)
        return s;
    }

    static void MergeLocalCopy(BoardStore s, string layer, string folder, bool announce)
    {
        var copy = Path.Combine(SettingsStore.CacheFolder(layer), BoardStore.FileName);
        try
        {
            if (!File.Exists(copy) || new FileInfo(copy).Length < 200 || !s.MergeFile(copy) || !announce) return;
            Log.Error($"{folder} had no board.json – this computer's copy merged into it");
            Instance.Dispatcher.BeginInvoke(() => Instance._tray?.Balloon("TaskWall", L.F("Folder „{0}” nie miał danych tablicy – zapisałem w nim kopię z tego komputera.", folder)));
        }
        catch (Exception ex) { Log.Error("merge local copy", ex); }
    }

    /// <summary>Up to 0.15.0 offline edits went to %APPDATA%TaskWall<account>: merged once, the folder is then renamed.</summary>
    static void MergeLegacyOffline(BoardStore s, string layer)
    {
        var legacy = Path.Combine(SettingsStore.Dir, layer);
        try
        {
            if (!File.Exists(Path.Combine(legacy, BoardStore.FileName)) || !s.MergeFile(Path.Combine(legacy, BoardStore.FileName))) return;
            Directory.Move(legacy, legacy + "-merged-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Log.Error($"offline data from {legacy} merged into {s.Folder}");
        }
        catch (Exception ex) { Log.Error("merge old offline data", ex); }
    }

    /// <summary>The folder's drive is there (for Google Drive: Google Drive itself – the same letter may be a local disk).</summary>
    static bool FolderReady(string folder)
    {
        var root = Path.GetPathRoot(folder);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return false;
        return !SettingsStore.IsGoogleDrivePath(folder) || SettingsStore.IsOnGoogleDrive(folder);
    }

    /// <summary>Praca ⇄ Prywatne.</summary>
    public void SwitchLayer(string layer)
    {
        if (layer == Settings.Layer || Board == null) return;
        Board.EndEditing(); // whatever is being typed belongs to the current layer
        Store.SaveNow();
        Settings.Layer = layer;
        Store = OpenLayer(layer);
        CalendarService.Layer = layer;
        SettingsStore.Save(Settings);
        foreach (var b in Boards) b.OnLayerChanged();
    }

    /// <summary>Settings changed the folder of a layer: move that layer's data there (merged).</summary>
    public void ChangeLayerFolder(string layer, string folder)
    {
        OpenLayer(layer).SwitchFolder(folder);
        // a folder chosen by hand replaces the one that was waited for
        _pendingFolders.Remove(layer);
        if (_pendingFolders.Count == 0) _folderRetry.Stop();
        var st = OpenLayer(layer);
        st.Offline = null;
        if (Dev.Arg("--data") == null) { st.MirrorFolder = SettingsStore.CacheFolder(layer); st.WriteMirror(null); }
        Settings.AccountFor(layer).Folder = folder;
        if (layer == "private") Settings.PrivateFolder = folder; else Settings.DataFolder = folder;
        ApplySettings();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        foreach (var s in Stores.Values)
            try { s.SaveNow(); } catch (Exception ex) { Log.Error("save on exit", ex); }
        _tray?.Dispose();
        base.OnExit(e);
    }

    public void ApplySettings()
    {
        ApplyTheme();
        SettingsStore.Save(Settings);
        SetAutostart(Settings.StartWithWindows);
        if (Board == null || _clock == null) return;

        UpdateSecond();
        foreach (var b in Boards)
        {
            b.ApplyScale(Settings.UiScale);
            b.Rebuild();
        }
        _clock.SetScale(Settings.UiScale);
        _clock.ApplySettings();
        LayoutWindows();
        RegisterHotkey();
        ScheduleGlass();
    }

    /// <summary>A working day: Mon–Fri that isn't a holiday, company day off or leave – or a day with overtime set by hand.</summary>
    bool IsWorkingDay(DateTime d) => (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !DaysOff.Is(d))
        || Stores.Values.Any(s => s.Overtime(Days.Key(d)).Count > 0 || s.HasOwnHours(Days.Key(d)));

    public void OnNewDay()
    {
        if (Settings.StretchActive) foreach (var st in Stores.Values) st.StretchActive(DateTime.Today);
        if (Settings.AutoRollover && IsWorkingDay(DateTime.Today)) Board?.RollOverdue();
    }

    public async void RefreshCalendars()
    {
        // every account has its own calendars; events are tagged with the account and shown only there
        var feeds = Settings.Accounts.SelectMany(a => a.Calendars.Select(f => new CalendarFeed { Name = f.Name, Url = f.Url, Kind = f.Kind, Enabled = f.Enabled, Layer = a.Id })).ToList();
        try { await CalendarService.RefreshAsync(feeds); }
        catch (Exception ex) { Log.Error("calendars", ex); }
        ScheduleTrim();
    }

    /// <summary>
    /// The fine background pattern over the glass: "dots" (a dot every 16 px), "grid" (thin lines every 16 px, a little
    /// stronger every 80 px – like drawing paper), or none. Very faint, so the tasks stay what you look at.
    /// </summary>
    static Brush Pattern(string? kind, double visibility = 1, double scale = 1)
    {
        if (kind is not ("dots" or "grid" or "fabric")) return Brushes.Transparent;
        var g = new DrawingGroup();
        byte V(byte a) => (byte)Math.Clamp(a * visibility, 0, 255); // the visibility slider: 0–300 %
        scale = Math.Clamp(scale, 0.25, 1); // larger than designed only blurs the pattern
        if (kind == "fabric")
        {
            // a barely visible checkerboard turned by 45° – reads like woven fabric, not like a pattern
            const double cell = 5;
            var ink = new SolidColorBrush(Th.Ink(V(Th.Light ? (byte)0x0B : (byte)0x07)));
            g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, cell * 2, cell * 2))));
            g.Children.Add(new GeometryDrawing(ink, null, new RectangleGeometry(new Rect(0, 0, cell, cell))));
            g.Children.Add(new GeometryDrawing(ink, null, new RectangleGeometry(new Rect(cell, cell, cell, cell))));
            return new DrawingBrush(g)
            {
                TileMode = TileMode.Tile, Viewport = new Rect(0, 0, (cell * 2) * scale, (cell * 2) * scale), ViewportUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, cell * 2, cell * 2), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill,
                Transform = new RotateTransform(45),
            };
        }
        if (kind == "dots")
        {
            var dot = new SolidColorBrush(Th.Ink(V(Th.Light ? (byte)0x30 : (byte)0x26)));
            g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
            g.Children.Add(new GeometryDrawing(dot, null, new EllipseGeometry(new Point(8, 8), 0.9, 0.9)));
            return new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, (16) * scale, (16) * scale), ViewportUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 16, 16), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        }
        var minor = new Pen(new SolidColorBrush(Th.Ink(V(Th.Light ? (byte)0x14 : (byte)0x0E))), 0.6);
        var major = new Pen(new SolidColorBrush(Th.Ink(V(Th.Light ? (byte)0x24 : (byte)0x1A))), 0.8);
        g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 80, 80))));
        for (int i = 1; i < 5; i++)
        {
            g.Children.Add(new GeometryDrawing(null, minor, new LineGeometry(new Point(i * 16 + 0.5, 0), new Point(i * 16 + 0.5, 80))));
            g.Children.Add(new GeometryDrawing(null, minor, new LineGeometry(new Point(0, i * 16 + 0.5), new Point(80, i * 16 + 0.5))));
        }
        g.Children.Add(new GeometryDrawing(null, major, new LineGeometry(new Point(0.5, 0), new Point(0.5, 80))));
        g.Children.Add(new GeometryDrawing(null, major, new LineGeometry(new Point(0, 0.5), new Point(80, 0.5))));
        return new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, (80) * scale, (80) * scale), ViewportUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, 80, 80), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
    }

    void ApplyTheme()
    {
        Color accent = DefaultAccent;
        if (Settings.Accent == "auto") accent = _autoAccent ?? DefaultAccent;
        else
        {
            try { accent = (Color)ColorConverter.ConvertFromString(Settings.Accent); }
            catch { }
        }
        Resources["AccentBrush"] = new SolidColorBrush(accent);
        Resources["AccentSoft"] = new SolidColorBrush(Color.FromArgb(0x38, accent.R, accent.G, accent.B));
        byte a = (byte)Math.Clamp(Settings.TintOpacity * 255, Settings.Blur ? 0 : 40, 255);
        // no transparency at all: a solid surface in the wallpaper's hue (like the dialogs), nothing shows through
        Resources["GlassTint"] = new SolidColorBrush(!Settings.Opaque ? Th.Glass(a)
            : _surfaceTint is { } st ? (Th.Light ? Wallpaper.Surface(st, 0.95, 0.05) : Wallpaper.Surface(st, 0.095, 0.28)) : Th.Glass(255));
        Resources["GlassPattern"] = Pattern(Settings.Pattern, Settings.PatternVisibility, Settings.PatternScale);
        // menus, tool tips and dialogs: the hue of the wallpaper behind the board, so they sit with the glass
        if (_surfaceTint is { } tint)
        {
            Resources["MenuBg"] = new SolidColorBrush(Th.Light ? Wallpaper.Surface(tint, 0.975, 0.06) : Wallpaper.Surface(tint, 0.13, 0.32));
            Resources["DialogBg"] = new SolidColorBrush(Th.Light ? Wallpaper.Surface(tint, 0.955, 0.06) : Wallpaper.Surface(tint, 0.105, 0.30));
            Resources["CardBg"] = new SolidColorBrush(Th.Light ? Wallpaper.Surface(tint, 0.99, 0.03) : Wallpaper.Surface(tint, 0.15, 0.28));
        }
    }

    // ---------- placement ----------

    public void LayoutWindows()
    {
        if (Board == null || _clock == null) return;
        var mon = Monitors.Pick(Settings.Monitor);
        double s = mon.Scale, ui = Settings.UiScale;
        var wa = mon.Work;
        int P(double dip) => (int)Math.Round(dip * s);

        // the board as set, or (the button next to "hide") the whole screen under the top bar
        bool max = Settings.BoardMax;
        // one gap everywhere: under the bar, above the taskbar, at the sides of a whole-screen board
        int edge = P(Math.Clamp(Settings.Gap, 0, 40)); // Settings → Ekran → Odstęp
        int bw = max ? wa.Width - 2 * edge : (int)Math.Round(wa.Width * Math.Clamp(Settings.WidthPercent, 30, 100) / 100);
        double weekH = wa.Height / s * Math.Clamp(Settings.WeekHeightPercent, 5, 100) / 100;
        int barH = P(ClockWindow.BarHeight * ui);
        int minTop = wa.Y + (Settings.ShowClock ? barH : 0) + edge;
        int bh = max || Settings.WeekHeightPercent >= 99 ? wa.Y + wa.Height - edge - minTop // "od zegara do paska zadań": all of it
            : Math.Min(P(BoardWindow.ChromeHeight * ui + Board.HeightInWeeks * weekH), wa.Y + wa.Height - minTop - edge);
        int maxTop = Math.Max(minTop, wa.Y + wa.Height - bh - edge);
        int top = max ? minTop : minTop + (int)Math.Round((maxTop - minTop) * Math.Clamp(Settings.VerticalPercent, 0, 100) / 100);
        int left = wa.X + (wa.Width - bw) / 2;

        Board.PlacePhysical(left, top, bw, bh, s);
        if (Settings.ShowClock)
        {
            // the bar as wide as the board, hanging from the top edge; the month opens under the date
            _clock.PlacePhysical(left, wa.Y, bw, barH, s);
            int cw = P(CalendarWindow.TabWidth * ui);
            _clock.Calendar.PlacePhysical(wa.X + (wa.Width - cw) / 2, wa.Y + barH + P(6), cw, P(CalendarWindow.CalendarHeight * ui), s);
        }
        if (_second != null && SecondScreen() is { } m2)
        {
            // the second screen: the whole of it
            int e2 = (int)Math.Round(Math.Clamp(Settings.Gap, 0, 40) * m2.Scale);
            _second.PlacePhysical(m2.Work.X + e2, m2.Work.Y + e2, m2.Work.Width - 2 * e2, m2.Work.Height - 2 * e2, m2.Scale);
        }

        // first time: paint the glass before showing, so the board never flashes empty
        if (!_glassReady) { RefreshGlass(); _glassReady = true; }
        else ScheduleGlass();

        foreach (var b in Boards)
            if (Settings.BoardHidden) { if (b.IsVisible) b.Hide(); }
            else if (!b.IsVisible) b.Show();
        if (Settings.ShowClock) { if (!_clock.IsVisible) _clock.Show(); }
        else
        {
            if (_clock.IsVisible) _clock.Hide();
            if (_clock.Calendar.IsVisible) _clock.Calendar.Hide();
        }
    }

    /// <summary>The second screen chosen in Settings, when it is connected and not the board's own screen.</summary>
    static MonitorInfo? SecondScreen()
    {
        if (string.IsNullOrEmpty(Settings.SecondMonitor)) return null;
        var main = Monitors.Pick(Settings.Monitor);
        return Monitors.All().FirstOrDefault(m => m.Device == Settings.SecondMonitor && m.Device != main.Device);
    }

    /// <summary>Opens or closes the board of the second screen (a setting, or the screen plugged in / out).</summary>
    void UpdateSecond()
    {
        if (Board == null) return;
        bool want = SecondScreen() != null;
        if (want && _second == null)
        {
            _second = new BoardWindow(second: true);
            _cornerKey2 = "";
            Anim.Enter(_second.FrameElement, 0, 22, 520);
        }
        else if (!want && _second != null)
        {
            var old = _second;
            _second = null;
            old.EndEditing();
            old.Close();
        }
    }

    string _cornerKey2 = "";
    readonly DispatcherTimer _boardsSync = new() { Interval = TimeSpan.FromMilliseconds(150) };
    DateTime _dirtyAt;

    /// <summary>A board's frosted glass and its sharp corners, from the wallpaper of its own screen.</summary>
    static void PaintBoard(BoardWindow b, MonitorInfo mon, Wallpaper.Source src, ref string cornerKey)
    {
        double blurPx = Math.Clamp(Settings.BlurStrength, 0, 100) * 1.3 * mon.Scale;
        var br = b.PhysicalRect;
        // without blur the board shows the plain wallpaper at half resolution
        b.SetGlass(Settings.Blur ? Wallpaper.RenderGlass(src, br, blurPx) : Wallpaper.RenderGlass(src, br, 0, 0.5));
        // sharp squares behind the rounded corners: redone only when the board moved or the wallpaper changed
        int c = (int)Math.Ceiling(b.Radius * mon.Scale) + 2;
        var key = $"{src.GetHashCode()}|{br.X},{br.Y},{br.Width},{br.Height}|{c}";
        if (key != cornerKey)
        {
            cornerKey = key;
            b.SetCorners(Wallpaper.RenderCorners(src, br, c), c / mon.Scale);
        }
        b.UpdateDrawerGlass();
    }

    /// <summary>Re-read the wallpaper (settings button / tray / Windows wallpaper change).</summary>
    public void RefreshWallpaper()
    {
        Wallpaper.Invalidate();
        _cornerKey = _cornerKey2 = "";
        RefreshGlass();
    }

    // ---------- frosted glass ----------

    void ScheduleGlass()
    {
        _glassTimer.Stop();
        _glassTimer.Start();
    }

    void RefreshGlass()
    {
        if (Board == null || _clock == null) return;
        try
        {
            var mon = Monitors.Pick(Settings.Monitor);
            var src = Wallpaper.Resolve(mon);
            double blurPx = Math.Clamp(Settings.BlurStrength, 0, 100) * 1.3 * mon.Scale;
            var br = Board.PhysicalRect;
            PaintBoard(Board, mon, src, ref _cornerKey);
            if (_second != null && SecondScreen() is { } m2) PaintBoard(_second, m2, Wallpaper.Resolve(m2), ref _cornerKey2);

            var cr = _clock.PhysicalRect;
            _clock.SetGlass(Settings.Blur && cr.Width > 0 ? Wallpaper.RenderGlass(src, cr, blurPx) : null);
            var kr = _clock.Calendar.PhysicalRect;
            _clock.Calendar.SetGlass(Settings.Blur && kr.Width > 0 ? Wallpaper.RenderGlass(src, kr, blurPx) : null);

            if (Wallpaper.Average(src, br) is { } avg && avg != _surfaceTint)
            {
                _surfaceTint = avg;
                ApplyTheme();
            }
            if (Settings.Accent == "auto")
            {
                var col = Wallpaper.Accent(src, br);
                if (col != _autoAccent)
                {
                    _autoAccent = col;
                    ApplyTheme();
                    foreach (var b in Boards) b.Rebuild();
                }
            }
        }
        catch (Exception ex) { Log.Error("glass", ex); }
        ScheduleTrim();
    }

    // ---------- memory ----------

    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

    /// <summary>Called a few seconds after heavy work (wallpaper, views): frees temporary bitmaps.</summary>
    public void ScheduleTrim()
    {
        _trimTimer.Stop();
        _trimTimer.Start();
    }

    static void TrimMemory()
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect();
        SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
    }

    // ---------- global hotkey ----------

    void RegisterHotkey()
    {
        if (Board == null || Board.Hwnd == IntPtr.Zero) return;
        if (_hotkeySource == null)
        {
            _hotkeySource = HwndSource.FromHwnd(Board.Hwnd);
            _hotkeySource?.AddHook(HotkeyProc);
        }
        Native.UnregisterHotKey(Board.Hwnd, HotkeyId);
        (uint mods, uint vk)? key = Settings.Hotkey switch
        {
            "Ctrl+Alt+D" => (Native.MOD_CONTROL | Native.MOD_ALT, 0x44),
            "Ctrl+Shift+Space" => (Native.MOD_CONTROL | Native.MOD_SHIFT, 0x20),
            "Ctrl+Alt+T" => (Native.MOD_CONTROL | Native.MOD_ALT, 0x54),
            "Win+Shift+D" => (Native.MOD_WIN | Native.MOD_SHIFT, 0x44),
            _ => null,
        };
        HotkeyTaken = key is { } k && !Native.RegisterHotKey(Board.Hwnd, HotkeyId, k.mods | Native.MOD_NOREPEAT, k.vk);
        if (HotkeyTaken) Log.Error($"hotkey {Settings.Hotkey} is used by another program");
    }

    IntPtr HotkeyProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            if (GlassWindow.IsPeeking) GlassWindow.Peek(false);
            else
            {
                SetBoardHidden(false); // the hotkey always brings a hidden board back
                GlassWindow.Peek(true);
                Board?.QuickAdd();
            }
        }
        return IntPtr.Zero;
    }

    // ---------- windows ----------

    TodayWindow? _today;
    DateTime _todayClosed;

    public void ToggleToday()
    {
        // the click that deactivates (closes) the card arrives right before this one: don't reopen it
        if (_today != null || (DateTime.Now - _todayClosed).TotalMilliseconds < 300) { _today?.Close(); return; }
        _today = new TodayWindow();
        _today.Closed += (_, _) => { _today = null; _todayClosed = DateTime.Now; ScheduleTrim(); };
        _today.Show();
    }

    public void ShowSettings()
    {
        GlassWindow.EndOverlays(); // a topmost board (Win+D / hotkey) would cover the dialog
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow { Topmost = GlassWindow.DevTopmost || GlassWindow.AlwaysOnTop };
            _settingsWindow.Closed += (_, _) => { _settingsWindow = null; ScheduleTrim(); };
            _settingsWindow.Show();
        }
        _settingsWindow.Activate();
    }

    public void ImportNotion() => ImportNotion(null);

    public void ImportNotion(string? path)
    {
        if (_importOpen) return;
        GlassWindow.EndOverlays();
        if (path == null)
        {
            var dlg = new OpenFileDialog
            {
                Title = L.T("Wybierz eksport z Notion"),
                Filter = L.T("Eksport Notion (*.csv;*.zip)|*.csv;*.zip|Wszystkie pliki|*.*"),
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads",
            };
            if (dlg.ShowDialog() != true) return;
            path = dlg.FileName;
        }

        NotionExport export;
        try { export = NotionImport.Load(path); }
        catch (Exception ex)
        {
            Log.Error("notion import load", ex);
            MessageBox.Show(L.T("Nie udało się wczytać pliku:") + "\n" + ex.Message, "TaskWall", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (export.Table.Rows.Count == 0)
        {
            MessageBox.Show(L.T("Plik nie zawiera żadnych wierszy."), "TaskWall");
            return;
        }

        _importOpen = true;
        try
        {
            var win = new ImportWindow(export) { Topmost = GlassWindow.DevTopmost || GlassWindow.AlwaysOnTop };
            if (win.ShowDialog() == true)
            {
                Board?.RevealBacklog();
                _tray?.Balloon("TaskWall", L.F("Zaimportowano: {0}, zaktualizowano: {1}", win.Imported, win.Updated));
            }
        }
        finally { _importOpen = false; ScheduleTrim(); }
    }

    void SetAutostart(bool on)
    {
        if (Dev.Arg("--data") != null) return; // test runs must not repoint autostart to a dev build
        if (_started && on == _lastAutostart) return; // registry only when it actually changes
        _lastAutostart = on;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            bool present = key.GetValue("TaskWall") != null;
            if (on && Environment.ProcessPath is { } exe) key.SetValue("TaskWall", $"\"{exe}\"");
            else if (!on && present) key.DeleteValue("TaskWall", false);
        }
        catch (Exception ex) { Log.Error("autostart", ex); }
    }

    void SetupTray()
    {
        _tray = new TrayIcon("TaskWall");
        _tray.Clicked += ToggleToday; // left click: today card; right click: menu
        _tray.BuildMenu = () =>
        {
            var menu = new ContextMenu();
            void Add(string header, Action a)
            {
                var mi = new MenuItem { Header = L.T(header) };
                mi.Click += (_, _) => a();
                menu.Items.Add(mi);
            }
            Add(Settings.BoardHidden ? "Pokaż tablicę" : "Ukryj tablicę", () => SetBoardHidden(!Settings.BoardHidden));
            // on top of all windows until chosen again (or Esc on the board)
            Add("Tablica nad wszystkim", () =>
            {
                SetBoardHidden(false);
                GlassWindow.SetAlwaysOnTop(!GlassWindow.AlwaysOnTop);
                if (GlassWindow.AlwaysOnTop) Board?.Activate();
            });
            ((MenuItem)menu.Items[^1]).IsCheckable = true;
            ((MenuItem)menu.Items[^1]).IsChecked = GlassWindow.AlwaysOnTop;
            Add("Dziś…", ToggleToday);
            Add("Komendy i skróty…", HelpWindow.ShowHelp);
            Add("Nowy alarm…", () => AlarmWindow.Edit(Store));
            Add("Ustawienia…", ShowSettings);
            if (Settings.AccountFor(Settings.Layer).Notion.Configured)
                Add("Synchronizuj Notion teraz", async () =>
                {
                    var r = await NotionSync.Run(Settings.Layer);
                    _tray?.Balloon("TaskWall – Notion", r.Error ?? L.F("Nowe: {0}, zmienione: {1} (stron: {2})", r.Added, r.Updated, r.Total));
                });
            Add("Importuj z Notion (CSV/ZIP)…", () => ImportNotion());
            Add(Settings.Layer == "private" ? "Przełącz na: Praca" : "Przełącz na: Prywatne", () => SwitchLayer(Settings.Layer == "private" ? "work" : "private"));
            Add("Archiwum…", () => Board?.ShowArchive());
            Add("Odśwież tapetę", RefreshWallpaper);
            Add("Odśwież kalendarze", RefreshCalendars);
            Add("Otwórz folder danych", () => GlassWindow.OpenUrl(Store.Folder));
            menu.Items.Add(new Separator());
            Add("Zamknij TaskWall", Shutdown);
            return menu;
        };
    }
}
