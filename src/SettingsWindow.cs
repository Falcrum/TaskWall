using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TaskWall;

/// <summary>
/// Settings as an accordion (one section open at a time, no long scrolling).
/// "Konta" holds everything that belongs to an account: name, data folder, Google calendars, categories.
/// </summary>
public sealed class SettingsWindow : DarkWindow
{
    static AppSettings S => App.Settings;
    readonly StackPanel _sections = new() { Margin = new Thickness(20, 16, 20, 20) };
    /// <summary>The bottom strip with "Gotowe", a fine line above it.</summary>
    readonly Border _footerBar = new() { BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = new SolidColorBrush(Th.Ink(0x10)) };
    /// <summary>The scrolling sections (dev screenshots scroll it).</summary>
    public ScrollViewer Scroller { get; }
    readonly List<(Border header, Border body, TextBlock chevron)> _accordion = new();
    readonly List<Action> _openers = new();
    readonly Dictionary<string, int> _sectionIndex = new();
    /// <summary>Opens a section by its position (dev screenshots).</summary>
    public void OpenSection(int index) { if (index >= 0 && index < _openers.Count) _openers[index](); }
    /// <summary>Opens a section by its key ("bar", "weather", "connections" …).</summary>
    public void OpenSection(string key) { if (_sectionIndex.TryGetValue(key, out var i)) _openers[i](); }
    readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    string _account = App.Settings.Layer;
    readonly Border _accountBody = new();
    TextBlock? _syncStatus, _calStatus;

