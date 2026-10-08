using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace TaskWall;

/// <summary>Backlog drawer, task menu (incl. recurring series), global search, overdue, keyboard, top bar.</summary>
public partial class BoardWindow
{
    bool _drawerOpen, _pinnedVisible = true;
    string? _tagFilter;
    bool _archiveTab;

    void InitFeatures()
    {
        BacklogScroll.DragOver += (_, e) =>
        {
            if (_archiveTab) { AcceptDrop(e); HideDropLine(); }
            else OnDragOverTarget(BacklogList, e);
        };
        BacklogScroll.Drop += (_, e) =>
        {
            if (!_archiveTab) { OnDrop(null, BacklogList, e); return; }
            e.Handled = true; // dropped on the archive list: archive it
            if (DraggedTask(e) is { } t && !t.Archived) Dispatcher.BeginInvoke(() => Do(() => Store.Archive(t)));
        };
        // buttons need their own DragOver, otherwise the board's "not a target" handler refuses the drop
        foreach (var b in new[] { BacklogButton, ArchiveButton, BacklogTabButton, ArchiveTabButton }) b.DragOver += (_, e) => AcceptDrop(e);
        ArchiveButton.Drop += (_, e) => { e.Handled = true; if (DraggedTask(e) is { } t && !t.Archived) Dispatcher.BeginInvoke(() => Do(() => Store.Archive(t))); };
        BacklogButton.DragEnter += (_, _) => { if (S.BacklogMode == "drawer") SetDrawer(true); };
        BacklogButton.Drop += (_, e) => { e.Handled = true; if (DraggedTask(e) is { } t) Dispatcher.BeginInvoke(() => Do(() => ToBacklog(t))); };
        PreviewKeyDown += OnKey;
        // a Notion export dropped anywhere on the board opens the import
        BodyRoot.Drop += (_, e) =>
        {
            if (e.Handled || ExternalDrop.ImportFile(e.Data) is not { } file) return;
            e.Handled = true;
            Dispatcher.BeginInvoke(() => App.Instance.ImportNotion(file));
        };
        // drop a task on a tab: into the backlog / into the archive
        BacklogTabButton.Drop += (_, e) => { e.Handled = true; if (DraggedTask(e) is { } t) Dispatcher.BeginInvoke(() => Do(() => ToBacklog(t))); };
        ArchiveTabButton.Drop += (_, e) => { e.Handled = true; if (DraggedTask(e) is { } t && !t.Archived) Dispatcher.BeginInvoke(() => Do(() => Store.Archive(t))); };
        // forget a pressed row when the button goes up anywhere (else a later drag over it would start a drag)
        AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) => _pressed = null), handledEventsToo: true);
        BodyRoot.SizeChanged += (_, _) =>
        {
            if (S.BacklogMode == "drawer") ApplyBacklogMode();
            SearchScroll.MaxHeight = Math.Max(120, BodyRoot.ActualHeight - 120);
        };
    }

    public void ShowMonth() => SetView(ViewMode.Month);
    public void ShowDay() => SetView(ViewMode.Day);

    /// <summary>The period on screen: the day, the weeks, the month or the year.</summary>
    (DateTime From, DateTime To, string Name) VisiblePeriod() => _view switch
    {
        ViewMode.Year => (new DateTime(_year, 1, 1), new DateTime(_year, 12, 31), _year.ToString()),
        ViewMode.Month => (_monthFirst, _monthFirst.AddMonths(1).AddDays(-1), _monthFirst.ToString("yyyy-MM")),
        ViewMode.Day => (_dayDate, _dayDate, DayKey(_dayDate)),
        _ => (FirstMonday, FirstMonday.AddDays(7 * WeekCount - 1), WeekCount == 1 ? $"T{WeekNo(FirstMonday)}-{FirstMonday.Year}" : $"T{WeekNo(FirstMonday)}-{WeekNo(FirstMonday.AddDays(7))}-{FirstMonday.Year}"),
    };

    /// <summary>
    /// The period on screen to CSV (semicolon-separated, UTF-8 with BOM → opens straight in Polish Excel): one row per day
    /// (mark, tasks, done, hours, meetings), totals, day marks and categories, then the task list.
    /// </summary>
    void Export_Click(object sender, RoutedEventArgs e)
    {
        var (from, to, name) = VisiblePeriod();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = L.T("Eksport okresu do CSV"),
            FileName = $"TaskWall-{App.AccountName(S.Layer)}-{name}.csv",
            Filter = "CSV (*.csv)|*.csv",
            InitialDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };
        GlassWindow.EndOverlays();
        if (dlg.ShowDialog() != true) return;

        string H(double h) => h.ToString("0.##", Pl);
        static string Q(string s) => s.Contains(';') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{App.AccountName(S.Layer)};{from:yyyy-MM-dd} – {to:yyyy-MM-dd}");
        sb.AppendLine();
        sb.AppendLine(L.T("Data;Dzień tygodnia;Tydzień;Oznaczenie;Zadania;Zrobione;Estymacja [h];Zrobione [h];Spotkania [h]"));

        var allTasks = new List<(DateTime Day, TaskItem Task)>();
        int tasksSum = 0, doneSum = 0;
        double hoursSum = 0, doneHoursSum = 0, meetSum = 0;
        var marks = new Dictionary<string, int>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var key = DayKey(d);
            var tasks = Store.Data.Tasks.Where(t => t.Day == key).Concat(Store.VirtualTasks(d)).ToList(); // archived ones too
            allTasks.AddRange(tasks.Select(t => (d, t)));
            int done = tasks.Count(t => t.Done);
            double hours = tasks.Sum(t => t.Estimate ?? 0), doneHours = tasks.Where(t => t.Done).Sum(t => t.Estimate ?? 0);
            double meet = CalendarService.On(d).Where(x => !x.IsHoliday).Sum(x => x.Hours) + Store.Alarms.Where(a => a.IsMeeting && a.Occurs(d)).Sum(a => a.Hours);
            var mark = Store.DayMark(key);
            if (mark != null) marks[mark] = marks.GetValueOrDefault(mark) + 1;
            tasksSum += tasks.Count; doneSum += done; hoursSum += hours; doneHoursSum += doneHours; meetSum += meet;
            sb.AppendLine($"{key};{d.ToString("dddd", Pl)};{WeekNo(d)};{mark};{tasks.Count};{done};{H(hours)};{H(doneHours)};{H(meet)}");
        }
        sb.AppendLine($"{L.T("Razem")};;;{string.Join(" ", marks.Select(kv => $"{kv.Key} {kv.Value}"))};{tasksSum};{doneSum};{H(hoursSum)};{H(doneHoursSum)};{H(meetSum)}");

        sb.AppendLine();
        sb.AppendLine(L.T("Oznaczenie;Opis;Dni"));
        foreach (var m in Store.MarkTypes) sb.AppendLine($"{Q(m.Code)};{Q(m.Label)};{marks.GetValueOrDefault(m.Code)}");
        foreach (var kv in marks.Where(kv => Store.MarkTypeFor(kv.Key) == null)) sb.AppendLine($"{Q(kv.Key)};;{kv.Value}");

        sb.AppendLine();
        sb.AppendLine(L.T("Kategoria;Zadania;Zrobione;Estymacja [h];Zrobione [h]"));
        var byCat = allTasks.SelectMany(x => (TaskItem.Tags(x.Task.Text, out _) is { Count: > 0 } tags ? tags : new List<string> { L.T("(bez kategorii)") }).Select(c => (c, x.Task)))
            .GroupBy(x => x.c).OrderByDescending(g => g.Sum(x => x.Task.Estimate ?? 0));
        foreach (var g in byCat)
        {
            var name2 = Store.CategoryFor(g.Key)?.Name ?? g.Key;
            sb.AppendLine($"{Q(name2)};{g.Count()};{g.Count(x => x.Task.Done)};{H(g.Sum(x => x.Task.Estimate ?? 0))};{H(g.Where(x => x.Task.Done).Sum(x => x.Task.Estimate ?? 0))}");
        }

        sb.AppendLine();
        sb.AppendLine(L.T("Data;Zadanie;Kategorie;Zrobione;Estymacja [h];Link"));
        foreach (var (d, t) in allTasks.OrderBy(x => x.Day).ThenBy(x => x.Task.Order))
        {
            var tags = TaskItem.Tags(t.Text, out var rest);
            sb.AppendLine($"{DayKey(d)};{Q(rest)};{Q(string.Join(", ", tags))};{(t.Done ? L.T("tak") : L.T("nie"))};{(t.Estimate is { } est ? H(est) : "")};{t.Url}");
        }
        try
        {
            System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), new System.Text.UTF8Encoding(true));
            SyncText.Text = L.T("zapisano CSV");
            _syncFlash.Stop();
            _syncFlash.Start();
        }
        catch (Exception ex) { MessageBox.Show(L.T("Nie udało się zapisać pliku:") + "\n" + ex.Message, "TaskWall"); }
    }

    TaskItem? DraggedTask(DragEventArgs e) =>
        e.Data.GetData(DragFormat) is string id ? Store.Find(id) : null;

    void AcceptDrop(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    static void ToBacklog(TaskItem t)
    {
        if (t.Archived) { t.Archived = false; t.ArchivedAt = null; }
        MoveToEnd(t, null);
    }

    /// <summary>Opens the side panel on the archive tab.</summary>
    public void ShowArchive()
    {
        _archiveTab = true;
        RevealBacklog();
    }

    void BacklogTab_Click(object sender, RoutedEventArgs e) { _archiveTab = false; BuildBacklog(); UpdateBacklogButton(); }
    void ArchiveTab_Click(object sender, RoutedEventArgs e) { _archiveTab = true; BuildBacklog(); UpdateBacklogButton(); }

    void EmptyArchive_Click(object sender, RoutedEventArgs e)
    {
        var all = Store.Data.Tasks.Where(t => t.Archived).ToList();
        if (all.Count == 0) return;
        if (MessageBox.Show(L.F("Usunąć na zawsze {0} zarchiwizowanych zadań z warstwy „{1}”?\nZnikną też z widoku roku. Ctrl+Z na tablicy cofa.", all.Count, App.LayerName(S.Layer)),
                "TaskWall", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Do(() => { foreach (var t in all) Store.Remove(t); });
    }

    /// <summary>Called by App after Praca ⇄ Prywatne.</summary>
    public void OnLayerChanged()
    {
        _tagFilter = null;
        _addingKey = null;
        _progress.Clear();
        _scrollOffsets.Clear();
        SearchBox.Text = "";
        CloseSearch();
        Rebuild();
        Anim.Enter(_view == ViewMode.Year ? YearHost : WeeksGrid, 0, 12, 320);
    }

    public void ShowSearch(string q)
    {
        OpenSearch();
        GlobalSearch.Text = q;
    }

    public void ShowYear()
    {
        _year = DateTime.Today.Year;
        SetView(ViewMode.Year);
    }

    // ---------- backlog ----------

    void BuildBacklog()
    {
        BacklogList.Children.Clear();
        var q = SearchBox.Text.Trim();
        SearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        int archived = Store.Data.Tasks.Count(t => t.Archived);
        ArchiveCount.Text = archived.ToString();
        foreach (var (btn, on) in new[] { (BacklogTabButton, !_archiveTab), (ArchiveTabButton, _archiveTab) })
        {
            btn.Background = on ? B(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
            btn.Opacity = on ? 1 : 0.7;
        }
        ImportButton.Visibility = _archiveTab ? Visibility.Collapsed : Visibility.Visible;
        EmptyArchiveButton.Visibility = _archiveTab && archived > 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchHint.Text = _archiveTab ? L.T("Filtruj archiwum…") : L.T("Filtruj backlog…");
        BacklogCount.Text = Store.Data.Tasks.Count(t => t.IsBacklog && !t.Archived).ToString();
        if (_archiveTab) { BuildArchiveList(q); return; }

        var all = Store.Data.Tasks.Where(t => t.IsBacklog && !t.Archived).OrderBy(t => t.Order).ToList();

        var tagCounts = all.SelectMany(t => TaskItem.Tags(t.Text, out _).Distinct()).GroupBy(x => x).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).ToList();
        if (_tagFilter != null && tagCounts.All(g => g.Key != _tagFilter)) _tagFilter = null;
        TagChips.Children.Clear();
        TagChips.Visibility = tagCounts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var g in tagCounts)
        {
            var tag = g.Key;
            var color = TagColor(tag);
            bool on = _tagFilter == tag;
            var chip = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(8, 1, 8, 2),
                Margin = new Thickness(0, 0, 5, 5),
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1),
                BorderBrush = B(A(color, on ? (byte)0xFF : (byte)0x55)),
                Background = B(A(color, on ? (byte)0x55 : (byte)0x18)),
                Child = new TextBlock { Text = $"{(tag == TaskItem.MeetingTag ? "Meeting" : tag)}  {g.Count()}", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = B(color) },
                ToolTip = on ? L.T("Pokaż wszystkie") : L.F("Pokaż tylko [{0}]", tag),
            };
            chip.MouseLeftButtonUp += (_, _) => { _tagFilter = on ? null : tag; BuildBacklog(); };
            TagChips.Children.Add(chip);
        }

        var shown = all.Where(t =>
            (_tagFilter == null || TaskItem.Tags(t.Text, out _).Contains(_tagFilter)) &&
            (q.Length == 0 || Matches(t.Text, q) || Matches(t.Status, q) || Matches(t.Priority, q))).ToList();

        BacklogCount.Text = shown.Count == all.Count ? all.Count.ToString() : $"{shown.Count}/{all.Count}";
        foreach (var t in shown) BacklogList.Children.Add(BuildTaskRow(t, true));
        BacklogList.Children.Add(BuildAddRow(BacklogKey, null));
        if (all.Count == 0)
            BacklogList.Children.Add(new TextBlock
            {
                Text = L.T("Backlog jest pusty. Zaimportuj zadania z Notion (eksport CSV albo ZIP) albo dodaj własne. Zadania przeciągaj stąd na konkretne dni."),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Res("FgFaint"),
                FontSize = 11.5,
                Margin = new Thickness(2, 10, 6, 0),
            });
        _panels[BacklogKey] = BacklogList;
    }

    /// <summary>Archive tab: newest first; drag a row onto a day (or the Backlog tab) to restore it.</summary>
    void BuildArchiveList(string q)
    {
        TagChips.Visibility = Visibility.Collapsed;
        var list = Store.Data.Tasks.Where(t => t.Archived && (q.Length == 0 || Matches(t.Text, q)))
            .OrderByDescending(t => t.ArchivedAt ?? t.Modified).Take(300).ToList();
        string? lastGroup = null;
        foreach (var t in list)
        {
            var when = (t.ArchivedAt ?? t.Modified).ToLocalTime();
            var group = when.Date == DateTime.Today ? L.T("Dziś") : when.Date == DateTime.Today.AddDays(-1) ? L.T("Wczoraj") : Pl.TextInfo.ToTitleCase(when.ToString("MMMM yyyy", Pl));
            if (group != lastGroup)
            {
                BacklogList.Children.Add(new TextBlock { Text = group, FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Res("FgFaint"), Margin = new Thickness(0, lastGroup == null ? 2 : 10, 0, 4) });
                lastGroup = group;
            }
            BacklogList.Children.Add(BuildArchiveRow(t));
        }
        if (list.Count == 0)
            BacklogList.Children.Add(new TextBlock
            {
                Text = q.Length > 0 ? L.T("Nic nie znaleziono.") : L.T("Archiwum jest puste. Trafiają tu zadania z ✕, środkowego przycisku myszy i „Wyczyść zrobione”."),
                TextWrapping = TextWrapping.Wrap, Foreground = Res("FgFaint"), FontSize = 11.5, Margin = new Thickness(2, 10, 6, 0),
            });
    }

    /// <summary>Archive row: same layout as a backlog row (circle, text, meta line), in one neutral grey.</summary>
    FrameworkElement BuildArchiveRow(TaskItem t)
    {
        var neutral = Color.FromRgb(0x9A, 0xA1, 0xB2);
        var row = new Border { Tag = t, CornerRadius = new CornerRadius(6), Padding = new Thickness(4, 3, 1, 3), Margin = new Thickness(-4, 0, 0, 1) };
        Anim.HoverBackground(row, Colors.Transparent, Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var circle = new Border
        {
            Width = 14, Height = 14, CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1.3),
            BorderBrush = B(A(neutral, 0xB0)), Background = t.Done ? B(A(neutral, 0x90)) : Brushes.Transparent,
            Margin = new Thickness(0, Math.Max(0, (S.FontSize * 1.33 - 14) / 2), 8, 0), VerticalAlignment = VerticalAlignment.Top,
        };
        if (t.Done)
            circle.Child = new TextBlock { Text = "\uE73E", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 8.5, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(circle);

        var sp = new StackPanel();
        Grid.SetColumn(sp, 1);
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Res("Fg"), FontSize = S.FontSize };
        var tags = TaskItem.Tags(t.Text, out var rest);
        foreach (var tag in tags)
            text.Inlines.Add(new InlineUIContainer(new Border
            {
                CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 0, 4, 1), Margin = new Thickness(0, 0, 5, 0), Background = B(A(neutral, 0x2E)),
                Child = new TextBlock { Text = tag == TaskItem.MeetingTag ? "Meeting" : tag, FontSize = Math.Max(8.5, S.FontSize - 3), FontWeight = FontWeights.Bold, Foreground = B(neutral) },
            }) { BaselineAlignment = BaselineAlignment.Center });
        text.Inlines.Add(new Run(tags.Count > 0 ? rest : t.Text));
        if (t.Done) { text.TextDecorations = TextDecorations.Strikethrough; text.Opacity = 0.75; }
        sp.Children.Add(text);
        var where = t.Day == null ? L.T("z backlogu") : ParseKey(t.Day).ToString("ddd, d MMM yyyy", Pl);
        var meta = new WrapPanel { Margin = new Thickness(0, 3, 0, 1) };
        meta.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(0, 0, 7, 0), Background = B(A(neutral, 0x40)),
            Child = new TextBlock { Text = t.Done ? L.T("zrobione") : L.T("niezrobione"), FontSize = 10.5, Foreground = Res("Fg") },
        });
        meta.Children.Add(new TextBlock { Text = where, FontSize = 10.5, Foreground = Res("FgFaint"), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(meta);
        g.Children.Add(sp);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Hidden };
        Grid.SetColumn(buttons, 2);
        Button Icon(string glyph, string tip, Action a)
        {
            var b = new Button { Style = (Style)FindResource("IconButton"), Content = glyph, FontSize = 10, Padding = new Thickness(5, 3, 5, 3), ToolTip = tip };
            b.Click += (_, _) => a();
            return b;
        }
        buttons.Children.Add(Icon("\uE7A7", L.T("Przywróć na swoje miejsce"), () => Do(() => Store.Unarchive(Store.Materialize(t)))));
        buttons.Children.Add(Icon("\uE74D", L.T("Usuń całkowicie (Ctrl+Z cofa)"), () => DeleteForGood(t, row)));
        g.Children.Add(buttons);
        row.Child = g;
        row.MouseEnter += (_, _) => buttons.Visibility = Visibility.Visible;
        row.MouseLeave += (_, _) => buttons.Visibility = Visibility.Hidden;
        row.MouseLeftButtonDown += (_, e) => { _pressed = t; _pressPoint = e.GetPosition(this); };
        row.MouseMove += (_, e) =>
        {
            if (_pressed != t || e.LeftButton != MouseButtonState.Pressed) return;
            var d = e.GetPosition(this) - _pressPoint;
            if (Math.Abs(d.X) + Math.Abs(d.Y) < 5) return;
            _pressed = null;
            StartDrag(t, row);
        };
        row.ToolTip = L.T("Przeciągnij na dzień albo na BACKLOG, żeby przywrócić");
        if (_flashId == t.Id) Flash(row);
        return row;
    }
    /// <summary>BACKLOG (n) and ARCHIWUM (n): highlighted while their tab is showing.</summary>
    void UpdateBacklogButton()
    {
        int n = Store.Data.Tasks.Count(t => t.IsBacklog && !t.Archived);
        int a = Store.Data.Tasks.Count(t => t.Archived);
        bool shown = S.BacklogMode == "drawer" ? _drawerOpen : _pinnedVisible;
        BacklogButton.Content = $"BACKLOG ({n})";
        ArchiveButton.Content = L.F("ARCHIWUM ({0})", a);
        foreach (var (btn, on) in new[] { (BacklogButton, shown && !_archiveTab), (ArchiveButton, shown && _archiveTab) })
        {
            btn.Background = on ? B(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
            btn.Foreground = on ? Res("Fg") : Res("FgDim");
        }
    }

    /// <summary>Opens the panel on a tab; clicking the button of the tab already showing closes it.</summary>
    void TogglePanel(bool archive)
    {
        bool shown = S.BacklogMode == "drawer" ? _drawerOpen : _pinnedVisible;
        if (shown && _archiveTab == archive)
        {
            if (S.BacklogMode == "drawer") SetDrawer(false);
            else { _pinnedVisible = false; Rebuild(); }
            return;
        }
        _archiveTab = archive;
        if (S.BacklogMode == "drawer") { BuildBacklog(); SetDrawer(true); }
        else { _pinnedVisible = true; Rebuild(); }
        UpdateBacklogButton();
    }

    void ToggleArchive_Click(object sender, RoutedEventArgs e) => TogglePanel(archive: true);

    public void RevealBacklog()
    {
        if (S.BacklogMode == "drawer") SetDrawer(true);
        else _pinnedVisible = true;
        Rebuild();
    }

    /// <summary>Drawer covers exactly the last two day columns (Saturday + Sunday) up to the right edge.</summary>
    double DrawerWidth()
    {
        double area = BoardArea.ActualWidth;
        if (area <= 0) return S.BacklogWidth;
        int days = S.ShowWeekends ? 7 : 5;
        var m = BoardArea.Margin;
        return area / days * 2 + m.Right + 4;
    }

    void ApplyBacklogMode()
    {
        if (S.BacklogMode == "drawer")
        {
            BacklogColumn.Width = new GridLength(0);
            Grid.SetColumn(BacklogPanel, 0);
            Grid.SetColumnSpan(BacklogPanel, 2);
            BacklogPanel.HorizontalAlignment = HorizontalAlignment.Right;
            BacklogPanel.Width = DrawerWidth();
            Panel.SetZIndex(BacklogPanel, 10);
            BacklogSurface.Background = B(Color.FromArgb(0xA8, 0x0C, 0x0E, 0x14));
            BacklogSurface.BorderBrush = B(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            if (!_drawerOpen && !DrawerShift.HasAnimatedProperties) DrawerShift.X = DrawerWidth() + 30;
            BacklogPanel.Visibility = _drawerOpen || DrawerShift.HasAnimatedProperties ? Visibility.Visible : Visibility.Collapsed;
            UpdateDrawerGlass();
        }
        else
        {
            BacklogColumn.Width = new GridLength(_pinnedVisible ? S.BacklogWidth : 0);
            Grid.SetColumn(BacklogPanel, 1);
            Grid.SetColumnSpan(BacklogPanel, 1);
            BacklogPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            BacklogPanel.Width = double.NaN;
            Panel.SetZIndex(BacklogPanel, 0);
            DrawerShift.BeginAnimation(TranslateTransform.XProperty, null);
            DrawerShift.X = 0;
            DrawerGlass.Background = null;
            BacklogSurface.Background = B(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF));
            BacklogSurface.BorderBrush = Res("Line");
            BacklogPanel.Visibility = _pinnedVisible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>The drawer shows the same frosted wallpaper as the window behind it (right-hand strip).</summary>
    public void UpdateDrawerGlass()
    {
        if (S.BacklogMode != "drawer" || GlassBrush?.ImageSource is not { } img || ActualWidth <= 0) { DrawerGlass.Background = null; return; }
        double frac = Math.Min(1, DrawerWidth() * S.UiScale / ActualWidth);
        DrawerGlass.Background = new ImageBrush(img)
        {
            ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewbox = new Rect(1 - frac, 0, frac, 1),
            Stretch = Stretch.Fill,
        };
    }

    void SetDrawer(bool open)
    {
        if (S.BacklogMode != "drawer" || _drawerOpen == open) return;
        _drawerOpen = open;
        BacklogPanel.Visibility = Visibility.Visible;
        double hidden = DrawerWidth() + 30;
        double from = DrawerShift.HasAnimatedProperties ? DrawerShift.X : open ? hidden : 0; // reversing mid-slide continues smoothly
        Anim.Slide(BacklogPanel, from, open ? 0 : hidden, 300, () =>
        {
            DrawerShift.BeginAnimation(TranslateTransform.XProperty, null);
            DrawerShift.X = _drawerOpen ? 0 : hidden;
            if (!_drawerOpen) BacklogPanel.Visibility = Visibility.Collapsed;
        });
        UpdateBacklogButton();
    }

    void BodyRoot_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return;
        if (_drawerOpen && !IsInside(src, BacklogPanel) && !IsInside(src, BacklogButton)) SetDrawer(false);
        if (SearchPanel.Visibility == Visibility.Visible && !IsInside(src, SearchPanel) && !IsInside(src, SearchButton)) CloseSearch();
    }

    static bool IsInside(DependencyObject? d, DependencyObject container)
    {
        for (; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (ReferenceEquals(d, container)) return true;
        return false;
    }

    // ---------- task context menu ----------

    ContextMenu BuildMenu(TaskItem t, StackPanel content, TextBlock text, FrameworkElement row)
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, Action action, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => action();
            return mi;
        }

        if (t.HasLink) menu.Items.Add(Item(L.T("Otwórz w Notion"), () => OpenLink(t)));
        menu.Items.Add(Item(t.HasLink ? L.T("Edytuj tekst") : L.T("Edytuj"), () => BeginEdit(t, content, text, series: false)));
        menu.Items.Add(Item(t.Done ? L.T("Oznacz jako niezrobione") : L.T("Oznacz jako zrobione"), () => Do(() =>
        {
            var real = Store.Materialize(t);
            real.Done = !real.Done;
            Store.Changed(real);
            _justToggled = real.Id;
        })));

        var todayKey = DayKey(DateTime.Today);
        if (t.Day != todayKey) menu.Items.Add(Item(L.T("Przenieś na dziś"), () => Do(() => MoveToEnd(t, todayKey))));
        var move = new MenuItem { Header = L.T("Przenieś do") };
        if (!t.IsBacklog) move.Items.Add(Item("Backlog", () => Do(() => MoveToEnd(t, null))));
        if (t.Day != null)
            move.Items.Add(Item(L.T("Ten sam dzień za tydzień"), () => Do(() => MoveToEnd(t, DayKey(ParseKey(t.Day).AddDays(7))))));
        move.Items.Add(new Separator());
        var first = IsWeekView ? FirstMonday : Monday(DateTime.Today);
        int span = IsWeekView ? 7 * WeekCount : 14;
        for (int i = 0; i < span; i++)
        {
            var date = first.AddDays(i);
            if (!S.ShowWeekends && IsWeekend(date)) continue;
            var key = DayKey(date);
            var label = date.ToString("dddd, d MMM", Pl);
            if (date == DateTime.Today) label += "  " + L.T("(dziś)");
            move.Items.Add(Item(label, () => Do(() => MoveToEnd(t, key)), key != t.Day));
        }
        menu.Items.Add(move);

        // recurring series
        if (t.Day != null)
        {
            var rule = Store.Rule(t.RuleId);
            var rep = new MenuItem { Header = rule != null ? L.T("Seria:") + " " + rule.Summary : L.T("Powtarzaj") };
            rep.Items.Add(Item(rule != null ? L.T("Zmień powtarzanie…") : L.T("Powtarzaj…  (co N, wybrane dni, okres od–do)"), () => RepeatWindow.ForTask(Store, t)));
            rep.Items.Add(new Separator());
            foreach (var (code, label) in RecurringRule.Patterns)
            {
                var d = ParseKey(t.Day);
                var detail = code switch
                {
                    "weekly" or "biweekly" => $" ({d.ToString("dddd", Pl)})",
                    "monthly" => " " + L.F("({0}. dnia)", d.Day),
                    _ => "",
                };
                rep.Items.Add(Item((rule?.Pattern == code ? "✓  " : "     ") + label + detail, () => Do(() =>
                {
                    if (rule != null) { rule.Pattern = code; rule.Interval = 1; rule.Weekdays = null; rule.Start = t.RuleDay ?? t.Day; Store.Changed(rule); return; }
                    var real = Store.Materialize(t);
                    var r = new RecurringRule { Text = real.Text, Pattern = code, Start = real.Day!, Estimate = real.Estimate, Order = real.Order };
                    Store.AddRule(r);
                    real.RuleId = r.Id;
                    real.RuleDay = real.Day;
                    Store.Changed(real);
                })));
            }
            if (rule != null)
            {
                rep.Items.Add(new Separator());
                rep.Items.Add(Item(L.T("Zmień nazwę serii"), () => BeginEdit(t, content, text, series: true)));
                rep.Items.Add(Item(L.T("Zakończ serię na tym dniu"), () => Do(() => { rule.End = t.RuleDay ?? t.Day; Store.Changed(rule); })));
                rep.Items.Add(Item(L.T("Usuń serię (wykonane zostają)"), () => Do(() => { rule.Deleted = true; Store.Changed(rule); })));
            }
            menu.Items.Add(rep);
        }

        bool hasList = t.Checklist is { Count: > 0 };
        menu.Items.Add(Item(hasList ? (_expanded.Contains(t.Id) ? L.T("Zwiń listę kontrolną") : L.T("Pokaż listę kontrolną")) : L.T("Dodaj listę kontrolną"), () =>
        {
            if (hasList && _expanded.Contains(t.Id)) _expanded.Remove(t.Id);
            else { _expanded.Add(t.Id); if (!hasList) _checkAddFor = t.Id; }
            Rebuild();
        }));

        var est = new MenuItem { Header = L.T("Estymacja") +(t.Estimate is > 0 ? $"  ({Hours(t.Estimate.Value)})" : "") };
        foreach (var h in new double?[] { null, 0.5, 1, 1.5, 2, 3, 4, 6, 8 })
        {
            var label = h == null ? L.T("Brak") : Hours(h.Value);
            if (t.Estimate == h) label = "✓  " + label;
            est.Items.Add(Item(label, () => Do(() => { var real = Store.Materialize(t); real.Estimate = h; Store.Changed(real); })));
        }
        menu.Items.Add(est);

        var tags = TaskItem.Tags(t.Text, out _);
        var cats = new MenuItem { Header = L.T("Kategoria") +(tags.Count > 0 ? $"  ({string.Join(", ", tags.Select(x => Store.CategoryFor(x)?.Name ?? x))})" : "") };
        foreach (var c in Store.Categories)
        {
            bool has = tags.Contains(c.Key);
            var mi = Item((has ? "✓  " : "     ") + c.Name, () => Do(() =>
            {
                var real = Store.Materialize(t);
                real.Text = ToggleCategory(real.Text, c.Name);
                Store.Changed(real);
            }));
            try { mi.Icon = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = B((Color)ColorConverter.ConvertFromString(c.Color)) }; } catch { }
            cats.Items.Add(mi);
        }
        cats.Items.Add(new Separator());
        cats.Items.Add(Item(L.T("Edytuj kategorie…"), () => App.Instance.ShowSettings()));
        menu.Items.Add(cats);

        menu.Items.Add(Item(L.T("Duplikuj"), () =>
        {
            var copy = t.Clone();
            copy.Id = Guid.NewGuid().ToString("N");
            copy.IsVirtual = false;
            copy.RuleId = copy.RuleDay = null;
            copy.NotionId = null; // re-imports keep updating only the original
            copy.Order = t.Order + 0.001;
            _justAdded = copy.Id;
            Do(() => Store.Add(copy));
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(t.IsVirtual ? L.T("Pomiń to wystąpienie") : L.T("Archiwizuj"), () => ArchiveRow(t, row)));
        if (!t.IsVirtual) menu.Items.Add(Item(L.T("Usuń całkowicie"), () => DeleteForGood(t, row)));
        return menu;
    }

    /// <summary>Adds "[Name] " at the front, or removes it when it's already there.</summary>
    public static string ToggleCategory(string text, string name)
    {
        var rx = new System.Text.RegularExpressions.Regex(@"\[\s*" + System.Text.RegularExpressions.Regex.Escape(name) + @"\s*\]\s*", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        TaskItem.Tags(text, out var rest);
        var prefix = text[..^rest.Length];
        if (rx.IsMatch(prefix))
        {
            var left = rx.Replace(prefix, "", 1).Trim();
            return left.Length > 0 ? left + " " + rest : rest;
        }
        return $"[{name}] " + text.TrimStart();
    }

    // ---------- global search ----------

    static readonly CompareInfo Cmp = CultureInfo.InvariantCulture.CompareInfo;

    /// <summary>Case- and accent-insensitive "contains" (zolw → żółw).</summary>
    static bool Matches(string? text, string q) =>
        text != null && Cmp.IndexOf(text, q, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    void OpenSearch()
    {
        SetDrawer(false);
        SearchPanel.Visibility = Visibility.Visible;
        Anim.Enter(SearchPanel, 0, -8, 200);
        Activate();
        GlobalSearch.Focus();
        Keyboard.Focus(GlobalSearch);
        GlobalSearch.SelectAll();
        RunSearch();
    }

    void CloseSearch() => SearchPanel.Visibility = Visibility.Collapsed;

    void GlobalSearch_Changed(object sender, TextChangedEventArgs e) => RunSearch();

    sealed record Hit(DateTime? Date, string Label, string Text, bool Done, Action Go, bool Meeting = false, bool Archived = false);

    void RunSearch()
    {
        var q = GlobalSearch.Text.Trim();
        GlobalSearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchResults.Children.Clear();
        if (q.Length < 2)
        {
            SearchInfo.Text = L.T("Wpisz co najmniej 2 znaki. Szuka bez względu na wielkość liter i polskie znaki.");
            return;
        }

        var hits = new List<Hit>();
        foreach (var t in Store.Data.Tasks.Where(t => Matches(t.Text, q) || Matches(t.Status, q)))
        {
            var task = t;
            DateTime? d = t.Day != null ? ParseKey(t.Day) : null;
            var label = t.Archived ? L.T("Archiwum") +(d != null ? " · " + d.Value.ToString("d MMM yyyy", Pl) : "")
                : d == null ? "Backlog" : d.Value.ToString("ddd, d MMM yyyy", Pl);
            hits.Add(new Hit(d, label, t.Text, t.Done, () =>
            {
                _flashId = task.Id;
                if (task.Archived || task.Day == null)
                {
                    // make sure the row is actually listed: right tab, no filters
                    _archiveTab = task.Archived;
                    _tagFilter = null;
                    SearchBox.Text = "";
                    RevealBacklog();
                }
                else JumpTo(ParseKey(task.Day));
                _flashId = null;
            }, task.IsMeeting, task.Archived));
        }
        foreach (var r in Store.Data.Rules.Where(r => !r.Deleted && Matches(r.Text, q)))
        {
            var next = Enumerable.Range(0, 400).Select(i => DateTime.Today.AddDays(i)).FirstOrDefault(r.Occurs);
            if (next == default) continue;
            var label = L.F("Seria · {0} · najbliżej {1}", r.Summary, next.ToString("ddd d MMM", Pl));
            hits.Add(new Hit(next, label, r.Text, false, () => JumpTo(next)));
        }
        foreach (var ev in CalendarService.All.Where(e => !e.IsHoliday && Matches(e.Title, q)))
        {
            var date = ev.Start.Date;
            hits.Add(new Hit(date, date.ToString("ddd, d MMM yyyy", Pl) + (ev.AllDay ? "" : " · " + ev.Start.ToString("HH:mm")), ev.Title, false, () => JumpTo(date), Meeting: true));
        }

        // backlog first, then closest to today
        var ordered = hits.OrderBy(h => h.Date == null && !h.Archived ? 0 : 1)
            .ThenBy(h => h.Archived ? 1 : 0)
            .ThenBy(h => h.Date == null ? 0 : Math.Abs((h.Date.Value - DateTime.Today).TotalDays))
            .Take(80).ToList();
        SearchInfo.Text = hits.Count == 0 ? L.T("Nic nie znaleziono.") : hits.Count > ordered.Count ? L.F("Wyniki: {0} (pokazuję {1} najbliższych)", hits.Count, ordered.Count) : L.F("Wyniki: {0}", hits.Count);

        foreach (var h in ordered)
        {
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock { Text = h.Label, FontSize = 11, Foreground = h.Date == DateTime.Today ? Res("AccentBrush") : Res("FgFaint"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = Res("Fg") };
            if (h.Meeting) tb.Inlines.Add(new Run("  ") { FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 10, Foreground = B(MeetingColor) });
            tb.Inlines.Add(new Run(h.Text));
            if (h.Done) { tb.TextDecorations = TextDecorations.Strikethrough; tb.Opacity = 0.5; }
            Grid.SetColumn(tb, 1);
            row.Children.Add(tb);
            var bd = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 5, 8, 5), Cursor = Cursors.Hand, Child = row };
            Anim.HoverBackground(bd, Colors.Transparent, Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
            var go = h.Go;
            bd.MouseLeftButtonUp += (_, _) => { CloseSearch(); go(); };
            SearchResults.Children.Add(bd);
        }
    }

    // ---------- overdue ----------

    static List<TaskItem> Overdue()
    {
        var today = DayKey(DateTime.Today);
        return Store.Data.Tasks.Where(t => !t.Done && !t.Archived && t.Day != null && string.CompareOrdinal(t.Day, today) < 0)
            .OrderBy(t => t.Day).ThenBy(t => t.Order).ToList();
    }

    /// <summary>Moves unfinished tasks from past days to today. Returns how many were moved.</summary>
    public int RollOverdue()
    {
        var list = Overdue();
        if (list.Count == 0) return 0;
        Store.Checkpoint();
        foreach (var t in list) MoveToEnd(t, DayKey(DateTime.Today));
        Rebuild();
        return list.Count;
    }

    void Overdue_Click(object sender, RoutedEventArgs e) => RollOverdue();

    // ---------- keyboard ----------

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; OpenSearch(); return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.D1 or Key.D2)
        {
            e.Handled = true;
            App.Instance.SwitchLayer(e.Key == Key.D1 ? "work" : "private");
            return;
        }
        if (e.Key == Key.Escape && SearchPanel.Visibility == Visibility.Visible) { e.Handled = true; CloseSearch(); return; }
        if (Keyboard.FocusedElement is TextBox) return;
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            Undo();
        }
        else if (e.Key == Key.Escape)
        {
            if (_drawerOpen) SetDrawer(false);
            else if (!IsWeekView) SetView(_lastWeekView);
            else if (IsPeeking) Peek(false);
        }
    }

    public void Undo()
    {
        if (Store.Undo()) Rebuild();
        else SyncText.Text = L.T("nie ma czego cofnąć");
    }

    // ---------- top bar ----------

    void Prev_Click(object sender, RoutedEventArgs e) => Step(-1);
    void Next_Click(object sender, RoutedEventArgs e) => Step(1);

    void Step(int dir)
    {
        _slideDir = dir;
        switch (_view)
        {
            case ViewMode.Year: _year += dir; break;
            case ViewMode.Day: _dayDate = _dayDate.AddDays(dir); break;
            case ViewMode.Month:
                int rows = MonthRows();
                _monthFirst = _monthFirst.AddMonths(dir);
                Rebuild();
                if (rows != MonthRows()) App.Instance.LayoutWindows();
                return;
            default: _weekOffset += dir; break;
        }
        Rebuild();
    }

    void Today_Click(object sender, RoutedEventArgs e)
    {
        switch (_view)
        {
            case ViewMode.Year: _slideDir = Math.Sign(DateTime.Today.Year - _year); _year = DateTime.Today.Year; break;
            case ViewMode.Day: _slideDir = Math.Sign((DateTime.Today - _dayDate).TotalDays); _dayDate = DateTime.Today; break;
            case ViewMode.Month:
                var m = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
                _slideDir = Math.Sign((m - _monthFirst).TotalDays);
                _monthFirst = m;
                Rebuild();
                App.Instance.LayoutWindows();
                return;
            default: _slideDir = Math.Sign(-_weekOffset); _weekOffset = 0; break;
        }
        Rebuild();
    }

    void DayView_Click(object sender, RoutedEventArgs e) => SetView(ViewMode.Day);
    void OneWeek_Click(object sender, RoutedEventArgs e) => SetView(ViewMode.Week1);
    void Hide_Click(object sender, RoutedEventArgs e) => App.Instance.SetBoardHidden(true);
    void WeekView_Click(object sender, RoutedEventArgs e) => SetView(ViewMode.Week2);
    void WorkLayer_Click(object sender, RoutedEventArgs e) => App.Instance.SwitchLayer("work");
    void PrivateLayer_Click(object sender, RoutedEventArgs e) => App.Instance.SwitchLayer("private");
    void MonthView_Click(object sender, RoutedEventArgs e) => SetView(ViewMode.Month);
    void Year_Click(object sender, RoutedEventArgs e) => SetView(ViewMode.Year);
    void Search_Click(object sender, RoutedEventArgs e) { if (SearchPanel.Visibility == Visibility.Visible) CloseSearch(); else OpenSearch(); }
    void Settings_Click(object sender, RoutedEventArgs e) => App.Instance.ShowSettings();
    void Bar_RightClick(object sender, MouseButtonEventArgs e) => App.Instance.ShowSettings();
    void Import_Click(object sender, RoutedEventArgs e) => App.Instance.ImportNotion();
    void Search_Changed(object sender, TextChangedEventArgs e) { if (IsLoaded) BuildBacklog(); }

    void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = PlacementMode.Bottom };
        void Add(string header, Action a, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        Add(L.T("Szukaj…  (Ctrl+F)"), OpenSearch);
        Add(L.T("Cofnij  (Ctrl+Z)"), Undo, Store.CanUndo);
        menu.Items.Add(new Separator());
        if (S.AccountFor(S.Layer).Notion.Configured)
            Add(L.T("Synchronizuj Notion teraz"), async () => { var r = await NotionSync.Run(S.Layer); SyncText.Text = r.Error ?? L.F("Notion: nowe {0}, zmienione {1}", r.Added, r.Updated); _syncFlash.Stop(); _syncFlash.Start(); });
        Add(L.T("Importuj z Notion…"), () => App.Instance.ImportNotion());
        Add(L.T("Eksportuj widoczny okres do CSV…"), () => Export_Click(this, new RoutedEventArgs()));
        Add(L.T("Odśwież kalendarze"), () => App.Instance.RefreshCalendars());
        Add(L.T("Otwórz folder danych"), () => OpenUrl(Store.Folder));
        Add(L.T("Ustawienia…"), () => App.Instance.ShowSettings());
        menu.IsOpen = true;
    }


    /// <summary>Upcoming alarms and meetings of this account (next 7 days) + "Nowy alarm…" / "Nowe spotkanie…".</summary>
    void Alarm_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AlarmButton, Placement = PlacementMode.Bottom };
        var now = DateTime.Now;
        var upcoming = Store.Alarms.Select(a => (a, at: a.NextStart(now))).Where(x => x.at is { } t && t < now.AddDays(7))
            .OrderBy(x => x.at).Take(14).ToList();
        if (upcoming.Count == 0) menu.Items.Add(new MenuItem { Header = L.T("Brak alarmów i spotkań w najbliższym tygodniu"), IsEnabled = false });
        foreach (var (a, at) in upcoming)
        {
            var d = at!.Value;
            var when = d.Date == DateTime.Today ? L.T("dziś") : d.Date == DateTime.Today.AddDays(1) ? L.T("jutro") : d.ToString("ddd d MMM", Pl);
            var time = a.IsMeeting ? $"{a.Time}–{a.EndAt(d):HH:mm}" : a.Time;
            var mi = new MenuItem
            {
                Header = $"{(a.IsMeeting ? "◆" : "⏰")}  {when} {time}   {(a.Text.Length > 0 ? a.Text : a.IsMeeting ? L.T("Spotkanie") : L.T("Alarm"))}{(a.Once ? "" : "  ↻")}",
                ToolTip = AlarmService.Until(d),
            };
            mi.Click += (_, _) => AlarmWindow.Edit(Store, a, d.Date);
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        var add = new MenuItem { Header = L.T("Nowy alarm…") };
        add.Click += (_, _) => AlarmWindow.Edit(Store);
        menu.Items.Add(add);
        var meet = new MenuItem { Header = L.T("Nowe spotkanie…") };
        meet.Click += (_, _) => AlarmWindow.Edit(Store, null, DateTime.Today, meeting: true);
        menu.Items.Add(meet);
        menu.IsOpen = true;
    }

    void ToggleBacklog_Click(object sender, RoutedEventArgs e) => TogglePanel(archive: false);

    void ClearDone_Click(object sender, RoutedEventArgs e)
    {
        var (first, last, _) = VisiblePeriod();
        var keys = Enumerable.Range(0, (int)(last - first).TotalDays + 1).Select(i => first.AddDays(i))
            .Where(d => _view == ViewMode.Day || S.ShowWeekends || !IsWeekend(d)).Select(DayKey).ToHashSet(); // only what's on screen
        var done = Store.Data.Tasks.Where(t => t.Done && !t.Archived && (t.IsBacklog || keys.Contains(t.Day!))).ToList();
        if (done.Count == 0) return;
        Do(() => { foreach (var t in done) Store.Archive(t); });
        SyncText.Text = L.F("zarchiwizowano {0}", done.Count);
        _syncFlash.Stop();
        _syncFlash.Start();
    }
}
