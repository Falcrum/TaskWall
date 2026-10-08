using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DeskWall;

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
    public static string AccountName(string layer) => Settings.AccountFor(layer).Name is { Length: > 0 } n ? n : layer == "private" ? "Prywatne" : "Praca";
    public static BoardWindow? Board { get; private set; }

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
    bool _glassReady;
    string _cornerKey = "";
    HwndSource? _hotkeySource;
    readonly DispatcherTimer _glassTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly DispatcherTimer _trimTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly DispatcherTimer _calendarTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    readonly DispatcherTimer _notionTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    bool _started;
    readonly DispatcherTimer _folderRetry = new() { Interval = TimeSpan.FromSeconds(30) };
    readonly System.Collections.Generic.Dictionary<string, string> _pendingFolders = new(); // layer -> folder not reachable at start
    bool _lastAutostart;
    readonly DispatcherTimer _applySoon = new() { Interval = TimeSpan.FromMilliseconds(150) };

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // installer switches (DeskWall-Setup.exe, --install, --uninstall) run before anything else
        try { if (Installer.HandleCommandLine()) { Shutdown(); return; } }
        catch (Exception ex) { Log.Error("installer", ex); Shutdown(); return; }
        // started by the installer / "Zainstaluj w systemie": let the old copy exit first
        if (Dev.Arg("--wait-for") is { } pidText && int.TryParse(pidText, out var pid))
            try { System.Diagnostics.Process.GetProcessById(pid).WaitForExit(15000); } catch { /* already gone */ }
        // dev runs (--data) live next to the installed copy
        _mutex = new Mutex(true, Dev.Arg("--data") != null ? @"Local\DeskWall.Dev" : @"Local\DeskWall.SingleInstance", out bool first);
        if (!first) { Shutdown(); return; }

        // after start-up a stray exception is logged and the app keeps running; during start-up it must not leave
        // an invisible process holding the single-instance mutex
        DispatcherUnhandledException += (_, a) =>
        {
            Log.Error("unhandled", a.Exception);
            if (_started) { a.Handled = true; return; }
            a.Handled = true;
            MessageBox.Show("DeskWall nie mógł się uruchomić:\n" + a.Exception.Message + "\n\nSzczegóły: %APPDATA%\\DeskWall\\error.log", "DeskWall", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        };
        try { Start(); _started = true; }
        catch (Exception ex)
        {
            Log.Error("startup", ex);
            MessageBox.Show("DeskWall nie mógł się uruchomić:\n" + ex.Message + "\n\nSzczegóły: %APPDATA%\\DeskWall\\error.log", "DeskWall", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    void Start()
    {

        Settings = SettingsStore.Load();
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
        CalendarService.Changed += () => Board.OnExternalChange(quiet: true);

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
        _folderRetry.Tick += (_, _) => RetryPendingFolders();
        _trimTimer.Tick += (_, _) => { _trimTimer.Stop(); TrimMemory(); };
        _calendarTimer.Tick += (_, _) => RefreshCalendars();
        _calendarTimer.Start();

        // start-up animation: board rises in, clock drops down
        Anim.Enter(Board.FrameElement, 0, 22, 520);
        Anim.Enter(_clock.FrameElement, 0, -30, 520, 150);
        ApplySettings();
        if (Settings.AutoRollover) Board.RollOverdue();
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
            AlarmService.Changed += () => Board?.OnExternalChange(quiet: true);
            AlarmService.Start();
            // Notion: each account with a token syncs on its own interval (first run right after start-up)
            _notionTimer.Tick += (_, _) => NotionSync.Tick();
            _notionTimer.Start();
            NotionSync.Tick();
        }, DispatcherPriority.ApplicationIdle);
        Dev.AfterStartup();
    }

    /// <summary>Hides the whole board (the clock follows its own setting); the tray icon / hotkey bring it back.</summary>
    public void SetBoardHidden(bool hidden)
    {
        if (Settings.BoardHidden == hidden || Board == null) return;
        Settings.BoardHidden = hidden;
        SettingsStore.Save(Settings);
        if (hidden)
        {
            Board.EndEditing();
            if (GlassWindow.IsPeeking) GlassWindow.Peek(false);
            var frame = Board.FrameElement;
            var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(Settings.Animations ? 200 : 1));
            fade.Completed += (_, _) =>
            {
                Board.Hide();
                frame.BeginAnimation(UIElement.OpacityProperty, null);
                frame.Opacity = 1;
                ScheduleTrim();
            };
            frame.BeginAnimation(UIElement.OpacityProperty, fade);
            _tray?.Balloon("DeskWall", "Tablica ukryta. Pokażesz ją z menu ikony w zasobniku albo skrótem " + Settings.Hotkey + ".");
        }
        else
        {
            LayoutWindows();
            Anim.Enter(Board.FrameElement, 0, 22, 420);
        }
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
        foreach (var (layer, folder) in _pendingFolders.ToList())
        {
            var root = Path.GetPathRoot(folder);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            try
            {
                Stores[layer].SwitchFolder(folder); // merges what was written to the local fallback meanwhile
                _pendingFolders.Remove(layer);
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
        // at logon Google Drive / OneDrive may mount their drive a bit after us: wait up to ~90 s for the drive itself
        var root = Path.GetPathRoot(folder);
        for (int i = 0; i < 45 && !string.IsNullOrEmpty(root) && !Directory.Exists(root); i++) Thread.Sleep(2000);
        try { s.Open(folder); }
        catch (Exception ex)
        {
            Log.Error("open data folder " + folder, ex);
            var fallback = Path.Combine(SettingsStore.Dir, layer);
            s.Open(fallback);
            _pendingFolders[layer] = folder; // keep trying; once reachable, the fallback data is merged into it
            _folderRetry.Start();
            Dispatcher.BeginInvoke(() => _tray?.Balloon("DeskWall", $"Folder „{LayerName(layer)}” jest niedostępny ({folder}). Zapisuję lokalnie i przeniosę dane, gdy się pojawi."));
        }
        s.ExternalChange += () => { if (ReferenceEquals(s, Store)) Board?.OnExternalChange(); };
        Stores[layer] = s;
        return s;
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
        Board.OnLayerChanged();
    }

    /// <summary>Settings changed the folder of a layer: move that layer's data there (merged).</summary>
    public void ChangeLayerFolder(string layer, string folder)
    {
        OpenLayer(layer).SwitchFolder(folder);
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

        Board.ApplyScale(Settings.UiScale);
        _clock.SetScale(Settings.UiScale);
        Board.Rebuild();
        _clock.UpdateText();
        LayoutWindows();
        RegisterHotkey();
        ScheduleGlass();
    }

    public void OnNewDay()
    {
        if (Settings.AutoRollover) Board?.RollOverdue();
    }

    public async void RefreshCalendars()
    {
        // every account has its own calendars; events are tagged with the account and shown only there
        var feeds = Settings.Accounts.SelectMany(a => a.Calendars.Select(f => new CalendarFeed { Name = f.Name, Url = f.Url, Kind = f.Kind, Enabled = f.Enabled, Layer = a.Id })).ToList();
        try { await CalendarService.RefreshAsync(feeds); }
        catch (Exception ex) { Log.Error("calendars", ex); }
        ScheduleTrim();
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
        Resources["GlassTint"] = new SolidColorBrush(Color.FromArgb(a, 0x08, 0x09, 0x0D));
    }

    // ---------- placement ----------

    public void LayoutWindows()
    {
        if (Board == null || _clock == null) return;
        var mon = Monitors.Pick(Settings.Monitor);
        double s = mon.Scale, ui = Settings.UiScale;
        var wa = mon.Work;
        int P(double dip) => (int)Math.Round(dip * s);

        int bw = (int)Math.Round(wa.Width * Math.Clamp(Settings.WidthPercent, 30, 100) / 100);
        double weekH = wa.Height / s * Math.Clamp(Settings.WeekHeightPercent, 5, 60) / 100;
        int clockCollapsed = P(ClockWindow.HeaderHeight * ui);
        int clockFull = P((ClockWindow.HeaderHeight + ClockWindow.CalendarHeight) * ui);
        int cw = P(ClockWindow.TabWidth * ui);
        int minTop = wa.Y + (Settings.ShowClock ? clockCollapsed + P(18) : P(14));
        int bh = Math.Min(P(BoardWindow.ChromeHeight * ui + Board.HeightInWeeks * weekH), wa.Y + wa.Height - minTop - P(14));
        int maxTop = Math.Max(minTop, wa.Y + wa.Height - bh - P(14));
        int top = minTop + (int)Math.Round((maxTop - minTop) * Math.Clamp(Settings.VerticalPercent, 0, 100) / 100);
        int left = wa.X + (wa.Width - bw) / 2;

        Board.PlacePhysical(left, top, bw, bh, s);
        if (Settings.ShowClock) _clock.PlacePhysical(wa.X + (wa.Width - cw) / 2, wa.Y, cw, clockFull, s);

        // first time: paint the glass before showing, so the board never flashes empty
        if (!_glassReady) { RefreshGlass(); _glassReady = true; }
        else ScheduleGlass();

        if (Settings.BoardHidden) { if (Board.IsVisible) Board.Hide(); }
        else if (!Board.IsVisible) Board.Show();
        if (Settings.ShowClock) { if (!_clock.IsVisible) _clock.Show(); }
        else if (_clock.IsVisible) _clock.Hide();
    }

    /// <summary>Re-read the wallpaper (settings button / tray / Windows wallpaper change).</summary>
    public void RefreshWallpaper()
    {
        Wallpaper.Invalidate();
        _cornerKey = "";
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
            // without blur the board shows the plain wallpaper at half resolution
            Board.SetGlass(Settings.Blur ? Wallpaper.RenderGlass(src, br, blurPx) : Wallpaper.RenderGlass(src, br, 0, 0.5));

            // sharp squares behind the rounded corners: redone only when the board moved or the wallpaper changed
            int c = (int)Math.Ceiling(Board.Radius * mon.Scale) + 2;
            var key = $"{src.GetHashCode()}|{br.X},{br.Y},{br.Width},{br.Height}|{c}";
            if (key != _cornerKey)
            {
                _cornerKey = key;
                Board.SetCorners(Wallpaper.RenderCorners(src, br, c), c / mon.Scale);
            }

            var cr = _clock.PhysicalRect;
            _clock.SetGlass(Settings.Blur && cr.Width > 0 ? Wallpaper.RenderGlass(src, cr, blurPx) : null);
            Board.UpdateDrawerGlass();

            if (Settings.Accent == "auto")
            {
                var col = Wallpaper.Accent(src, br);
                if (col != _autoAccent)
                {
                    _autoAccent = col;
                    ApplyTheme();
                    Board.Rebuild();
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
            _settingsWindow = new SettingsWindow { Topmost = GlassWindow.DevTopmost };
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
                Title = "Wybierz eksport z Notion",
                Filter = "Eksport Notion (*.csv;*.zip)|*.csv;*.zip|Wszystkie pliki|*.*",
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
            MessageBox.Show("Nie udało się wczytać pliku:\n" + ex.Message, "DeskWall", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (export.Table.Rows.Count == 0)
        {
            MessageBox.Show("Plik nie zawiera żadnych wierszy.", "DeskWall");
            return;
        }

        _importOpen = true;
        try
        {
            var win = new ImportWindow(export) { Topmost = GlassWindow.DevTopmost };
            if (win.ShowDialog() == true)
            {
                Board?.RevealBacklog();
                _tray?.Balloon("DeskWall", $"Zaimportowano: {win.Imported}, zaktualizowano: {win.Updated}");
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
            bool present = key.GetValue("DeskWall") != null;
            if (on && Environment.ProcessPath is { } exe) key.SetValue("DeskWall", $"\"{exe}\"");
            else if (!on && present) key.DeleteValue("DeskWall", false);
        }
        catch (Exception ex) { Log.Error("autostart", ex); }
    }

    void SetupTray()
    {
        _tray = new TrayIcon("DeskWall");
        _tray.Clicked += ToggleToday; // left click: today card; right click: menu
        _tray.BuildMenu = () =>
        {
            var menu = new ContextMenu();
            void Add(string header, Action a)
            {
                var mi = new MenuItem { Header = header };
                mi.Click += (_, _) => a();
                menu.Items.Add(mi);
            }
            Add(Settings.BoardHidden ? "Pokaż tablicę" : "Ukryj tablicę", () => SetBoardHidden(!Settings.BoardHidden));
            Add("Dziś…", ToggleToday);
            Add("Nowy alarm…", () => AlarmWindow.Edit(Store));
            Add("Ustawienia…", ShowSettings);
            if (Settings.AccountFor(Settings.Layer).Notion.Configured)
                Add("Synchronizuj Notion teraz", async () =>
                {
                    var r = await NotionSync.Run(Settings.Layer);
                    _tray?.Balloon("DeskWall – Notion", r.Error ?? $"Nowe: {r.Added}, zmienione: {r.Updated} (stron: {r.Total})");
                });
            Add("Importuj z Notion (CSV/ZIP)…", () => ImportNotion());
            Add(Settings.Layer == "private" ? "Przełącz na: Praca" : "Przełącz na: Prywatne", () => SwitchLayer(Settings.Layer == "private" ? "work" : "private"));
            Add("Archiwum…", () => Board?.ShowArchive());
            Add("Odśwież tapetę", RefreshWallpaper);
            Add("Odśwież kalendarze", RefreshCalendars);
            Add("Otwórz folder danych", () => GlassWindow.OpenUrl(Store.Folder));
            menu.Items.Add(new Separator());
            Add("Zamknij DeskWall", Shutdown);
            return menu;
        };
    }
}
