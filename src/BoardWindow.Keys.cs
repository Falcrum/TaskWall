using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace TaskWall;

/// <summary>
/// The keyboard: a selected task moved with the arrows (↑ ↓ in the day, ← → to the next day, Ctrl+← → takes the task
/// along) and the command palette (Ctrl+K) – one box for views, going to a date, adding, searching and the selected
/// task's actions.
/// </summary>
public partial class BoardWindow
{
    // ---------- the selected task (arrows) ----------

    (string Id, string Day)? _sel;

    /// <summary>Every task row of a day in the order on screen: the lanes' block first (by lane), then the list.</summary>
    static List<FrameworkElement> NavRows(Panel panel)
    {
        var rows = new List<FrameworkElement>();
        foreach (var c in panel.Children.OfType<FrameworkElement>())
            if (c.Tag is TaskItem) rows.Add(c);
            else if (c is Grid block) rows.AddRange(block.Children.OfType<FrameworkElement>().Where(r => r.Tag is TaskItem).OrderBy(Grid.GetRow));
        return rows;
    }

    /// <summary>The days on screen with their rows, in date order.</summary>
    List<(string Day, List<FrameworkElement> Rows)> NavDays() =>
        _panels.OrderBy(kv => kv.Key, StringComparer.Ordinal).Where(kv => kv.Key != BacklogKey).Select(kv => (kv.Key, NavRows(kv.Value))).ToList();

    FrameworkElement? SelectedRow() =>
        _sel is { } s && _panels.TryGetValue(s.Day, out var p) ? NavRows(p).FirstOrDefault(r => ((TaskItem)r.Tag).Id == s.Id) : null;

    TaskItem? SelectedTask() => SelectedRow()?.Tag as TaskItem is { } t ? (t.IsVirtual ? t : Store.Find(t.Id)) : null;

    Border? _selFrame;

    /// <summary>A frame in the accent round the selected row (again after every build).</summary>
    void ApplySelection()
    {
        if (_selFrame != null) { _selFrame.BorderThickness = new Thickness(0); _selFrame.Padding = Pad(_selFrame, -1); _selFrame = null; }
        if (SelectedRow() is not Border row) { if (_sel != null && !_panels.ContainsKey(_sel.Value.Day)) _sel = null; return; }
        row.BorderBrush = Res("AccentBrush");
        row.BorderThickness = new Thickness(1);
        row.Padding = Pad(row, 1); // the frame takes the padding's place: nothing moves
        _selFrame = row;
        row.BringIntoView();
    }

    static Thickness Pad(Border b, double less) => new(Math.Max(0, b.Padding.Left - less), Math.Max(0, b.Padding.Top - less), Math.Max(0, b.Padding.Right - less), Math.Max(0, b.Padding.Bottom - less));

    void Select(string day, FrameworkElement? row)
    {
        _sel = row?.Tag is TaskItem t ? (t.Id, day) : null;
        ApplySelection();
    }

    /// <summary>↑ ↓ ← → on the board; false when the arrows mean something else here (no days on screen).</summary>
    bool Arrow(Key key, bool ctrl)
    {
        var days = NavDays();
        if (days.Count == 0) return false;
        int d = _sel is { } s ? days.FindIndex(x => x.Day == s.Day) : -1;
        int i = d >= 0 ? days[d].Rows.FindIndex(r => ((TaskItem)r.Tag).Id == _sel!.Value.Id) : -1;
        if (d < 0 || i < 0)
        {
            // nothing selected yet: ← → still go through the periods, ↓ ↑ pick a task (today's first, or the first day's)
            if (key is Key.Left or Key.Right) return false;
            var start = days.FindIndex(x => x.Day == DayKey(DateTime.Today) && x.Rows.Count > 0);
            if (start < 0) start = days.FindIndex(x => x.Rows.Count > 0);
            if (start >= 0) Select(days[start].Day, days[start].Rows[0]);
            return true;
        }
        if (ctrl && key is Key.Left or Key.Right && SelectedTask() is { IsVirtual: false } moving)
        {
            // the task goes along to the day before / after (a weekend skipped when it isn't shown)
            var to = ParseKey(days[d].Day).AddDays(key == Key.Left ? -1 : 1);
            while (!Weekends && IsWeekend(to)) to = to.AddDays(key == Key.Left ? -1 : 1);
            var toKey = DayKey(to);
            _sel = (moving.Id, toKey);
            Do(() => MoveToEnd(moving, toKey));
            if (!_panels.ContainsKey(toKey)) JumpTo(to);
            return true;
        }
        switch (key)
        {
            case Key.Up: if (i > 0) Select(days[d].Day, days[d].Rows[i - 1]); break;
            case Key.Down: if (i < days[d].Rows.Count - 1) Select(days[d].Day, days[d].Rows[i + 1]); break;
            default:
                int dir = key == Key.Left ? -1 : 1;
                // the next day with tasks; past the edge of the screen: the next period, its first day
                for (int n = d + dir; n >= 0 && n < days.Count; n += dir)
                    if (days[n].Rows.Count > 0) { Select(days[n].Day, days[n].Rows[Math.Min(i, days[n].Rows.Count - 1)]); return true; }
                Step(dir);
                var next = NavDays();
                var edge = dir > 0 ? next.FirstOrDefault(x => x.Rows.Count > 0) : next.LastOrDefault(x => x.Rows.Count > 0);
                if (edge.Rows != null) Select(edge.Day, edge.Rows[0]); else _sel = null;
                break;
        }
        return true;
    }