    public SettingsWindow()
    {
        Title = L.T("TaskWall – ustawienia");
        Width = 640;
        Height = 780;
        MinWidth = 520;
        // many options: the whole height of the work area (from the bar to the top) and half the screen's width, in the middle
        var (px, py, pw, ph, scale) = TallRect();
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = px / scale; Top = py / scale; Width = pw / scale; Height = ph / scale;
        SourceInitialized += (_, _) => PlaceTall();
        ResizeMode = ResizeMode.CanResize;
        // the sections scroll; "Gotowe" stays at the window's bottom edge (the window is as tall as the screen)
        Scroller = new ScrollViewer { Content = _sections, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var root = new DockPanel();
        DockPanel.SetDock(_footerBar, Dock.Bottom);
        root.Children.Add(_footerBar);
        root.Children.Add(Scroller);
        Content = root;

        // what a fresh install still needs, at the top (each one is also marked "UZUPEŁNIJ" in its section)
        var todo = new List<(string What, string Section)>();
        foreach (var a in S.Accounts.Where(a => a.NotionSources.Any(l => l.Database.Length > 0) && a.Notion.TokenProtected == null))
            todo.Add((L.F("token Notion konta „{0}”", a.Name), "connections"));
        if (S.Weather.Enabled && !S.Weather.Ready) todo.Add((L.T("miasto do prognozy pogody"), "weather"));
        if (todo.Count > 0)
        {
            var box = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 10),
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0xE8, 0xA2, 0x3A)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x90, 0xE8, 0xA2, 0x3A)), BorderThickness = new Thickness(1),
            };
            var list = new StackPanel();
            list.Children.Add(Label(L.T("Do uzupełnienia"), 12, "Fg", FontWeights.Bold));
            foreach (var (what, section) in todo)
            {
                var s = section;
                var go = Btn("→ " + what, "LinkButton", (_, _) => OpenSection(s));
                go.HorizontalAlignment = HorizontalAlignment.Left;
                list.Children.Add(go);
            }
            box.Child = list;
            _sections.Children.Add(box);
        }

        // from what is changed most to what is set once
        AddSection("general", L.T("Ogólne"), L.T("język, autostart, skrót klawiszowy, alarmy i przypomnienia"), BuildGeneral);
        AddSection("look", L.T("Wygląd"), L.T("motyw, kolor akcentu, tło i wzór, wygląd zadań"), BuildLook);
        AddSection("board", L.T("Tablica"), L.T("weekendy, przenoszenie zadań, godziny pracy, backlog"), BuildBoard);
        AddSection("screen", L.T("Ekran i rozmiar"), L.T("monitor, drugi ekran, skala, szerokość i wysokość tablicy"), BuildScreen);
        AddSection("bar", L.T("Pasek i dock"), L.T("pasek nad tablicą albo pasek z dockiem zamiast paska zadań Windows"), BuildBar);
        AddSection("weather", L.T("Pogoda"), L.T("prognoza w nagłówkach dni, lokalizacja"), BuildWeather);
        AddSection("accounts", L.T("Konta"), L.T("Praca i Prywatne: folder, kalendarze Google, kategorie, oznaczenia dni"), BuildAccounts);
        AddSection("connections", L.T("Połączenia"), L.T("Notion: automatyczne wczytywanie zadań z bazy"), BuildConnections);
        AddSection("info", L.T("Info"), L.T("wersja, aktualizacje, kopia zapasowa"), BuildInstall);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 12, 20, 14) };
        footer.Children.Add(Btn(L.T("Importuj z Notion…"), "SecondaryButton", (_, _) => App.Instance.ImportNotion()));
        footer.Children.Add(Btn(L.T("Gotowe"), "PrimaryButton", (_, _) => Close()));
        _footerBar.Child = footer;

        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        CalendarService.Changed += UpdateCalStatus;
        Closed += (_, _) => { _statusTimer.Stop(); CalendarService.Changed -= UpdateCalStatus; };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /// <summary>The work area of the board's screen (the bar's strip left out), half its width, centred – in physical pixels.</summary>
    (int X, int Y, int W, int H, double Scale) TallRect()
    {
        var mon = Monitors.Pick(S.Monitor);
        var wa = mon.Work;
        int w = Math.Min(wa.Width, Math.Max((int)Math.Round(MinWidth * mon.Scale), wa.Width / 2));
        return (wa.X + (wa.Width - w) / 2, wa.Y, w, wa.Height, mon.Scale);
    }

    /// <summary>Pins that rectangle once the window exists (onto the screen first – a change of DPI rescales the window –, then the size).</summary>
    void PlaceTall()
    {
        try
        {
            var (x, y, w, hgt, _) = TallRect();
            var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            Native.SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOSIZE);
            Native.SetWindowPos(h, IntPtr.Zero, x, y, w, hgt, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        catch (Exception ex) { Log.Error("settings place", ex); }
    }

    // ---------- accordion ----------

    void AddSection(string key, string title, string subtitle, Func<FrameworkElement> build, bool open = false)
    {
        _sectionIndex[key] = _openers.Count;
        // one card per section (like the day cells of the board): header, and the body inside the same card when open
        var chevron = new TextBlock { Text = "", FontFamily = Ui.Font("IconFont"), FontSize = 11, Foreground = Ui.Res("FgDim"), VerticalAlignment = VerticalAlignment.Center };
        var head = new DockPanel();
        DockPanel.SetDock(chevron, Dock.Right);
        head.Children.Add(chevron);
        var titles = new StackPanel();
        var name = Label(title.ToUpper(BoardWindow.Pl), 12, "Fg", FontWeights.Bold);
        titles.Children.Add(name);
        titles.Children.Add(Label(subtitle, 11.5, "FgFaint"));
        head.Children.Add(titles);
        var header = new Border { Child = head, Padding = new Thickness(14, 10, 14, 10), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand };
        Anim.HoverBackground(header, Colors.Transparent, Th.Ink(0x0E));
        var body = new Border { Padding = new Thickness(14, 2, 14, 14), Visibility = Visibility.Collapsed };
        var card = new Border
        {
            CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Th.Ink(0x09)), BorderBrush = new SolidColorBrush(Th.Ink(0x12)),
            Child = new StackPanel { Children = { header, body } },
        };
        bool built = false;
        void Open()
        {
            if (!built) { BeginScope(key); body.Child = build(); built = true; }
            Sync(); // a setting changed in another section can show or hide rows here
            foreach (var (h, b, c) in _accordion)
            {
                bool me = ReferenceEquals(b, body);
                b.Visibility = me ? Visibility.Visible : Visibility.Collapsed;
                c.Text = me ? "" : "";
                if (h.Child is DockPanel dp && dp.Children[1] is StackPanel sp && sp.Children[0] is TextBlock tb)
                    tb.Foreground = me ? Ui.Res("AccentBrush") : Ui.Res("Fg");
            }
            Anim.Enter(body, 0, -6, 200);
        }
        header.MouseLeftButtonUp += (_, _) =>
        {
            if (body.Visibility == Visibility.Visible) { body.Visibility = Visibility.Collapsed; chevron.Text = ""; name.Foreground = Ui.Res("Fg"); }
            else Open();
        };
        _accordion.Add((header, body, chevron));
        _openers.Add(Open);
        _sections.Children.Add(card);
        if (open) Dispatcher.BeginInvoke(Open);
    }

    // ---------- Konta ----------

    FrameworkElement BuildAccounts()
    {
        var panel = new StackPanel();
        var switcher = new Border { CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Th.Ink(0x10)), Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 10) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        switcher.Child = buttons;
        void Paint()
        {
            foreach (Button b in buttons.Children)
            {
                bool on = (string)b.Tag == _account;
                b.Background = on ? new SolidColorBrush(Th.Ink(0x30)) : Brushes.Transparent;
                b.Foreground = on ? Ui.Res("Fg") : Ui.Res("FgDim");
                b.Content = App.AccountName((string)b.Tag).ToUpper(BoardWindow.Pl);
            }
        }
        foreach (var id in new[] { "work", "private" })
        {
            var b = new Button { Style = (Style)Application.Current.Resources["BarButton"], Tag = id, Padding = new Thickness(14, 4, 14, 4) };
            b.Click += (_, _) => { _account = id; Paint(); FillAccount(Paint); };
            buttons.Children.Add(b);
        }
        Paint();
        panel.Children.Add(switcher);
        panel.Children.Add(Note(L.T("Każde konto ma osobny folder danych, kalendarze, kategorie i oznaczenia dni. Przełączanie: ikona warstwy na górnym pasku, Ctrl+1 / Ctrl+2.") + "\n\n"
            + L.T("Synchronizacja między komputerami: każdy komputer zapisuje swój plik w folderze konta (devices\\board-NAZWA.json) i scala pliki pozostałych – zadania, alarmy, oznaczenia i notatki; ustawienia programu tak samo (devices\\settings-NAZWA.json w folderze konta Praca). Lokalne zostają: foldery, ekran i rozmiar, autostart, adresy kalendarzy i tokeny Notion. Folder na Dysku Google jest rozpoznawany niezależnie od litery dysku (G:, H:…).")));
        panel.Children.Add(_accountBody);
        FillAccount(Paint);
        return panel;
    }

    void FillAccount(Action repaintNames)
    {
        var id = _account;
        var acc = S.AccountFor(id);
        var store = App.StoreFor(id);
        var p = new StackPanel();
        _accountBody.Child = p;
        BeginScope("account");

        // name
        var name = new TextBox { Style = (Style)Application.Current.Resources["FieldBox"], Text = acc.Name, MaxLength = 20, Width = 240, HorizontalAlignment = HorizontalAlignment.Right };
        void CommitName()
        {
            var v = name.Text.Trim();
            if (v.Length == 0 || v == acc.Name) { name.Text = acc.Name; return; }
            acc.Name = v;
            SettingsStore.Save(S);
            repaintNames();
            App.Board?.Rebuild();
        }
        name.LostKeyboardFocus += (_, _) => CommitName();
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitName(); };

        // folder
        var folder = new StackPanel();
        folder.Children.Add(FolderRow(id));
        _syncStatus = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Foreground = Ui.Res("FgFaint"), Tag = store };
        folder.Children.Add(_syncStatus);
        UpdateStatus();
        p.Children.Add(Group(L.T("Konto"), Row(L.T("Nazwa konta"), null, name), Stacked(L.T("Folder danych"), L.T("Tu są zadania, kopie dzienne i raporty konta. Folder na Dysku Google albo w OneDrive synchronizuje komputery."), folder)));

        // calendars
        var cal = new StackPanel();
        var feeds = new StackPanel();
        cal.Children.Add(feeds);
        void FillFeeds()
        {
            feeds.Children.Clear();
            foreach (var feed in acc.Calendars.ToList())
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var remove = Btn(L.T("Usuń"), "LinkButton", (_, _) => { acc.Calendars.Remove(feed); SettingsStore.Save(S); FillFeeds(); App.Instance.RefreshCalendars(); });
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);
                var cb = new CheckBox { IsChecked = feed.Enabled, Content = $"{feed.Name}  ·  {(feed.Kind == "holidays" ? L.T("święta") : L.T("spotkania"))}", ToolTip = feed.Kind == "holidays" ? feed.Url : L.T("tajny adres iCal (ukryty)") };
                cb.Checked += (_, _) => { feed.Enabled = true; SettingsStore.Save(S); App.Instance.RefreshCalendars(); };
                cb.Unchecked += (_, _) => { feed.Enabled = false; SettingsStore.Save(S); App.Instance.RefreshCalendars(); };
                row.Children.Add(cb);
                feeds.Children.Add(row);
            }
            if (acc.Calendars.Count == 0) feeds.Children.Add(Label(L.T("Brak kalendarzy."), 11.5, "FgFaint"));
        }
        FillFeeds();
        var add = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        add.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        add.ColumnDefinitions.Add(new ColumnDefinition());
        add.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        add.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var feedName = new TextBox { Style = (Style)Application.Current.Resources["FieldBox"], Text = id == "private" ? "Gmail" : L.T("Praca"), Margin = new Thickness(0, 0, 6, 0), ToolTip = L.T("Nazwa kalendarza") };
        var feedUrl = new TextBox { Style = (Style)Application.Current.Resources["FieldBox"], Margin = new Thickness(0, 0, 6, 0), ToolTip = "https://calendar.google.com/calendar/ical/…/basic.ics" };
        var kind = new ComboBox { MinWidth = 110 };
        kind.Items.Add(new ComboBoxItem { Content = L.T("Spotkania"), Tag = "events" });
        kind.Items.Add(new ComboBoxItem { Content = L.T("Święta"), Tag = "holidays" });
        kind.SelectedIndex = 0;
        var addBtn = Btn(L.T("Dodaj"), "SecondaryButton", (_, _) =>
        {
            var url = feedUrl.Text.Trim();
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("webcal", StringComparison.OrdinalIgnoreCase)) { feedUrl.Focus(); return; }
            acc.Calendars.Add(new CalendarFeed { Name = feedName.Text.Trim().Length > 0 ? feedName.Text.Trim() : L.T("Kalendarz"), Url = url, Kind = (string)((ComboBoxItem)kind.SelectedItem).Tag });
            feedUrl.Text = "";
            SettingsStore.Save(S);
            FillFeeds();
            App.Instance.RefreshCalendars();
        });
        Grid.SetColumn(feedUrl, 1);
        Grid.SetColumn(kind, 2);
        Grid.SetColumn(addBtn, 3);
        add.Children.Add(feedName);
        add.Children.Add(feedUrl);
        add.Children.Add(kind);
        add.Children.Add(addBtn);
        cal.Children.Add(add);
        var calRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var refresh = Btn(L.T("Odśwież kalendarze"), "LinkButton", (_, _) => App.Instance.RefreshCalendars());
        refresh.Margin = new Thickness(-7, 0, 0, 0);
        DockPanel.SetDock(refresh, Dock.Left);
        calRow.Children.Add(refresh);
        _calStatus = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Res("FgFaint"), Margin = new Thickness(8, 0, 0, 0) };
        calRow.Children.Add(_calStatus);
        cal.Children.Add(calRow);
        UpdateCalStatus();
        p.Children.Add(Group(L.T("Kalendarze Google"), L.T("Adres iCal: Kalendarz Google → Ustawienia → kalendarz → Integracja kalendarza → Tajny adres w formacie iCal. Adres jest zapisywany tylko na tym komputerze."), cal));


        // categories (stored in this account's board.json, so they sync with its other computers)
        var catBox = new StackPanel();
        var cats = new StackPanel();
        catBox.Children.Add(cats);
        FillCategories(store, cats);
        var addCat = Btn(L.T("+ Dodaj kategorię"), "SecondaryButton", (_, _) =>
        {
            var list = store.Categories.Select(x => new Category { Name = x.Name, Color = x.Color }).ToList();
            var used = list.Select(x => x.Color).ToHashSet(StringComparer.OrdinalIgnoreCase);
            list.Add(new Category { Name = L.T("Nowa"), Color = Category.Palette.FirstOrDefault(x => !used.Contains(x)) ?? Category.Palette[0] });
            SaveCategories(store, list);
            FillCategories(store, cats, focusLast: true);
        });
        addCat.HorizontalAlignment = HorizontalAlignment.Left;
        addCat.Margin = new Thickness(0, 6, 0, 0);
        catBox.Children.Add(addCat);
        p.Children.Add(Group(L.T("Kategorie"), L.T("Etykieta [Nazwa] na początku zadania. Przypisanie: prawy przycisk → Kategoria, etykiety pod polem nowego zadania albo #nazwa."), catBox));

        // day marks (HO, BŚU, Urlop …) – also per account, synced like the categories
        var markBox = new StackPanel();
        var marks = new StackPanel();
        markBox.Children.Add(marks);
        FillMarks(store, marks);
        var addMark = Btn(L.T("+ Dodaj oznaczenie"), "SecondaryButton", (_, _) =>
        {
            var list = store.MarkTypes.Select(x => x.Clone()).ToList();
            var used = list.Select(x => x.Color).ToHashSet(StringComparer.OrdinalIgnoreCase);
            list.Add(new MarkType { Code = L.T("NOWE"), Label = "", Color = Category.Palette.FirstOrDefault(x => !used.Contains(x)) ?? Category.Palette[0] });
            store.SetMarkTypes(list);
            App.Board?.Rebuild();
            FillMarks(store, marks, focusLast: true);
        });
        addMark.HorizontalAlignment = HorizontalAlignment.Left;
        addMark.Margin = new Thickness(0, 6, 0, 0);
        markBox.Children.Add(addMark);
        p.Children.Add(Group(L.T("Oznaczenia dni"), L.T("Kod w nagłówku dnia (np. HO, BŚU, Urlop). Ustawianie: prawy przycisk na dniu; stały cykl: przycisk Cykl…. Uwzględniane w widoku roku i eksporcie CSV."), markBox));

        // clearing this account's data (goes to the other computers too)
        var clearRow = new WrapPanel { Margin = new Thickness(0, 0, 0, -8) };
        void ClearButton(string label, Func<int> count, Func<int> clear, string what)
        {
            var b = Btn(label, "SecondaryButton", (_, _) =>
            {
                int n = count();
                if (n == 0) { MessageBox.Show(this, L.F("Konto „{0}” nie ma: {1}.", App.AccountName(id), what), "TaskWall"); return; }
                if (MessageBox.Show(this, L.F("Usunąć z konta „{0}”: {1} ({2})?", App.AccountName(id), what, n), "TaskWall", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                clear();
                AlarmService.Reschedule();
                App.Board?.Rebuild();
                FillMarks(store, marks);
            });
            b.Margin = new Thickness(0, 0, 8, 8);
            clearRow.Children.Add(b);
        }
        ClearButton(L.T("Usuń wszystkie zadania"), () => store.Data.Tasks.Count + store.Data.Rules.Count(r => !r.Deleted), store.ClearTasks, L.T("wszystkie zadania (dni, backlog, archiwum, serie)"));
        ClearButton(L.T("Usuń spotkania"), () => store.Alarms.Count(a => a.IsMeeting), () => store.ClearAlarms(meetings: true), L.T("spotkania"));
        ClearButton(L.T("Usuń alarmy"), () => store.Alarms.Count(a => !a.IsMeeting), () => store.ClearAlarms(meetings: false), L.T("alarmy"));
        ClearButton(L.T("Usuń oznaczenia dni"), () => store.Data.Days.Count(kv => !string.IsNullOrEmpty(kv.Value.Mark)) + store.Data.MarkRules.Count(r => !r.Deleted), store.ClearMarks, L.T("oznaczenia dni (też powtarzane)"));
        Button? restore = null;
        restore = Btn(L.T("Przywróć z kopii…"), "SecondaryButton", (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = restore, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            var backups = store.Backups();
            if (backups.Count == 0) menu.Items.Add(new MenuItem { Header = L.T("Brak kopii w folderze backup"), IsEnabled = false });
            foreach (var (file, when, count) in backups.Take(25))
            {
                var mi = new MenuItem { Header = L.F("{0}  ·  zadań: {1}  ·  {2}", when.ToString("ddd d MMM yyyy, HH:mm", BoardWindow.Pl), count, Path.GetFileName(Path.GetDirectoryName(file)) == "backup" ? Path.GetFileName(file) : Path.GetFileName(Path.GetDirectoryName(file)) + "\\" + Path.GetFileName(file)) };
                mi.Click += (_, _) =>
                {
                    if (MessageBox.Show(this, L.F("Przywrócić zadania, oznaczenia dni i serie konta „{0}” do stanu z kopii ({1})?\nObecny stan da się przywrócić Ctrl+Z na tablicy.", App.AccountName(id), when.ToString("g", BoardWindow.Pl)), "TaskWall",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    if (store.RestoreFrom(file)) App.Board?.Rebuild();
                };
                menu.Items.Add(mi);
            }
            menu.IsOpen = true;
        });
        restore.Margin = new Thickness(0, 0, 8, 8);
        clearRow.Children.Add(restore);
        p.Children.Add(Group(L.T("Usuwanie i przywracanie danych"), L.T("Usuwa dane konta, również na innych komputerach synchronizujących ten folder. Zadania i oznaczenia można przywrócić przez Ctrl+Z lub z kopii dziennej."), clearRow));
    }

    FrameworkElement FolderRow(string layer)
    {
        var box = new TextBox { IsReadOnly = true, Style = (Style)Application.Current.Resources["FieldBox"], Text = App.LayerFolder(layer) };
        var panel = new StackPanel();
        var row = new DockPanel();
        var open = Btn(L.T("Otwórz"), "SecondaryButton", (_, _) => GlassWindow.OpenUrl(App.LayerFolder(layer)));
        var change = Btn(L.T("Zmień…"), "SecondaryButton", (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L.F("Folder konta „{0}”", App.AccountName(layer)), InitialDirectory = Directory.Exists(box.Text) ? box.Text : "" };
            if (dlg.ShowDialog(this) == true) UseFolder(layer, dlg.FolderName, box);
        });
        DockPanel.SetDock(open, Dock.Right);
        DockPanel.SetDock(change, Dock.Right);
        row.Children.Add(open);
        row.Children.Add(change);
        row.Children.Add(box);
        panel.Children.Add(row);
        var quick = new WrapPanel { Margin = new Thickness(-7, 4, 0, 0) };
        string sub = layer == "private" ? "TaskWall Prywatne" : "TaskWall";
        void Quick(string label, string? root, string missingTip)
        {
            var b = Btn(label, "LinkButton", (_, _) => { if (root != null) UseFolder(layer, Path.Combine(root, sub), box); });
            b.IsEnabled = root != null;
            b.ToolTip = root != null ? Path.Combine(root, sub) : missingTip;
            b.Margin = new Thickness(0, 0, 4, 0);
            quick.Children.Add(b);
        }
        Quick("Google Drive", SettingsStore.GoogleDriveRoot(), L.T("Zainstaluj „Google Drive for desktop” – pojawi się „Mój dysk”, który TaskWall wykryje sam."));
        Quick("OneDrive", SettingsStore.OneDriveRoot(), L.T("OneDrive nie jest skonfigurowany na tym komputerze."));
        panel.Children.Add(quick);
        return panel;
    }

    /// <summary>
    /// Guards against a wrong pick: the backup sub-folder, a folder with TaskWall data one level down, the other
    /// account's folder, or a busy folder without any TaskWall data. Returns the folder to use, or null to cancel.
    /// </summary>
    string? CheckDataFolder(string layer, string path)
    {
        path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        bool HasData(string dir) => File.Exists(Path.Combine(dir, BoardStore.FileName));
        MessageBoxResult Ask(string text) => MessageBox.Show(this, text, "TaskWall", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (SettingsStore.DataFolderFix(path) is { } parent && parent != path)
            return Ask(L.F("Wybrany folder to kopie zapasowe TaskWall.\nUżyć folderu z danymi:\n{0}?", parent)) == MessageBoxResult.Yes ? parent : null;
        foreach (var other in S.Accounts.Where(a => a.Id != layer))
            if (string.Equals(Path.GetFullPath(App.LayerFolder(other.Id)).TrimEnd(Path.DirectorySeparatorChar), path, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, L.F("Ten folder należy już do konta „{0}”. Każde konto potrzebuje osobnego folderu.", App.AccountName(other.Id)), "TaskWall", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
        if (HasData(path)) return path;
        foreach (var name in new[] { "TaskWall", "TaskWall Prywatne" })
            if (HasData(Path.Combine(path, name)) && Ask(L.F("W tym folderze nie ma danych TaskWall, ale są w podfolderze:\n{0}\nUżyć go?", Path.Combine(path, name))) == MessageBoxResult.Yes)
                return Path.Combine(path, name);
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Take(4).Count() > 3
            && Ask(L.F("Folder „{0}” nie zawiera danych TaskWall, a są w nim inne pliki.\nZacząć tutaj nową, pustą tablicę?", path)) != MessageBoxResult.Yes)
            return null;
        return path;
    }

    void UseFolder(string layer, string path, TextBox box)
    {
        if (CheckDataFolder(layer, path) is not { } checkedPath) return;
        path = checkedPath;
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(App.LayerFolder(layer)), StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            App.Instance.ChangeLayerFolder(layer, path);
            box.Text = path;
        }
        catch (Exception ex)
        {
            Log.Error("switch folder", ex);
            MessageBox.Show(this, L.T("Nie udało się użyć tego folderu:\n") + ex.Message, "TaskWall", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    static void SaveCategories(BoardStore store, List<Category> list)
    {
        store.SetCategories(list);
        App.Board?.Rebuild();
    }

    void FillCategories(BoardStore store, StackPanel cats, bool focusLast = false)
    {
        cats.Children.Clear();
        var list = store.Categories.Select(x => new Category { Name = x.Name, Color = x.Color }).ToList();
        TextBox? last = null;
        for (int i = 0; i < list.Count; i++)
        {
            int index = i;
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            Color col;
            try { col = (Color)ColorConverter.ConvertFromString(list[i].Color); } catch { col = Colors.Gray; }
            var swatch = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(col), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 10, 0), ToolTip = L.T("Zmień kolor") };
            swatch.MouseLeftButtonUp += (_, _) => PickColor(swatch, c =>
            {
                list[index].Color = c;
                SaveCategories(store, list);
                FillCategories(store, cats);
            });
            DockPanel.SetDock(swatch, Dock.Left);
            row.Children.Add(swatch);
            var del = Btn(L.T("Usuń"), "LinkButton", (_, _) => { list.RemoveAt(index); SaveCategories(store, list); FillCategories(store, cats); });
            del.ToolTip = L.T("Usuwa kategorię z listy (zadania zachowują swoją etykietę)");
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);
            var name = new TextBox { Style = (Style)Application.Current.Resources["FieldBox"], Text = list[i].Name, MaxLength = 24 };
            void Commit()
            {
                var v = name.Text.Trim().Trim('[', ']').Trim();
                if (v.Length == 0 || v == list[index].Name) { name.Text = list[index].Name; return; }
                list[index].Name = v;
                SaveCategories(store, list);
            }
            name.LostKeyboardFocus += (_, _) => Commit();
            name.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
            row.Children.Add(name);
            cats.Children.Add(row);
            last = name;
        }
        if (focusLast && last != null) last.Loaded += (_, _) => { last.Focus(); last.SelectAll(); };
    }

    /// <summary>Weather layer: city (Open-Meteo search), what to show.</summary>
    FrameworkElement BuildWeather()
    {
        var w = S.Weather;
        var p = new StackPanel();
        var status = Label("", 11.5, "FgDim");
        void ShowStatus() => status.Text = !w.Ready ? L.T("Wybierz miasto, aby włączyć prognozę.")
            : WeatherService.Error != null ? L.F("Błąd: {0}", WeatherService.Error)
            : WeatherService.Fetched is { } f ? L.F("{0} · pobrano {1}", w.City, f.ToString("HH:mm")) : w.City;
        var row = new DockPanel();
        var city = new TextBox { Style = (Style)Application.Current.Resources["FieldBox"], Text = w.City };
        var results = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        async void Search()
        {
            results.Children.Clear();
            if (city.Text.Trim().Length < 2) return;
            try
            {
                var found = await WeatherService.Find(city.Text);
                if (found.Count == 0) results.Children.Add(Label(L.T("Nie znaleziono miejscowości."), 11.5, "FgFaint"));
                foreach (var (label, lat, lon) in found)
                {
                    var b = Btn(label, "LinkButton", (_, _) =>
                    {
                        w.City = label; w.Latitude = lat; w.Longitude = lon; w.Enabled = true;
                        city.Text = label;
                        results.Children.Clear();
                        SettingsStore.Save(S);
                        WeatherService.Refresh();
                        ShowStatus();
                    });
                    b.HorizontalAlignment = HorizontalAlignment.Left;
                    b.Margin = new Thickness(-7, 0, 0, 0);
                    results.Children.Add(b);
                }
            }
            catch (Exception ex) { results.Children.Add(Label(L.F("Błąd: {0}", ex.Message), 11.5, "FgFaint")); }
        }
        var find = Btn(L.T("Szukaj"), "SecondaryButton", (_, _) => Search());
        DockPanel.SetDock(find, Dock.Right);
        row.Children.Add(find);
        row.Children.Add(city);
        city.KeyDown += (_, e) => { if (e.Key == Key.Enter) Search(); };
        var refresh = Btn(L.T("Odśwież prognozę"), "LinkButton", (_, _) => WeatherService.Refresh());
        refresh.HorizontalAlignment = HorizontalAlignment.Left;
        refresh.Margin = new Thickness(-7, 2, 0, 0);
        var place = new StackPanel();
        place.Children.Add(row);
        place.Children.Add(results);
        status.Margin = new Thickness(0, 6, 0, 0);
        place.Children.Add(status);
        place.Children.Add(refresh);
        var cityRow = Stacked(L.T("Miejscowość"), w.Enabled && !w.Ready ? L.T("Wpisz miasto i wybierz je z listy, aby włączyć prognozę.") : L.T("Do serwisu trafiają tylko jej współrzędne."), place,
            needed: w.Enabled && !w.Ready);
        p.Children.Add(SwitchGroup(L.T("Pogoda w nagłówkach dni"), L.T("Symbol i temperatura maks. / min. na 16 dni (Open-Meteo, bez konta)."), w.Enabled, v => { w.Enabled = v; if (v) WeatherService.Refresh(); },
            cityRow,
            Toggle(L.T("Opady"), L.T("Szansa (0–100%) i przewidywana ilość (mm)."), w.ShowRain, v => w.ShowRain = v),
            Toggle(L.T("Wiatr"), L.T("Prędkość w km/h."), w.ShowWind, v => w.ShowWind = v)));
        WeatherService.Changed += ShowStatus;
        Closed += (_, _) => WeatherService.Changed -= ShowStatus;
        ShowStatus();
        return p;
    }

    /// <summary>Connections to other services (Notion for now), per layer.</summary>
    FrameworkElement BuildConnections()
    {
        var panel = new StackPanel();
        var body = new Border();
        var switcher = new Border { CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Th.Ink(0x10)), Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 10) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        switcher.Child = buttons;
        string current = S.Layer;
        void Fill()
        {
            foreach (Button b in buttons.Children)
            {
                bool on = (string)b.Tag == current;
                b.Background = on ? new SolidColorBrush(Th.Ink(0x30)) : Brushes.Transparent;
                b.Foreground = on ? Ui.Res("Fg") : Ui.Res("FgDim");
            }
            BeginScope("connection");
            body.Child = BuildNotion(S.AccountFor(current), App.StoreFor(current));
        }
        foreach (var acc in S.Accounts)
        {
            var id = acc.Id;
            var b = new Button { Style = (Style)Application.Current.Resources["BarButton"], Tag = id, Padding = new Thickness(14, 4, 14, 4), Content = L.Up(App.AccountName(id)) };
            b.Click += (_, _) => { current = id; Fill(); };
            buttons.Children.Add(b);
        }
        panel.Children.Add(switcher);
        panel.Children.Add(Note(L.T("Połączenia ustawia się osobno dla każdego konta. Zadania trafiają do backlogu wybranego konta.")));
        panel.Children.Add(body);
        Fill();
        return panel;
    }

    FrameworkElement BuildNotion(Account acc, BoardStore store)
    {
        var link = acc.Notion; // token and options of the account; its database is the first one
        var p = new StackPanel();

        // the token is never shown: a saved one appears as ******** (typing / pasting replaces it)
        const string Mask = "************************";
        var tokenRow = new DockPanel();
        var token = new PasswordBox { Height = 34, Padding = new Thickness(8, 6, 8, 6), Background = Ui.Res("Field"), Foreground = Ui.Res("Fg"), BorderBrush = Ui.Res("FieldBorder"), CaretBrush = Ui.Res("Fg"), PasswordChar = '*' };
        var tokenState = Label("", 11.5, "FgDim");
        bool filling = false;
        void ShowTokenState()
        {
            tokenState.Text = link.TokenProtected != null ? L.T("Token zapisany (zaszyfrowany). Wklejenie nowego zastępuje poprzedni.") : L.T("Brak tokenu.");
            filling = true;
            token.Password = link.TokenProtected != null ? Mask : "";
            filling = false;
        }
        var clear = Btn(L.T("Usuń token"), "LinkButton", (_, _) => { link.TokenProtected = null; SettingsStore.Save(S); ShowTokenState(); });
        DockPanel.SetDock(clear, Dock.Right);
        tokenRow.Children.Add(clear);
        // where to get one – right next to the field
        var open = Btn(L.T("Utwórz token w Notion ↗"), "LinkButton", (_, _) => GlassWindow.OpenUrl("https://www.notion.so/developers/tokens"));
        open.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(open, Dock.Right);
        tokenRow.Children.Add(open);
        tokenRow.Children.Add(token);
        token.GotKeyboardFocus += (_, _) => { if (token.Password == Mask) token.SelectAll(); };
        async void Verify()
        {
            // a live check right away: a wrong paste shows here, not as a sync error hours later
            if (link.TokenProtected == null) return;
            tokenState.Text = L.T("Sprawdzam token w Notion…");
            var problem = await NotionSync.Check(link);
            tokenState.Text = problem == null ? L.T("✓ Token działa (zapisany, zaszyfrowany).") : "✗ " + problem;
            tokenState.Foreground = problem == null ? Ui.Res("AccentBrush") : new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x55));
        }
        token.PasswordChanged += (_, _) =>
        {
            if (filling || token.Password == Mask) return;
            var pasted = NotionSync.Clean(token.Password); // never the mask with it, nor spaces around
            if (pasted.Length < 20) return;
            link.TokenProtected = NotionSync.Protect(pasted);
            SettingsStore.Save(S);
            ShowTokenState();
            Verify();
        };
        ShowTokenState();
        Verify();
        var tokenBox = new StackPanel();
        tokenBox.Children.Add(tokenRow);
        tokenState.Margin = new Thickness(0, 5, 0, 0);
        tokenBox.Children.Add(tokenState);
        var tokenField = Stacked(L.T("Osobisty token"), L.T("Szyfrowany dla bieżącego użytkownika Windows i zapisywany tylko na tym komputerze; inny komputer potrzebuje tego samego albo nowego tokenu."), tokenBox,
            needed: link.TokenProtected == null && acc.NotionSources.Any(l => l.Database.Length > 0));

        var every = Combo(new[] { 5, 10, 15, 30, 60, 120, 240 }.Select(m => (m.ToString(), m < 60 ? $"{m} min" : $"{m / 60} h")).ToArray(), link.Minutes.ToString(),
            k => { link.Minutes = int.Parse(k); SettingsStore.Save(S); }, 110);
        var limit = Combo(new[] { 100, 250, 500, 1000, 2000 }.Select(n => (n.ToString(), n.ToString())).ToArray(), link.MaxPages.ToString(),
            k => { link.MaxPages = int.Parse(k); SettingsStore.Save(S); }, 110);
        // ----- the databases (one per project), each with a short name shown on its tasks -----
        var projects = new StackPanel();
        var howTo = Btn(L.T("Jak skopiować link do widoku (pomoc Notion) ↗"), "LinkButton", (_, _) => GlassWindow.OpenUrl("https://www.notion.com/help/views-filters-and-sorts"));
        howTo.HorizontalAlignment = HorizontalAlignment.Left;
        howTo.Margin = new Thickness(-7, -4, 0, 4);
        projects.Children.Add(howTo);
        var list = new StackPanel();
        projects.Children.Add(list);
        var refreshers = new List<Action>();
        void FillSources()
        {
            list.Children.Clear();
            refreshers.Clear();
            list.Children.Add(BuildNotionSource(acc, store, link, null, refreshers));
            foreach (var extra in acc.NotionMore.ToList())
                list.Children.Add(BuildNotionSource(acc, store, extra, () => { acc.NotionMore.Remove(extra); SettingsStore.Save(S); FillSources(); }, refreshers));
            // a line between two projects, none above the first
            if (list.Children[0] is Border first) { first.BorderThickness = new Thickness(0); first.Padding = new Thickness(0, 4, 0, 10); }
        }
        FillSources();
        var add = Btn(L.T("+ Dodaj projekt"), "LinkButton", (_, _) =>
        {
            var used = new[] { acc.Notion }.Concat(acc.NotionMore).Select(l => l.Color).ToHashSet();
            acc.NotionMore.Add(new NotionLink { Color = Category.Palette.FirstOrDefault(c => !used.Contains(c)) ?? Category.Palette[0] });
            SettingsStore.Save(S);
            FillSources();
        });
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(-7, 0, 0, 0);
        projects.Children.Add(add);

        var syncRow = new DockPanel();
        var result = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Res("FgFaint"), Margin = new Thickness(10, 0, 0, 0), Text = link.LastResult ?? "" };
        Button? now = null;
        now = Btn(L.T("Synchronizuj teraz"), "SecondaryButton", async (_, _) =>
        {
            Keyboard.ClearFocus(); // the fields save on leaving them
            if (!link.Configured) { result.Text = L.T("Podaj link do bazy i token."); return; }
            now!.IsEnabled = false;
            result.Text = L.T("Łączę z Notion…");
            var r = await NotionSync.Run(acc.Id);
            now.IsEnabled = true;
            result.Text = link.LastResult ?? (r.Error ?? "");
            foreach (var refresh in refreshers) refresh();
        });
        now.Margin = new Thickness(0);
        DockPanel.SetDock(now, Dock.Left);
        syncRow.Children.Add(now);
        syncRow.Children.Add(result);

        p.Children.Add(Group("Notion", L.T("Wczytuje zadania z baz Notion do backlogu konta (tylko odczyt)."),
            tokenField,
            AutoSyncRow(link, every),
            Toggle(L.T("Filtry i sortowanie widoku z linku"), L.T("Zalecane: wczytywane jest to, co pokazuje widok w Notion."), link.UseViewFilter, v => { link.UseViewFilter = v; SettingsStore.Save(S); }),
            Row(L.T("Limit zadań"), L.T("Nie wczytuj, jeśli widok ma więcej zadań."), limit),
            Toggle(L.T("Tylko zadania przypisane do mnie"), L.T("Pole osoby zawiera moje konto."), link.OnlyMine, v => { link.OnlyMine = v; SettingsStore.Save(S); }),
            Toggle(L.T("Zakończone w Notion oznaczaj jako zrobione"), null, link.SyncDone, v => { link.SyncDone = v; SettingsStore.Save(S); }),
            syncRow));
        p.Children.Add(Group(L.T("Projekty i bazy Notion"), L.T("Każdy projekt ma krótką nazwę i kolor – widać je na jego zadaniach, a @nazwa (np. @FoA) przypisuje projekt własnemu zadaniu. Z linkiem do bazy wczytywane są jej zadania."), projects));
        return p;
    }

    /// <summary>"Synchronizuj automatycznie co [10 min] [switch]": the interval next to the switch it belongs to, shown while it is on.</summary>
    FrameworkElement AutoSyncRow(NotionLink link, ComboBox every)
    {
        every.Margin = new Thickness(0, 0, 12, 0);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(When(every, () => link.Auto));
        var sw = SwitchControl(L.T("Synchronizuj automatycznie"), link.Auto, v => { link.Auto = v; SettingsStore.Save(S); });
        right.Children.Add(sw);
        return Row(L.T("Synchronizuj automatycznie"), L.T("Przy starcie programu i co wybrany czas."), right);
    }

    /// <summary>Which Notion projects are unfolded, for as long as TaskWall runs.</summary>
    static readonly Dictionary<NotionLink, bool> OpenProjects = new();

    /// <summary>One Notion database: name + colour (marks its tasks), link, statuses to read, last result.</summary>
    FrameworkElement BuildNotionSource(Account acc, BoardStore store, NotionLink link, Action? remove, List<Action> refreshers)
    {
        var card = new Border { BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = new SolidColorBrush(Th.Ink(0x10)), Padding = new Thickness(0, 10, 0, 10) };
        // folded like the groups: a header with the project's colour, name and state; the fields under it when open
        var outer = new StackPanel();
        card.Child = outer;
        var p = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var dot = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        var headName = Label("", 13, "Fg", FontWeights.SemiBold);
        var headSub = Label("", 11.5, "FgFaint");
        headSub.TextTrimming = TextTrimming.CharacterEllipsis;
        headSub.Margin = new Thickness(0, 2, 0, 0);
        var chevron = new TextBlock { FontFamily = Ui.Font("IconFont"), FontSize = 10, Foreground = Ui.Res("FgDim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 2, 0) };
        var headGrid = new Grid();
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headNames = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { headName, headSub } };
        Grid.SetColumn(headNames, 1);
        Grid.SetColumn(chevron, 2);
        headGrid.Children.Add(dot);
        headGrid.Children.Add(headNames);
        headGrid.Children.Add(chevron);
        var header = new Border { Child = headGrid, Background = Brushes.Transparent, Cursor = Cursors.Hand, Focusable = true, Padding = new Thickness(0, 2, 0, 2) };
        outer.Children.Add(header);
        outer.Children.Add(p);
        // a new project (no name, no database) opens by itself; the others folded, or as they were left
        bool open = OpenProjects.TryGetValue(link, out var wasOpen) ? wasOpen : link.Name.Length == 0 && link.Database.Length == 0;
        void Head()
        {
            dot.Background = new SolidColorBrush(BoardWindow.ParseColor(link.Color));
            headName.Text = link.Name.Length > 0 ? link.Name : L.T("Projekt bez nazwy");
            headSub.Text = link.Database.Length == 0 ? L.T("bez bazy Notion – tylko nazwa i kolor")
                : link.LastResult is { Length: > 0 } last ? last
                : link.ViewName is { } view ? L.F("widok „{0}”", view) : L.T("baza Notion");
            p.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = open ? "\uE70E" : "\uE70D";
            System.Windows.Automation.AutomationProperties.SetName(header, headName.Text);
        }
        void Flip()
        {
            open = !open;
            OpenProjects[link] = open;
            Head();
            if (open) Anim.Enter(p, 0, -4, 160);
        }
        header.MouseLeftButtonUp += (_, _) => Flip();
        header.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space && ReferenceEquals(e.OriginalSource, header)) { Flip(); e.Handled = true; } };
        refreshers.Add(Head);

        var top = new DockPanel();
        var swatch = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = L.T("Zmień kolor") };
        void Paint() { swatch.Background = new SolidColorBrush(BoardWindow.ParseColor(link.Color)); dot.Background = swatch.Background; }
        Paint();
        swatch.MouseLeftButtonUp += (_, _) =>
        {
            int i = Array.IndexOf(Category.Palette, link.Color);
            link.Color = Category.Palette[(i + 1) % Category.Palette.Length];
            Paint();
            SettingsStore.Save(S);
            App.Board?.Rebuild();
        };
        DockPanel.SetDock(swatch, Dock.Left);
        top.Children.Add(swatch);
        var name = new TextBox { Style = (Style)Application.Current.Resources["FieldBox"], Text = link.Name, Width = 130, ToolTip = L.T("Krótka nazwa widoczna na zadaniach z tej bazy, np. FoA") };
        name.LostKeyboardFocus += (_, _) =>
        {
            var v = name.Text.Trim();
            if (v == link.Name) return;
            var old = link.Name;
            link.Name = v;
            SettingsStore.Save(S);
            Head();
            // tasks already on the board follow the new name right away (without a name yet: only when this is the one database)
            bool only = new[] { acc.Notion }.Concat(acc.NotionMore).Count(l => l.Database.Length > 0) <= 1;
            foreach (var t in store.Data.Tasks.Where(t => old.Length > 0 ? t.NotionSource == old : t.NotionId != null && t.NotionSource == null && only).ToList())
            {
                t.NotionSource = v.Length > 0 ? v : null;
                store.Changed(t);
            }
            App.Board?.Rebuild();
        };
        DockPanel.SetDock(name, Dock.Left);
        top.Children.Add(name);
        if (remove != null)
        {
            var del = Btn(L.T("Usuń projekt"), "LinkButton", (_, _) => remove());
            DockPanel.SetDock(del, Dock.Right);
            top.Children.Add(del);
        }
        top.Children.Add(new TextBlock());
        p.Children.Add(Pair(L.T("Nazwa"), top));

        var db = new TextBox { Style = (Style)Application.Current.Resources["FieldBox"], Text = link.Database, ToolTip = L.T("Link do bazy: ••• przy widoku bazy → Copy link to view") };
        var dbState = Label("", 11.5, "FgDim");
        void ShowDbState()
        {
            var v = db.Text.Trim();
            var id = NotionImport.ExtractId(v);
            dbState.Text = v.Length == 0 ? L.T("Wklej link do bazy.")
                : id == null ? L.T("To nie wygląda na link do bazy Notion.")
                : NotionSync.ViewId(v) != null ? L.F("Baza i widok rozpoznane{0}. Wczytywane są zadania spełniające filtry widoku.", link.ViewName != null ? $" („{link.ViewName}”)" : "")
                : L.T("Link bez widoku (…?v=…): wczytywana byłaby cała baza. Zalecany link do widoku z filtrami.");
            dbState.Foreground = v.Length > 0 && id == null ? new SolidColorBrush(Color.FromRgb(0xF2, 0xA6, 0x5A)) : Ui.Res("FgDim");
        }
        db.TextChanged += (_, _) => ShowDbState();
        db.LostKeyboardFocus += (_, _) => { if (db.Text.Trim() != link.Database) { if (NotionImport.ExtractId(db.Text.Trim()) != NotionImport.ExtractId(link.Database)) { link.KnownStatuses = null; link.StatusIds = null; link.StatusGroups = null; } link.Database = db.Text.Trim(); SettingsStore.Save(S); Head(); } };
        ShowDbState();
        p.Children.Add(Pair(L.T("Link do bazy"), db));
        p.Children.Add(dbState);

        // statuses that are not imported (known after the first sync)
        var statuses = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var result = Label(link.LastResult ?? "", 11, "FgFaint");
        void FillStatuses()
        {
            statuses.Children.Clear();
            result.Text = link.LastResult ?? "";
            ShowDbState();
            var known = link.KnownStatuses ?? new();
            if (known.Count == 0) { statuses.Children.Add(Label(L.T("Lista statusów pojawi się po pierwszej synchronizacji. Domyślnie pomijane są statusy zakończone."), 11, "FgFaint")); return; }
            // like the status filter of a Notion view: only the ticked statuses are read
            var readNow = link.ReadStatuses ?? known.Where(s => !(link.SkipStatuses ?? known.Where(NotionImport.LooksDone).ToList()).Contains(s)).ToList();
            foreach (var s in known)
            {
                var chip = new System.Windows.Controls.Primitives.ToggleButton { Content = s, Style = (Style)Application.Current.Resources["Chip"], IsChecked = readNow.Contains(s), Margin = new Thickness(0, 0, 6, 6), ToolTip = L.T("Zaznaczone statusy są wczytywane") };
                chip.Click += (_, _) =>
                {
                    var now = link.ReadStatuses ?? new List<string>(readNow);
                    if (chip.IsChecked == true) { if (!now.Contains(s)) now.Add(s); } else now.Remove(s);
                    link.ReadStatuses = now;
                    SettingsStore.Save(S);
                };
                statuses.Children.Add(chip);
            }
        }
        FillStatuses();
        refreshers.Add(FillStatuses);
        p.Children.Add(Pair(L.T("Wczytywane statusy"), statuses));

        var condState = Label("", 11.5, "FgDim");
        // extra conditions, one per line ("Dział != PM") – for what a view filters by but the API doesn't give out
        var cond = new TextBox
        {
            Style = (Style)Application.Current.Resources["FieldBox"], AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 34,
            Text = string.Join(Environment.NewLine, link.Conditions ?? new()),
            ToolTip = L.T("Jeden warunek w wierszu: Kolumna = wartość albo Kolumna != wartość, np. Dział != PM"),
        };
        cond.LostKeyboardFocus += (_, _) =>
        {
            var lines = cond.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var bad = lines.Where(l => Condition.Parse(l) == null).ToList();
            condState.Text = bad.Count > 0 ? L.F("Nie rozumiem: {0}", string.Join(", ", bad)) : "";
            var list = lines.Where(l => Condition.Parse(l) != null).ToList();
            link.Conditions = list.Count > 0 ? list : null;
            SettingsStore.Save(S);
        };
        p.Children.Add(Pair(L.T("Dodatkowe warunki"), cond, L.T("Jeden w wierszu, np. „Dział != PM” albo „Projekt = Mnich DLC”. Działają też na zadania już wczytane: nieruszane zadania z backlogu, które ich nie spełniają, znikają przy synchronizacji.")));
        p.Children.Add(condState);
        var usable = (link.KnownColumns ?? new()).Where(c => c.EndsWith(": select") || c.EndsWith(": multi_select") || c.EndsWith(": status") || c.EndsWith(": people") || c.EndsWith(": rich_text")).Select(c => c[..c.LastIndexOf(':')]).ToList();
        if (usable.Count > 0) p.Children.Add(Label(L.F("Kolumny tej bazy: {0}", string.Join(", ", usable)), 11, "FgFaint"));
        p.Children.Add(result);
        Head();
        return card;
    }

    void FillMarks(BoardStore store, StackPanel host, bool focusLast = false)
    {
        host.Children.Clear();
        var list = store.MarkTypes.Select(x => x.Clone()).ToList();
        void Save() { store.SetMarkTypes(list); App.Board?.Rebuild(); }
        TextBox? last = null;
        if (list.Count == 0) host.Children.Add(Label(L.T("Brak oznaczeń dni na tym koncie."), 11.5, "FgFaint"));
        for (int i = 0; i < list.Count; i++)
        {
            int index = i;
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var swatch = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(BoardWindow.ParseColor(list[i].Color)), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 10, 0), ToolTip = L.T("Zmień kolor") };
            swatch.MouseLeftButtonUp += (_, _) => PickColor(swatch, c => { list[index].Color = c; Save(); FillMarks(store, host); });
            DockPanel.SetDock(swatch, Dock.Left);
            row.Children.Add(swatch);
            var del = Btn(L.T("Usuń"), "LinkButton", (_, _) => { list.RemoveAt(index); Save(); FillMarks(store, host); });
            del.ToolTip = L.T("Usuwa oznaczenie z listy (dni już oznaczone zostają w danych)");
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);
            // a cycle (hybrid work: HO every Thursday and Friday …)
            var code0 = list[i].Code;
            var cycle = Btn(L.T("Cykl…"), "LinkButton", (_, _) => RepeatWindow.ForMark(store, null, DateTime.Today, code0));
            cycle.ToolTip = L.T("Powtarzaj to oznaczenie w wybrane dni tygodnia, np. praca hybrydowa: HO co czwartek i piątek");
            DockPanel.SetDock(cycle, Dock.Right);
            row.Children.Add(cycle);
            // frame (day keeps its own colours, e.g. HO) or a coloured day (e.g. BŚU)
            var style = Btn(list[i].Ring ? L.T("◯ ramka") : L.T("● tło"), "LinkButton", (_, _) => { list[index].Style = list[index].Ring ? "fill" : "ring"; Save(); FillMarks(store, host); });
            style.ToolTip = L.T("Ramka: dzień zachowuje kolory zadań (np. w widoku roku), widać tylko obrys i etykietę. Tło: cały dzień w kolorze oznaczenia.");
            DockPanel.SetDock(style, Dock.Right);
            row.Children.Add(style);
            // days a year (BŚU 27, sick leave 33 …): the year view counts what is used and what is left
            var allowance = new TextBox { Style = (Style)Application.Current.Resources["FieldBox"], Text = list[i].Allowance?.ToString() ?? "", Width = 50, Margin = new Thickness(6, 0, 4, 0), ToolTip = L.T("Dni w roku (np. 27) – widok 365 pokazuje wykorzystane i pozostałe; puste = nie liczyć") };
            void CommitAllowance()
            {
                int? v = int.TryParse(allowance.Text.Trim(), out var a) && a > 0 ? a : null;
                if (v == list[index].Allowance) return;
                list[index].Allowance = v;
                allowance.Text = v?.ToString() ?? "";
                Save();
            }
            allowance.LostKeyboardFocus += (_, _) => CommitAllowance();
            allowance.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitAllowance(); };
            // days left from earlier years (what the company says is still to take at the start of a year)
            int carryYear = list[i].CarryYear ?? DateTime.Today.Year;
            var carry = new TextBox { Style = (Style)Application.Current.Resources["FieldBox"], Text = list[i].CarryOver?.ToString() ?? "", Width = 44, Margin = new Thickness(6, 0, 4, 0),
                ToolTip = L.F("Zaległe dni z poprzednich lat na początek {0} roku – kolejne lata przenoszą resztę same; puste = brak", carryYear) };
            void CommitCarry()
            {
                int? v = int.TryParse(carry.Text.Trim(), out var a) && a > 0 ? a : null;
                if (v == list[index].CarryOver) return;
                list[index].CarryOver = v;
                list[index].CarryYear = v == null ? null : list[index].CarryYear ?? DateTime.Today.Year;
                carry.Text = v?.ToString() ?? "";
                Save();
            }
            carry.LostKeyboardFocus += (_, _) => CommitCarry();
            carry.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitCarry(); };
            var carryLabel = new TextBlock { Text = L.F("zaległe {0}", carryYear), FontSize = 11, Foreground = Ui.Res("FgFaint"), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(carryLabel, Dock.Right);
            DockPanel.SetDock(carry, Dock.Right);
            row.Children.Add(carryLabel);
            row.Children.Add(carry);
            var allowanceLabel = new TextBlock { Text = L.T("dni/rok"), FontSize = 11, Foreground = Ui.Res("FgFaint"), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(allowanceLabel, Dock.Right);
            DockPanel.SetDock(allowance, Dock.Right);
            row.Children.Add(allowanceLabel);
            row.Children.Add(allowance);
            var code = new TextBox { Style = (Style)Application.Current.Resources["FieldBox"], Text = list[i].Code, MaxLength = 8, Width = 90, Margin = new Thickness(0, 0, 6, 0), ToolTip = L.T("Kod w nagłówku dnia") };
            var label = new TextBox { Style = (Style)Application.Current.Resources["FieldBox"], Text = list[i].Label, MaxLength = 40, ToolTip = L.T("Opis (np. Home Office)") };
            void Commit()
            {
                var c = code.Text.Trim().ToUpper(BoardWindow.Pl);
                var l = label.Text.Trim();
                if (c.Length == 0) { code.Text = list[index].Code; return; }
                if (c == list[index].Code && l == list[index].Label) return;
                list[index].Code = c;
                list[index].Label = l;
                Save();
            }
            code.LostKeyboardFocus += (_, _) => Commit();
            label.LostKeyboardFocus += (_, _) => Commit();
            code.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
            label.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
            DockPanel.SetDock(code, Dock.Left);
            row.Children.Add(code);
            row.Children.Add(label);
            host.Children.Add(row);
            last = code;
        }
        // repeating marks of this account
        var series = store.Data.MarkRules.Where(r => !r.Deleted).ToList();
        if (series.Count > 0)
        {
            var t = Label(L.T("Powtarzane:"), 11, "FgDim", FontWeights.SemiBold);
            t.Margin = new Thickness(0, 8, 0, 2);
            host.Children.Add(t);
        }
        foreach (var r in series)
        {
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            var del = Btn(L.T("Usuń"), "LinkButton", (_, _) => { r.Deleted = true; store.Changed(r); App.Board?.Rebuild(); FillMarks(store, host); });
            var edit = Btn(L.T("Zmień…"), "LinkButton", (_, _) => RepeatWindow.ForMark(store, r, r.StartDate));
            DockPanel.SetDock(del, Dock.Right);
            DockPanel.SetDock(edit, Dock.Right);
            row.Children.Add(del);
            row.Children.Add(edit);
            row.Children.Add(new TextBlock { Text = $"{r.Text}  ·  {r.Summary}", Foreground = Ui.Res("Fg"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            host.Children.Add(row);
        }
        if (focusLast && last != null) last.Loaded += (_, _) => { last.Focus(); last.SelectAll(); };
    }

    /// <summary>Small palette popup under a swatch.</summary>
    static void PickColor(FrameworkElement anchor, Action<string> picked)
    {
        var wrap = new WrapPanel { Width = 6 * 30 };
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            Child = new Border { Background = Ui.Res("MenuBg"), BorderBrush = Ui.Res("FieldBorder"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(6), Child = wrap },
        };
        foreach (var hex in Category.Palette)
        {
            var b = new Border { Width = 22, Height = 22, Margin = new Thickness(4), CornerRadius = new CornerRadius(11), Cursor = Cursors.Hand, Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) };
            b.MouseLeftButtonUp += (_, _) => { popup.IsOpen = false; picked(hex); };
            wrap.Children.Add(b);
        }
        popup.IsOpen = true;
    }

    void UpdateStatus()
    {
        if (_syncStatus?.Tag is not BoardStore st) return;
        string saved = st.LastSaved is { } s ? s.ToString("HH:mm:ss") : "—";
        string ext = st.LastExternalChange is { } x ? x.ToString("HH:mm:ss") : "—";
        _syncStatus.Text = L.F("Ostatni zapis: {0}   ·   zmiany z innego komputera: {1}   ·   kopie dzienne w podfolderze backup", saved, ext);
    }

    void UpdateCalStatus()
    {
        if (_calStatus == null) return;
        var when = CalendarService.LastFetch is { } t ? t.ToString("HH:mm") : "—";
        _calStatus.Text = L.F("pobrano {0}  ·  wydarzeń: {1}, świąt: {2}", when, CalendarService.Count(false), CalendarService.Count(true))
            + (CalendarService.LastError != null ? L.F("\nBłąd: {0}", CalendarService.LastError) : "");
    }

    // ---------- other sections ----------
    //
    // Every section is a few groups: a small heading and one card of rows. A row is the setting's name with one short line under
    // it saying what it does, and the control on the right (a switch, a drop-down, a slider); a wide control (a list, a text
    // field) comes under the name. Options that change nothing in the current state are hidden, not indented: they appear in
    // the same card when they start to matter (see When / Sync).

    FrameworkElement BuildScreen()
    {
        var p = new StackPanel();
        var monitors = Monitors.All();
        var monCombo = new ComboBox { Width = 300, HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < monitors.Count; i++)
            monCombo.Items.Add(new ComboBoxItem { Content = monitors[i].Label(i), Tag = monitors[i].Device });
        int idx = monitors.FindIndex(m => m.Device == S.Monitor);
        monCombo.SelectedIndex = Math.Max(0, idx >= 0 ? idx : monitors.FindIndex(m => m.Primary));
        monCombo.SelectionChanged += (_, _) => { if (monCombo.SelectedItem is ComboBoxItem it) { S.Monitor = (string)it.Tag; App.Instance.ApplySettings(); } };
        // a second board on another screen, with a view of its own (picked on that board, remembered)
        var second = new ComboBox { Width = 300, HorizontalAlignment = HorizontalAlignment.Right };
        second.Items.Add(new ComboBoxItem { Content = L.T("Brak"), Tag = "" });
        for (int i = 0; i < monitors.Count; i++)
            second.Items.Add(new ComboBoxItem { Content = monitors[i].Label(i), Tag = monitors[i].Device });
        second.SelectedItem = second.Items.Cast<ComboBoxItem>().FirstOrDefault(it => (string)it.Tag == S.SecondMonitor) ?? second.Items[0];
        second.SelectionChanged += (_, _) => { if (second.SelectedItem is ComboBoxItem it) { S.SecondMonitor = (string)it.Tag; App.Instance.ApplySettings(); } };
        p.Children.Add(Group(L.T("Monitory"),
            Row(L.T("Monitor"), null, monCombo),
            Row(L.T("Drugi ekran"), L.T("Druga tablica z własnym widokiem, np. Dzień tutaj, a Timeline tam. Zmiany widać od razu na obu."), second)));
        p.Children.Add(Group(L.T("Rozmiar i położenie"),
            Row(L.T("Skala całości"), null, Slider(80, 220, S.UiScale * 100, v => S.UiScale = Math.Round(v) / 100, v => $"{v:0}%")),
            Row(L.T("Szerokość tablicy"), null, Slider(40, 100, S.WidthPercent, v => S.WidthPercent = v, v => $"{v:0}%")),
            Row(L.T("Wysokość tygodnia"), L.T("Do końca: od zegara aż do paska."), Slider(10, 100, S.WeekHeightPercent, v => S.WeekHeightPercent = v, v => v >= 99 ? L.T("do końca") : $"{v:0}%")),
            Row(L.T("Położenie w pionie"), null, Slider(0, 100, S.VerticalPercent, v => S.VerticalPercent = v, v => $"{v:0}%")),
            When(Row(L.T("Odstęp od krawędzi"), L.T("Przy prostych narożnikach tablica przylega do krawędzi bez odstępu."), Slider(0, 40, S.Gap, v => S.Gap = Math.Round(v), v => $"{Math.Round(v):0} px")), () => S.RoundCorners)));
        return p;
    }

    FrameworkElement BuildBoard()
    {
        var p = new StackPanel();
        // one choice for folding done tasks into "✓ 3 zrobione": never, on past days, everywhere
        var fold = Combo(new[] { ("none", L.T("nie")), ("past", L.T("w minionych dniach")), ("all", L.T("we wszystkich dniach")) },
            S.CollapseDone ? "all" : S.FoldPastDone ? "past" : "none", k => { S.CollapseDone = k == "all"; S.FoldPastDone = k == "past"; App.Instance.ApplySettings(); });
        p.Children.Add(Group(L.T("Widok"),
            Toggle(L.T("Pokazuj weekendy"), null, S.ShowWeekends, v => S.ShowWeekends = v),
            Toggle(L.T("Przycisk widoku roku (365 dni)"), null, S.ShowYearButton, v => S.ShowYearButton = v),
            Row(L.T("Zwijaj zrobione zadania"), L.T("Zrobione zadania dnia w jednej linii „✓ 3 zrobione” – rozwija ją kliknięcie."), fold)));
        p.Children.Add(Group(L.T("Zadania"),
            Toggle(L.T("Przenoś niezrobione zadania na dziś"), L.T("Niezrobione zadania z minionych dni same trafiają na dziś."), S.AutoRollover, v => S.AutoRollover = v),
            Toggle(L.T("Zadania w trakcie na kolejne dni"), L.T("W trakcie, review i testy przechodzą na kolejne dni robocze aż do zrobienia (bez weekendów, świąt i urlopu)."), S.StretchActive, v => S.StretchActive = v),
            Toggle(L.T("Podsumowanie poprzedniego tygodnia"), L.T("Przy pierwszym uruchomieniu w tygodniu."), S.WeeklySummary, v => S.WeeklySummary = v)));

        // default working hours (the day view; a day can have its own – drag the edges there)
        var hours = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        static TimeSpan? ParseHour(string s) =>
            TimeSpan.TryParseExact(s.Trim(), new[] { @"h\:mm", @"hh\:mm", "%h" }, System.Globalization.CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromHours(24) ? t : null;
        TextBox HourBox(Func<string> current, Action<string> set, bool end)
        {
            var box = new TextBox { Style = (Style)Application.Current.Resources["FieldBox"], Width = 70, Text = current() };
            box.LostKeyboardFocus += (_, _) =>
            {
                // the start before the end; a wrong entry goes back to what is set now
                var other = ParseHour(end ? S.WorkStart : S.WorkEnd);
                if (ParseHour(box.Text) is { } t && (other == null || (end ? t > other : t < other)))
                { box.Text = t.ToString(@"hh\:mm"); set(box.Text); App.Instance.ApplySettings(); }
                else box.Text = current();
            };
            return box;
        }
        hours.Children.Add(HourBox(() => S.WorkStart, v => S.WorkStart = v, false));
        hours.Children.Add(new TextBlock { Text = "  –  ", VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Res("FgDim") });
        hours.Children.Add(HourBox(() => S.WorkEnd, v => S.WorkEnd = v, true));
        // a lunch break on every working day ("12:00-12:30"; empty: none)
        var lunch = new TextBox { Style = (Style)Application.Current.Resources["FieldBox"], Width = 120, Text = S.Lunch, HorizontalAlignment = HorizontalAlignment.Right };
        lunch.LostKeyboardFocus += (_, _) =>
        {
            var text = lunch.Text.Trim().Replace("–", "-").Replace(" ", "");
            if (text.Length == 0) { S.Lunch = ""; App.Instance.ApplySettings(); }
            else if (BoardStore.ParsePeriod(text) is { } p && p.End - p.Start <= TimeSpan.FromHours(3))
            { S.Lunch = $"{p.Start:hh\\:mm}-{p.End:hh\\:mm}"; lunch.Text = S.Lunch; App.Instance.ApplySettings(); }
            else lunch.Text = S.Lunch; // not a time range: back to what is set
        };
        p.Children.Add(Group(L.T("Widok dnia"),
            Row(L.T("Godziny pracy"), L.T("Domyślne; każdy dzień może mieć własne – przeciągnij krawędzie na osi."), hours),
            Row(L.T("Przerwa na lunch"), L.T("Np. 12:00-12:30, co dzień roboczy; nie liczy się do pracy. Pusto: bez lunchu."), lunch),
            Toggle(L.T("Notatnik po lewej, oś godzin po prawej"), null, S.DayNotesLeft, v => S.DayNotesLeft = v)));

        p.Children.Add(Group(L.T("Backlog i archiwum"),
            Row(L.T("Backlog"), null, Combo(new[] { ("drawer", L.T("Wysuwany po kliknięciu")), ("pinned", L.T("Przypięty z prawej strony")) }, S.BacklogMode == "pinned" ? "pinned" : "drawer",
                k => { S.BacklogMode = k; App.Instance.ApplySettings(); }, 240)),
            When(Child(Row(L.T("Szerokość"), null, Slider(220, 560, S.BacklogWidth, v => S.BacklogWidth = v, v => $"{v:0} px"))), () => S.BacklogMode == "pinned")));
        return p;
    }

    FrameworkElement BuildLook()
    {
        var p = new StackPanel();
        // theme: applied when the app starts (all windows are built in it)
        // Glass: the dark tint over the blurred wallpaper (the theme TaskWall had so far, "dark" in the settings file); Dark: flat graphite
        var restartTheme = Btn(L.T("Uruchom ponownie"), "SecondaryButton", (_, _) => App.Instance.Restart());
        restartTheme.Visibility = Visibility.Collapsed;
        restartTheme.HorizontalAlignment = HorizontalAlignment.Right;
        restartTheme.Margin = new Thickness(0, 6, 0, 0);
        var theme = Combo(new[] { ("light", L.T("Jasny")), ("dark", L.T("Glass")), ("studio", L.T("Ciemny")), ("auto", L.T("Jasny albo Glass – z jasności tapety")) }, S.Theme,
            k => { S.Theme = k; SettingsStore.Save(S); restartTheme.Visibility = Visibility.Visible; }, 260);
        var themeBox = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        themeBox.Children.Add(theme);
        themeBox.Children.Add(restartTheme);

        var swatches = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        void Mark(Border sel) { foreach (Border b in swatches.Children) b.BorderThickness = new Thickness(0); sel.BorderThickness = new Thickness(2); }
        var auto = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand,
            BorderBrush = Ui.Res("Fg"), BorderThickness = new Thickness(S.Accent == "auto" ? 2 : 0),
            Background = new LinearGradientBrush(Color.FromRgb(0xF2, 0xA6, 0x5A), Color.FromRgb(0x5B, 0x8C, 0xFF), 45),
            ToolTip = L.T("Automatycznie z tapety"),
            Child = new TextBlock { Text = "A", FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        auto.MouseLeftButtonUp += (_, _) => { S.Accent = "auto"; Mark(auto); App.Instance.ApplySettings(); };
        swatches.Children.Add(auto);
        foreach (var hex in new[] { "#5B8CFF", "#8B6CFF", "#E26D9A", "#F0A04B", "#4CC38A", "#3BB8C9", "#D9DCE3" })
        {
            var sw = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(6, 0, 0, 0),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)), Cursor = Cursors.Hand,
                BorderBrush = Ui.Res("Fg"), BorderThickness = new Thickness(string.Equals(S.Accent, hex, StringComparison.OrdinalIgnoreCase) ? 2 : 0),
            };
            sw.MouseLeftButtonUp += (_, _) => { S.Accent = hex; Mark(sw); App.Instance.ApplySettings(); };
            swatches.Children.Add(sw);
        }
        p.Children.Add(Group(L.T("Styl"),
            Row(L.T("Motyw"), L.T("Jasny i Glass: szkło z odcieniem tapety. Ciemny: neutralny grafit, cienkie linie. Zmiana po ponownym uruchomieniu."), themeBox),
            Row(L.T("Kolor akcentu"), null, swatches),
            Toggle(L.T("Zaokrąglone narożniki"), L.T("Wyłączone: proste rogi, a tablica i paski przylegają do krawędzi ekranu."), S.RoundCorners, v => S.RoundCorners = v),
            When(Toggle(L.T("Poświata"), L.T("Miękka łuna w kolorze akcentu u góry tablicy."), S.Glow, v => S.Glow = v), () => !Th.Studio),
            Toggle(L.T("Animacje"), null, S.Animations, v => S.Animations = v)));

        var refresh = Btn(L.T("Odśwież"), "SecondaryButton", (_, _) => App.Instance.RefreshWallpaper());
        refresh.Margin = new Thickness(0);
        var background = Combo(new[] { ("frosted", L.T("Matowe szkło")), ("glass", L.T("Przezroczyste szkło")), ("solid", L.T("Pełne, bez przezroczystości")) },
            S.Opaque ? "solid" : S.Blur ? "frosted" : "glass", k => { S.Opaque = k == "solid"; if (k != "solid") S.Blur = k == "frosted"; App.Instance.ApplySettings(); }, 240);
        p.Children.Add(Group(L.T("Tło"),
            Row(L.T("Tło tablicy"), L.T("Matowe: rozmyta tapeta pod szkłem. Przezroczyste: ostra tapeta. Pełne: jednolity kolor."), background),
            When(Child(Row(L.T("Przyciemnienie"), null, Slider(0, 95, S.TintOpacity * 100, v => S.TintOpacity = v / 100, v => $"{v:0}%"))), () => !S.Opaque),
            When(Child(Row(L.T("Rozmycie"), null, Slider(0, 100, S.BlurStrength, v => S.BlurStrength = v, v => $"{v:0}"))), () => !S.Opaque && S.Blur),
            Row(L.T("Wzór"), null, Combo(new[] { ("none", L.T("brak")), ("fabric", L.T("materiał (szachownica pod kątem 45°)")), ("dots", L.T("drobne kropki")), ("grid", L.T("drobna kratka")) }, S.Pattern,
                k => { S.Pattern = k; App.Instance.ApplySettings(); }, 290)),
            When(Child(Row(L.T("Widoczność"), null, Slider(0, 300, S.PatternVisibility * 100, v => S.PatternVisibility = v / 100, v => $"{v:0}%"))), () => S.Pattern != "none"),
            When(Child(Row(L.T("Skala"), null, Slider(25, 100, S.PatternScale * 100, v => S.PatternScale = v / 100, v => $"{v:0}%"))), () => S.Pattern != "none"),
            When(Row(L.T("Tapeta"), L.T("Wczytaj ją ponownie, np. po zmianie przez pokaz slajdów albo Spotlight."), refresh), () => !S.Opaque)));


        // the task state: a bar that fills up, or a round icon (the shape says the state, the colours help)
        p.Children.Add(Group(L.T("Zadania i przyciski"),
            Row(L.T("Znacznik stanu zadania"), L.T("Pasek wypełnia się z postępem; koło jak w Linear / Jira – czytelne też bez kolorów."),
                Combo(new[] { ("bar", L.T("Pasek (wypełnia się)")), ("circle", L.T("Koło (jak Linear / Jira)")) }, S.StateIcon, k => { S.StateIcon = k; App.Instance.ApplySettings(); }, 220)),
            Row(L.T("Rozmiar tekstu zadań"), null, Slider(10, 17, S.FontSize, v => S.FontSize = Math.Round(v * 2) / 2, v => $"{Math.Round(v * 2) / 2:0.0}")),
            Toggle(L.T("Podpisy przy ikonach górnego paska"), null, S.BarCaptions, v => S.BarCaptions = v)));
        return p;
    }

    /// <summary>
    /// Settings → Pasek i dock: one choice of the bar (none, the small one over the board, the bar with the dock that takes the place
    /// of Windows' taskbar); under it only what applies to the bar chosen.
    /// </summary>
    FrameworkElement BuildBar()
    {
        var p = new StackPanel();
        bool Full() => S.AppBar;
        bool AnyBar() => S.AppBar || S.ShowClock;
        string Kind() => S.AppBar ? "full" : S.ShowClock ? "board" : "none";

        var kindHint = Label("", 11.5, "FgFaint");
        kindHint.TextWrapping = TextWrapping.Wrap;
        void KindHint() => kindHint.Text = Kind() switch
        {
            "full" => L.T("Pasek przez cały ekran zamiast paska zadań Windows: Start, wyszukiwarka, dock, zasobnik, sieć, głośność, zegar. Windows rezerwuje dla niego miejsce, przy pełnym ekranie się chowa."),
            "board" => L.T("Wąski pasek tak szeroki jak tablica: pogoda, godzina z kalendarzem, obciążenie."),
            _ => L.T("Bez paska – sama tablica."),
        };
        KindHint();
        var kind = Combo(new[] { ("none", L.T("Brak paska")), ("board", L.T("Nad tablicą")), ("full", L.T("Z dockiem – zamiast paska zadań")) }, Kind(), tag =>
        {
            S.AppBar = tag == "full";
            S.ShowClock = tag != "none";
            KindHint();
            App.Instance.ApplySettings();
        }, 260);
        p.Children.Add(Group(null, RowWith(L.T("Pasek"), kindHint, kind)));

        p.Children.Add(When(Group(L.T("Pasek z dockiem"),
            Row(L.T("Położenie"), null, Combo(new[] { ("top", L.T("Góra ekranu")), ("bottom", L.T("Dół ekranu")) }, S.AppBarTop ? "top" : "bottom",
                tag => { S.AppBarTop = tag == "top"; App.Instance.ApplySettings(); })),
            Row(L.T("Wysokość"), L.T("Ikony docka: 36, 28 albo 20 px; napisy i kółka rosną razem z paskiem."), Combo(new[] { ("large", L.T("Duży")), ("medium", L.T("Średni")), ("small", L.T("Mały")) }, S.BarSize,
                tag => { S.BarSize = tag; App.Instance.ApplySettings(); })),
            Row(L.T("Szerokość"), L.T("Szkło paska równo z krawędziami tablicy albo od brzegu do brzegu ekranu."), Combo(new[] { ("board", L.T("Jak tablica")), ("screen", L.T("Cały ekran")) }, S.BarWidth,
                tag => { S.BarWidth = tag; App.Instance.ApplySettings(); })),
            Row(L.T("Wyszukiwarka"), L.T("Lupka na środku paska. TaskWall: pole tuż nad nią – programy, kalkulator, internet, pliki. Pozostałe otwiera ich skrót (paleta i PowerToys Run wymagają PowerToys)."),
                Combo(new[] { ("TaskWall", L.T("TaskWall – pole przy lupce")), ("Win+Alt+Space", L.T("Paleta poleceń PowerToys (Win+Alt+Spacja)")), ("Alt+Space", L.T("PowerToys Run (Alt+Spacja)")), ("Win+S", L.T("Wyszukiwanie Windows (Win+S)")) }, S.SearchKeys,
                    tag => { S.SearchKeys = tag; App.Instance.ApplySettings(); }, 300)),
            Toggle(L.T("Ukryj pasek zadań Windows"), L.T("Wraca, gdy wyłączysz ten pasek albo zamkniesz TaskWall – także po awarii."), S.HideTaskbar, v => S.HideTaskbar = v)), Full));

        p.Children.Add(When(SwitchGroup(L.T("Dock"), L.T("Przypięte i uruchomione programy na środku paska."), S.Dock, v => S.Dock = v, DockSettings().ToArray()), Full));

        // the load rings: one tick per kind, the order stays CPU, GPU, RAM, disk, network
        var meters = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var names = new Dictionary<string, string> { ["cpu"] = "CPU", ["gpu"] = "GPU", ["ram"] = "RAM", ["disk"] = L.T("Dyski"), ["net"] = L.T("Sieć") };
        foreach (var kindName in SysMeters.Kinds)
        {
            var k = kindName;
            var chip = new System.Windows.Controls.Primitives.ToggleButton { Content = names[k], Style = (Style)Application.Current.Resources["Chip"], IsChecked = S.Meters.Split(',').Contains(k), Margin = new Thickness(6, 0, 0, 0) };
            chip.Click += (_, _) =>
            {
                var set = S.Meters.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
                if (chip.IsChecked == true) set.Add(k); else set.Remove(k);
                S.Meters = string.Join(",", SysMeters.Kinds.Where(set.Contains));
                App.Instance.ApplySettings();
                Sync();
            };
            meters.Children.Add(chip);
        }
        p.Children.Add(When(Group(L.T("Na pasku"),
            Toggle(L.T("Pogoda teraz"), L.T("Miejscowość ustawiasz w sekcji Pogoda; odświeżana co 30 minut."), S.BarWeather, v => S.BarWeather = v),
            Row(L.T("Obciążenie"), L.T("Kręgi jak w Menedżerze zadań, odczyt co 2 s, tylko gdy pasek jest widoczny."), meters),
            When(Child(Row(L.T("Kolory kółek"), L.T("Stopniowo: od spokojnej szarości do swojego koloru wraz z obciążeniem. Zawsze: każde w swoim kolorze."),
                Combo(new[] { ("high", L.T("Stopniowo z obciążeniem")), ("always", L.T("Zawsze")) }, S.MeterColors, tag => { S.MeterColors = tag; App.Instance.ApplySettings(); }))), () => S.Meters.Length > 0),
            Toggle(L.T("Zegar 24-godzinny"), null, S.Use24h, v => S.Use24h = v)), AnyBar));
        return p;
    }

    /// <summary>A drop-down of (tag, label) choices; <paramref name="set"/> gets the chosen tag, then the dependent rows follow.</summary>
    ComboBox Combo((string Tag, string Label)[] items, string current, Action<string> set, double width = 200)
    {
        var c = new ComboBox { Width = width, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (tag, label) in items) c.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        c.SelectedItem = c.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == current) ?? c.Items[0];
        c.SelectionChanged += (_, _) => { if (c.SelectedItem is ComboBoxItem it) { set((string)it.Tag); Sync(); } };
        return c;
    }

    /// <summary>The dock's rows: on / off, then (while on) the magnification, the thin dock, the counters, the pinned programs.</summary>
    IEnumerable<FrameworkElement> DockSettings()
    {
        yield return Row(L.T("Powiększanie ikon"), L.T("Ikona pod kursorem rośnie, sąsiednie trochę mniej – jak w docku macOS."),
            Combo(new[] { ("off", L.T("Wyłączone")), ("subtle", L.T("Subtelne (1,25×)")), ("full", L.T("Pełne (1,55×)")) }, S.DockMagnify, tag => { S.DockMagnify = tag; App.Instance.ApplySettings(); }));
        yield return Toggle(L.T("Liczniki powiadomień"), L.T("Kółko w kolorze akcentu z liczbą powiadomień czekających w programie (Centrum powiadomień Windows, licznik programu, tytuł okna)."), S.DockBadges, v => S.DockBadges = v);
        yield return Toggle(L.T("Nieuruchomione programy w skali szarości"), L.T("Przypięte programy, które nie działają, są szare; przy uruchamianiu nabierają koloru."), S.DockGrayIdle, v => S.DockGrayIdle = v);
        yield return Toggle(L.T("Win+1…9 przełącza programy docka"), L.T("Jak na pasku zadań: Win+cyfra przywołuje albo uruchamia n-ty program, Shift+Win+cyfra otwiera nowe okno."), S.DockWinKeys, v => { S.DockWinKeys = v; App.Instance.ApplySettings(); });
        yield return Toggle(L.T("Otwieraj stos najechaniem"), L.T("Po chwili (0,4 s) bez klikania; zamyka się, gdy kursor odejdzie od stosu i wyspy."), S.StackHover, v => S.StackHover = v);
        var pins = new StackPanel();
        void FillPins()
        {
            pins.Children.Clear();
            foreach (var pin in S.DockPins.ToList())
            {
                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                var remove = Btn(L.T("Odepnij"), "LinkButton", (_, _) => { S.DockPins.Remove(pin); App.Instance.ApplySettings(); FillPins(); });
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);
                row.Children.Add(new TextBlock { Text = (pin.Name.Length > 0 ? pin.Name : pin.Key) + (pin.Items != null ? "  ·  " + L.F("stos: {0}", pin.Items.Count) : ""), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = pin.Exe ?? pin.AppId, Foreground = Ui.Res("Fg") });
                pins.Children.Add(row);
            }
            if (S.DockPins.Count == 0) pins.Children.Add(Label(L.T("Nic nie jest przypięte."), 11.5, "FgFaint"));
            var add = Btn(L.T("Dodaj program…"), "SecondaryButton", (_, _) => { if (AddPin()) FillPins(); });
            // a stack: a tile on the dock that opens an island of programs and nested stacks over the bar
            var addStack = Btn(L.T("Dodaj stos…"), "SecondaryButton", (_, _) =>
            {
                if (AskWindow.Ask(L.T("Nowy stos"), L.T("Nazwa stosu:"), L.T("Stos")) is not { Length: > 0 } n) return;
                S.DockPins.Add(StackIsland.NewStack(n.Trim()));
                App.Instance.ApplySettings();
                FillPins();
            });
            // the stacks to a file and back (another computer, a backup): the import replaces or adds, asking about a stack it already has
            var export = Btn(L.T("Eksportuj stosy…"), "LinkButton", (_, _) => StackIsland.ExportStacks(this));
            export.IsEnabled = S.DockPins.Any(p => p.Items != null);
            var import = Btn(L.T("Importuj stosy…"), "LinkButton", (_, _) => { if (StackIsland.ImportStacks(this)) { App.Instance.ApplySettings(); FillPins(); } });
            var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var b in new FrameworkElement[] { add, addStack, export, import }) { b.Margin = new Thickness(0, 0, 8, 4); b.VerticalAlignment = VerticalAlignment.Center; buttons.Children.Add(b); }
            pins.Children.Add(buttons);
        }
        FillPins();
        yield return Stacked(L.T("Przypięte programy"), L.T("Kliknięcie ikony przywołuje program, ponowne go minimalizuje; środkowy przycisk otwiera nowe okno, prawy przypina i zamyka. Stos to kafelek z wyspą programów i zagnieżdżonych stosów – programy dodasz jego „+” albo prawym przyciskiem na ikonie w docku. Kolejność zmieniasz przeciągając ikony."), pins);
    }

    /// <summary>A program file or a shortcut chosen by hand → pinned to the dock (a shortcut by the program it points to, so its windows group under it).</summary>
    bool AddPin()
    {
        if (StackIsland.PickProgram(this) is not { } pin) return false;
        if (S.DockPins.Any(p => string.Equals(p.Key, pin.Key, StringComparison.OrdinalIgnoreCase))) return false;
        S.DockPins.Add(pin);
        App.Instance.ApplySettings();
        return true;
    }

    FrameworkElement BuildGeneral()
    {
        var p = new StackPanel();
        // language: applied after a restart (all windows are built in it)
        var restart = Btn(L.T("Uruchom ponownie"), "SecondaryButton", (_, _) => App.Instance.Restart());
        restart.Visibility = Visibility.Collapsed;
        restart.Margin = new Thickness(0, 0, 8, 0);
        var lang = Combo(new[] { ("pl", "Polski"), ("en", "English") }, S.Language == "en" ? "en" : "pl", k =>
        {
            S.Language = k;
            SettingsStore.Save(S);
            restart.Visibility = S.Language == (L.En ? "en" : "pl") ? Visibility.Collapsed : Visibility.Visible;
        }, 160);
        var langRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        langRow.Children.Add(restart);
        langRow.Children.Add(lang);

        var warn = Label(L.T("Ten skrót jest zajęty przez inny program – wybierz inny."), 11.5, "FgDim");
        warn.Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xA6, 0x5A));
        warn.TextWrapping = TextWrapping.Wrap;
        warn.MaxWidth = 240;
        warn.Margin = new Thickness(0, 4, 0, 0);
        void UpdateWarn() => warn.Visibility = App.Instance.HotkeyTaken ? Visibility.Visible : Visibility.Collapsed;
        var hot = Combo(new[] { ("Ctrl+Shift+Space", L.T("Ctrl + Shift + Spacja")), ("Ctrl+Alt+D", "Ctrl + Alt + D"), ("Ctrl+Alt+T", "Ctrl + Alt + T"), ("Win+Shift+D", "Win + Shift + D"), ("", L.T("Wyłączony")) }, S.Hotkey,
            k => { S.Hotkey = k; App.Instance.ApplySettings(); UpdateWarn(); }, 200);
        UpdateWarn();
        var hotBox = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        hotBox.Children.Add(hot);
        hotBox.Children.Add(warn);

        p.Children.Add(Group(L.T("Program"),
            Row("Język / Language", null, langRow),
            Toggle(L.T("Uruchamiaj razem z Windows"), null, S.StartWithWindows, v => S.StartWithWindows = v),
            Row(L.T("Skrót klawiszowy"), L.T("Pokazuje tablicę na wierzchu z polem nowego zadania na dziś; Esc chowa."), hotBox),
            Toggle(L.T("Otwieraj linki Notion w aplikacji Notion"), L.T("Zamiast w przeglądarce."), S.OpenNotionInApp, v => S.OpenNotionInApp = v)));
        var remind = Combo(new[] { ("-1", L.T("Wyłączone")), ("0", L.T("W chwili rozpoczęcia")), ("5", L.T("5 min przed")), ("10", L.T("10 min przed")), ("15", L.T("15 min przed")) },
            S.TaskReminder.ToString(), k => { S.TaskReminder = int.Parse(k); SettingsStore.Save(S); AlarmService.Reschedule(); });
        p.Children.Add(Group(L.T("Alarmy i przypomnienia"),
            Toggle(L.T("Dźwięk alarmów"), null, S.AlarmSound, v => S.AlarmSound = v),
            Row(L.T("Przypomnienie o zadaniach z godziną"), L.T("Zadania z godziną w treści, np. „14-16 przegląd”."), remind)));
        return p;
    }

    FrameworkElement BuildInstall()
    {
        var p = new StackPanel();
        var installed = Installer.IsInstalled;
        var btn = installed
            ? Btn(L.T("Odinstaluj…"), "SecondaryButton", (_, _) => Installer.Uninstall(this))
            : Btn(L.T("Zainstaluj w systemie"), "PrimaryButton", (_, _) => Installer.InstallFromRunningCopy(this));
        btn.Margin = new Thickness(0);
        // updates through the cloud folder
        var setup = Updater.SetupPath;
        var shared = setup != null ? Updater.VersionOf(setup) : null;
        var check = Btn(L.T("Sprawdź"), "SecondaryButton", (_, _) => Updater.Check(askAgain: true));
        check.Margin = new Thickness(0);
        p.Children.Add(Group(L.T("Program"),
            Row(L.F("Wersja {0}", Installer.Version), installed
                ? L.F("Zainstalowany w {0}. Odinstalujesz go stąd albo w Ustawieniach Windows → Aplikacje.", Installer.InstallDir)
                : L.T("Ta kopia nie jest zainstalowana. Instalacja kopiuje program do folderu użytkownika, dodaje skrót w menu Start i wpis w „Aplikacje i funkcje” (bez uprawnień administratora)."), btn),
            Row(L.T("Aktualizacje"), setup == null ? L.T("Brak folderu danych.")
                : shared == null ? L.F("Brak instalatora w folderze danych ({0}). Zainstalowana kopia zapisuje go automatycznie.", setup)
                : L.F("Instalator w folderze danych – wersja {0}. Komputery ze starszą wersją proponują aktualizację przy starcie.", shared.ToString(3)), check)));

        // backup: everything in one ZIP; restoring merges (the newer version of each task wins)
        var bRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var exp = Btn(L.T("Zapisz kopię…"), "SecondaryButton", (_, _) =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = Backup.DefaultName, Filter = "ZIP (*.zip)|*.zip", InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents") };
            if (dlg.ShowDialog(this) != true) return;
            try { Backup.Export(dlg.FileName); MessageBox.Show(this, L.F("Zapisano kopię:\n{0}", dlg.FileName), "TaskWall"); }
            catch (Exception ex) { Log.Error("backup", ex); MessageBox.Show(this, L.T("Nie udało się zapisać pliku:") + "\n" + ex.Message, "TaskWall"); }
        });
        exp.Margin = new Thickness(0, 0, 8, 0);
        bRow.Children.Add(exp);
        var imp = Btn(L.T("Przywróć z kopii…"), "SecondaryButton", (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "ZIP (*.zip)|*.zip" };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                int n = Backup.Restore(dlg.FileName);
                App.Board?.Rebuild();
                MessageBox.Show(this, n > 0 ? L.F("Scalono zadania z kopii ({0} kont). Ctrl+Z na tablicy cofa.", n) : L.T("W tej kopii nie ma danych kont."), "TaskWall");
            }
            catch (Exception ex) { Log.Error("restore backup", ex); MessageBox.Show(this, L.T("Nie udało się odczytać kopii:") + "\n" + ex.Message, "TaskWall"); }
        });
        imp.Margin = new Thickness(0);
        bRow.Children.Add(imp);
        p.Children.Add(Group(L.T("Kopia zapasowa"),
            Stacked(L.T("Wszystkie konta w jednym pliku ZIP"), L.T("Zadania, kopie dzienne, raporty i ustawienia. Przywracanie scala zadania z tablicą – nowsza wersja każdego zadania wygrywa."), bRow)));
        var where = Label(L.T("Dane: foldery kont  ·  ustawienia: %APPDATA%\\TaskWall"), 11, "FgFaint");
        where.Margin = new Thickness(2, 8, 0, 0);
        p.Children.Add(where);
        return p;
    }

    // ---------- small builders ----------

    /// <summary>Rows shown only while their condition holds (an option that changes nothing now is hidden): checked again by <see cref="Sync"/>.</summary>
    readonly List<(FrameworkElement El, Func<bool> When, string Scope)> _when = new();

    /// <summary>
    /// A card of rows that folds like a drop-down: its header (the name; folded, what is inside or what it is for; a switch when the
    /// whole group is one feature that can be off) and the rows under it, a fine line between two of them – none above a row that
    /// belongs to the one before it (<see cref="Child"/>).
    /// </summary>
    sealed class GroupInfo
    {
        public FrameworkElement Outer = null!;
        public Border? Header;
        public Border Body = null!;
        public Panel Rows = null!;
        public TextBlock? Sub, Chevron;
        public string? Hint;
        public string Key = "", Scope = "";
        public Func<bool>? On;
        public bool Open;
    }

    readonly List<GroupInfo> _groups = new();
    /// <summary>Which groups are open (by section and name), for as long as TaskWall runs: a group opened stays open the next time.</summary>
    static readonly Dictionary<string, bool> OpenGroups = new();
    /// <summary>Rows that belong to the row before them (a slider of a choice above it): no line between them.</summary>
    readonly HashSet<FrameworkElement> _children = new();
    /// <summary>The section (or the account's / connection's body) being built: the groups' keys and what a rebuild forgets.</summary>
    string _scope = "";
    int _ordinal;
    bool _syncQueued;

    void BeginScope(string scope)
    {
        // a body built again (another account): what the old one registered goes
        _groups.RemoveAll(g => g.Scope == scope);
        _when.RemoveAll(w => w.Scope == scope);
        _scope = scope;
        _ordinal = 0;
    }

    T When<T>(T el, Func<bool> when) where T : FrameworkElement
    {
        _when.Add((el, when, _scope));
        el.Visibility = when() ? Visibility.Visible : Visibility.Collapsed;
        return el;
    }

    /// <summary>A row that belongs to the row before it (shown with it, no line between them).</summary>
    T Child<T>(T el) where T : FrameworkElement
    {
        _children.Add(el);
        return el;
    }

    void SyncSoon()
    {
        if (_syncQueued) return;
        _syncQueued = true;
        Dispatcher.BeginInvoke(() => { _syncQueued = false; Sync(); }, DispatcherPriority.Loaded);
    }

    /// <summary>A setting changed: the rows that depend on it appear or go, the lines between rows and the folded headers follow.</summary>
    void Sync()
    {
        foreach (var (el, when, _) in _when) el.Visibility = when() ? Visibility.Visible : Visibility.Collapsed;
        foreach (var g in _groups)
        {
            bool first = true;
            var names = new List<string>();
            foreach (FrameworkElement r in g.Rows.Children)
            {
                if (r.Visibility != Visibility.Visible) continue;
                if (r is Border b) b.BorderThickness = new Thickness(0, first || _children.Contains(b) ? 0 : 1, 0, 0);
                if (r is Border { Child: FrameworkElement { Tag: string name } } && !_children.Contains(r)) names.Add(name);
                first = false;
            }
            bool on = g.On?.Invoke() ?? true;
            bool open = g.Header == null || g.Open && on;
            if (first && g.On == null) g.Outer.Visibility = Visibility.Collapsed; // nothing to show (a hidden group's own When decides otherwise)
            else if (!_when.Any(w => ReferenceEquals(w.El, g.Outer))) g.Outer.Visibility = Visibility.Visible;
            g.Body.Visibility = open && !first ? Visibility.Visible : Visibility.Collapsed;
            if (g.Header != null)
            {
                double r = Th.Corner(8);
                g.Header.CornerRadius = g.Body.Visibility == Visibility.Visible ? new CornerRadius(r, r, 0, 0) : new CornerRadius(r);
                g.Header.Cursor = on && !first ? Cursors.Hand : null;
                if (g.Chevron != null)
                {
                    g.Chevron.Visibility = on && !first ? Visibility.Visible : Visibility.Hidden;
                    g.Chevron.Text = open ? "" : "";
                }
                System.Windows.Automation.AutomationProperties.SetItemStatus(g.Header, open ? L.T("rozwinięta") : L.T("zwinięta"));
            }
            if (g.Sub != null)
            {
                // open: what the group is for (if it says); folded: that, or the names of what is inside
                string text = open ? g.Hint ?? "" : g.Hint ?? string.Join(", ", names.Select((n, i) => i == 0 ? n : Lower(n)));
                g.Sub.Text = text;
                g.Sub.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    /// <summary>"Kolor akcentu" → "kolor akcentu" in a list after a comma (names like "CPU" or "Notion" stay).</summary>
    static string Lower(string s) => s.Length > 1 && char.IsUpper(s[0]) && char.IsLower(s[1]) ? char.ToLower(s[0], L.Culture) + s[1..] : s;

    /// <summary>A group of rows (no name: always open, for a section's first choice).</summary>
    FrameworkElement Group(string? title, params FrameworkElement[] rows) => MakeGroup(title, null, null, rows);

    /// <summary>A group with a short line under its name saying what it is for (accounts, connections).</summary>
    FrameworkElement Group(string? title, string? hint, params FrameworkElement[] rows) => MakeGroup(title, hint, null, rows);

    /// <summary>A group that is one feature with a switch in its header (the dock, the forecast): its rows only while it is on.</summary>
    FrameworkElement SwitchGroup(string title, string? hint, bool value, Action<bool> set, params FrameworkElement[] rows)
    {
        bool current = value;
        GroupInfo? info = null;
        var sw = SwitchControl(title, value, v =>
        {
            current = v;
            set(v);
            if (v && info != null) { info.Open = true; OpenGroups[info.Key] = true; } // switched on: its options at hand
        });
        var outer = MakeGroup(title, hint, (sw, () => current), rows);
        info = _groups[^1];
        return outer;
    }

    void SetOpen(GroupInfo g, bool open)
    {
        if (g.Open == open) return;
        g.Open = open;
        OpenGroups[g.Key] = open;
        Sync();
        if (open) Anim.Enter(g.Body, 0, -4, 160);
    }

    FrameworkElement MakeGroup(string? title, string? hint, (System.Windows.Controls.Primitives.ToggleButton Switch, Func<bool> On)? toggle, FrameworkElement[] rows)
    {
        var g = new GroupInfo { Hint = hint, Scope = _scope, On = toggle?.On };
        var outer = new StackPanel { Margin = new Thickness(0, title == null ? 4 : 8, 0, 0) };
        var card = new Border
        {
            CornerRadius = new CornerRadius(Th.Corner(8)), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Th.Ink(0x12)),
            Background = new SolidColorBrush(Th.Ink(0x07)),
        };
        var inside = new StackPanel();
        card.Child = inside;
        outer.Children.Add(card);

        var list = new StackPanel();
        bool needed = false;
        foreach (var r in rows)
        {
            // each row in its own frame: the line above it (Sync leaves it off the first visible one and off a row's own child)
            bool child = _children.Contains(r);
            var frame = new Border { Child = r, Padding = child ? new Thickness(14, 0, 14, 10) : new Thickness(14, 10, 14, 10), BorderBrush = new SolidColorBrush(Th.Ink(0x10)), Visibility = r.Visibility };
            if (child) { _children.Add(frame); Dim(r); }
            int i = _when.FindIndex(w => ReferenceEquals(w.El, r));
            if (i >= 0) { _when[i] = (frame, _when[i].When, _when[i].Scope); r.Visibility = Visibility.Visible; }
            list.Children.Add(frame);
            needed |= HasNeeded(r);
        }
        g.Rows = list;
        g.Body = new Border { Child = list };

        if (title != null)
        {
            // the header: the name, under it a short line, the switch (if any) and the chevron on the right
            g.Key = _scope + "/" + title;
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = Label(title, 13, "Fg", FontWeights.SemiBold);
            names.Children.Add(name);
            g.Sub = Label("", 11.5, "FgFaint");
            g.Sub.TextTrimming = TextTrimming.CharacterEllipsis;
            g.Sub.Margin = new Thickness(0, 2, 0, 0);
            names.Children.Add(g.Sub);
            head.Children.Add(names);
            if (toggle is { } t)
            {
                t.Switch.Margin = new Thickness(18, 0, 0, 0);
                t.Switch.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(t.Switch, 1);
                head.Children.Add(t.Switch);
            }
            g.Chevron = new TextBlock { FontFamily = Ui.Font("IconFont"), FontSize = 10, Foreground = Ui.Res("FgDim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 2, 0) };
            Grid.SetColumn(g.Chevron, 2);
            head.Children.Add(g.Chevron);
            var header = new Border { Child = head, Padding = new Thickness(14, 11, 14, 11), Background = Brushes.Transparent, Focusable = true };
            System.Windows.Automation.AutomationProperties.SetName(header, title);
            Anim.HoverBackground(header, Colors.Transparent, Th.Ink(0x0A));
            g.Header = header;
            // the first group of a section opens by itself (and one that still needs something filled in), the rest folded
            g.Open = OpenGroups.TryGetValue(g.Key, out var known) ? known : _ordinal == 0 || needed;
            _ordinal++;
            void Flip()
            {
                if (g.On?.Invoke() == false || g.Rows.Children.OfType<FrameworkElement>().All(r => r.Visibility != Visibility.Visible)) return;
                SetOpen(g, !g.Open);
            }
            header.MouseLeftButtonUp += (_, e) => { if (toggle == null || e.OriginalSource is not DependencyObject d || !IsInside(d, toggle.Value.Switch)) Flip(); };
            header.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space && ReferenceEquals(e.OriginalSource, header)) { Flip(); e.Handled = true; } };
            inside.Children.Add(header);
            g.Body.BorderThickness = new Thickness(0, 1, 0, 0);
            g.Body.BorderBrush = new SolidColorBrush(Th.Ink(0x10));
        }
        inside.Children.Add(g.Body);
        g.Outer = outer;
        _groups.Add(g);
        SyncSoon();
        return outer;
    }

    /// <summary>A row that belongs to the one above it: its name a step quieter.</summary>
    static void Dim(FrameworkElement row)
    {
        if (row is Grid { Children: [StackPanel { Children: [TextBlock name, ..] }, ..] }) { name.Foreground = Ui.Res("FgDim"); name.FontSize = 12; }
    }

    /// <summary>Does a row hold a "UZUPEŁNIJ" mark (then its group opens by itself)?</summary>
    static bool HasNeeded(DependencyObject d)
    {
        if (d is FrameworkElement { Tag: "needed" }) return true;
        foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
            if (HasNeeded(c)) return true;
        return false;
    }

    /// <summary>The name and the short line under it (the left side of a row).</summary>
    static FrameworkElement Caption(string label, FrameworkElement? hint)
    {
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = Label(label, 12.5, "Fg");
        name.TextWrapping = TextWrapping.Wrap;
        left.Children.Add(name);
        if (hint != null) { hint.Margin = new Thickness(0, 2, 0, 0); left.Children.Add(hint); }
        return left;
    }

    static TextBlock? HintText(string? hint)
    {
        if (hint == null) return null;
        var t = Label(hint, 11.5, "FgFaint");
        t.TextWrapping = TextWrapping.Wrap;
        return t;
    }

    /// <summary>A row: the name and its short line on the left, the control on the right.</summary>
    static FrameworkElement Row(string label, string? hint, FrameworkElement control) => RowWith(label, HintText(hint), control);

    static FrameworkElement RowWith(string label, FrameworkElement? hint, FrameworkElement control)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 150 });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Tag = label;
        g.Children.Add(Caption(label, hint));
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(18, control.Margin.Top, 0, control.Margin.Bottom);
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    /// <summary>A row with a wide control (a list, a text field): under the name, the whole width.</summary>
    static FrameworkElement Stacked(string label, string? hint, FrameworkElement control, bool needed = false)
    {
        var sp = new StackPanel { Tag = label };
        sp.Children.Add(Caption(label, HintText(hint)));
        if (needed && sp.Children[0] is StackPanel cap && cap.Children[0] is TextBlock name)
        {
            // the name and "UZUPEŁNIJ" side by side
            cap.Children.RemoveAt(0);
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(name);
            head.Children.Add(Needed());
            cap.Children.Insert(0, head);
        }
        control.Margin = new Thickness(0, 8, 0, 0);
        sp.Children.Add(control);
        return sp;
    }

    /// <summary>An on / off row: a switch on the right.</summary>
    FrameworkElement Toggle(string label, string? hint, bool value, Action<bool> set)
    {
        var sw = SwitchControl(label, value, set);
        var row = Row(label, hint, sw);
        // the whole row toggles (a bigger target than the switch)
        row.Cursor = Cursors.Hand;
        row.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is DependencyObject d && !IsInside(d, sw)) sw.IsChecked = sw.IsChecked != true; };
        if (row is Panel panel) panel.Background = Brushes.Transparent;
        return row;
    }

    /// <summary>The switch alone: sets, applies (the settings are saved), the dependent rows follow.</summary>
    System.Windows.Controls.Primitives.ToggleButton SwitchControl(string name, bool value, Action<bool> set)
    {
        var sw = new System.Windows.Controls.Primitives.ToggleButton { Style = (Style)Application.Current.Resources["Switch"], IsChecked = value };
        System.Windows.Automation.AutomationProperties.SetName(sw, name);
        sw.Checked += (_, _) => { set(true); App.Instance.ApplySettings(); Sync(); };
        sw.Unchecked += (_, _) => { set(false); App.Instance.ApplySettings(); Sync(); };
        return sw;
    }

    static bool IsInside(DependencyObject d, DependencyObject parent)
    {
        for (var x = d; x != null; x = x is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (ReferenceEquals(x, parent)) return true;
        return false;
    }

    /// <summary>A slider with its value on the right of it (the right side of a row).</summary>
    static FrameworkElement Slider(double min, double max, double value, Action<double> set, Func<double, string> fmt)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Width = 190, VerticalAlignment = VerticalAlignment.Center };
        var v = Label(fmt(slider.Value), 12, "FgDim");
        v.Width = 54;
        v.VerticalAlignment = VerticalAlignment.Center;
        v.TextAlignment = TextAlignment.Right;
        sp.Children.Add(slider);
        sp.Children.Add(v);
        slider.ValueChanged += (_, e) =>
        {
            set(e.NewValue);
            v.Text = fmt(e.NewValue);
            App.Instance.ApplySettingsSoon(); // dragging fires many ticks: apply at most every 150 ms
        };
        return sp;
    }

    /// <summary>"UZUPEŁNIJ": something a fresh install still needs (a token, a city …).</summary>
    static FrameworkElement Needed(string? why = null)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0xE8, 0xA2, 0x3A)), BorderBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xA2, 0x3A)), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = L.T("UZUPEŁNIJ"), FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xA2, 0x3A)) },
            ToolTip = why, Tag = "needed",
        };
        return b;
    }

    /// <summary>A heading inside a section, its explanation as a short line under it, and a "UZUPEŁNIJ" mark when needed.</summary>
    static FrameworkElement Sub(string title, string? info = null, bool needed = false)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 18, 0, 6) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Label(title.ToUpper(BoardWindow.Pl), 10.5, "FgDim", FontWeights.Bold));
        if (needed) row.Children.Add(Needed());
        sp.Children.Add(row);
        if (HintText(info) is { } h) { h.Margin = new Thickness(0, 3, 0, 0); sp.Children.Add(h); }
        return sp;
    }

    /// <summary>A section's longer explanation, folded under "Jak to działa" (a click opens it) instead of a paragraph or a tool tip.</summary>
    static FrameworkElement Note(string info)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        var text = HintText(info)!;
        text.Visibility = Visibility.Collapsed;
        text.Margin = new Thickness(0, 2, 0, 4);
        var toggle = Btn(L.T("Jak to działa") + "  ▸", "LinkButton", (_, _) => { });
        toggle.HorizontalAlignment = HorizontalAlignment.Left;
        toggle.Margin = new Thickness(-7, 0, 0, 0);
        toggle.Click += (_, _) =>
        {
            bool open = text.Visibility != Visibility.Visible;
            text.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            toggle.Content = L.T("Jak to działa") + (open ? "  ▾" : "  ▸");
        };
        sp.Children.Add(toggle);
        sp.Children.Add(text);
        return sp;
    }

    /// <summary>A labelled control under its label (account and connection forms); the explanation as a short line under the label.</summary>
    static FrameworkElement Pair(string label, FrameworkElement control, string? info = null, bool needed = false)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, info == null ? 5 : 1) };
        head.Children.Add(Label(label, 12, "FgDim"));
        if (needed) head.Children.Add(Needed());
        sp.Children.Add(head);
        if (HintText(info) is { } h) { h.Margin = new Thickness(0, 0, 0, 5); sp.Children.Add(h); }
        sp.Children.Add(control);
        return sp;
    }

    static FrameworkElement Check(string label, bool value, Action<bool> set, string? info = null)
    {
        var cb = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 5, 0, info == null ? 5 : 1) };
        cb.Checked += (_, _) => { set(true); App.Instance.ApplySettings(); };
        cb.Unchecked += (_, _) => { set(false); App.Instance.ApplySettings(); };
        if (info == null) return cb;
        var sp = new StackPanel();
        sp.Children.Add(cb);
        var h = HintText(info)!;
        h.Margin = new Thickness(24, 0, 0, 5);
        sp.Children.Add(h);
        return sp;
    }
}
