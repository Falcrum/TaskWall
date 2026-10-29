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
/// <summary>Editing a task's text in place (and the add box's helpers).</summary>
public partial class BoardWindow
{
    // ---------- inline editing ----------

    (string Id, bool Series)? _editOnBuild;

    /// <summary>Edit from a menu: in place, or (from another day of a multi-day task) on its current day.</summary>
    void EditSomewhere(TaskItem t, StackPanel? content, TextBlock? text, bool series = false)
    {
        if (content != null && text != null) { BeginEdit(t, content, text, series); return; }
        if (t.Day == null) return;
        _editOnBuild = (t.Id, series);
        if (_panels.ContainsKey(t.Day)) Rebuild(); else JumpTo(ParseKey(t.Day));
    }

    void BeginEdit(TaskItem t, StackPanel content, TextBlock text, bool series)
    {
        int idx = content.Children.IndexOf(text);
        // a context menu can outlive its row (rebuilt meanwhile): never edit inside detached visuals
        if (idx < 0 || PresentationSource.FromVisual(content) == null) return;
        var rule = series ? Store.Rule(t.RuleId) : null;
        var box = new TextBox { Style = (Style)FindResource("InlineBox"), Text = rule?.Text ?? t.Text, FontSize = S.FontSize };
        content.Children.RemoveAt(idx);
        content.Children.Insert(idx, box);
        bool finished = false;
        Action cancel = () => finished = true;
        _editors.Add(cancel);
        _editorBoxes[cancel] = box;

        void Finish(bool commit)
        {
            if (finished) return;
            finished = true;
            _editors.Remove(cancel);
            var v = box.Text.Trim();
            Action? change = null;
            if (commit && rule != null && v.Length > 0 && v != rule.Text)
                change = () => { rule.Text = v; Store.Changed(rule); };
            else if (commit && rule == null && v.Length == 0)
                change = () => { if (t.IsVirtual && Store.Rule(t.RuleId) is { } r) { if (!r.Skips.Contains(t.RuleDay!)) r.Skips.Add(t.RuleDay!); Store.Changed(r); } else Store.Archive(Store.Materialize(t)); };
            else if (commit && rule == null && v != t.Text)
                change = () =>
                {
                    // the same commands as for a new task ("14-16", "!!", "2h", "#art"), but a date word stays text
                    var p = SmartAdd.Parse(v, Store.Categories, dates: false);
                    var real = Store.Materialize(t);
                    real.Text = p.Text.Length > 0 ? p.Text : v;
                    if (p.Time is { } tm) { real.Time = tm.ToString(@"hh\:mm"); real.End = p.End?.ToString(@"hh\:mm"); }
                    if (p.Priority != null) real.Priority = p.Priority;
                    if (p.Estimate != null) { var oldEst = real.Estimate; real.Estimate = p.Estimate; ApplyAutoPlan(real, real.Day, oldEst); }
                    if (p.Project != null && real.NotionId == null) real.NotionSource = p.Project; // "@FoA" (a Notion task keeps its database)
                    Store.Changed(real);
                };
            Commit(change, null);
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Finish(true); }
            else if (e.Key == Key.Escape) { e.Handled = true; Finish(false); }
        };
        box.LostKeyboardFocus += (_, _) => Finish(true);
        FocusBox(box);
    }

    FrameworkElement BuildAddRow(string key, DateTime? date, bool compact = false)
    {
        var host = new Border { Margin = new Thickness(0, compact ? 1 : 3, 0, 0), Background = Brushes.Transparent };
        if (_addingKey == key)
        {
            host.Child = BuildAddEditor(key);
            return host;
        }
        var label = new TextBlock
        {
            Text = key == BacklogKey ? L.T("+  dodaj do backlogu") : compact ? "+" : L.T("+  dodaj zadanie"),
            Foreground = Res("FgFaint"),
            FontSize = Math.Max(10, S.FontSize - 1),
            Cursor = Cursors.IBeam,
            Padding = new Thickness(1, compact ? 0 : 2, 0, 2),
            Opacity = date.HasValue && date < DateTime.Today ? 0.6 : 1,
            ToolTip = compact ? L.T("Dodaj zadanie") : null,
        };
        host.MouseEnter += (_, _) => label.Foreground = Res("FgDim");
        host.MouseLeave += (_, _) => label.Foreground = Res("FgFaint");
        host.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (host.Child is not TextBlock) return; // already editing (e.g. a category chip was clicked)
            _addingKey = key;
            host.Child = BuildAddEditor(key);
        };
        host.Child = label;
        return host;
    }

    FrameworkElement BuildAddEditor(string key)
    {
        var host = new StackPanel();
        var grid = new Grid();
        host.Children.Add(grid);
        var box = new TextBox { Style = (Style)FindResource("FieldBox"), FontSize = S.FontSize, Padding = new Thickness(6, 3, 6, 3), Text = _addPrefix ?? "" };
        _addPrefix = null;
        var hint = new TextBlock
        {
            Text = L.T("nowe zadanie…  ([Meeting], [ART]…)"), Foreground = Res("FgFaint"), FontSize = S.FontSize,
            Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
            Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        // smart add preview: "jutro … #art 2h" → where / how long / which category
        var preview = new TextBlock { FontSize = 10.5, Foreground = Res("AccentBrush"), Margin = new Thickness(2, 3, 0, 0), Visibility = Visibility.Collapsed };
        List<Insights.Ratio>? ratios = null; // measured time against the estimate per category, worked out on the first key
        box.TextChanged += (_, _) =>
        {
            hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var p = SmartAdd.Parse(box.Text, Store.Categories);
            var desc = SmartAdd.Describe(p);
            // "2h #shader" and shader tasks usually take 1.4× their estimate: say so before it is added
            if (p.Estimate is > 0 and var est && TaskItem.Tags(p.Text, out _) is { Count: > 0 } newTags)
            {
                ratios ??= Insights.EstimateRatios(Store.Data.Tasks);
                if (Insights.RatioFor(newTags, ratios) is { } ratio && Math.Abs(ratio.Factor - 1) >= 0.1)
                    desc += (desc.Length > 0 ? "   ·   " : "") + L.F("{0} trwa zwykle ×{1} → ~{2}", ratio.Tag, ratio.Factor.ToString("0.0#", Pl), Hours(Math.Round(est * ratio.Factor * 4) / 4));
            }
            if (TaskItem.Tags(p.Text, out var rest).Contains(TaskItem.MeetingTag))
                desc = AlarmService.ParseMeeting(rest, p.Estimate, out var f, out var t, out _)
                    ? L.F("→ spotkanie {0:hh\\:mm}–{1:hh\\:mm}", f, t) + (p.Day is { } d ? $", {d.ToString("ddd d MMM", Pl)}" : "")
                    : L.T("→ dopisz godziny, np. 14-15:30 (albo kliknij Meeting)");
            preview.Text = desc;
            preview.Visibility = desc.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        hint.Text = key == BacklogKey ? L.T("nowe zadanie…  (jutro, pt, 12.10, 2h, #art)") : L.T("nowe zadanie…  (2h, 14-16, !!, #art · F1 = komendy)");
        grid.Children.Add(box);
        grid.Children.Add(hint);
        host.Children.Add(preview);

        // category chips: click toggles "[Name] " in front of the text (not focusable, so the box keeps focus)
        var chips = new WrapPanel { Margin = new Thickness(0, 4, 0, 2) };
        foreach (var c in Store.Categories)
        {
            Color col;
            try { col = (Color)ColorConverter.ConvertFromString(c.Color); } catch { col = Colors.Gray; }
            var chip = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(0, 0, 4, 3),
                Background = B(A(col, 0x24)), Cursor = Cursors.Hand,
                Child = new TextBlock { Text = c.Name, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Tx(col) },
            };
            chip.PreviewMouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true; // the chip isn't focusable: the text box keeps focus and the user keeps typing
                if (string.Equals(c.Name, TaskItem.MeetingTag, StringComparison.OrdinalIgnoreCase))
                {
                    // a meeting is not a task: open the meeting window with what was typed so far
                    TaskItem.Tags(SmartAdd.Parse(box.Text, Store.Categories).Text, out var title);
                    var day = key == BacklogKey ? DateTime.Today : DateTime.ParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    box.Text = "";
                    // the window takes the focus: the empty editor then closes by itself
                    Dispatcher.BeginInvoke(() => AlarmWindow.Edit(Store, null, day, meeting: true, title: title));
                    return;
                }
                box.Text = ToggleCategory(box.Text, c.Name);
                box.CaretIndex = box.Text.Length;
                Keyboard.Focus(box);
            };
            chip.MouseLeftButtonUp += (_, e) => e.Handled = true;
            chips.Children.Add(chip);
        }
        if (chips.Children.Count > 0) host.Children.Add(chips);
        Anim.Enter(host, 0, -4, 180);
        bool finished = false;
        Action cancel = () => finished = true;
        _editors.Add(cancel);
        _editorBoxes[cancel] = box;

        void Finish(bool keepOpen)
        {
            if (finished) return;
            finished = true;
            _editors.Remove(cancel);
            var change = AddChange(box.Text, key, out bool empty);
            if (_addingKey == key) _addingKey = keepOpen && !empty ? key : null; // another "+" may have taken over meanwhile
            Commit(change, host);
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Finish(keepOpen: true); }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                box.Text = "";
                Finish(keepOpen: false);
                if (IsPeeking) Peek(false);
            }
        };
        box.LostKeyboardFocus += (_, _) => Finish(keepOpen: false);
        FocusBox(box);
        return host;
    }

    /// <summary>
    /// What typing <paramref name="text"/> into a day's (or the backlog's) add box does – a new task or a meeting, parsed
    /// like there ("jutro 2h #art", "[Meeting] 14-15 Sprint"); null when there is nothing to add.
    /// </summary>
    Action? AddChange(string text, string key, out bool empty)
    {
        var parsed = SmartAdd.Parse(text, Store.Categories);
        var v = parsed.Text;
        empty = v.Length == 0 || TaskItem.Tags(v, out var rest) is { Count: > 0 } && rest.Length == 0;
        Action? change = null;
        // "[Meeting] 14-15:30 Sprint" / "#meeting o 10 daily 30 min" → a meeting (with its hours), not a task
        var tags = TaskItem.Tags(v, out var withoutTags);
        if (!empty && tags.Contains(TaskItem.MeetingTag) && AlarmService.ParseMeeting(withoutTags, parsed.Estimate, out var from, out var to, out var title))
        {
            var day = parsed.Day ?? (key == BacklogKey ? DateTime.Today : DateTime.ParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture));
            var meeting = new Alarm { Kind = "meeting", Text = title, Day = DayKey(day), Time = from.ToString(@"hh\:mm"), End = to.ToString(@"hh\:mm"), Remind = 5 };
            change = () => { Store.AddAlarm(meeting); Dispatcher.BeginInvoke(AlarmService.Reschedule); };
            return change; // a meeting, not a task
        }
        if (!empty)
        {
            string? day = parsed.Day is { } pd ? DayKey(pd) : key == BacklogKey ? null : key;
            var siblings = Store.Data.Tasks.Where(x => x.Day == day && !x.Archived);
            var t = new TaskItem { Text = v, Day = day, Estimate = parsed.Estimate, Time = parsed.Time?.ToString(@"hh\:mm"), End = parsed.End?.ToString(@"hh\:mm"), Priority = parsed.Priority, NotionSource = parsed.Project, PlanEnd = parsed.Days is { } nd && day != null ? PlanEndAfter(day, nd) : AutoPlanEnd(day, parsed.Estimate), Order = siblings.Any() ? siblings.Max(x => x.Order) + 1 : 0 };
            _justAdded = t.Id;
            change = () => Store.Add(t);
            if (parsed.Day != null && day != key) // went to another day: say where, and show that week if it's off-screen
            {
                SyncText.Text = L.F("dodano: {0}", parsed.Day.Value.ToString("ddd d MMM", Pl));
                _syncFlash.Stop();
                _syncFlash.Start();
            }
        }
        return change;
    }

    /// <summary>
    /// Applies an editor's result. When another editor is still open (the user clicked into a second one),
    /// the data is changed without a rebuild, so the new editor isn't destroyed; it rebuilds when it closes.
    /// </summary>
    void Commit(Action? change, UIElement? editorUi)
    {
        if (_openEditors > 0)
        {
            if (change != null) { Store.Checkpoint(); change(); }
            if (editorUi != null) editorUi.Visibility = Visibility.Collapsed;
            return;
        }
        if (change != null) Do(change);
        else Rebuild();
    }

    void FocusBox(TextBox box)
    {
        void Go()
        {
            Activate();
            box.Focus();
            Keyboard.Focus(box);
            box.CaretIndex = box.Text.Length;
            (box.Parent as FrameworkElement ?? box).BringIntoView(); // a full day scrolls down to the new box
        }
        if (box.IsLoaded) Go();
        else box.Loaded += (_, _) => Go();
    }

}
