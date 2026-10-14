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
    readonly List<(Border header, Border body, TextBlock chevron)> _accordion = new();
    readonly List<Action> _openers = new();
    /// <summary>Opens a section by its position (dev screenshots).</summary>
    public void OpenSection(int index) { if (index >= 0 && index < _openers.Count) _openers[index](); }
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
        ResizeMode = ResizeMode.CanResize;
        Content = new ScrollViewer { Content = _sections, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        AddSection(L.T("Ogólne"), L.T("język, autostart, zegar, alarmy i przypomnienia, linki Notion, skrót klawiszowy"), BuildGeneral);
        AddSection(L.T("Konta"), L.T("Praca i Prywatne: folder, kalendarze Google, kategorie, oznaczenia dni"), BuildAccounts);
        AddSection(L.T("Połączenia"), L.T("Notion: automatyczne wczytywanie zadań z bazy"), BuildConnections);
        AddSection(L.T("Pogoda"), L.T("prognoza w nagłówkach dni, lokalizacja"), BuildWeather);
        AddSection(L.T("Ekran i rozmiar"), L.T("monitor, skala, szerokość i wysokość tablicy"), BuildScreen);
        AddSection(L.T("Tablica"), L.T("weekendy, widok roku, zaległe zadania, backlog"), BuildBoard);
        AddSection(L.T("Wygląd"), L.T("matowe szkło, przyciemnienie, kolor akcentu, animacje"), BuildLook);
        AddSection(L.T("Info"), L.T("wersja, instalacja i deinstalacja"), BuildInstall);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        footer.Children.Add(Btn(L.T("Importuj z Notion…"), "SecondaryButton", (_, _) => App.Instance.ImportNotion()));
        footer.Children.Add(Btn(L.T("Gotowe"), "PrimaryButton", (_, _) => Close()));
        _sections.Children.Add(footer);

        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        CalendarService.Changed += UpdateCalStatus;
        Closed += (_, _) => { _statusTimer.Stop(); CalendarService.Changed -= UpdateCalStatus; };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    // ---------- accordion ----------

    void AddSection(string title, string subtitle, Func<FrameworkElement> build, bool open = false)
    {
        // one card per section (like the day cells of the board): header, and the body inside the same card when open
        var chevron = new TextBlock { Text = "", FontFamily = Ui.Font("IconFont"), FontSize = 11, Foreground = Ui.Res("FgDim"), VerticalAlignment = VerticalAlignment.Center };
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
            if (!built) { body.Child = build(); built = true; }
            foreach (var (h, b, c) in _accordion)
            {
                bool me = ReferenceEquals(b, body);
                b.Visibility = me ? Visibility.Visible : Visibility.Collapsed;
                c.Text = me ? "" : "";
                if (h.Child is DockPanel dp && dp.Children[1] is StackPanel sp && sp.Children[0] is TextBlock tb)
                    tb.Foreground = me ? Ui.Res("AccentBrush") : Ui.Res("Fg");
            }
            Anim.Enter(body, 0, -6, 200);
        }
        header.MouseLeftButtonUp += (_, _) =>
        {
            if (body.Visibility == Visibility.Visible) { body.Visibility = Visibility.Collapsed; chevron.Text = ""; name.Foreground = Ui.Res("Fg"); }
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
        panel.Children.Add(Label(L.T("Każde konto ma osobny folder danych, kalendarze, kategorie i oznaczenia dni. Przełączanie: ikona warstwy na górnym pasku, Ctrl+1 / Ctrl+2."), 11.5, "FgDim"));
        var syncNote = Label(L.T("Synchronizacja między komputerami: zadania, alarmy i oznaczenia są w folderze konta (board.json), ustawienia programu – w pliku taskwall-settings.json w folderze konta Praca. Lokalne zostają: foldery, ekran i rozmiar, autostart, adresy kalendarzy i tokeny Notion. Folder na Dysku Google jest rozpoznawany niezależnie od litery dysku (G:, H:…)."), 11.5, "FgFaint");
        syncNote.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(syncNote);
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

        // name
        var name = new TextBox { Style = (Style)Application.Current.Resources["FieldBox"], Text = acc.Name, MaxLength = 20, Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
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
        p.Children.Add(Pair(L.T("Nazwa konta"), name));

        // folder
        p.Children.Add(Sub(L.T("Folder danych")));
        p.Children.Add(FolderRow(id));
        _syncStatus = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Foreground = Ui.Res("FgFaint"), Tag = store };
        p.Children.Add(_syncStatus);
        UpdateStatus();

        // calendars
        p.Children.Add(Sub(L.T("Kalendarze Google")));
        p.Children.Add(Label(L.T("Adres iCal: Kalendarz Google → Ustawienia → kalendarz → Integracja kalendarza → Tajny adres w formacie iCal. Adres jest zapisywany tylko na tym komputerze."), 11, "FgFaint"));
        var feeds = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        p.Children.Add(feeds);
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
        p.Children.Add(add);
        var calRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var refresh = Btn(L.T("Odśwież kalendarze"), "LinkButton", (_, _) => App.Instance.RefreshCalendars());
        refresh.Margin = new Thickness(-7, 0, 0, 0);
        DockPanel.SetDock(refresh, Dock.Left);
        calRow.Children.Add(refresh);
        _calStatus = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Res("FgFaint"), Margin = new Thickness(8, 0, 0, 0) };
        calRow.Children.Add(_calStatus);
        p.Children.Add(calRow);
        UpdateCalStatus();


        // categories (stored in this account's board.json, so they sync with its other computers)
        p.Children.Add(Sub(L.T("Kategorie")));
        p.Children.Add(Label(L.T("Etykieta [Nazwa] na początku zadania. Przypisanie: prawy przycisk → Kategoria, etykiety pod polem nowego zadania albo #nazwa."), 11, "FgFaint"));
        var cats = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        p.Children.Add(cats);
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
        p.Children.Add(addCat);

        // day marks (HO, BŚU, Urlop …) – also per account, synced like the categories
        p.Children.Add(Sub(L.T("Oznaczenia dni")));
        p.Children.Add(Label(L.T("Kod w nagłówku dnia (np. HO, BŚU, Urlop). Ustawianie: prawy przycisk na dniu; stały cykl: przycisk Cykl…. Uwzględniane w widoku roku i eksporcie CSV."), 11, "FgFaint"));
        var marks = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        p.Children.Add(marks);
        FillMarks(store, marks);
        var addMark = Btn(L.T("+ Dodaj oznaczenie"), "SecondaryButton", (_, _) =>
        {
            var list = store.MarkTypes.Select(x => new MarkType { Code = x.Code, Label = x.Label, Color = x.Color, Style = x.Style }).ToList();
            var used = list.Select(x => x.Color).ToHashSet(StringComparer.OrdinalIgnoreCase);
            list.Add(new MarkType { Code = L.T("NOWE"), Label = "", Color = Category.Palette.FirstOrDefault(x => !used.Contains(x)) ?? Category.Palette[0] });
            store.SetMarkTypes(list);
            App.Board?.Rebuild();
            FillMarks(store, marks, focusLast: true);
        });
        addMark.HorizontalAlignment = HorizontalAlignment.Left;
        addMark.Margin = new Thickness(0, 6, 0, 0);
        p.Children.Add(addMark);

        // clearing this account's data (goes to the other computers too)
        p.Children.Add(Sub(L.T("Wyczyść dane")));
        p.Children.Add(Label(L.T("Usuwa dane konta, również na innych komputerach synchronizujących ten folder. Zadania i oznaczenia można przywrócić przez Ctrl+Z lub z kopii dziennej."), 11, "FgFaint"));
        var clearRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
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
        p.Children.Add(clearRow);
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
        p.Children.Add(Label(L.T("Prognoza na 16 dni (Open-Meteo, bez konta): symbol pogody, temperatura maks. / min., szansa opadów i wiatr w nagłówku dnia. Do serwisu wysyłane są wyłącznie współrzędne miasta."), 11, "FgFaint"));
        var status = Label("", 11.5, "FgDim");
        void ShowStatus() => status.Text = !w.Ready ? L.T("Wybierz miasto, aby włączyć prognozę.")
            : WeatherService.Error != null ? L.F("Błąd: {0}", WeatherService.Error)
            : WeatherService.Fetched is { } f ? L.F("{0} · pobrano {1}", w.City, f.ToString("HH:mm")) : w.City;
        p.Children.Add(Check(L.T("Pokazuj pogodę w nagłówkach dni"), w.Enabled, v => { w.Enabled = v; if (v) WeatherService.Refresh(); }));
        p.Children.Add(Check(L.T("Opady: szansa (0–100%) i przewidywana ilość (mm)"), w.ShowRain, v => w.ShowRain = v));
        p.Children.Add(Check(L.T("Prędkość wiatru (km/h)"), w.ShowWind, v => w.ShowWind = v));

        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
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
        p.Children.Add(Pair(L.T("Miasto"), row));
        p.Children.Add(results);
        p.Children.Add(status);
        var refresh = Btn(L.T("Odśwież prognozę"), "LinkButton", (_, _) => WeatherService.Refresh());
        refresh.HorizontalAlignment = HorizontalAlignment.Left;
        refresh.Margin = new Thickness(-7, 4, 0, 0);
        p.Children.Add(refresh);
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
            var p = new StackPanel();
            p.Children.Add(Sub("Notion"));
            p.Children.Add(BuildNotion(S.AccountFor(current), App.StoreFor(current)));
            body.Child = p;
        }
        foreach (var acc in S.Accounts)
        {
            var id = acc.Id;
            var b = new Button { Style = (Style)Application.Current.Resources["BarButton"], Tag = id, Padding = new Thickness(14, 4, 14, 4), Content = L.Up(App.AccountName(id)) };
            b.Click += (_, _) => { current = id; Fill(); };
            buttons.Children.Add(b);
        }
        panel.Children.Add(switcher);
        panel.Children.Add(Label(L.T("Połączenia ustawia się osobno dla każdego konta. Zadania trafiają do backlogu wybranego konta."), 11.5, "FgDim"));
        panel.Children.Add(body);
        Fill();
        return panel;
    }

    FrameworkElement BuildNotion(Account acc, BoardStore store)
    {
        var link = acc.Notion;
        var p = new StackPanel();
        p.Children.Add(Label(L.T("Wczytuje zadania z bazy Notion do backlogu konta (tylko odczyt). Wymaga osobistego tokenu Notion. Token jest szyfrowany dla bieżącego użytkownika Windows i zapisywany tylko na tym komputerze; na innym komputerze należy podać ten sam albo nowy token."), 11, "FgFaint"));

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
        db.LostKeyboardFocus += (_, _) => { if (db.Text.Trim() != link.Database) { link.Database = db.Text.Trim(); SettingsStore.Save(S); } };
        ShowDbState();
        p.Children.Add(Pair(L.T("Link do bazy"), db));
        p.Children.Add(dbState);
        p.Children.Add(Label(L.T("Link do widoku: w Notion nazwa widoku nad tabelą → Copy link to view. Baza na całą stronę: Ctrl+L."), 11, "FgFaint"));
        var howTo = Btn(L.T("Jak skopiować link do widoku (pomoc Notion) ↗"), "LinkButton", (_, _) => GlassWindow.OpenUrl("https://www.notion.com/help/views-filters-and-sorts"));
        howTo.HorizontalAlignment = HorizontalAlignment.Left;
        howTo.Margin = new Thickness(-7, 2, 0, 8);
        p.Children.Add(howTo);

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
        token.PasswordChanged += (_, _) =>
        {
            if (filling || token.Password == Mask || token.Password.Trim().Length < 20) return;
            link.TokenProtected = NotionSync.Protect(token.Password);
            SettingsStore.Save(S);
            ShowTokenState();
        };
        ShowTokenState();
        p.Children.Add(Pair(L.T("Osobisty token"), tokenRow));
        p.Children.Add(tokenState);

        var every = new ComboBox { Width = 90, Margin = new Thickness(8, 0, 0, 0) };
        foreach (var m in new[] { 5, 10, 15, 30, 60, 120, 240 }) every.Items.Add(new ComboBoxItem { Content = m < 60 ? $"{m} min" : $"{m / 60} h", Tag = m });
        every.SelectedItem = every.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == link.Minutes) ?? every.Items[1];
        every.SelectionChanged += (_, _) => { if (every.SelectedItem is ComboBoxItem it) { link.Minutes = (int)it.Tag; SettingsStore.Save(S); } };
        var auto = new CheckBox { Content = L.T("Synchronizuj przy starcie programu i co"), IsChecked = link.Auto, VerticalAlignment = VerticalAlignment.Center };
        auto.Checked += (_, _) => { link.Auto = true; SettingsStore.Save(S); };
        auto.Unchecked += (_, _) => { link.Auto = false; SettingsStore.Save(S); };
        var autoRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4) };
        autoRow.Children.Add(auto);
        autoRow.Children.Add(every);
        p.Children.Add(autoRow);
        var viewFilter = new CheckBox { Content = L.T("Używaj filtrów i sortowania widoku z linku (zalecane)"), IsChecked = link.UseViewFilter, Margin = new Thickness(0, 4, 0, 4) };
        viewFilter.Checked += (_, _) => { link.UseViewFilter = true; SettingsStore.Save(S); };
        viewFilter.Unchecked += (_, _) => { link.UseViewFilter = false; SettingsStore.Save(S); };
        p.Children.Add(viewFilter);
        var limit = new ComboBox { Width = 110, Margin = new Thickness(8, 0, 0, 0) };
        foreach (var n in new[] { 100, 250, 500, 1000, 2000 }) limit.Items.Add(new ComboBoxItem { Content = n.ToString(), Tag = n });
        limit.SelectedItem = limit.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == link.MaxPages) ?? limit.Items[2];
        limit.SelectionChanged += (_, _) => { if (limit.SelectedItem is ComboBoxItem it) { link.MaxPages = (int)it.Tag; SettingsStore.Save(S); } };
        var limitRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        limitRow.Children.Add(new TextBlock { Text = L.T("Nie wczytuj, jeśli widok ma więcej zadań niż"), Foreground = Ui.Res("Fg"), VerticalAlignment = VerticalAlignment.Center });
        limitRow.Children.Add(limit);
        p.Children.Add(limitRow);
        var mine = new CheckBox { Content = L.T("Tylko zadania przypisane do mnie (pole osoby zawiera moje konto)"), IsChecked = link.OnlyMine, Margin = new Thickness(0, 4, 0, 4) };
        mine.Checked += (_, _) => { link.OnlyMine = true; SettingsStore.Save(S); };
        mine.Unchecked += (_, _) => { link.OnlyMine = false; SettingsStore.Save(S); };
        p.Children.Add(mine);
        var done = new CheckBox { Content = L.T("Zakończone w Notion oznaczaj jako zrobione"), IsChecked = link.SyncDone, Margin = new Thickness(0, 4, 0, 4) };
        done.Checked += (_, _) => { link.SyncDone = true; SettingsStore.Save(S); };
        done.Unchecked += (_, _) => { link.SyncDone = false; SettingsStore.Save(S); };
        p.Children.Add(done);

        // statuses that are not imported (known after the first sync)
        var statuses = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        void FillStatuses()
        {
            statuses.Children.Clear();
            var known = link.KnownStatuses ?? new();
            if (known.Count == 0) { statuses.Children.Add(Label(L.T("Lista statusów pojawi się po pierwszej synchronizacji. Domyślnie pomijane są statusy zakończone."), 11, "FgFaint")); return; }
            var skip = link.SkipStatuses ?? known.Where(NotionImport.LooksDone).ToList();
            foreach (var s in known)
            {
                var chip = new System.Windows.Controls.Primitives.ToggleButton { Content = s, Style = (Style)Application.Current.Resources["Chip"], IsChecked = !skip.Contains(s), Margin = new Thickness(0, 0, 6, 6), ToolTip = L.T("Zaznaczone statusy są wczytywane") };
                chip.Click += (_, _) =>
                {
                    var now = link.SkipStatuses ?? known.Where(NotionImport.LooksDone).ToList();
                    if (chip.IsChecked == true) now.Remove(s); else if (!now.Contains(s)) now.Add(s);
                    link.SkipStatuses = now;
                    SettingsStore.Save(S);
                };
                statuses.Children.Add(chip);
            }
        }
        FillStatuses();
        p.Children.Add(Pair(L.T("Wczytywane statusy"), statuses));

        var syncRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var result = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Res("FgFaint"), Margin = new Thickness(10, 0, 0, 0), Text = link.LastResult ?? "" };
        Button? now = null;
        now = Btn(L.T("Synchronizuj teraz"), "SecondaryButton", async (_, _) =>
        {
            link.Database = db.Text.Trim();
            if (!link.Configured) { result.Text = L.T("Podaj link do bazy i token."); return; }
            now!.IsEnabled = false;
            result.Text = L.T("Łączę z Notion…");
            var r = await NotionSync.Run(acc.Id);
            now.IsEnabled = true;
            result.Text = link.LastResult ?? (r.Error ?? "");
            FillStatuses();
        });
        now.Margin = new Thickness(0);
        DockPanel.SetDock(now, Dock.Left);
        syncRow.Children.Add(now);
        syncRow.Children.Add(result);
        p.Children.Add(syncRow);
        return p;
    }

    void FillMarks(BoardStore store, StackPanel host, bool focusLast = false)
    {
        host.Children.Clear();
        var list = store.MarkTypes.Select(x => new MarkType { Code = x.Code, Label = x.Label, Color = x.Color, Style = x.Style }).ToList();
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

    FrameworkElement BuildScreen()
    {
        var p = new StackPanel();
        var monitors = Monitors.All();
        var monCombo = new ComboBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        for (int i = 0; i < monitors.Count; i++)
            monCombo.Items.Add(new ComboBoxItem { Content = monitors[i].Label(i), Tag = monitors[i].Device });
        int idx = monitors.FindIndex(m => m.Device == S.Monitor);
        monCombo.SelectedIndex = Math.Max(0, idx >= 0 ? idx : monitors.FindIndex(m => m.Primary));
        monCombo.SelectionChanged += (_, _) => { if (monCombo.SelectedItem is ComboBoxItem it) { S.Monitor = (string)it.Tag; App.Instance.ApplySettings(); } };
        p.Children.Add(Pair("Monitor", monCombo));
        p.Children.Add(SliderRow(L.T("Skala całości"), 80, 220, S.UiScale * 100, v => S.UiScale = Math.Round(v) / 100, v => $"{v:0}%"));
        p.Children.Add(SliderRow(L.T("Szerokość tablicy"), 40, 100, S.WidthPercent, v => S.WidthPercent = v, v => $"{v:0}%"));
        p.Children.Add(SliderRow(L.T("Wysokość tygodnia"), 10, 40, S.WeekHeightPercent, v => S.WeekHeightPercent = v, v => L.F("{0:0}% ekranu", v)));
        p.Children.Add(SliderRow(L.T("Położenie w pionie"), 0, 100, S.VerticalPercent, v => S.VerticalPercent = v, v => $"{v:0}%"));
        p.Children.Add(SliderRow(L.T("Rozmiar tekstu zadań"), 10, 17, S.FontSize, v => S.FontSize = Math.Round(v * 2) / 2, v => $"{Math.Round(v * 2) / 2:0.0}"));
        return p;
    }

    FrameworkElement BuildBoard()
    {
        var p = new StackPanel();
        p.Children.Add(Check(L.T("Pokazuj weekendy"), S.ShowWeekends, v => S.ShowWeekends = v));
        p.Children.Add(Check(L.T("Zwijaj zrobione zadania w dniu do jednej linii („✓ 3 zrobione”)"), S.CollapseDone, v => S.CollapseDone = v));
        p.Children.Add(Check(L.T("Podsumowanie poprzedniego tygodnia przy pierwszym uruchomieniu w tygodniu"), S.WeeklySummary, v => S.WeeklySummary = v));
        p.Children.Add(Check(L.T("Przycisk widoku roku (365 dni)"), S.ShowYearButton, v => S.ShowYearButton = v));
        p.Children.Add(Check(L.T("Automatycznie przenoś niezrobione zadania z minionych dni na dziś"), S.AutoRollover, v => S.AutoRollover = v));
        var mode = new ComboBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        mode.Items.Add(new ComboBoxItem { Content = L.T("Wysuwany po kliknięciu (na szerokość weekendu)"), Tag = "drawer" });
        mode.Items.Add(new ComboBoxItem { Content = L.T("Przypięty z prawej strony"), Tag = "pinned" });
        mode.SelectedIndex = S.BacklogMode == "pinned" ? 1 : 0;
        mode.SelectionChanged += (_, _) => { if (mode.SelectedItem is ComboBoxItem it) { S.BacklogMode = (string)it.Tag; App.Instance.ApplySettings(); } };
        p.Children.Add(Pair(L.T("Backlog i archiwum"), mode));
        p.Children.Add(SliderRow(L.T("Szerokość (przypięty)"), 220, 560, S.BacklogWidth, v => S.BacklogWidth = v, v => $"{v:0} px"));
        return p;
    }

    FrameworkElement BuildLook()
    {
        var p = new StackPanel();
        // theme: applied when the app starts (all windows are built in it)
        var theme = new ComboBox { Width = 240 };
        foreach (var (code, label) in new[] { ("dark", L.T("Ciemny")), ("light", L.T("Jasny (do jasnych tapet)")), ("auto", L.T("Automatycznie z jasności tapety")) })
            theme.Items.Add(new ComboBoxItem { Content = label, Tag = code });
        theme.SelectedItem = theme.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == S.Theme) ?? theme.Items[0];
        var restartTheme = Btn(L.T("Uruchom ponownie"), "SecondaryButton", (_, _) => App.Instance.Restart());
        restartTheme.Visibility = Visibility.Collapsed;
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedItem is not ComboBoxItem it) return;
            S.Theme = (string)it.Tag;
            SettingsStore.Save(S);
            restartTheme.Visibility = Visibility.Visible;
        };
        var themeRow = new StackPanel { Orientation = Orientation.Horizontal };
        themeRow.Children.Add(theme);
        themeRow.Children.Add(restartTheme);
        p.Children.Add(Pair(L.T("Motyw"), themeRow));
        p.Children.Add(Check(L.T("Matowe szkło (rozmyta tapeta pod tablicą)"), S.Blur, v => S.Blur = v));
        p.Children.Add(Check(L.T("Animacje"), S.Animations, v => S.Animations = v));
        p.Children.Add(Check(L.T("Podpisy przy ikonach górnego paska"), S.BarCaptions, v => S.BarCaptions = v));
        p.Children.Add(SliderRow(L.T("Rozmycie"), 0, 100, S.BlurStrength, v => S.BlurStrength = v, v => $"{v:0}"));
        p.Children.Add(SliderRow(L.T("Przyciemnienie"), 0, 95, S.TintOpacity * 100, v => S.TintOpacity = v / 100, v => $"{v:0}%"));
        var refresh = Btn(L.T("Odśwież tapetę"), "SecondaryButton", (_, _) => App.Instance.RefreshWallpaper());
        refresh.HorizontalAlignment = HorizontalAlignment.Left;
        refresh.Margin = new Thickness(0, 4, 0, 8);
        refresh.ToolTip = L.T("Wczytaj tapetę ponownie, np. po zmianie przez pokaz slajdów albo Windows Spotlight");
        p.Children.Add(refresh);

        var swatches = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        void Mark(Border sel) { foreach (Border b in swatches.Children) b.BorderThickness = new Thickness(0); sel.BorderThickness = new Thickness(2); }
        var auto = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 10, 0), Cursor = Cursors.Hand,
            BorderBrush = Brushes.White, BorderThickness = new Thickness(S.Accent == "auto" ? 2 : 0),
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
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 10, 0),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)), Cursor = Cursors.Hand,
                BorderBrush = Brushes.White, BorderThickness = new Thickness(string.Equals(S.Accent, hex, StringComparison.OrdinalIgnoreCase) ? 2 : 0),
            };
            sw.MouseLeftButtonUp += (_, _) => { S.Accent = hex; Mark(sw); App.Instance.ApplySettings(); };
            swatches.Children.Add(sw);
        }
        p.Children.Add(Pair(L.T("Kolor akcentu"), swatches));
        return p;
    }

    FrameworkElement BuildGeneral()
    {
        var p = new StackPanel();
        // language: applied after a restart (all windows are built in it)
        var lang = new ComboBox { Width = 200 };
        lang.Items.Add(new ComboBoxItem { Content = "Polski", Tag = "pl" });
        lang.Items.Add(new ComboBoxItem { Content = "English", Tag = "en" });
        lang.SelectedIndex = S.Language == "en" ? 1 : 0;
        var restart = Btn(L.T("Uruchom ponownie"), "SecondaryButton", (_, _) => App.Instance.Restart());
        restart.Visibility = Visibility.Collapsed;
        lang.SelectionChanged += (_, _) =>
        {
            if (lang.SelectedItem is not ComboBoxItem it) return;
            S.Language = (string)it.Tag;
            SettingsStore.Save(S);
            restart.Visibility = S.Language == (L.En ? "en" : "pl") ? Visibility.Collapsed : Visibility.Visible;
        };
        var langRow = new StackPanel { Orientation = Orientation.Horizontal };
        langRow.Children.Add(lang);
        langRow.Children.Add(restart);
        p.Children.Add(Pair("Język / Language", langRow));
        p.Children.Add(Check(L.T("Uruchamiaj razem z Windows"), S.StartWithWindows, v => S.StartWithWindows = v));
        p.Children.Add(Check(L.T("Pokazuj zegar z kalendarzem"), S.ShowClock, v => S.ShowClock = v));
        p.Children.Add(Check(L.T("Zegar 24-godzinny"), S.Use24h, v => S.Use24h = v));
        p.Children.Add(Check(L.T("Dźwięk alarmów"), S.AlarmSound, v => S.AlarmSound = v));
        var remind = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (m, label) in new[] { (-1, L.T("Wyłączone")), (0, L.T("W chwili rozpoczęcia")), (5, L.T("5 min przed")), (10, L.T("10 min przed")), (15, L.T("15 min przed")) })
            remind.Items.Add(new ComboBoxItem { Content = label, Tag = m });
        remind.SelectedItem = remind.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == S.TaskReminder) ?? remind.Items[2];
        remind.SelectionChanged += (_, _) => { if (remind.SelectedItem is ComboBoxItem it) { S.TaskReminder = (int)it.Tag; SettingsStore.Save(S); AlarmService.Reschedule(); } };
        p.Children.Add(Pair(L.T("Przypomnienie o zadaniach z godziną (np. 14-16)"), remind));
        p.Children.Add(Check(L.T("Otwieraj linki w aplikacji Notion (zamiast w przeglądarce)"), S.OpenNotionInApp, v => S.OpenNotionInApp = v));
        var hot = new ComboBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (key, label) in new[] { ("Ctrl+Shift+Space", L.T("Ctrl + Shift + Spacja")), ("Ctrl+Alt+D", "Ctrl + Alt + D"), ("Ctrl+Alt+T", "Ctrl + Alt + T"), ("Win+Shift+D", "Win + Shift + D"), ("", L.T("Wyłączony")) })
            hot.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        hot.SelectedItem = hot.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == S.Hotkey) ?? hot.Items[0];
        var warn = Label(L.T("Ten skrót jest zajęty przez inny program – wybierz inny."), 11.5, "FgDim");
        warn.Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xA6, 0x5A));
        void UpdateWarn() => warn.Visibility = App.Instance.HotkeyTaken ? Visibility.Visible : Visibility.Collapsed;
        hot.SelectionChanged += (_, _) => { if (hot.SelectedItem is ComboBoxItem it) { S.Hotkey = (string)it.Tag; App.Instance.ApplySettings(); UpdateWarn(); } };
        UpdateWarn();
        p.Children.Add(Pair(L.T("Skrót: tablica na wierzch + nowe zadanie na dziś (Esc chowa)"), hot));
        p.Children.Add(warn);
        return p;
    }

    FrameworkElement BuildInstall()
    {
        var p = new StackPanel();
        var installed = Installer.IsInstalled;
        p.Children.Add(Label(installed
            ? L.F("TaskWall jest zainstalowany w {0}. Odinstalujesz go stąd albo w Ustawieniach Windows → Aplikacje.", Installer.InstallDir)
            : L.T("Ta kopia nie jest zainstalowana. Instalacja kopiuje program do folderu użytkownika, dodaje skrót w menu Start i wpis w „Aplikacje i funkcje” (bez uprawnień administratora)."), 11.5, "FgDim"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var btn = installed
            ? Btn(L.T("Odinstaluj…"), "SecondaryButton", (_, _) => Installer.Uninstall(this))
            : Btn(L.T("Zainstaluj w systemie"), "PrimaryButton", (_, _) => Installer.InstallFromRunningCopy(this));
        btn.Margin = new Thickness(0);
        row.Children.Add(btn);
        p.Children.Add(row);
        var ver = Label(L.F("Wersja {0}  ·  dane: foldery kont  ·  ustawienia: %APPDATA%\\TaskWall", Installer.Version), 11, "FgFaint");
        ver.Margin = new Thickness(0, 10, 0, 0);
        p.Children.Add(ver);
        // updates through the cloud folder
        var setup = Updater.SetupPath;
        var shared = setup != null ? Updater.VersionOf(setup) : null;
        var upd = Label(setup == null ? L.T("Aktualizacje: brak folderu danych.")
            : shared == null ? L.F("Aktualizacje: brak instalatora w folderze danych ({0}). Zainstalowana kopia zapisuje go automatycznie.", setup)
            : L.F("Aktualizacje: instalator w folderze danych – wersja {0}. Komputery ze starszą wersją proponują aktualizację przy starcie.", shared.ToString(3)), 11, "FgFaint");
        upd.Margin = new Thickness(0, 8, 0, 4);
        p.Children.Add(upd);
        var check = Btn(L.T("Sprawdź aktualizację"), "SecondaryButton", (_, _) => Updater.Check(askAgain: true));
        check.HorizontalAlignment = HorizontalAlignment.Left;
        check.Margin = new Thickness(0);
        p.Children.Add(check);
        return p;
    }

    // ---------- small builders ----------

    static FrameworkElement Sub(string title)
    {
        var t = Label(title.ToUpper(BoardWindow.Pl), 11, "FgDim", FontWeights.Bold);
        t.Margin = new Thickness(0, 16, 0, 6);
        return t;
    }

    static FrameworkElement Pair(string label, FrameworkElement control)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
        var l = Label(label, 12, "FgDim");
        l.Margin = new Thickness(0, 0, 0, 5);
        sp.Children.Add(l);
        sp.Children.Add(control);
        return sp;
    }

    static FrameworkElement Check(string label, bool value, Action<bool> set)
    {
        var cb = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 5, 0, 5) };
        cb.Checked += (_, _) => { set(true); App.Instance.ApplySettings(); };
        cb.Unchecked += (_, _) => { set(false); App.Instance.ApplySettings(); };
        return cb;
    }

    static FrameworkElement SliderRow(string label, double min, double max, double value, Action<double> set, Func<double, string> fmt)
    {
        var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        var l = Label(label, 12.5);
        l.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(l);
        var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(slider, 1);
        g.Children.Add(slider);
        var v = Label(fmt(slider.Value), 12, "FgDim");
        v.VerticalAlignment = VerticalAlignment.Center;
        v.TextAlignment = TextAlignment.Right;
        Grid.SetColumn(v, 2);
        g.Children.Add(v);
        slider.ValueChanged += (_, e) =>
        {
            set(e.NewValue);
            v.Text = fmt(e.NewValue);
            App.Instance.ApplySettingsSoon(); // dragging fires many ticks: apply at most every 150 ms
        };
        return g;
    }
}
