using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TaskWall;
/// <summary>A task row: its state bar, text, chips, checklist and menu, and what the row's buttons do.</summary>
public partial class BoardWindow
{
    // ---------- task row ----------

    FrameworkElement BuildTaskRow(TaskItem t, bool inBacklog, bool compact = false, (int Index, int Total)? span = null, int depth = 0, bool inLane = false)
    {
        var wrapper = new Border
        {
            Tag = t,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4, compact ? 1 : 3, 1, compact ? 1 : 3),
            Margin = new Thickness(-4 + depth * (compact ? 12 : 18), 0, 0, compact ? 0 : 1), // Notion sub-items indented under their main task
        };
        Anim.HoverBackground(wrapper, Colors.Transparent, Th.Ink(0x16));
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // the open checklist: full width under the row
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        wrapper.Child = grid;
        double fs = compact ? Math.Max(9.5, S.FontSize - 1.5) : S.FontSize;

        // the state bar on the left (empty / half / full; priority colour, green when done) – click: the next state
        var circle = TaskMarks.Status(t, TaskMarks.BarWidth, () =>
            // three states: not started → in progress → done → not started (Shift: straight to done)
            SetState(t, NextState(t, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))), gap: compact ? 5 : 7);
        grid.Children.Add(circle);
        if (_justToggled == t.Id) Anim.FillUp(circle); // the new state fills in
        // the project: a thin bar in its colour on the right edge
        if (TaskMarks.Right(t) is { } proj) { Grid.SetColumn(proj, 4); Grid.SetRowSpan(proj, 2); proj.Margin = new Thickness(3, 1, 0, 1); grid.Children.Add(proj); }

        // text with tag chips (+ notion meta in backlog)
        var content = new StackPanel();
        Grid.SetColumn(content, 1);
        // in a multi-day lane: one line (the lanes stay compact and aligned); the full text is in the tool tip
        bool oneLine = span != null || inLane; // a lane, or a task moved up into a free lane
        var text = new TextBlock { TextWrapping = oneLine ? TextWrapping.NoWrap : TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Res("Fg"), FontSize = fs };
        if (oneLine) text.ToolTip = t.Text;
        var tags = TaskItem.Tags(t.Text, out var rest);
        if (t.Time != null) // planned hours ("14-16" typed with the task) – part of what the task says, so in front
            text.Inlines.Add(new Run((t.End != null ? $"{t.Time}–{t.End}" : t.Time) + "  ") { FontWeight = FontWeights.SemiBold, Foreground = Res("FgDim") });
        // only the status circle before the name; the day of a multi-day task, the project and the categories go to a
        // small second line under it, so every name starts at the same place
        var meta = new WrapPanel { Margin = new Thickness(0, 1, 0, 0) };
        double mfs = Math.Max(8, fs - 3.5);
        if (oneLine) { meta.Height = MetaHeight(fs); meta.ClipToBounds = true; } // a multi-day lane: the same height as its other days' rows
        foreach (var tag in tags) meta.Children.Add(TagChip(tag, mfs));
        if (t.IsTracking || t.TrackedMinutes > 0) // the work timer: "⏱ 1h 05m", live while it runs
        {
            var tl = new TextBlock { FontSize = mfs + 0.5, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                Foreground = t.IsTracking ? Res("AccentBrush") : Res("FgFaint"), FontWeight = t.IsTracking ? FontWeights.Bold : FontWeights.Normal,
                ToolTip = t.IsTracking ? L.T("Licznik czasu pracy działa – zatrzymasz go w menu zadania") : L.T("Zmierzony czas pracy") };
            void Show() => tl.Text = (t.IsTracking ? "⏵ " : "⏱ ") + Duration(t.TrackedNow);
            Show();
            if (t.IsTracking) _trackLabels.Add((tl, Show));
            meta.Children.Add(tl);
        }
        if (span is { } sp) // a multi-day task on its current day: "↷ 3/3"
            meta.Children.Add(new TextBlock
            {
                Text = $"↷ {sp.Index}/{sp.Total}", FontSize = mfs + 0.5, FontWeight = FontWeights.SemiBold, Foreground = Res("AccentBrush"),
                Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = L.F("Zadanie wielodniowe: dzień {0} z {1}", sp.Index, sp.Total),
            });
        if (!inBacklog && Insights.DaysInState(t) is var stuck && stuck >= Insights.StuckDays) // e.g. "⚠ 4 dni w review"
            meta.Children.Add(new TextBlock
            {
                Text = "⚠ " + L.F("{0} dni · {1}", stuck, TaskMarks.StateName(t)), FontSize = mfs + 0.5, FontWeight = FontWeights.SemiBold,
                Foreground = Tx(TaskMarks.StateColor(t)), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                ToolTip = L.F("Od {0} dni roboczych w tym samym stanie – utknęło?", stuck),
            });
        if (t.IsHold) // waiting on someone: "‖ hold · 3 dni" (on purpose – no warning)
            meta.Children.Add(new TextBlock
            {
                Text = "‖ " + L.F("hold · {0} dni", Insights.DaysOnHold(t)), FontSize = mfs + 0.5, FontWeight = FontWeights.SemiBold,
                Foreground = Tx(TaskMarks.HoldRed), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                ToolTip = L.F("Wstrzymane – czeka na innych. Po wznowieniu (klik na pasku, H) wraca do: {0}", TaskMarks.StateName(t, t.WorkState)),
            });
        if (t.Done) meta.Opacity = 0.55;
        var mainRun = new Run(tags.Count > 0 ? rest : t.Text);
        text.Inlines.Add(mainRun);
        if (t.RuleId != null)
            text.Inlines.Add(new Run("  ") { FontFamily = (FontFamily)FindResource("IconFont"), FontSize = Math.Max(8, fs - 4), Foreground = Res("FgFaint") });
        if (t.HasLink)
        {
            text.Inlines.Add(new Run("  ") { FontFamily = (FontFamily)FindResource("IconFont"), FontSize = Math.Max(8, fs - 4), Foreground = Res("AccentBrush") });
            text.Cursor = Cursors.Hand;
            text.ToolTip = L.T("Kliknij, aby otworzyć w Notion") +(t.Status != null ? $"\nStatus: {t.Status}" : "");
        }
        if (t.RuleId != null && Store.Rule(t.RuleId) is { } rule)
            text.ToolTip = (text.ToolTip is string s0 ? s0 + "\n" : "") + L.T("Seria: ") + rule.Summary;
        var side = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(side, 2);
        if (t.Checklist is { Count: > 0 } cl)
        {
            int cd = cl.Count(x => x.Done);
            var chip = new Border
            {
                CornerRadius = new CornerRadius(4), Padding = new Thickness(4, 0, 4, 1), Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand,
                Background = B(Th.Ink(0x1E)), ToolTip = _expanded.Contains(t.Id) ? L.T("Zwiń podzadania") : L.T("Pokaż podzadania"),
                Child = new TextBlock { Text = $"☑ {cd}/{cl.Count}", FontSize = Math.Max(8.5, fs - 2.5), Foreground = cd == cl.Count ? Res("AccentBrush") : Res("FgDim") },
            };
            // its own column next to the estimate: never cut off by a one-line (multi-day) row, never over the estimate
            chip.Margin = new Thickness(6, Math.Max(0, (fs * 1.33 - (fs + 1)) / 2), 0, 0);
            chip.VerticalAlignment = VerticalAlignment.Top;
            chip.PreviewMouseLeftButtonDown += (_, e) => e.Handled = true;
            chip.PreviewMouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (!_expanded.Remove(t.Id)) { _expanded.Add(t.Id); _justExpanded = t.Id; }
                Rebuild();
            };
            side.Children.Add(chip);
        }
        if (t.Done)
        {
            mainRun.TextDecorations = TextDecorations.Strikethrough; // only the words – not the category chips
            text.Opacity = 0.45;
        }
        content.Children.Add(text);
        if (meta.Children.Count > 0) content.Children.Add(meta);
        if (_editOnBuild is { } pe && pe.Id == t.Id && !t.IsVirtual)
        {
            _editOnBuild = null;
            Dispatcher.BeginInvoke(() => BeginEdit(t, content, text, pe.Series), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        // an empty list shows only while its first point is being typed ("Dodaj podzadania"), then it goes away again
        if (!inLane && _expanded.Contains(t.Id) && (t.Checklist is { Count: > 0 } || _checkAddFor == t.Id))
        {
            // (a multi-day task in its lane: the subtasks go under the lanes of that day only, see BuildDay)
            // its own row across the text, counters and estimate columns: the points use the whole width of the day
            var checklist = BuildChecklist(t, fs);
            if (_justExpanded == t.Id) { _justExpanded = null; Anim.Enter(checklist, 0, -6, 240); } // the subtasks unfold
            Grid.SetRow(checklist, 1);
            Grid.SetColumn(checklist, 1);
            Grid.SetColumnSpan(checklist, 3);
            grid.Children.Add(checklist);
        }

        if (inBacklog && (!string.IsNullOrEmpty(t.Priority) || !string.IsNullOrEmpty(t.Status)))
        {
            // the backlog: priority and Notion status on the same second line
            if (!string.IsNullOrEmpty(t.Priority)) meta.Children.Add(PriorityPill(t.Priority));
            if (!string.IsNullOrEmpty(t.Status))
                meta.Children.Add(new TextBlock { Text = t.Status, FontSize = 10.5, Foreground = Res("FgFaint"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) });
            if (!content.Children.Contains(meta)) content.Children.Add(meta);
        }
        grid.Children.Add(content);

        if (t.Estimate is > 0)
        {
            var est = new TextBlock
            {
                Text = Est(t.Estimate.Value),
                FontSize = Math.Max(9, fs - 2.5),
                Foreground = Res("FgFaint"),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(6, 1, 0, 0),
                Opacity = t.Done ? 0.5 : 1,
            };
            side.Children.Add(est);
        }
        grid.Children.Add(side);

        // archive (on hover)
        var del = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = "",
            FontSize = 8,
            Padding = new Thickness(4, 3, 4, 3),
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Hidden,
            ToolTip = t.IsVirtual ? L.T("Pomiń to wystąpienie serii") : L.T("Archiwizuj (albo kliknij kółkiem myszy)"),
        };
        Grid.SetColumn(del, 3);
        del.Click += (_, _) => ArchiveRow(t, wrapper);
        grid.Children.Add(del);

        wrapper.MouseEnter += (_, _) => del.Visibility = Visibility.Visible;
        wrapper.MouseEnter += (_, _) => _hover = (t, content, text, wrapper);
        wrapper.MouseLeave += (_, _) => del.Visibility = Visibility.Hidden;

        // click / drag / middle-click / context menu
        wrapper.MouseLeftButtonDown += (_, e) => { _pressed = t; _pressPoint = e.GetPosition(this); };
        wrapper.MouseMove += (_, e) =>
        {
            if (_pressed != t || e.LeftButton != MouseButtonState.Pressed) return;
            var d = e.GetPosition(this) - _pressPoint;
            if (Math.Abs(d.X) + Math.Abs(d.Y) < 5) return;
            _pressed = null;
            StartDrag(t, wrapper);
        };
        wrapper.MouseLeftButtonUp += (_, e) =>
        {
            if (_pressed != t) return;
            _pressed = null;
            e.Handled = true;
            if (t.HasLink) OpenLink(t);
            else BeginEdit(t, content, text, series: false);
        };
        wrapper.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) { e.Handled = true; ArchiveRow(t, wrapper); }
        };
        wrapper.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var menu = BuildMenu(t, content, text, wrapper);
            menu.PlacementTarget = wrapper;
            menu.IsOpen = true;
        };
        if (DevMenu && t.Day == DayKey(DateTime.Today) && !t.Done && wrapper.IsLoaded == false)
            DevOpenMenu = () => { var m = BuildMenu(t, content, text, wrapper); m.PlacementTarget = wrapper; m.IsOpen = true; };
        if (_justAdded == t.Id) Anim.Enter(wrapper, 0, -6, 240);
        if (_flashId == t.Id) Flash(wrapper);
        return wrapper;
    }

    public Action? DevOpenMenu;
    static readonly bool DevMenu = Dev.Arg("--open") == "menu"; // read once, not per task row
    string? _justExpanded;
    public void DevExpandAll() { foreach (var t in Store.Data.Tasks.Where(t => t.Checklist is { Count: > 0 })) _expanded.Add(t.Id); Rebuild(); }
    /// <summary>Task row under the mouse (keyboard shortcuts act on it).</summary>
    (TaskItem Task, StackPanel Content, TextBlock Text, FrameworkElement Row)? _hover;
    /// <summary>Height of the small second line of a task row (fixed in multi-day lanes, so they line up across days).</summary>
    static double MetaHeight(double fs) => Math.Ceiling(Math.Max(8, fs - 3.5) * 1.45 + 4);

    /// <summary>Labels of running timers, refreshed every 20 s (cleared on each rebuild).</summary>
    readonly List<(FrameworkElement El, Action Show)> _trackLabels = new();
    System.Windows.Threading.DispatcherTimer? _trackTimer;

    /// <summary>"45 min", "1h 05m".</summary>
    public static string Duration(double minutes)
    {
        var m = (int)Math.Round(minutes);
        return m < 60 ? $"{m} min" : $"{m / 60}h {m % 60:00}m";
    }

    readonly HashSet<string> _expanded = new();  // tasks whose checklist is open
    string? _checkAddFor;                         // task whose "+ punkt" box is open

    /// <summary>Sub-task list under a task: tick, add, remove. Every change is a normal (undoable, synced) task edit.</summary>
    FrameworkElement BuildChecklist(TaskItem t, double fs)
    {
        var list = new StackPanel { Margin = new Thickness(0, 3, 0, 1) };
        var items = t.Checklist ?? new List<CheckItem>();
        void Edit(Action<List<CheckItem>> change) => Do(() =>
        {
            var real = Store.Materialize(t);
            real.Checklist ??= new List<CheckItem>();
            change(real.Checklist);
            if (real.Checklist.Count == 0) real.Checklist = null;
            Store.Changed(real);
            _expanded.Add(real.Id);
        });
        for (int i = 0; i < items.Count; i++)
        {
            int index = i;
            var item = items[i];
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var box = new Border
            {
                Width = 11, Height = 11, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1.2), Cursor = Cursors.Hand,
                BorderBrush = item.Done ? Res("AccentBrush") : B(Th.Ink(0x88)),
                Background = item.Done ? Res("AccentBrush") : Brushes.Transparent, Margin = new Thickness(1, 2, 7, 0), VerticalAlignment = VerticalAlignment.Top,
            };
            var label = new TextBlock { Text = item.Text, TextWrapping = TextWrapping.Wrap, FontSize = Math.Max(9.5, fs - 1.5), Foreground = Res("FgDim"), Cursor = Cursors.Hand };
            if (item.Done) { label.TextDecorations = TextDecorations.Strikethrough; label.Opacity = 0.6; }
            Grid.SetColumn(label, 1);
            var del = new Button { Style = (Style)FindResource("IconButton"), Content = "", FontSize = 7, Padding = new Thickness(3, 2, 3, 2), Visibility = Visibility.Hidden, ToolTip = L.T("Usuń podzadanie") };
            Grid.SetColumn(del, 2);
            del.Click += (_, _) => Edit(l => l.RemoveAt(index));
            MouseButtonEventHandler toggle = (_, e) => { e.Handled = true; Edit(l => l[index].Done = !l[index].Done); };
            box.MouseLeftButtonDown += toggle;
            label.MouseLeftButtonDown += toggle;
            row.MouseEnter += (_, _) => del.Visibility = Visibility.Visible;
            row.MouseLeave += (_, _) => del.Visibility = Visibility.Hidden;
            row.Children.Add(box);
            row.Children.Add(label);
            row.Children.Add(del);
            list.Children.Add(row);
        }

        // "+ punkt"
        var addHost = new Border { Background = Brushes.Transparent, Margin = new Thickness(0, 1, 0, 0) };
        void OpenAdd()
        {
            var input = new TextBox { Style = (Style)FindResource("InlineBox"), FontSize = Math.Max(9.5, fs - 1.5) };
            addHost.Child = input;
            bool finished = false;
            Action cancel = () => finished = true;
            _editors.Add(cancel);
            _editorBoxes[cancel] = input;
            void Finish(bool keepOpen)
            {
                if (finished) return;
                finished = true;
                _editors.Remove(cancel);
                var v = input.Text.Trim();
                _checkAddFor = keepOpen && v.Length > 0 ? t.Id : null;
                // changed my mind: nothing typed into a list that has no points → no list (and no "+ punkt" left behind)
                if (v.Length == 0 && t.Checklist is not { Count: > 0 }) { _expanded.Remove(t.Id); Dispatcher.BeginInvoke(Rebuild); }
                Commit(v.Length > 0 ? () =>
                {
                    var real = Store.Materialize(t);
                    (real.Checklist ??= new List<CheckItem>()).Add(new CheckItem { Text = v });
                    Store.Changed(real);
                    _expanded.Add(real.Id);
                    if (_checkAddFor != null) _checkAddFor = real.Id;
                } : null, addHost);
            }
            input.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; Finish(true); }
                else if (e.Key == Key.Escape) { e.Handled = true; input.Text = ""; Finish(false); }
            };
            input.LostKeyboardFocus += (_, _) => Finish(false);
            FocusBox(input);
        }
        var addLabel = new TextBlock { Text = L.T("+  podzadanie"), FontSize = Math.Max(9.5, fs - 2), Foreground = Res("FgFaint"), Cursor = Cursors.IBeam, Margin = new Thickness(1, 0, 0, 0) };
        addHost.Child = addLabel;
        addHost.MouseLeftButtonDown += (_, e) => e.Handled = true; // not a drag / edit of the task
        addHost.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (addHost.Child is TextBlock) OpenAdd(); };
        list.Children.Add(addHost);
        if (_checkAddFor == t.Id) addHost.Loaded += (_, _) => OpenAdd();
        return list;
    }

    /// <summary>Search result highlight: pulse the row and scroll it into view.</summary>
    static void Flash(Border row)
    {
        row.Loaded += (_, _) =>
        {
            row.BringIntoView();
            var brush = new SolidColorBrush(Colors.Transparent); // its own (it is animated)
            row.Background = brush;
            var accent = ((SolidColorBrush)Res("AccentBrush")).Color;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(A(accent, 0x70), Colors.Transparent, TimeSpan.FromMilliseconds(1800)) { BeginTime = TimeSpan.FromMilliseconds(250) });
        };
    }

    static readonly Regex LeadingSymbols = new(@"^[^\p{L}\p{N}]+");

    public static string CleanLabel(string s) => LeadingSymbols.Replace(s.Trim(), "").Trim();

    /// <summary>3 high, 2 medium, 1 low, 0 unknown – from the name in any language Notion or the board uses.</summary>
    public static int PriorityLevel(string? priority)
    {
        var p = (priority ?? "").ToLowerInvariant();
        return p.Contains("high") || p.Contains("wysok") || p.Contains("urgent") || p.Contains("pilne") || p.Contains("krytycz") || p.Contains("critical") ? 3
            : p.Contains("medium") || p.Contains("średn") || p.Contains("normal") ? 2
            : p.Contains("low") || p.Contains("nisk") ? 1 : 0;
    }

    /// <summary>One scale everywhere (circles, pills, timeline stripes): high red, medium amber, low a quiet slate.</summary>
    public static Color PriorityColor(string priority) => PriorityLevel(priority) switch
    {
        3 => Color.FromRgb(0xE0, 0x5A, 0x55),
        2 => Color.FromRgb(0xE8, 0xA2, 0x3A),
        1 => Color.FromRgb(0x7E, 0x9A, 0xAE),
        _ => Color.FromRgb(0x8A, 0x90, 0x9E),
    };

    public static int PriorityRank(string? p) => string.IsNullOrEmpty(p) ? 9 : 3 - PriorityLevel(p);

    FrameworkElement PriorityPill(string priority) => new Border
    {
        Background = B(PriorityColor(priority)),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(5, 0, 5, 1),
        Margin = new Thickness(0, 0, 7, 0),
        Child = new TextBlock { Text = priority, FontSize = 10.5, Foreground = Brushes.White },
    };

    void OpenLink(TaskItem t)
    {
        if (string.IsNullOrEmpty(t.Url)) return;
        var url = t.Url;
        if (S.OpenNotionInApp && url.StartsWith("https://www.notion.so/", StringComparison.OrdinalIgnoreCase))
            url = "notion://www.notion.so/" + url["https://www.notion.so/".Length..];
        OpenUrl(url);
    }

    /// <summary>✕ / middle click: archive (a virtual series occurrence is skipped instead).</summary>
    void ArchiveRow(TaskItem t, FrameworkElement row) => Anim.Leave(row, () => Do(() =>
    {
        if (t.IsVirtual && Store.Rule(t.RuleId) is { } r)
        {
            if (!r.Skips.Contains(t.RuleDay!)) r.Skips.Add(t.RuleDay!);
            Store.Changed(r);
        }
        else if (Store.Find(t.Id) is { } live) Store.Archive(live); // the live instance (row may be older than a merge)
    }, after: () => ShowUndo(t.IsVirtual ? L.F("Pominięto w serii: {0}", Short(t.Text)) : L.F("Zarchiwizowano: {0}", Short(t.Text)))));

    void DeleteForGood(TaskItem t, FrameworkElement row) => Anim.Leave(row, () => Do(() =>
    {
        if (t.IsVirtual && Store.Rule(t.RuleId) is { } r) { if (!r.Skips.Contains(t.RuleDay!)) r.Skips.Add(t.RuleDay!); Store.Changed(r); }
        else if (Store.Find(t.Id) is { } live) Store.Discard(live); // to the bin (30 days)
    }, after: () => ShowUndo(L.F("Usunięto do kosza: {0}", Short(t.Text)))));

}
