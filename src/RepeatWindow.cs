using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace TaskWall;

/// <summary>
/// Repeating series of a task or a day mark: every N days / weeks (chosen weekdays) / months / years or every
/// workday, from a day, optionally until a day ("okresowe": e.g. Urlop 3–14 sierpnia, HO every Friday until June).
/// </summary>
public sealed class RepeatWindow : DarkWindow
{
    readonly BoardStore _store;
    readonly RecurringRule? _rule;
    readonly TaskItem? _task;
    readonly ComboBox _mark = new() { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
    readonly ComboBox _pattern = new() { Width = 190 };
    readonly TextBox _interval = new() { Style = (Style)Application.Current.Resources["FieldBox"], Width = 54, Text = "1", Margin = new Thickness(10, 0, 6, 0) };
    readonly TextBlock _unit = Label("", 12.5, "FgDim");
    readonly StackPanel _intervalRow = new() { Orientation = Orientation.Horizontal };
    readonly WrapPanel _weekdays = new() { Margin = new Thickness(0, 8, 0, 0) };
    readonly TextBox _from = new() { Style = (Style)Application.Current.Resources["FieldBox"], Width = 150 };
    readonly TextBox _to = new() { Style = (Style)Application.Current.Resources["FieldBox"], Width = 150 };
    readonly TextBlock _preview = Label("", 12, "FgDim");
    readonly CheckBox _skipHolidays = new() { Content = L.T("Pomijaj niedziele i święta"), Margin = new Thickness(0, 12, 0, 0) };
    readonly Button _save;
    readonly List<(ToggleButton Button, int Day)> _dayButtons = new();

    static (string Code, string Label)[] Patterns => new[]
        { ("daily", L.T("Dni")), ("weekdays", L.T("W dni robocze")), ("weekly", L.T("Tygodnie")), ("monthly", L.T("Miesiące")), ("yearly", L.T("Lata")) };

    RepeatWindow(BoardStore store, RecurringRule? rule, TaskItem? task, DateTime day, string? mark)
    {
        _store = store;
        _rule = rule;
        _task = task;
        bool forMark = task == null;
        Title = forMark ? L.T("Powtarzane oznaczenie dnia") : L.T("Powtarzanie zadania");
        Width = 500;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;

        var p = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };
        p.Children.Add(Label((forMark ? L.T("POWTARZANE OZNACZENIE") : L.T("POWTARZANIE ZADANIA")) + "  ·  " + L.Up(App.AccountName(store.Layer)), 11.5, "FgDim", FontWeights.Bold));
        if (forMark)
        {
            p.Children.Add(Caption(L.T("OZNACZENIE")));
            foreach (var m in store.MarkTypes) _mark.Items.Add(new ComboBoxItem { Content = m.Label.Length > 0 && m.Label != m.Code ? $"{m.Code}  ·  {m.Label}" : m.Code, Tag = m.Code });
            p.Children.Add(_mark);
            var code = rule?.Text ?? mark ?? store.MarkTypes.FirstOrDefault()?.Code;
            _mark.SelectedIndex = Math.Max(0, store.MarkTypes.FindIndex(m => m.Code == code));
        }
        else
        {
            var name = Label(task!.Text, 15, "Fg", FontWeights.Bold);
            name.Margin = new Thickness(0, 2, 0, 0);
            p.Children.Add(name);
        }

        p.Children.Add(Caption(L.T("POWTARZAJ")));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (c, label) in Patterns) _pattern.Items.Add(new ComboBoxItem { Content = label, Tag = c });
        row.Children.Add(_pattern);
        _intervalRow.Children.Add(new TextBlock { Text = L.T("co"), Foreground = Ui.Res("FgDim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) });
        _intervalRow.Children.Add(_interval);
        _unit.VerticalAlignment = VerticalAlignment.Center;
        _intervalRow.Children.Add(_unit);
        row.Children.Add(_intervalRow);
        p.Children.Add(row);

        var names = BoardWindow.Pl.DateTimeFormat.AbbreviatedDayNames;
        foreach (var d in new[] { 1, 2, 3, 4, 5, 6, 0 })
        {
            var b = new ToggleButton { Content = names[d].TrimEnd('.').ToUpper(BoardWindow.Pl), Style = (Style)Application.Current.Resources["Chip"], Margin = new Thickness(0, 0, 6, 0), MinWidth = 40 };
            b.Checked += (_, _) => Update();
            b.Unchecked += (_, _) => Update();
            _dayButtons.Add((b, d));
            _weekdays.Children.Add(b);
        }
        p.Children.Add(_weekdays);

        p.Children.Add(Caption(L.T("OD – DO  (puste „do” = bez końca)")));
        var period = new StackPanel { Orientation = Orientation.Horizontal };
        period.Children.Add(_from);
        period.Children.Add(new TextBlock { Text = "–", Foreground = Ui.Res("FgDim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) });
        period.Children.Add(_to);
        period.Children.Add(new TextBlock { Text = L.T("np. 14.10 · jutro · pt"), Foreground = Ui.Res("FgFaint"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
        p.Children.Add(period);
        p.Children.Add(_skipHolidays);

        _preview.Margin = new Thickness(0, 14, 0, 0);
        p.Children.Add(_preview);

        var buttons = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        if (rule != null)
        {
            var del = Btn(forMark ? L.T("Usuń serię") : L.T("Usuń serię (wykonane zostają)"), "SecondaryButton", (_, _) =>
            {
                _store.Checkpoint();
                rule.Deleted = true;
                _store.Changed(rule);
                Done();
            });
            del.Margin = new Thickness(0);
            DockPanel.SetDock(del, Dock.Left);
            buttons.Children.Add(del);
        }
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(Btn(L.T("Anuluj"), "SecondaryButton", (_, _) => Close()));
        _save = Btn(L.T("Zapisz"), "PrimaryButton", (_, _) => Save());
        right.Children.Add(_save);
        buttons.Children.Add(right);
        p.Children.Add(buttons);
        Content = p;

        // initial values
        var start = rule?.StartDate is { } sd && sd > DateTime.MinValue ? sd : day;
        _from.Text = start.ToString("dd.MM.yyyy");
        _to.Text = rule?.End != null && DateTime.TryParse(rule.End, out var e) ? e.ToString("dd.MM.yyyy") : "";
        var pattern = rule?.Pattern ?? "weekly";
        int interval = rule?.Interval ?? 1;
        if (pattern == "biweekly") { pattern = "weekly"; interval = 2; }
        _pattern.SelectedIndex = Math.Max(0, Array.FindIndex(Patterns, x => x.Code == pattern));
        _interval.Text = Math.Max(1, interval).ToString();
        var days = rule?.Weekdays is { Count: > 0 } w ? w : new List<int> { (int)start.DayOfWeek };
        foreach (var (b, d) in _dayButtons) b.IsChecked = days.Contains(d);
        _skipHolidays.IsChecked = rule?.SkipSundaysHolidays ?? false;
        _skipHolidays.Checked += (_, _) => Update();
        _skipHolidays.Unchecked += (_, _) => Update();

        _pattern.SelectionChanged += (_, _) => Update();
        _interval.TextChanged += (_, _) => Update();
        _from.TextChanged += (_, _) => Update();
        _to.TextChanged += (_, _) => Update();
        _mark.SelectionChanged += (_, _) => Update();
        PreviewKeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Escape) Close();
            else if (ev.Key == Key.Enter && _save.IsEnabled) { ev.Handled = true; Save(); }
        };
        Loaded += (_, _) => Activate();
        Update();
    }

    static TextBlock Caption(string text)
    {
        var t = Label(text, 11, "FgDim", FontWeights.SemiBold);
        t.Margin = new Thickness(0, 12, 0, 4);
        return t;
    }

    string Pattern => (_pattern.SelectedItem as ComboBoxItem)?.Tag as string ?? "weekly";

    static DateTime? Day(string text)
    {
        var s = text.Trim();
        if (s.Length == 0) return null;
        if (DateTime.TryParseExact(s, new[] { "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd" }, null, DateTimeStyles.None, out var d)) return d;
        return SmartAdd.ParseDay(s);
    }

    /// <summary>The series as typed (not saved); null = something can't be read.</summary>
    RecurringRule? Read()
    {
        if (Day(_from.Text) is not { } from) return null;
        DateTime? to = _to.Text.Trim().Length == 0 ? null : Day(_to.Text);
        if (_to.Text.Trim().Length > 0 && (to == null || to < from)) return null;
        if (!int.TryParse(_interval.Text.Trim(), out var n) || n < 1 || n > 99) return null;
        var r = new RecurringRule { Pattern = Pattern, Interval = Pattern == "weekdays" ? 1 : n, Start = from.ToString("yyyy-MM-dd"), End = to?.ToString("yyyy-MM-dd"), SkipSundaysHolidays = _skipHolidays.IsChecked == true };
        if (Pattern == "weekly")
        {
            var days = _dayButtons.Where(x => x.Button.IsChecked == true).Select(x => x.Day).ToList();
            if (days.Count == 0) return null;
            r.Weekdays = days;
        }
        return r;
    }

    void Update()
    {
        var pattern = Pattern;
        _weekdays.Visibility = pattern == "weekly" ? Visibility.Visible : Visibility.Collapsed;
        _intervalRow.Visibility = pattern == "weekdays" ? Visibility.Collapsed : Visibility.Visible;
        int.TryParse(_interval.Text.Trim(), out var n);
        _unit.Text = L.T(pattern switch // each Polish form maps to the English singular / plural
        {
            "daily" => n == 1 ? "dzień" : "dni",
            "weekly" => n == 1 ? "tydzień" : n is >= 2 and <= 4 ? "tygodnie" : "tygodni",
            "monthly" => n == 1 ? "miesiąc" : n is >= 2 and <= 4 ? "miesiące" : "miesięcy",
            "yearly" => n == 1 ? "rok" : n is >= 2 and <= 4 ? "lata" : "lat",
            _ => "",
        });
        var r = Read();
        _save.IsEnabled = r != null && (_task != null || _mark.SelectedItem != null);
        if (r == null)
        {
            _preview.Text = L.T("Sprawdź daty (np. 14.10.2026), co ile i dni tygodnia.");
            _preview.Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xA6, 0x5A));
            return;
        }
        _preview.Foreground = Ui.Res("FgDim");
        var next = new List<DateTime>();
        for (var d = r.StartDate; next.Count < 6 && d < r.StartDate.AddYears(3); d = d.AddDays(1))
            if (r.Occurs(d) && d >= DateTime.Today.AddDays(-1)) next.Add(d);
        int total = r.End != null ? CountUntil(r) : -1;
        _preview.Text = "→ " + r.Summary
            + (next.Count > 0 ? "\n" + L.T("Najbliżej:") + " " + string.Join(", ", next.Select(d => d.ToString("ddd d MMM", BoardWindow.Pl))) + (next.Count == 6 ? " …" : "") : "\n" + L.T("Brak dni w tym okresie."))
            + (total >= 0 ? "\n" + L.F("Razem dni w okresie: {0}", total) : "");
    }

    static int CountUntil(RecurringRule r)
    {
        int n = 0;
        var end = DateTime.Parse(r.End!);
        for (var d = r.StartDate; d <= end && n < 5000; d = d.AddDays(1)) if (r.Occurs(d)) n++;
        return n;
    }

    void Save()
    {
        if (Read() is not { } r) return;
        _store.Checkpoint();
        if (_rule != null)
        {
            _rule.Pattern = r.Pattern; _rule.Interval = r.Interval; _rule.Weekdays = r.Weekdays; _rule.Start = r.Start; _rule.End = r.End; _rule.SkipSundaysHolidays = r.SkipSundaysHolidays;
            if (_task == null && _mark.SelectedItem is ComboBoxItem it) _rule.Text = (string)it.Tag;
            _rule.Deleted = false;
            _store.Changed(_rule);
        }
        else if (_task != null)
        {
            var real = _store.Materialize(_task);
            r.Text = real.Text; r.Estimate = real.Estimate; r.Order = real.Order;
            _store.AddRule(r);
            // this task becomes the series' occurrence on its own day (or stays a one-off when it's outside the series)
            if (r.Occurs(DateTime.ParseExact(real.Day!, "yyyy-MM-dd", null)))
            {
                real.RuleId = r.Id;
                real.RuleDay = real.Day;
                _store.Changed(real);
            }
        }
        else if (_mark.SelectedItem is ComboBoxItem it)
        {
            r.Text = (string)it.Tag;
            _store.AddMarkRule(r);
        }
        Done();
    }

    void Done()
    {
        App.Board?.Rebuild();
        Close();
    }

    public static void ForTask(BoardStore store, TaskItem task)
    {
        if (task.Day == null) return;
        GlassWindow.EndOverlays();
        new RepeatWindow(store, store.Rule(task.RuleId), task, DateTime.ParseExact(task.RuleDay ?? task.Day, "yyyy-MM-dd", null), null).Show();
    }

    public static void ForMark(BoardStore store, RecurringRule? rule, DateTime day, string? mark = null)
    {
        GlassWindow.EndOverlays();
        new RepeatWindow(store, rule, null, day, mark).Show();
    }
}
