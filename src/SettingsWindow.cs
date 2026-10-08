using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DeskWall;

/// <summary>
/// Settings as an accordion (one section open at a time, no long scrolling).
/// "Konta" holds everything that belongs to an account: name, data folder, Google calendars, categories.
/// </summary>
public sealed class SettingsWindow : DarkWindow
{
    static AppSettings S => App.Settings;
    readonly StackPanel _sections = new() { Margin = new Thickness(20, 16, 20, 20) };
    readonly List<(Border header, Border body, TextBlock chevron)> _accordion = new();
    readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    string _account = App.Settings.Layer;
    readonly Border _accountBody = new();
    TextBlock? _syncStatus, _calStatus;

    public SettingsWindow()
    {
        Title = "DeskWall – ustawienia";
        Width = 640;
        Height = 780;
        MinWidth = 520;
        ResizeMode = ResizeMode.CanResize;
        Content = new ScrollViewer { Content = _sections, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        AddSection("Konta", "Praca i Prywatne: folder danych, kalendarze Google, kategorie", BuildAccounts, open: true);
        AddSection("Ekran i rozmiar", "monitor, skala, szerokość i wysokość tablicy", BuildScreen);
        AddSection("Tablica", "weekendy, widok roku, zaległe zadania, backlog", BuildBoard);
        AddSection("Wygląd", "matowe szkło, przyciemnienie, kolor akcentu, animacje", BuildLook);
        AddSection("Ogólne", "autostart, zegar, Notion, skrót klawiszowy", BuildGeneral);
        AddSection("Instalacja", "instalacja w systemie, odinstalowanie, wersja", BuildInstall);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        footer.Children.Add(Btn("Importuj z Notion…", "SecondaryButton", (_, _) => App.Instance.ImportNotion()));
        footer.Children.Add(Btn("Gotowe", "PrimaryButton", (_, _) => Close()));
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
        var chevron = new TextBlock { Text = "", FontFamily = Ui.Font("IconFont"), FontSize = 11, Foreground = Ui.Res("FgDim"), VerticalAlignment = VerticalAlignment.Center };
        var head = new DockPanel();
        DockPanel.SetDock(chevron, Dock.Right);
        head.Children.Add(chevron);
        var titles = new StackPanel();
        titles.Children.Add(Label(title.ToUpper(BoardWindow.Pl), 12, "Fg", FontWeights.Bold));
        titles.Children.Add(Label(subtitle, 11.5, "FgFaint"));
        head.Children.Add(titles);
        var header = new Border { Child = head, Padding = new Thickness(14, 10, 14, 10), CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 6) };
        Anim.HoverBackground(header, Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        var body = new Border { Padding = new Thickness(14, 4, 14, 14), Visibility = Visibility.Collapsed };
        bool built = false;
        void Open()
        {
            if (!built) { body.Child = build(); built = true; }
            foreach (var (h, b, c) in _accordion)
            {
                bool me = ReferenceEquals(b, body);
                b.Visibility = me ? Visibility.Visible : Visibility.Collapsed;
                c.Text = me ? "" : "";
            }
            Anim.Enter(body, 0, -6, 200);
        }
        header.MouseLeftButtonUp += (_, _) =>
        {
            if (body.Visibility == Visibility.Visible) { body.Visibility = Visibility.Collapsed; chevron.Text = ""; }
            else Open();
        };
        _accordion.Add((header, body, chevron));
        _sections.Children.Add(header);
        _sections.Children.Add(body);
        if (open) Dispatcher.BeginInvoke(Open);
    }

    // ---------- Konta ----------

    FrameworkElement BuildAccounts()
    {
        var panel = new StackPanel();
        var switcher = new Border { CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)), Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 10) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        switcher.Child = buttons;
        void Paint()
        {
            foreach (Button b in buttons.Children)
            {
                bool on = (string)b.Tag == _account;
                b.Background = on ? new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
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
        panel.Children.Add(Label("Każde konto ma osobne dane (zadania, archiwum, kategorie) w swoim folderze w chmurze i własne kalendarze Google. Na tablicy przełączasz je przyciskiem na środku górnego paska albo Ctrl+1 / Ctrl+2.", 11.5, "FgDim"));
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
        p.Children.Add(Pair("Nazwa konta", name));

        // folder
        p.Children.Add(Sub("Folder danych"));
        p.Children.Add(FolderRow(id));
        _syncStatus = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Foreground = Ui.Res("FgFaint"), Tag = store };
        p.Children.Add(_syncStatus);
        UpdateStatus();

        // calendars
        p.Children.Add(Sub("Kalendarze Google"));
        p.Children.Add(Label("W Kalendarzu Google: Ustawienia → wybierz kalendarz → Integracja kalendarza → „Tajny adres w formacie iCal”. Dla konta Prywatne możesz podać kalendarz z prywatnego Gmaila. Adres zostaje tylko na tym komputerze.", 11, "FgFaint"));
        var feeds = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        p.Children.Add(feeds);
        void FillFeeds()
        {
            feeds.Children.Clear();
            foreach (var feed in acc.Calendars.ToList())
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var remove = Btn("Usuń", "LinkButton", (_, _) => { acc.Calendars.Remove(feed); SettingsStore.Save(S); FillFeeds(); App.Instance.RefreshCalendars(); });
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);
                var cb = new CheckBox { IsChecked = feed.Enabled, Content = $"{feed.Name}  ·  {(feed.Kind == "holidays" ? "święta" : "spotkania")}", ToolTip = feed.Kind == "holidays" ? feed.Url : "tajny adres iCal (ukryty)" };
                cb.Checked += (_, _) => { feed.Enabled = true; SettingsStore.Save(S); App.Instance.RefreshCalendars(); };
                cb.Unchecked += (_, _) => { feed.Enabled = false; SettingsStore.Save(S); App.Instance.RefreshCalendars(); };
                row.Children.Add(cb);
                feeds.Children.Add(row);
            }
            if (acc.Calendars.Count == 0) feeds.Children.Add(Label("Brak kalendarzy.", 11.5, "FgFaint"));
        }
        FillFeeds();
        var add = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        add.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        add.ColumnDefinitions.Add(new ColumnDefinition());
        add.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        add.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var feedName = new TextBox { Style = (Style)Application.Current.Resources["FieldBox"], Text = id == "private" ? "Gmail" : "Praca", Margin = new Thickness(0, 0, 6, 0), ToolTip = "Nazwa kalendarza" };
        var feedUrl = new TextBox { Style = (Style)Application.Current.Resources["FieldBox"], Margin = new Thickness(0, 0, 6, 0), ToolTip = "https://calendar.google.com/calendar/ical/…/basic.ics" };
        var kind = new ComboBox { MinWidth = 110 };
        kind.Items.Add(new ComboBoxItem { Content = "Spotkania", Tag = "events" });
        kind.Items.Add(new ComboBoxItem { Content = "Święta", Tag = "holidays" });
        kind.SelectedIndex = 0;
        var addBtn = Btn("Dodaj", "SecondaryButton", (_, _) =>
        {
            var url = feedUrl.Text.Trim();
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("webcal", StringComparison.OrdinalIgnoreCase)) { feedUrl.Focus(); return; }
            acc.Calendars.Add(new CalendarFeed { Name = feedName.Text.Trim().Length > 0 ? feedName.Text.Trim() : "Kalendarz", Url = url, Kind = (string)((ComboBoxItem)kind.SelectedItem).Tag });
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
        var refresh = Btn("Odśwież kalendarze", "LinkButton", (_, _) => App.Instance.RefreshCalendars());
        refresh.Margin = new Thickness(-7, 0, 0, 0);
        DockPanel.SetDock(refresh, Dock.Left);
        calRow.Children.Add(refresh);
        _calStatus = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Res("FgFaint"), Margin = new Thickness(8, 0, 0, 0) };
        calRow.Children.Add(_calStatus);
        p.Children.Add(calRow);
        UpdateCalStatus();

        // categories (stored in this account's board.json, so they sync with its other computers)
        p.Children.Add(Sub("Kategorie"));
        p.Children.Add(Label("Etykieta [Nazwa] na początku zadania. Wybierasz ją prawym przyciskiem → Kategoria, klikając pod polem nowego zadania albo wpisując #nazwa.", 11, "FgFaint"));
        var cats = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        p.Children.Add(cats);
        FillCategories(store, cats);
        var addCat = Btn("+ Dodaj kategorię", "SecondaryButton", (_, _) =>
        {
            var list = store.Categories.Select(x => new Category { Name = x.Name, Color = x.Color }).ToList();
            var used = list.Select(x => x.Color).ToHashSet(StringComparer.OrdinalIgnoreCase);
            list.Add(new Category { Name = "Nowa", Color = Category.Palette.FirstOrDefault(x => !used.Contains(x)) ?? Category.Palette[0] });
            SaveCategories(store, list);
            FillCategories(store, cats, focusLast: true);
        });
        addCat.HorizontalAlignment = HorizontalAlignment.Left;
        addCat.Margin = new Thickness(0, 6, 0, 0);
        p.Children.Add(addCat);
    }

    FrameworkElement FolderRow(string layer)
    {
        var box = new TextBox { IsReadOnly = true, Style = (Style)Application.Current.Resources["FieldBox"], Text = App.LayerFolder(layer) };
        var panel = new StackPanel();
        var row = new DockPanel();
        var open = Btn("Otwórz", "SecondaryButton", (_, _) => GlassWindow.OpenUrl(App.LayerFolder(layer)));
        var change = Btn("Zmień…", "SecondaryButton", (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = $"Folder konta „{App.AccountName(layer)}”", InitialDirectory = Directory.Exists(box.Text) ? box.Text : "" };
            if (dlg.ShowDialog(this) == true) UseFolder(layer, dlg.FolderName, box);
        });
        DockPanel.SetDock(open, Dock.Right);
        DockPanel.SetDock(change, Dock.Right);
        row.Children.Add(open);
        row.Children.Add(change);
        row.Children.Add(box);
        panel.Children.Add(row);
        var quick = new WrapPanel { Margin = new Thickness(-7, 4, 0, 0) };
        string sub = layer == "private" ? "DeskWall Prywatne" : "DeskWall";
        void Quick(string label, string? root, string missingTip)
        {
            var b = Btn(label, "LinkButton", (_, _) => { if (root != null) UseFolder(layer, Path.Combine(root, sub), box); });
            b.IsEnabled = root != null;
            b.ToolTip = root != null ? Path.Combine(root, sub) : missingTip;
            b.Margin = new Thickness(0, 0, 4, 0);
            quick.Children.Add(b);
        }
        Quick("Google Drive", SettingsStore.GoogleDriveRoot(), "Zainstaluj „Google Drive for desktop” – pojawi się „Mój dysk”, który DeskWall wykryje sam.");
        Quick("OneDrive", SettingsStore.OneDriveRoot(), "OneDrive nie jest skonfigurowany na tym komputerze.");
        panel.Children.Add(quick);
        return panel;
    }

    void UseFolder(string layer, string path, TextBox box)
    {
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(App.LayerFolder(layer)), StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            App.Instance.ChangeLayerFolder(layer, path);
            box.Text = path;
        }
        catch (Exception ex)
        {
            Log.Error("switch folder", ex);
            MessageBox.Show(this, "Nie udało się użyć tego folderu:\n" + ex.Message, "DeskWall", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            var swatch = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(col), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 10, 0), ToolTip = "Zmień kolor" };
            swatch.MouseLeftButtonUp += (_, _) => PickColor(swatch, c =>
            {
                list[index].Color = c;
                SaveCategories(store, list);
                FillCategories(store, cats);
            });
            DockPanel.SetDock(swatch, Dock.Left);
            row.Children.Add(swatch);
            var del = Btn("Usuń", "LinkButton", (_, _) => { list.RemoveAt(index); SaveCategories(store, list); FillCategories(store, cats); });
            del.ToolTip = "Usuwa kategorię z listy (zadania zachowują swoją etykietę)";
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
        _syncStatus.Text = $"Ostatni zapis: {saved}   ·   zmiany z innego komputera: {ext}   ·   kopie dzienne w podfolderze backup";
    }

    void UpdateCalStatus()
    {
        if (_calStatus == null) return;
        var when = CalendarService.LastFetch is { } t ? t.ToString("HH:mm") : "—";
        _calStatus.Text = $"pobrano {when}  ·  wydarzeń: {CalendarService.Count(false)}, świąt: {CalendarService.Count(true)}"
            + (CalendarService.LastError != null ? $"\nBłąd: {CalendarService.LastError}" : "");
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
        p.Children.Add(SliderRow("Skala całości", 80, 220, S.UiScale * 100, v => S.UiScale = Math.Round(v) / 100, v => $"{v:0}%"));
        p.Children.Add(SliderRow("Szerokość tablicy", 40, 100, S.WidthPercent, v => S.WidthPercent = v, v => $"{v:0}%"));
        p.Children.Add(SliderRow("Wysokość tygodnia", 10, 40, S.WeekHeightPercent, v => S.WeekHeightPercent = v, v => $"{v:0}% ekranu"));
        p.Children.Add(SliderRow("Położenie w pionie", 0, 100, S.VerticalPercent, v => S.VerticalPercent = v, v => $"{v:0}%"));
        p.Children.Add(SliderRow("Rozmiar tekstu zadań", 10, 17, S.FontSize, v => S.FontSize = Math.Round(v * 2) / 2, v => $"{Math.Round(v * 2) / 2:0.0}"));
        return p;
    }

    FrameworkElement BuildBoard()
    {
        var p = new StackPanel();
        p.Children.Add(Check("Pokazuj weekendy", S.ShowWeekends, v => S.ShowWeekends = v));
        p.Children.Add(Check("Przycisk widoku roku (365 dni)", S.ShowYearButton, v => S.ShowYearButton = v));
        p.Children.Add(Check("Automatycznie przenoś niezrobione zadania z minionych dni na dziś", S.AutoRollover, v => S.AutoRollover = v));
        var mode = new ComboBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        mode.Items.Add(new ComboBoxItem { Content = "Wysuwany po kliknięciu (na szerokość weekendu)", Tag = "drawer" });
        mode.Items.Add(new ComboBoxItem { Content = "Przypięty z prawej strony", Tag = "pinned" });
        mode.SelectedIndex = S.BacklogMode == "pinned" ? 1 : 0;
        mode.SelectionChanged += (_, _) => { if (mode.SelectedItem is ComboBoxItem it) { S.BacklogMode = (string)it.Tag; App.Instance.ApplySettings(); } };
        p.Children.Add(Pair("Backlog i archiwum", mode));
        p.Children.Add(SliderRow("Szerokość (przypięty)", 220, 560, S.BacklogWidth, v => S.BacklogWidth = v, v => $"{v:0} px"));
        return p;
    }

    FrameworkElement BuildLook()
    {
        var p = new StackPanel();
        p.Children.Add(Check("Matowe szkło (rozmyta tapeta pod tablicą)", S.Blur, v => S.Blur = v));
        p.Children.Add(Check("Animacje", S.Animations, v => S.Animations = v));
        p.Children.Add(SliderRow("Rozmycie", 0, 100, S.BlurStrength, v => S.BlurStrength = v, v => $"{v:0}"));
        p.Children.Add(SliderRow("Przyciemnienie", 0, 95, S.TintOpacity * 100, v => S.TintOpacity = v / 100, v => $"{v:0}%"));
        var refresh = Btn("Odśwież tapetę", "SecondaryButton", (_, _) => App.Instance.RefreshWallpaper());
        refresh.HorizontalAlignment = HorizontalAlignment.Left;
        refresh.Margin = new Thickness(0, 4, 0, 8);
        refresh.ToolTip = "Wczytaj tapetę ponownie, np. po zmianie przez pokaz slajdów albo Windows Spotlight";
        p.Children.Add(refresh);

        var swatches = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        void Mark(Border sel) { foreach (Border b in swatches.Children) b.BorderThickness = new Thickness(0); sel.BorderThickness = new Thickness(2); }
        var auto = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 10, 0), Cursor = Cursors.Hand,
            BorderBrush = Brushes.White, BorderThickness = new Thickness(S.Accent == "auto" ? 2 : 0),
            Background = new LinearGradientBrush(Color.FromRgb(0xF2, 0xA6, 0x5A), Color.FromRgb(0x5B, 0x8C, 0xFF), 45),
            ToolTip = "Automatycznie z tapety",
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
        p.Children.Add(Pair("Kolor akcentu", swatches));
        return p;
    }

    FrameworkElement BuildGeneral()
    {
        var p = new StackPanel();
        p.Children.Add(Check("Uruchamiaj razem z Windows", S.StartWithWindows, v => S.StartWithWindows = v));
        p.Children.Add(Check("Pokazuj zegar z kalendarzem", S.ShowClock, v => S.ShowClock = v));
        p.Children.Add(Check("Zegar 24-godzinny", S.Use24h, v => S.Use24h = v));
        p.Children.Add(Check("Dźwięk alarmów", S.AlarmSound, v => S.AlarmSound = v));
        p.Children.Add(Check("Otwieraj linki w aplikacji Notion (zamiast w przeglądarce)", S.OpenNotionInApp, v => S.OpenNotionInApp = v));
        var hot = new ComboBox { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (key, label) in new[] { ("Ctrl+Shift+Space", "Ctrl + Shift + Spacja"), ("Ctrl+Alt+D", "Ctrl + Alt + D"), ("Ctrl+Alt+T", "Ctrl + Alt + T"), ("Win+Shift+D", "Win + Shift + D"), ("", "Wyłączony") })
            hot.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        hot.SelectedItem = hot.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == S.Hotkey) ?? hot.Items[0];
        var warn = Label("Ten skrót jest zajęty przez inny program – wybierz inny.", 11.5, "FgDim");
        warn.Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xA6, 0x5A));
        void UpdateWarn() => warn.Visibility = App.Instance.HotkeyTaken ? Visibility.Visible : Visibility.Collapsed;
        hot.SelectionChanged += (_, _) => { if (hot.SelectedItem is ComboBoxItem it) { S.Hotkey = (string)it.Tag; App.Instance.ApplySettings(); UpdateWarn(); } };
        UpdateWarn();
        p.Children.Add(Pair("Skrót: tablica na wierzch + nowe zadanie na dziś (Esc chowa)", hot));
        p.Children.Add(warn);
        return p;
    }

    FrameworkElement BuildInstall()
    {
        var p = new StackPanel();
        var installed = Installer.IsInstalled;
        p.Children.Add(Label(installed
            ? $"DeskWall jest zainstalowany w {Installer.InstallDir}. Odinstalujesz go stąd albo w Ustawieniach Windows → Aplikacje."
            : "Ta kopia nie jest zainstalowana. Instalacja kopiuje program do folderu użytkownika, dodaje skrót w menu Start i wpis w „Aplikacje i funkcje” (bez uprawnień administratora).", 11.5, "FgDim"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var btn = installed
            ? Btn("Odinstaluj…", "SecondaryButton", (_, _) => Installer.Uninstall(this))
            : Btn("Zainstaluj w systemie", "PrimaryButton", (_, _) => Installer.InstallFromRunningCopy(this));
        btn.Margin = new Thickness(0);
        row.Children.Add(btn);
        p.Children.Add(row);
        var ver = Label($"Wersja {Installer.Version}  ·  dane zostają w folderach kont, ustawienia w %APPDATA%\\DeskWall", 11, "FgFaint");
        ver.Margin = new Thickness(0, 10, 0, 0);
        p.Children.Add(ver);
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
