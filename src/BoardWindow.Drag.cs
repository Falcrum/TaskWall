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
/// <summary>Dragging tasks: between days, onto the backlog and the archive, onto the next period at the screen's edge, and files / links dropped from outside.</summary>
public partial class BoardWindow
{
    // ---------- drag & drop ----------

    void StartDrag(TaskItem t, FrameworkElement source)
    {
        bool materialized = false;
        if (t.IsVirtual)
        {
            Store.Checkpoint();
            t = Store.Materialize(t);
            materialized = true;
        }
        if (_drawerOpen && !S.DrawerLocked && IsInside(source, BacklogPanel)) SetDrawer(false); // make room for dropping on the days (unless pinned)
        source.Opacity = 0.35;
        try
        {
            var result = DragDrop.DoDragDrop(source, new DataObject(DragFormat, t.Id), DragDropEffects.Move | DragDropEffects.Copy);
            if (result == DragDropEffects.None && materialized && Store.Undo()) Rebuild(); // cancelled: back to a virtual occurrence
        }
        finally
        {
            source.Opacity = 1;
            HideDropLine();
        }
    }

    // ---------- dragging onto the next / previous period: hold the task at the left / right edge ----------
    readonly DispatcherTimer _edgeTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    int _edgeDir;

    void EdgeScroll(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat) || _view is ViewMode.Year or ViewMode.Timeline or ViewMode.Report || WeeksGrid.ActualWidth <= 0) { StopEdge(); return; }
        var p = e.GetPosition(WeeksGrid);
        double edge = Math.Min(36, WeeksGrid.ActualWidth / 20);
        int dir = p.Y >= 0 && p.Y <= WeeksGrid.ActualHeight ? (p.X < edge ? -1 : p.X > WeeksGrid.ActualWidth - edge ? 1 : 0) : 0;
        if (dir == _edgeDir) return;
        _edgeDir = dir;
        _edgeTimer.Stop();
        if (dir == 0) { SyncText.Text = ""; return; }
        SyncText.Text = dir < 0 ? L.T("‹ przytrzymaj: poprzedni okres") : L.T("przytrzymaj: następny okres ›");
        _edgeTimer.Start();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct CursorPoint { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetCursorPos(out CursorPoint p);

    /// <summary>The mouse is still over the board (the drag didn't leave the window).</summary>
    bool CursorOverBoard()
    {
        if (!GetCursorPos(out var c) || PresentationSource.FromVisual(WeeksGrid) == null) return false;
        var p = WeeksGrid.PointFromScreen(new Point(c.X, c.Y));
        return p.X >= -4 && p.Y >= -4 && p.X <= WeeksGrid.ActualWidth + 4 && p.Y <= WeeksGrid.ActualHeight + 4;
    }

    void EdgeTick(object? s, EventArgs e)
    {
        // the drag left the window (no more DragOver events) or ended: stop
        if (_edgeDir == 0 || !CursorOverBoard()) { StopEdge(); return; }
        Step(_edgeDir); // keeps stepping while the task is held there
        _edgeTimer.Interval = TimeSpan.FromMilliseconds(900);
    }

    void StopEdge()
    {
        if (_edgeDir == 0) return;
        _edgeDir = 0;
        _edgeTimer.Stop();
        _edgeTimer.Interval = TimeSpan.FromMilliseconds(650);
        SyncText.Text = "";
    }

    void Root_DragOver(object sender, DragEventArgs e)
    {
        // Reached only when no day / backlog handled the event: not a valid target – except a Notion export file
        e.Effects = ExternalDrop.ImportFile(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None;
        HideDropLine();
        e.Handled = true;
    }

    void OnDragOverTarget(StackPanel panel, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(DragFormat))
        {
            // from outside: a link / text becomes a task here, a CSV/ZIP opens the import
            e.Effects = ExternalDrop.Accepts(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
            if (e.Effects != DragDropEffects.None && ExternalDrop.ImportFile(e.Data) == null) ShowDropLine(panel, DropIndex(panel, e));
            else HideDropLine();
            return;
        }
        e.Effects = (e.KeyStates & DragDropKeyStates.ControlKey) != 0 ? DragDropEffects.Copy : DragDropEffects.Move;
        ShowDropLine(panel, DropIndex(panel, e));
    }

    /// <summary>The day's own task rows in order – also those moved up into a free lane of the multi-day block.</summary>
    static List<FrameworkElement> TaskRows(Panel panel)
    {
        var rows = new List<FrameworkElement>();
        foreach (var c in panel.Children.OfType<FrameworkElement>())
            if (c.Tag is TaskItem) rows.Add(c);
            else if (c is Grid block)
                rows.AddRange(block.Children.OfType<FrameworkElement>().Where(r => r.Tag is TaskItem t && SpanDays(t) == null).OrderBy(Grid.GetRow));
        return rows;
    }

    /// <summary>Where a row sits among the panel's children (a row in the multi-day block: the block's place).</summary>
    static int PanelIndex(Panel panel, FrameworkElement row) =>
        row.Parent is Grid g && !ReferenceEquals(g, panel) && g.Parent == panel ? panel.Children.IndexOf(g) : panel.Children.IndexOf(row);

    static int DropIndex(Panel panel, DragEventArgs e)
    {
        var rows = TaskRows(panel);
        for (int i = 0; i < rows.Count; i++)
            if (e.GetPosition(rows[i]).Y < rows[i].ActualHeight / 2) return i;
        return rows.Count;
    }

    void ShowDropLine(StackPanel panel, int rowIndex)
    {
        _dropLine ??= new Border { Height = 2, CornerRadius = new CornerRadius(1), Margin = new Thickness(0, 1, 4, 1), IsHitTestVisible = false };
        _dropLine.Background = Res("AccentBrush");
        var rows = TaskRows(panel);
        int at = rowIndex < rows.Count ? PanelIndex(panel, rows[rowIndex])
               : rows.Count > 0 ? PanelIndex(panel, rows[^1]) + 1 : panel.Children.Count - 1;
        if (_dropLine.Parent == panel)
        {
            int cur = panel.Children.IndexOf(_dropLine);
            if (cur == at || cur == at - 1) return; // already there (avoid flicker)
        }
        if (_dropLine.Parent is Panel old) old.Children.Remove(_dropLine);
        rows = TaskRows(panel);
        at = rowIndex < rows.Count ? PanelIndex(panel, rows[rowIndex])
           : rows.Count > 0 ? PanelIndex(panel, rows[^1]) + 1 : Math.Max(0, panel.Children.Count - 1);
        panel.Children.Insert(at, _dropLine);
    }

    void HideDropLine()
    {
        if (_dropLine?.Parent is Panel p) p.Children.Remove(_dropLine);
    }

    void OnDrop(string? day, StackPanel panel, DragEventArgs e)
    {
        e.Handled = true;
        HideDropLine();
        if (!e.Data.GetDataPresent(DragFormat)) { DropExternal(day, panel, e); return; }
        if (e.Data.GetData(DragFormat) is not string id) return;
        var t = Store.Data.Tasks.FirstOrDefault(x => x.Id == id);
        if (t == null) return;
        bool copy = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
        // Shift: stretch instead of move – a later day becomes the planned end, an earlier one the start
        if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0 && t.Day != null && day != null && day != t.Day && !t.Archived)
        {
            var target = ParseKey(day);
            Do(() =>
            {
                var real = Store.Materialize(t);
                if (string.CompareOrdinal(day, real.Day) > 0) real.PlanEnd = day;
                else real.WorkedDays = WorkdaysBetween(target, ParseKey(real.Day!)).Select(DayKey)
                        .Concat(real.WorkedDays?.Where(k => string.CompareOrdinal(k, day) >= 0) ?? Enumerable.Empty<string>())
                        .Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
                if (string.CompareOrdinal(day, real.Day) < 0 && !real.Done) real.Started = true; // stretched back: it started earlier
                Store.Changed(real);
            });
            return;
        }

        var visible = TaskRows(panel).Select(r => (TaskItem)r.Tag).ToList();
        int index = DropIndex(panel, e);
        int origin = visible.IndexOf(t);
        if (origin >= 0 && !copy)
        {
            if (origin < index) index--;
            visible.RemoveAt(origin);
            if (origin == index) { Dispatcher.BeginInvoke(Rebuild); return; } // dropped where it already was (clears the highlight)
        }
        double order = OrderBetween(index > 0 ? visible[index - 1] : null, index < visible.Count ? visible[index] : null);

        var dayBefore = t.Day;
        Store.Checkpoint();
        if (copy)
        {
            var c = t.Clone();
            c.Id = Guid.NewGuid().ToString("N");
            c.Day = day;
            c.Order = order;
            c.RuleId = c.RuleDay = null;
            c.Archived = false; // a copy dragged out of the archive goes onto the board
            c.ArchivedAt = null;
            c.NotionId = null;  // the Notion import keeps updating only the original
            c.WorkedDays = null;
            c.Started = null;                         // a fresh copy: not started, planned from its own day
            c.TrackStart = null;                      // … with no time measured
            c.TrackedMinutes = 0;
            c.Done = false;
            c.Review = c.Testing = c.Hold = null; // … in no state of the original
            c.DayStates = null;
            c.PlanEnd = AutoPlanEnd(day, c.Estimate);
            Store.Add(c);
            _justAdded = c.Id;
        }
        else
        {
            if (t.Archived) { t.Archived = false; t.ArchivedAt = null; } // dragged out of the archive tab
            t.NoteMove(t.Day, day, DayKey(DateTime.Today)); // a past day keeps a "continued" trace
            var from = t.Day;
            // a Notion main task takes its sub-items from the same place along
            if (t.NotionId != null && from != day)
            {
                int n = 0;
                foreach (var c in Store.Data.Tasks.Where(c => c.ParentNotionId == t.NotionId && c.Day == from && !c.Archived && c != t).ToList())
                {
                    c.NoteMove(c.Day, day, DayKey(DateTime.Today));
                    c.Day = day;
                    c.Order = order + 0.001 * ++n;
                    Store.Changed(c);
                }
            }
            t.Day = day;
            t.Order = order;
            ApplyAutoPlan(t, from, t.Estimate);
            Store.Changed(t);
            _justAdded = t.Id;
        }
        if (day != null && (copy || dayBefore != day)) WarnIfFull(day);
        Dispatcher.BeginInvoke(Rebuild); // let DoDragDrop unwind before replacing the visuals
    }

    /// <summary>Tasks + meetings of a day, in hours.</summary>
    double DayTotal(string key)
    {
        var date = ParseKey(key);
        double meetings = CalendarService.On(date).Where(e => !e.IsHoliday).Sum(e => e.Hours) + Store.Alarms.Sum(a => a.HoursOn(date));
        return DayLoad(key, Store.Data.Tasks.Where(x => x.Day == key && !x.Archived), openOnly: false) + meetings;
    }

    /// <summary>After a drop: a day over 8 h says so (the bar's "Cofnij" takes the move back).</summary>
    void WarnIfFull(string key)
    {
        var date = ParseKey(key);
        if (IsWeekend(date) || DaysOff.Name(date) != null) return; // no workday limit on days off
        var total = DayTotal(key);
        if (total > DayHours + 0.01)
            ShowUndo(L.F("{0}: {1} z {2} – ponad dzień pracy", ParseKey(key).ToString("dddd d MMM", Pl), Hours(total), Hours(DayHours)));
    }

    /// <summary>Link / text / file dropped from another program.</summary>
    void DropExternal(string? day, StackPanel panel, DragEventArgs e)
    {
        if (ExternalDrop.ImportFile(e.Data) is { } file)
        {
            Dispatcher.BeginInvoke(() => App.Instance.ImportNotion(file));
            return;
        }
        var items = ExternalDrop.Tasks(e.Data);
        if (items.Count == 0) return;
        var visible = TaskRows(panel).Select(r => (TaskItem)r.Tag).ToList();
        int index = DropIndex(panel, e);
        double prev = index > 0 ? visible[index - 1].Order : (visible.Count > 0 ? visible[0].Order - 1 : 0);
        double next = index < visible.Count ? visible[index].Order : prev + items.Count + 1;
        Dispatcher.BeginInvoke(() => Do(() =>
        {
            for (int i = 0; i < items.Count; i++)
            {
                var t = items[i];
                t.Day = day;
                t.Order = prev + (next - prev) * (i + 1) / (items.Count + 1);
                Store.Add(t);
                _justAdded = t.Id;
            }
        }));
    }

    static double OrderBetween(TaskItem? prev, TaskItem? next) =>
        prev == null && next == null ? 0 :
        prev == null ? next!.Order - 1 :
        next == null ? prev.Order + 1 :
        (prev.Order + next.Order) / 2;

    static void MoveToEnd(TaskItem t, string? day)
    {
        t = Store.Materialize(t);
        var siblings = Store.Data.Tasks.Where(x => x.Day == day && x != t && !x.Archived).ToList();
        var oldDay = t.Day;
        t.NoteMove(t.Day, day, DayKey(DateTime.Today));
        t.Day = day;
        t.Order = siblings.Count > 0 ? siblings.Max(x => x.Order) + 1 : 0;
        ApplyAutoPlan(t, oldDay, t.Estimate);
        Store.Changed(t);
    }
}