    // ---------- the command palette (Ctrl+K) ----------

    sealed record Command(string Title, string Group, Action Run, string? Hint = null);

    Border? _palette;
    TextBox? _paletteBox;
    StackPanel? _paletteList;
    List<Command> _shown = new();
    int _paletteIndex;

    /// <summary>Dev runs (--open palette=text): a task picked with ↓, the palette open with that text in it.</summary>
    public void DevPalette(string text)
    {
        Arrow(Key.Down, false);
        Arrow(Key.Down, false);
        OpenPalette();
        bool run = text.StartsWith("run:");
        _paletteBox!.Text = run ? text[4..] : text;
        if (run && _shown.Count > 0) RunPalette(_shown[0]); // "run:na jutro" – the first match, done
    }

    public void OpenPalette()
    {
        if (_palette == null)
        {
            _paletteBox = new TextBox { FontSize = 15, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = Res("Fg"), CaretBrush = Res("Fg"), Padding = new Thickness(2, 4, 2, 8) };
            _paletteList = new StackPanel();
            var hint = new TextBlock { FontSize = 10.5, Foreground = Res("FgFaint"), Margin = new Thickness(2, 6, 0, 0), Text = L.T("↑ ↓ wybór · Enter wykonaj · Esc zamknij · wpisz datę („pt”, „12.11”, „T45”), zadanie do dodania albo czego szukać") };
            var stack = new StackPanel();
            stack.Children.Add(_paletteBox);
            stack.Children.Add(new Border { Height = 1, Background = Res("Line"), Margin = new Thickness(0, 0, 0, 6) });
            stack.Children.Add(_paletteList);
            stack.Children.Add(hint);
            _palette = new Border
            {
                Width = 600, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 60, 0, 0),
                Padding = new Thickness(14, 10, 14, 10), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
                BorderBrush = Res("Line"), Child = stack,
                Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 6, Opacity = 0.45, Color = Colors.Black },
            };
            _palette.SetResourceReference(Border.BackgroundProperty, "MenuBg");
            Grid.SetColumnSpan(_palette, 2);
            Panel.SetZIndex(_palette, 1000);
            BodyRoot.Children.Add(_palette);
            _paletteBox.TextChanged += (_, _) => FillPalette();
            _paletteBox.PreviewKeyDown += PaletteKey;
            _paletteBox.LostKeyboardFocus += (_, e) => { if (e.NewFocus is not DependencyObject f || !IsInside(f, _palette)) ClosePalette(); };
        }
        _palette.Visibility = Visibility.Visible;
        _paletteBox!.Text = "";
        FillPalette();
        Anim.Enter(_palette, 0, -8, 180);
        Activate();
        Dispatcher.BeginInvoke(() => { _paletteBox.Focus(); Keyboard.Focus(_paletteBox); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    void ClosePalette()
    {
        if (_palette == null || _palette.Visibility != Visibility.Visible) return;
        _palette.Visibility = Visibility.Collapsed;
    }

    void PaletteKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: e.Handled = true; ClosePalette(); break;
            case Key.Down: e.Handled = true; if (_shown.Count > 0) { _paletteIndex = (_paletteIndex + 1) % _shown.Count; MarkPalette(); } break;
            case Key.Up: e.Handled = true; if (_shown.Count > 0) { _paletteIndex = (_paletteIndex + _shown.Count - 1) % _shown.Count; MarkPalette(); } break;
            case Key.Enter: e.Handled = true; if (_paletteIndex < _shown.Count) RunPalette(_shown[_paletteIndex]); break;
        }
    }

    void RunPalette(Command c)
    {
        ClosePalette();
        Dispatcher.BeginInvoke(() =>
        {
            try { c.Run(); } catch (Exception ex) { Log.Error("palette " + c.Title, ex); }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    static readonly CompareInfo PaletteCmp = CultureInfo.InvariantCulture.CompareInfo;

    /// <summary>Every word typed is somewhere in the title (no case, no Polish letters needed: "zrob" finds "zrobione").</summary>
    static bool Matches(string title, string[] words) =>
        words.All(w => PaletteCmp.IndexOf(title, w, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);

    void FillPalette()
    {
        var q = _paletteBox!.Text.Trim();
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var all = PaletteCommands();
        var list = q.Length == 0 ? all : all.Where(c => Matches(c.Title + " " + c.Group, words)).ToList();
        if (q.Length > 0)
        {
            // what the text itself can be: a date to go to, a task to add, a search
            if (WeekOrDay(q) is { } day)
                list.Insert(0, new Command(L.F("Pokaż {0}", day.ToString("dddd d MMMM yyyy", Pl)), L.T("Przejdź"), () => { _sel = null; JumpTo(day); }));
            var target = _sel?.Day ?? (IsWeekView ? DayKey(_view == ViewMode.Day ? _dayDate : DateTime.Today) : DayKey(DateTime.Today));
            var add = new Command(L.F("Dodaj zadanie: {0}", q), L.T("Dodaj"), () =>
            {
                if (AddChange(q, target, out _) is { } change) Do(change);
            }, L.F("na {0} (albo dzień z tekstu: „jutro”, „pt”)", ParseKey(target).ToString("ddd d MMM", Pl)));
            var find = new Command(L.F("Szukaj: {0}", q), L.T("Szukaj"), () => ShowSearch(q));
            if (list.Count == 0) { list.Add(add); list.Add(find); }
            else { list.Add(add); list.Add(find); }
        }
        _shown = list.Take(10).ToList();
        _paletteIndex = 0;
        _paletteList!.Children.Clear();
        foreach (var c in _shown)
        {
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new TextBlock { FontSize = 13, Foreground = Res("Fg"), TextTrimming = TextTrimming.CharacterEllipsis };
            title.Inlines.Add(new System.Windows.Documents.Run(c.Title));
            if (c.Hint != null) title.Inlines.Add(new System.Windows.Documents.Run("   " + c.Hint) { Foreground = Res("FgFaint"), FontSize = 11 });
            row.Children.Add(title);
            var group = new TextBlock { Text = c.Group.ToUpper(Pl), FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = Res("FgFaint"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(group, 1);
            row.Children.Add(group);
            var item = new Border { Child = row, Padding = new Thickness(8, 5, 8, 5), CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            var cmd = c;
            item.MouseLeftButtonUp += (_, e) => { e.Handled = true; RunPalette(cmd); };
            item.MouseEnter += (_, _) => { _paletteIndex = _shown.IndexOf(cmd); MarkPalette(); };
            _paletteList.Children.Add(item);
        }
        if (_shown.Count == 0) _paletteList.Children.Add(new TextBlock { Text = L.T("Nic nie pasuje."), Foreground = Res("FgFaint"), Margin = new Thickness(8, 4, 0, 4) });
        MarkPalette();
    }

    void MarkPalette()
    {
        for (int i = 0; i < _paletteList!.Children.Count; i++)
            if (_paletteList.Children[i] is Border b) b.Background = i == _paletteIndex ? B(Th.Ink(0x24)) : Brushes.Transparent;
    }

    /// <summary>"T45" / "tydzień 45" → that week's Monday; otherwise a day as the add box reads it ("pt", "12.11", "jutro").</summary>
    static DateTime? WeekOrDay(string q)
    {
        var m = Regex.Match(q, @"^(?:t|tc|tydzie[nń]|w|wk|week)\s*(\d{1,2})$", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var w) && w is >= 1 and <= 53)
        {
            int year = DateTime.Today.Year;
            try { return ISOWeek.ToDateTime(year, Math.Min(w, ISOWeek.GetWeeksInYear(year)), DayOfWeek.Monday); } catch { return null; }
        }
        // only the whole text as a day: "zrobić raport w pt" is a task, not a date
        var p = SmartAdd.Parse(q, Store.Categories);
        if (p.Day is { } pd && p.Text.Trim().Length == 0) return pd;
        return SmartAdd.ParseDay(q) is { } d && p.Text.Trim() == q ? d : null; // a bare date the add box keeps as text ("12.11")
    }

    List<Command> PaletteCommands()
    {
        var go = L.T("Widok");
        var list = new List<Command>
        {
            new(L.T("Dziś"), go, () => Today_Click(this, new RoutedEventArgs()), "T"),
            new(L.T("Dzień"), go, () => SetView(ViewMode.Day), "1"),
            new(L.T("1 tydzień"), go, () => SetView(ViewMode.Week1), "2"),
            new(L.T("2 tygodnie"), go, () => SetView(ViewMode.Week2), "3"),
            new(L.T("Miesiąc"), go, () => SetView(ViewMode.Month), "4"),
            new(L.T("Rok"), go, () => SetView(ViewMode.Year), "5"),
            new("Timeline", go, () => SetView(ViewMode.Timeline), "6"),
            new(L.T("Poprzedni okres"), go, () => Step(-1), "←"),
            new(L.T("Następny okres"), go, () => Step(1), "→"),
        };
        var t = SelectedTask();
        if (t != null)
        {
            var sel = L.T("Zaznaczone");
            var name = Short(TaskItem.Tags(t.Text, out var rest) is { Count: > 0 } ? rest : t.Text);
            list.Insert(0, new(L.F("{0}: następny stan", name), sel, () => SetState(t, NextState(t)), L.T("spacja")));
            list.Insert(1, new(L.F("{0}: zrobione", name), sel, () => SetState(t, TaskState.Done)));
            list.Insert(2, new(L.F("{0}: na dziś", name), sel, () => { _sel = (t.Id, DayKey(DateTime.Today)); Do(() => MoveToEnd(t, DayKey(DateTime.Today))); }));
            list.Insert(3, new(L.F("{0}: na jutro", name), sel, () => { _sel = (t.Id, DayKey(DateTime.Today.AddDays(1))); Do(() => MoveToEnd(t, DayKey(DateTime.Today.AddDays(1)))); }));
            list.Insert(4, new(L.F("{0}: do backlogu", name), sel, () => { _sel = null; Do(() => ToBacklog(t)); }));
            list.Insert(5, new(L.F("{0}: edytuj", name), sel, () => EditSomewhere(t, null, null), "Enter / F2"));
            for (int p = 1; p <= 3; p++) { var pn = SmartAdd.PriorityName(p); list.Add(new(L.F("{0}: priorytet {1}", name, new string('!', p)), sel, () => SetPriority(t, pn), $"Shift+{p}")); }
        }
        var act = L.T("Akcje");
        list.Add(new(L.T("Dodaj spotkanie…"), act, () => AlarmWindow.Edit(Store, null, _sel is { } s ? ParseKey(s.Day) : DateTime.Today, meeting: true)));
        list.Add(new(L.T("Dodaj alarm…"), act, () => AlarmWindow.Edit(Store, null, _sel is { } s2 ? ParseKey(s2.Day) : DateTime.Today)));
        if (Overdue().Count > 0) list.Add(new(L.F("Przenieś niezrobione na dziś ({0})", Overdue().Count), act, () => RollOverdue()));
        list.Add(new(L.T("Szukaj wszędzie…"), act, OpenSearch, "Ctrl+F"));
        list.Add(new("Backlog", act, () => TogglePanel(archive: false), "B"));
        list.Add(new(L.T("Archiwum"), act, () => TogglePanel(archive: true), "A"));
        list.Add(new(L.T("Raport tygodniowy"), act, () => ReportWindow.Show(Store, S.Layer, ReportDay(), month: false), "R"));
        list.Add(new(L.T("Raport miesięczny"), act, () => ReportWindow.Show(Store, S.Layer, ReportDay(), month: true)));
        list.Add(new(L.T("Synchronizuj teraz"), act, () => Sync_Click(this, new RoutedEventArgs())));
        foreach (var a in S.Accounts.Where(a => a.Id != S.Layer)) { var id = a.Id; list.Add(new(L.F("Konto: {0}", App.AccountName(id)), act, () => App.Instance.SwitchLayer(id), id == "work" ? "Ctrl+1" : "Ctrl+2")); }
        if (!IsSecond) list.Add(new(S.BoardMax ? L.T("Tablica w swoim rozmiarze") : L.T("Tablica na cały ekran"), act, ToggleMax, "F11"));
        list.Add(new(L.T("Ukryj tablicę"), act, () => App.Instance.SetBoardHidden(true)));
        list.Add(new(L.T("Ustawienia"), act, () => App.Instance.ShowSettings()));
        list.Add(new(L.T("Skróty i pomoc"), act, HelpWindow.ShowHelp, "F1"));
        return list;
    }
}
